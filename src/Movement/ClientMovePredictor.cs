/// <summary>
/// 확정 상태에서 미처리 자기 입력만 재적용하는 클라이언트 예측기입니다.
/// 호스트가 처리한 명령은 거부 여부와 관계없이 제거하며, 예측은 틱·대기열 한도 안에서만 진행합니다.
/// </summary>
public sealed class ClientMovePredictor
{
    private readonly List<PendingMoveCommand> pending = new();
    private readonly int maxPendingCommands;
    private readonly int maxPredictionTicks;

    private ConfirmedMoveState confirmed;
    private uint lastSequence;
    private bool isHalted;

    public ClientMovePredictor(int maxPendingCommands, int maxPredictionTicks)
    {
        this.maxPendingCommands = Math.Max(1, maxPendingCommands);
        this.maxPredictionTicks = Math.Max(0, maxPredictionTicks);
    }

    public ConfirmedMoveState Confirmed => confirmed;
    public IReadOnlyList<PendingMoveCommand> Pending => pending;

    public bool CanAcceptInput => confirmed != null
        && confirmed.Status == MovementStatus.Active
        && !isHalted
        && pending.Count < maxPendingCommands;

    // 자기 입력으로 명령을 만들어 보관합니다. 다음 로컬 틱(localTick + 1)부터 예측에 반영되며, 호출자는 true일 때 이 명령을 호스트로 보냅니다.
    public bool TryCreateCommand(InputKey key, int localTick, out MoveCommand command)
    {
        if (!CanAcceptInput)
        {
            command = default;
            return false;
        }

        lastSequence = SequenceMath.Next(lastSequence);
        command = new MoveCommand(confirmed.Epoch, lastSequence, key, localTick + 1);
        pending.Add(new PendingMoveCommand(command, localTick + 1));
        return true;
    }

    // 받은 확정 상태를 반영합니다. 이미 반영한 것보다 오래된 상태는 무시합니다.
    public ConfirmedApplyResult ApplyConfirmed(ConfirmedMoveState state, ulong localClientId)
    {
        if (state == null || (confirmed != null && !SequenceMath.IsNewer(state.Revision, confirmed.Revision)))
        {
            return ConfirmedApplyResult.IgnoredStale;
        }

        var epochChanged = confirmed == null || state.Epoch != confirmed.Epoch;
        confirmed = state;

        if (epochChanged)
        {
            pending.Clear();
            isHalted = false;
            return ConfirmedApplyResult.EpochChanged;
        }

        var lastProcessed = state.GetLastProcessedSequence(localClientId);
        // 호스트가 처리한 명령(거부 포함)은 이후 예측에서 제외한다.
        pending.RemoveAll(item => !SequenceMath.IsNewer(item.Command.Sequence, lastProcessed));
        return ConfirmedApplyResult.Applied;
    }

    // 확정 상태부터 targetTick까지(한도 안에서) 대기 중인 자기 명령을 다시 적용하며 계산합니다.
    // predictedTick은 실제로 계산을 마친 틱입니다. Halt 후 정지 확정 전이면 false입니다.
    public bool TryPredict(
        int targetTick,
        float distancePerTick,
        ISharedCharacterCollision collision,
        out SharedMoveState predicted,
        out int predictedTick)
    {
        if (confirmed == null)
        {
            predicted = default;
            predictedTick = 0;
            return false;
        }

        // 이전 예측에 누적하지 않고, 마지막 확정 상태를 기준으로 매번 다시 계산한다.
        predicted = confirmed.State;
        predictedTick = confirmed.ServerTick;
        if (confirmed.Status != MovementStatus.Active)
        {
            return true;
        }

        if (isHalted)
        {
            return false;
        }

        var lastTick = targetTick - predictedTick > maxPredictionTicks
            ? predictedTick + maxPredictionTicks
            : targetTick;
        var nextPending = 0;
        while (lastTick - predictedTick > 0)
        {
            predictedTick++;
            // 확정 틱 이하의 미처리 입력은 첫 재계산 틱부터 적용한다.
            while (nextPending < pending.Count && pending[nextPending].LocalApplyTick - predictedTick <= 0)
            {
                predicted = predicted.WithDirection(SharedMovementRules.DirectionForKey(pending[nextPending].Command.Key));
                nextPending++;
            }

            predicted = SharedMovementRules.Advance(predicted, distancePerTick, collision, out _);
        }

        return true;
    }

    // 이동 시계를 재기준 설정해 틱 번호가 delta만큼 바뀌면, 미처리 명령의 로컬 적용 틱도 같은 만큼 옮겨
    // "입력을 본 다음 로컬 이동 단계"라는 의미를 유지합니다.
    public void ShiftPendingApplyTicks(int delta)
    {
        for (var i = 0; i < pending.Count; i++)
        {
            pending[i] = new PendingMoveCommand(pending[i].Command, pending[i].LocalApplyTick + delta);
        }
    }

    // 골인·종료를 먼저 알게 되었을 때 새 Epoch의 확정 상태를 받기 전까지 예측과 입력을 멈춥니다.
    public void Halt()
    {
        isHalted = true;
        pending.Clear();
    }

    // 네트워크 오브젝트가 사라질 때 보관 중인 상태를 모두 버립니다.
    public void Clear()
    {
        confirmed = null;
        isHalted = true;
        pending.Clear();
    }
}
