/// <summary>
/// 방 이탈 요청, 통신 종료, 세션 정리와 로비 이동의 순서를 조정합니다.
/// 현재 세션이 없으면 종료 요청을 생략합니다.
/// </summary>
public sealed class LeaveSessionUseCase
{
    private readonly ISessionLifetime sessions;
    private readonly ISessionStore sessionStore;
    private readonly ISessionNoticeStore noticeStore;
    private readonly ILocalSceneLoader sceneLoader;

    public LeaveSessionUseCase(
        ISessionLifetime sessions,
        ISessionStore sessionStore,
        ISessionNoticeStore noticeStore,
        ILocalSceneLoader sceneLoader)
    {
        this.sessions = sessions;
        this.sessionStore = sessionStore;
        this.noticeStore = noticeStore;
        this.sceneLoader = sceneLoader;
    }

    public async Task<Result> ExecuteAsync(SessionEndReason reason, CancellationToken cancellationToken)
    {
        var scope = sessions.Current;
        if (scope == null)
        {
            return Result.Success();
        }

        // 방 해제는 결과를 기다리지 않는다. 실패해도 서비스 측 시간 초과로 정리되며 화면 전환을 늦추지 않는다.
        _ = scope.Directory.LeaveAsync(CancellationToken.None);
        scope.Runner.Shutdown();

        sessionStore.Clear();
        sessions.End();
        noticeStore.Post(reason);

        return await sceneLoader.LoadSingleAsync(SceneId.Lobby, cancellationToken);
    }
}
