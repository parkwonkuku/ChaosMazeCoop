/// <summary>
/// 과거의 새 접촉으로 재계산이 취소됐을 때 상태·겹침 복원과 현재 틱 적용을 확인합니다.
/// 제품 HostMoveAuthority를 사용하며 충돌 조회만 고정된 가짜 맵으로 바꿉니다.
/// </summary>
public static class ReplayCancellationCase
{
    private static int assertions;
    private static readonly InputKey[] HostKeys = { InputKey.W, InputKey.S };
    private static readonly InputKey[] GuestKeys = { InputKey.A, InputKey.D };

    public static void Run()
    {
        assertions = 0;
        foreach (var kind in new[] { MapContactKind.Star, MapContactKind.Goal, MapContactKind.Hazard })
        {
            CancelPastContact(kind);
        }

        ReapplyWholeIncomingBatch();
        Console.WriteLine($"PASS: 4 scenarios, {assertions} assertions");
    }

    private static HostMoveAuthority Create(PointMap map)
    {
        var host = new HostMoveAuthority(Lookup, map, 0.1f, 6, rollbackTicks: 6);
        host.ResetTo(Vector2.Zero, 0);
        Require(host.Receive(0, new MoveCommand(host.Epoch, 1, InputKey.W, 1))
            == MoveCommandResult.Queued, "initial input queued");
        for (var tick = 1; tick <= 4; tick++) host.Step(tick);
        Require(Near(host.State.Position, new Vector2(0, 0.4f)), "original path before replay");
        return host;
    }

    private static void CancelPastContact(MapContactKind kind)
    {
        var map = new PointMap(kind);
        var host = Create(map);
        Require(host.Receive(1, new MoveCommand(host.Epoch, 1, InputKey.D, 1))
            == MoveCommandResult.Queued, "late direction queued");

        var result = host.Step(5);

        Require(map.PastTargetQueried, "replay reached the new past contact");
        Require(host.LastRollbackBlockedByContact, "replay canceled by contact");
        Require(host.LastRollbackTicks == 0, "canceled replay not reported as applied rewind");
        Require(host.LastAppliedCommands.Count == 1 && host.LastAppliedCommands[0].ApplyTick == 5,
            "late input applied on current tick");
        Require(Near(host.StateBeforeLastStep.Position, new Vector2(0, 0.4f))
            && host.StateBeforeLastStep.Direction == MoveDirection.Up, "position and direction restored");
        Require(Near(host.State.Position, new Vector2(0.1f, 0.4f))
            && host.State.Direction == MoveDirection.Right, "current tick applied after restore");
        Require(NoContacts(result), "past contact and already-overlapping star not emitted");

        var restored = host.StateBeforeLastStep;
        var tick5 = host.State;
        var appliedTick = host.LastAppliedCommands[0].ApplyTick;
        var pastEvents = result.EnteredStarIds.Count(id => id != 77)
            + (result.GoalEntered ? 1 : 0) + (result.HazardEntered ? 1 : 0);
        var overlappingEvents = result.EnteredStarIds.Count(id => id == 77);

        var next = host.Step(6);
        Require(Near(host.State.Position, new Vector2(0.2f, 0.4f)), "next tick has no speculative state");
        Require(NoContacts(next), "next tick does not duplicate contact");

        Console.WriteLine($"PASS {kind}: before={Point(restored.Position)}/{restored.Direction}; "
            + $"tick5={Point(tick5.Position)}/{tick5.Direction}; applyTick={appliedTick}; "
            + $"pastEvent={pastEvents}; overlappingStarEvent={overlappingEvents}; tick6={Point(host.State.Position)}");
    }

    private static void ReapplyWholeIncomingBatch()
    {
        var map = new PointMap(MapContactKind.Star);
        var host = Create(map);
        Require(host.Receive(1, new MoveCommand(host.Epoch, 1, InputKey.D, 1))
            == MoveCommandResult.Queued, "late input queued");
        Require(host.Receive(0, new MoveCommand(host.Epoch, 2, InputKey.S, 5))
            == MoveCommandResult.Queued, "current input queued after late input");

        var result = host.Step(5);

        Require(map.PastTargetQueried && host.LastRollbackBlockedByContact, "batch replay canceled");
        Require(host.LastAppliedCommands.Count == 2
            && host.LastAppliedCommands.All(command => command.ApplyTick == 5), "whole batch moved to current tick");
        Require(host.LastAppliedCommands[0].SenderClientId == 1
            && host.LastAppliedCommands[1].SenderClientId == 0, "receipt order preserved");
        Require(Near(host.State.Position, new Vector2(0, 0.3f))
            && host.State.Direction == MoveDirection.Down, "last direction determines the single tick movement");
        Require(NoContacts(result), "batch fallback emits no speculative contact");

        var applied = string.Join(",", host.LastAppliedCommands.Select(command => command.ApplyTick));
        var order = string.Join(",", host.LastAppliedCommands.Select(command => command.SenderClientId == 0 ? "host" : "guest"));
        var events = result.EnteredStarIds.Count + (result.GoalEntered ? 1 : 0) + (result.HazardEntered ? 1 : 0);
        Console.WriteLine($"PASS Batch: inputs=D@1,S@5; applied={applied}; order={order}; "
            + $"tick5={Point(host.State.Position)}/{host.State.Direction}; event={events}");
    }

    private static bool Lookup(ulong id, out IReadOnlyList<InputKey> keys)
    {
        keys = id == 0 ? HostKeys : GuestKeys;
        return id < 2;
    }

    private static bool Near(Vector2 a, Vector2 b) => Vector2.Distance(a, b) < 0.00001f;
    private static string Point(Vector2 value) => FormattableString.Invariant($"({value.X:0.000},{value.Y:0.000})");
    private static bool NoContacts(HostStepOutcome result) =>
        !result.HazardEntered && !result.GoalEntered && result.EnteredStarIds.Count == 0;

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        assertions++;
    }

    private sealed class PointMap : ISharedCharacterCollision
    {
        private readonly MapContactKind targetKind;
        public bool PastTargetQueried { get; private set; }

        public PointMap(MapContactKind targetKind) => this.targetKind = targetKind;
        public float CastBlocking(Vector2 from, Vector2 direction, float distance) => distance;

        public void CastContacts(Vector2 from, Vector2 direction, float distance, List<MapContact> results)
        {
            var end = from + direction * distance;
            // 시작부터 겹친 별: 실패한 재계산이 이 겹침을 잃어버려도, 복원 후 다시 발행되면 안 된다.
            if (InsideExistingStar(from) || InsideExistingStar(end))
                results.Add(new MapContact(100, MapContactKind.Star, 77, 0));

            var delta = new Vector2(0.2f, 0) - from;
            var along = Vector2.Dot(delta, direction);
            var side = delta - direction * along;
            var touches = distance == 0
                ? delta.Length() < 0.00001f
                : side.Length() < 0.00001f && along >= -0.00001f && along <= distance + 0.00001f;
            if (touches)
            {
                PastTargetQueried = true;
                results.Add(new MapContact(200, targetKind, 7, Math.Max(0, along)));
            }
        }

        private static bool InsideExistingStar(Vector2 position) =>
            position.X >= -0.00001f && position.X <= 0.15f
            && position.Y >= -0.00001f && position.Y <= 0.5f;
    }
}
