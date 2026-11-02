/// <summary>
/// 호스트의 입력 검증·이동 확정·제한적 재계산을 담당합니다.
/// 과거의 새 접촉으로 재계산이 실패하면 Step이 상태를 복원하고 수신 입력을 현재 틱에 적용합니다.
/// </summary>
public sealed class HostMoveAuthority
{
    public const int MaxQueuedCommands = 64;

    // 막힌 지점 바로 앞의 벽도 접촉으로 잡기 위한 여유 거리입니다. 충돌 조회의 표면 여유(0.01)보다 커야 합니다.
    public const float ContactReachTolerance = 0.05f;

    private readonly AssignedKeysLookup assignedKeysLookup;
    private readonly ISharedCharacterCollision collision;
    private readonly float distancePerTick;
    private readonly int heartbeatIntervalTicks;
    private readonly int rollbackTicks;
    private readonly List<RollbackFrame> frames = new();
    private readonly SortedDictionary<ulong, PlayerSequenceRecord> players = new();
    private readonly List<QueuedCommand> queue = new();
    private readonly List<MapContact> contacts = new();
    private readonly List<MapContact> endContacts = new();
    private readonly HashSet<int> overlappingContactIds = new();
    private readonly List<int> enteredStarIds = new();
    private readonly List<AppliedMoveCommand> appliedCommands = new();

    private uint epoch;
    private uint revision;
    private int serverTick;
    private int lastPublishedTick;
    private bool hasPublished;
    private bool isDirty;

    public HostMoveAuthority(
        AssignedKeysLookup assignedKeysLookup,
        ISharedCharacterCollision collision,
        float distancePerTick,
        int heartbeatIntervalTicks,
        int rollbackTicks = 0)
    {
        this.assignedKeysLookup = assignedKeysLookup;
        this.collision = collision;
        this.distancePerTick = distancePerTick;
        this.heartbeatIntervalTicks = heartbeatIntervalTicks < 1 ? 1 : heartbeatIntervalTicks;
        this.rollbackTicks = Math.Clamp(rollbackTicks, 0, 32);
    }

    public SharedMoveState State { get; private set; }
    // 재계산을 마친 현재 틱 직전 상태입니다. 화면에서 롤백 차이와 정상 한 틱 이동을 분리할 때 사용합니다.
    public SharedMoveState StateBeforeLastStep { get; private set; }
    public MovementStatus Status { get; private set; } = MovementStatus.NotStarted;
    public uint Epoch => epoch;
    public int ServerTick => serverTick;
    public int LastRollbackTicks { get; private set; }
    public int LastRollbackFromTick { get; private set; }
    public bool LastRollbackBlockedByContact { get; private set; }

    // 마지막 Step에서 수신 순서대로 실제 적용한 명령들입니다. 진단 로그용이며 다음 Step에서 다시 채워집니다.
    public IReadOnlyList<AppliedMoveCommand> LastAppliedCommands => appliedCommands;

    // 명령 하나를 검증합니다. 통과하면 다음 Step에서 적용되도록 수신 순서대로 쌓습니다.
    public MoveCommandResult Receive(ulong senderClientId, MoveCommand command)
    {
        if (!assignedKeysLookup(senderClientId, out var assignedKeys))
        {
            return MoveCommandResult.RejectedNotParticipant;
        }

        var player = GetOrAddPlayer(senderClientId);
        if (!SequenceMath.IsNewer(command.Sequence, player.LastReceivedSequence))
        {
            return MoveCommandResult.IgnoredDuplicate;
        }

        player.LastReceivedSequence = command.Sequence;
        var result = Validate(command, assignedKeys);
        if (result == MoveCommandResult.Queued)
        {
            queue.Add(new QueuedCommand(senderClientId, command));
            return result;
        }

        // 거부한 명령도 처리 완료로 알려 클라이언트의 미처리 입력에서 제거한다.
        MarkProcessed(senderClientId, command.Sequence);
        return result;
    }

    // 입력 삽입 → 필요한 구간 재계산 → 현재 틱의 접촉 판정 순입니다.
    // 과거 경로에 새로운 별·위험 지형·골 접촉이 생기면 되감기를 취소하고 새 입력을 현재 틱에 적용합니다.
    // 외부 게임 판정의 취소·재발행 없이 일반 이동 구간에서만 입력 시점을 복원합니다.
    public HostStepOutcome Step(int tick)
    {
        serverTick = tick;
        StateBeforeLastStep = State;
        appliedCommands.Clear();
        LastRollbackTicks = 0;
        LastRollbackBlockedByContact = false;
        LastRollbackFromTick = tick;
        if (Status != MovementStatus.Active) return HostStepOutcome.None;

        // 되감기 실패 시 위치뿐 아니라 기존 접촉 정보도 함께 복원한다.
        var presentBefore = State;
        var presentOverlaps = overlappingContactIds.ToArray();
        frames.Add(new RollbackFrame(tick, presentBefore, presentOverlaps));

        while (frames.Count > 1 && (long)tick - frames[0].Tick > rollbackTicks)
            frames.RemoveAt(0);

        var firstChanged = frames.Count - 1;
        var incoming = queue.ToArray();

        foreach (var queued in incoming)
        {
            var player = GetOrAddPlayer(queued.SenderClientId);
            var requested = rollbackTicks > 0 && queued.Command.HasApplyTick ? queued.Command.ApplyTick : tick;

            // 기기 시각을 신뢰하지 않고, 보관 범위·판정 경계·현재 틱·송신자 내 명령 순서를 강제합니다.
            var applyTick = Math.Clamp(requested, frames[0].Tick, tick);
            // 같은 사용자의 후속 명령이 앞선 명령보다 과거에 적용되지 않게 한다.
            applyTick = Math.Clamp(Math.Max(applyTick, player.LastApplyTick), frames[0].Tick, tick);
            player.LastApplyTick = applyTick;

            var index = frames.FindIndex(frame => frame.Tick >= applyTick);
            frames[index].Commands.Add(queued);
            firstChanged = Math.Min(firstChanged, index);

            MarkProcessed(queued.SenderClientId, queued.Command.Sequence);
            appliedCommands.Add(new AppliedMoveCommand(queued.SenderClientId, queued.Command, frames[index].Tick));
        }
        queue.Clear();

        LastRollbackFromTick = frames[firstChanged].Tick;
        LastRollbackTicks = tick - LastRollbackFromTick;
        var before = frames[firstChanged].Before;
        var overlaps = frames[firstChanged].OverlapsBefore;

        if (!ReplayFrames(firstChanged, before, overlaps, out var outcome))
        {
            // 과거에 새 판정이 생기면 되감기를 취소하고, 이번 수신 입력 전체를 현재 틱으로 옮긴다.
            var currentFrame = frames[frames.Count - 1];
            currentFrame.Commands.Clear();
            currentFrame.Commands.AddRange(incoming);
            frames.Clear();
            frames.Add(currentFrame);
            appliedCommands.Clear();

            foreach (var item in incoming)
            {
                GetOrAddPlayer(item.SenderClientId).LastApplyTick = tick;
                appliedCommands.Add(new AppliedMoveCommand(item.SenderClientId, item.Command, tick));
            }

            LastRollbackBlockedByContact = true;
            LastRollbackTicks = 0;
            LastRollbackFromTick = tick;
            ReplayFrames(0, presentBefore, presentOverlaps, out outcome);
        }

        // 이미 발행할 판정을 이후 입력이 취소하지 못하도록 현재 틱에 되감기 경계를 둡니다.
        if (HasContacts(outcome)) frames.Clear();
        return outcome;
    }

    // false 반환 시 State는 임시 계산 상태다. 호출자가 원래 상태에서 현재 틱을 다시 계산해야 한다.
    private bool ReplayFrames(int first, SharedMoveState before, IEnumerable<int> overlaps, out HostStepOutcome outcome)
    {
        outcome = HostStepOutcome.None;
        State = before;
        overlappingContactIds.Clear();
        overlappingContactIds.UnionWith(overlaps);

        for (var i = first; i < frames.Count; i++)
        {
            var frame = frames[i];
            // 변경된 경로를 이후 되감기의 시작 상태로 다시 기록한다.
            frame.Before = State;
            frame.OverlapsBefore = overlappingContactIds.ToArray();

            if (i == frames.Count - 1)
                StateBeforeLastStep = State;

            // 같은 틱의 명령은 기록 순서대로 적용하며, 마지막 명령의 방향으로 이동한다.
            foreach (var command in frame.Commands)
                State = State.WithDirection(SharedMovementRules.DirectionForKey(command.Command.Key));

            var from = State.Position;
            var direction = SharedMovementRules.ToVector(State.Direction);
            var travel = State.Direction == MoveDirection.None ? 0f : distancePerTick;

            State = SharedMovementRules.Advance(State, distancePerTick, collision, out var movedDistance);
            outcome = CollectEnteredContacts(from, direction, travel, movedDistance);

            // 현재 틱의 접촉은 반환하되, 과거에 새 판정을 만드는 재계산은 거부한다.
            if (i < frames.Count - 1 && HasContacts(outcome))
                return false;
        }
        return true;
    }

    private static bool HasContacts(HostStepOutcome outcome) =>
        outcome.HazardEntered || outcome.GoalEntered || outcome.EnteredStarIds.Count > 0;

    // 런을 새로 시작합니다. Epoch를 올려 이전 런의 늦은 명령을 거절하고, 시작 위치에서 멈춘 상태로 이동을 허용합니다.
    public void ResetTo(Vector2 position, int tick)
    {
        if (tick - serverTick > 0)
        {
            serverTick = tick;
        }

        epoch = SequenceMath.Next(epoch);
        State = new SharedMoveState(position, MoveDirection.None);
        Status = MovementStatus.Active;
        RejectQueuedCommands();
        frames.Clear();

        foreach (var player in players.Values)
            player.LastApplyTick = serverTick;

        RefreshOverlaps(position);
        isDirty = true;
    }

    // 골인이나 종료로 이동을 멈춥니다. 이후 명령은 거절되고 접촉 판정도 하지 않습니다.
    public void Stop()
    {
        if (Status == MovementStatus.Stopped)
        {
            return;
        }

        Status = MovementStatus.Stopped;
        State = State.WithDirection(MoveDirection.None);
        RejectQueuedCommands();
        frames.Clear();
        isDirty = true;
    }

    // 상태가 바뀌었거나 마지막 전송 후 주기가 지났으면 보낼 확정 상태를 만듭니다.
    public bool TryCreateSnapshot(out ConfirmedMoveState snapshot)
    {
        if (!isDirty && hasPublished && serverTick - lastPublishedTick < heartbeatIntervalTicks)
        {
            snapshot = null;
            return false;
        }

        revision = SequenceMath.Next(revision);
        isDirty = false;
        hasPublished = true;
        lastPublishedTick = serverTick;
        snapshot = new ConfirmedMoveState(epoch, serverTick, revision, State, Status, CreateAcks());
        return true;
    }

    private MoveCommandResult Validate(MoveCommand command, IReadOnlyList<InputKey> assignedKeys)
    {
        if (command.Epoch != epoch)
        {
            return MoveCommandResult.RejectedStaleEpoch;
        }

        if (Status != MovementStatus.Active)
        {
            return MoveCommandResult.RejectedNotActive;
        }

        if (assignedKeys == null || !assignedKeys.Contains(command.Key))
        {
            return MoveCommandResult.RejectedKeyNotAssigned;
        }

        return queue.Count >= MaxQueuedCommands ? MoveCommandResult.RejectedQueueFull : MoveCommandResult.Queued;
    }

    private void RejectQueuedCommands()
    {
        foreach (var queued in queue)
        {
            MarkProcessed(queued.SenderClientId, queued.Command.Sequence);
        }

        queue.Clear();
    }

    // 이번 틱에 실제로 도달한 경로 안에서, 이전 틱 끝에 겹쳐 있지 않던 충돌체만 진입으로 모읍니다.
    private HostStepOutcome CollectEnteredContacts(Vector2 from, Vector2 direction, float travel, float movedDistance)
    {
        contacts.Clear();
        enteredStarIds.Clear();
        collision.CastContacts(from, direction, travel, contacts);
        // BoxCast와 OverlapBox의 접촉 여유가 달라, 이동 끝에서만 새 접촉이 잡힐 수 있습니다.
        // 이 접촉도 진입으로 처리한 뒤에 이전 접촉 목록으로 옮겨야 수집을 놓치지 않습니다.
        endContacts.Clear();
        collision.CastContacts(State.Position, Vector2.Zero, 0f, endContacts);
        contacts.AddRange(endContacts);

        var hazardEntered = false;
        var goalEntered = false;
        foreach (var contact in contacts)
        {
            if (contact.Distance > movedDistance + ContactReachTolerance || overlappingContactIds.Contains(contact.Id))
            {
                continue;
            }

            switch (contact.Kind)
            {
                case MapContactKind.Hazard:
                    hazardEntered = true;
                    break;
                case MapContactKind.Star:
                    if (!enteredStarIds.Contains(contact.StarId))
                    {
                        enteredStarIds.Add(contact.StarId);
                    }

                    break;
                case MapContactKind.Goal:
                    goalEntered = true;
                    break;
            }
        }

        UpdateOverlappingContactIds();
        return new HostStepOutcome(hazardEntered, goalEntered, enteredStarIds);
    }

    private void RefreshOverlaps(Vector2 position)
    {
        endContacts.Clear();
        collision.CastContacts(position, Vector2.Zero, 0f, endContacts);
        UpdateOverlappingContactIds();
    }

    private void UpdateOverlappingContactIds()
    {
        overlappingContactIds.Clear();
        foreach (var contact in endContacts)
        {
            overlappingContactIds.Add(contact.Id);
        }
    }

    private void MarkProcessed(ulong senderClientId, uint sequence)
    {
        var player = GetOrAddPlayer(senderClientId);
        if (SequenceMath.IsNewer(sequence, player.LastProcessedSequence))
        {
            player.LastProcessedSequence = sequence;
        }

        isDirty = true;
    }

    private PlayerSequenceRecord GetOrAddPlayer(ulong clientId)
    {
        if (!players.TryGetValue(clientId, out var player))
        {
            player = new PlayerSequenceRecord();
            players.Add(clientId, player);
        }

        return player;
    }

    private PlayerSequenceAck[] CreateAcks()
    {
        var acks = new PlayerSequenceAck[players.Count];
        var index = 0;
        foreach (var player in players)
        {
            acks[index++] = new PlayerSequenceAck(player.Key, player.Value.LastProcessedSequence);
        }

        return acks;
    }

    private sealed class PlayerSequenceRecord
    {
        public uint LastReceivedSequence;
        public uint LastProcessedSequence;
        public int LastApplyTick = int.MinValue;
    }

    private sealed class RollbackFrame
    {
        public RollbackFrame(int tick, SharedMoveState before, IEnumerable<int> overlaps)
        { Tick = tick; Before = before; OverlapsBefore = overlaps.ToArray(); }
        public int Tick { get; }
        public SharedMoveState Before;
        public int[] OverlapsBefore;
        public readonly List<QueuedCommand> Commands = new();
    }

    private readonly struct QueuedCommand
    {
        public QueuedCommand(ulong senderClientId, MoveCommand command)
        {
            SenderClientId = senderClientId;
            Command = command;
        }

        public ulong SenderClientId { get; }
        public MoveCommand Command { get; }
    }
}
