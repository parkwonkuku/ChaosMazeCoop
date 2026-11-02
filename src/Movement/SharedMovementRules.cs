/// <summary>
/// 호스트 확정과 클라이언트 예측이 공유하는 한 틱의 이동 규칙입니다.
/// 충돌 조회는 인터페이스로 전달받으며 수집·도착 이벤트는 실행하지 않습니다.
/// </summary>
public static class SharedMovementRules
{
    public static MoveDirection DirectionForKey(InputKey key)
    {
        return key switch
        {
            InputKey.W => MoveDirection.Up,
            InputKey.A => MoveDirection.Left,
            InputKey.S => MoveDirection.Down,
            InputKey.D => MoveDirection.Right,
            _ => MoveDirection.None
        };
    }

    public static Vector2 ToVector(MoveDirection direction)
    {
        return direction switch
        {
            MoveDirection.Up => Vector2.UnitY,
            MoveDirection.Left => -Vector2.UnitX,
            MoveDirection.Down => -Vector2.UnitY,
            MoveDirection.Right => Vector2.UnitX,
            _ => Vector2.Zero
        };
    }

    // 현재 방향으로 한 틱 이동합니다. 막히면 막히기 직전까지만 이동하고 방향은 유지합니다.
    public static SharedMoveState Advance(
        SharedMoveState state,
        float distancePerTick,
        ISharedCharacterCollision collision,
        out float movedDistance)
    {
        movedDistance = 0f;
        if (state.Direction == MoveDirection.None || distancePerTick <= 0f)
        {
            return state;
        }

        var direction = ToVector(state.Direction);
        movedDistance = Math.Clamp(collision.CastBlocking(state.Position, direction, distancePerTick), 0f, distancePerTick);
        return new SharedMoveState(state.Position + direction * movedDistance, state.Direction);
    }
}
