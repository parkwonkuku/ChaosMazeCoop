/// <summary>
/// 위치 대신 회차·명령 번호·담당 키·적용 틱을 전달하는 입력 명령입니다.
/// 송신자 ID는 명령 본문이 아닌 RPC 수신 메타데이터에서 얻습니다.
/// </summary>
public readonly struct MoveCommand
{
    public MoveCommand(uint epoch, uint sequence, InputKey key, int applyTick = int.MinValue)
    {
        Epoch = epoch;
        Sequence = sequence;
        Key = key;
        ApplyTick = applyTick;
    }

    public uint Epoch { get; }
    public uint Sequence { get; }
    public InputKey Key { get; }
    // 같은 서버 틱 공간의 적용 예정 틱(클라이언트가 예측을 시작한 틱)입니다. 로컬 시각·위치는 보내지 않습니다.
    // int.MinValue는 타임스탬프 없는 오프라인·기존 호출이며, 다음 호스트 틱에 적용합니다.
    public int ApplyTick { get; }
    public bool HasApplyTick => ApplyTick != int.MinValue;
}
