/// <summary>
/// 게임 시작 조건·방 상태 변경·네트워크 씬 전환을 조정합니다.
/// 비동기 응답 후 세션 교체를 검사하고, 시작 거절 시 Waiting 복원 결과를 따로 반환합니다.
/// </summary>
public sealed class StartGameUseCase
{
    private readonly ISessionLifetime sessions;
    private readonly ISessionStore sessionStore;

    public StartGameUseCase(ISessionLifetime sessions, ISessionStore sessionStore)
    {
        this.sessions = sessions;
        this.sessionStore = sessionStore;
    }

    public async Task<StartGameResult> ExecuteAsync(
        IReadOnlyList<RoomMember> members,
        CancellationToken cancellationToken,
        Func<bool> isRoomUnchanged)
    {
        var block = RoomReadiness.CanStartGame(members);
        if (block != RoomBlockReason.None)
        {
            return StartGameResult.Failed(Result.Failure(new ApplicationError(
                ApplicationErrorCode.RoomNotReady,
                block.ToString())));
        }

        var scope = sessions.Current;
        if (scope == null)
        {
            return StartGameResult.Failed(Result.Failure(new ApplicationError(
                ApplicationErrorCode.RoomNotReady,
                "Network session is not active.")));
        }

        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, scope.LifetimeToken);
        var requestToken = requestCancellation.Token;
        ThrowIfSessionEnded(scope, requestToken);

        // 게임 화면이 방 화면 없이도 키 배정과 닉네임을 쓸 수 있도록 확정된 멤버를 세션에 남긴다.
        sessionStore.Set(sessionStore.Current.WithMembers(members));

        var stateResult = await scope.Directory.UpdateStateAsync(RoomLifecycleState.InGame, requestToken);
        ThrowIfSessionEnded(scope, requestToken);
        if (stateResult.IsFailure)
        {
            return StartGameResult.Failed(stateResult);
        }

        var sceneResult = isRoomUnchanged()
            ? scope.SceneLoader.LoadForAllClients(SceneId.Game)
            : Result.Failure(new ApplicationError(
                ApplicationErrorCode.RoomNotReady,
                "Room members or assigned keys changed while starting the game."));
        if (sceneResult.IsFailure)
        {
            // 요청 거절이나 방 구성 변경으로 시작할 수 없으면 방을 다시 검색에 노출한다.
            // 원래 실패 원인은 그대로 두고, 복원 결과는 따로 담아 돌려준다.
            var restoreResult = await scope.Directory.UpdateStateAsync(RoomLifecycleState.Waiting, requestToken);
            ThrowIfSessionEnded(scope, requestToken);
            return StartGameResult.FailedAfterStateChange(sceneResult, restoreResult);
        }

        return StartGameResult.Succeeded();
    }

    private void ThrowIfSessionEnded(SessionScope scope, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ReferenceEquals(scope, sessions.Current))
        {
            throw new OperationCanceledException("The room session has ended.", cancellationToken);
        }
    }
}
