/// <summary>
/// 확정 틱의 이동 상태와 Revision·Epoch·참가자별 처리 번호를 담습니다.
/// 클라이언트는 이 상태를 기준으로 처리된 명령을 제거하고 남은 입력을 재계산합니다.
/// </summary>
public sealed class ConfirmedMoveState
{
    public ConfirmedMoveState(
        uint epoch,
        int serverTick,
        uint revision,
        SharedMoveState state,
        MovementStatus status,
        IReadOnlyList<PlayerSequenceAck> acks)
    {
        Epoch = epoch;
        ServerTick = serverTick;
        Revision = revision;
        State = state;
        Status = status;
        Acks = acks ?? Array.Empty<PlayerSequenceAck>();
    }

    public uint Epoch { get; }
    public int ServerTick { get; }
    public uint Revision { get; }
    public SharedMoveState State { get; }
    public MovementStatus Status { get; }
    public IReadOnlyList<PlayerSequenceAck> Acks { get; }

    // 해당 플레이어의 마지막 처리 Sequence를 찾습니다. 한 번도 처리된 적이 없으면 0입니다.
    public uint GetLastProcessedSequence(ulong clientId)
    {
        foreach (var ack in Acks)
        {
            if (ack.ClientId == clientId)
            {
                return ack.LastProcessedSequence;
            }
        }

        return 0;
    }
}
