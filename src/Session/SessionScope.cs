/// <summary>
/// 한 온라인 세션이 소유한 Port와 수명을 관리합니다.
/// Dispose는 중복 호출을 막고 수명 취소와 어댑터 해제를 진행합니다.
/// </summary>
public sealed class SessionScope : IDisposable
{
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private bool isDisposed;

    public SessionScope(
        IRelayGateway relay,
        ILobbyDirectory directory,
        INetworkRunner runner,
        INetworkSceneLoader sceneLoader)
    {
        LifetimeToken = lifetimeCancellation.Token;
        Relay = relay ?? throw new ArgumentNullException(nameof(relay));
        Directory = directory ?? throw new ArgumentNullException(nameof(directory));
        Runner = runner ?? throw new ArgumentNullException(nameof(runner));
        SceneLoader = sceneLoader ?? throw new ArgumentNullException(nameof(sceneLoader));
    }

    public IRelayGateway Relay { get; }
    public ILobbyDirectory Directory { get; }
    public INetworkRunner Runner { get; }
    public INetworkSceneLoader SceneLoader { get; }
    public CancellationToken LifetimeToken { get; }

    // 세션 수명을 취소하고 IDisposable인 어댑터만 해제합니다.
    public void Dispose()
    {
        if (isDisposed)
        {
            return;
        }

        isDisposed = true;
        lifetimeCancellation.Cancel();
        (SceneLoader as IDisposable)?.Dispose();
        (Runner as IDisposable)?.Dispose();
        (Directory as IDisposable)?.Dispose();
        (Relay as IDisposable)?.Dispose();
        lifetimeCancellation.Dispose();
    }
}
