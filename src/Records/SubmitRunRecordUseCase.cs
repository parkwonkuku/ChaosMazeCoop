/// <summary>
/// 인증·프로필·개인 기록 저장 후 리더보드에 제출합니다.
/// 리더보드의 실패 Result와 개인 기록 결과를 구분하며, 예외·취소를 흡수하지는 않습니다.
/// </summary>
public sealed class SubmitRunRecordUseCase
{
    private readonly IApplicationInitializer applicationInitializer;
    private readonly IPlayerProfileRepository profileRepository;
    private readonly IReadOnlyProfileStore profileStore;
    private readonly IRunRecordRepository recordRepository;
    private readonly ILeaderboardGateway leaderboard;
    private readonly float minimumSeconds;

    public SubmitRunRecordUseCase(
        IApplicationInitializer applicationInitializer,
        IPlayerProfileRepository profileRepository,
        IReadOnlyProfileStore profileStore,
        IRunRecordRepository recordRepository,
        ILeaderboardGateway leaderboard,
        float minimumSeconds = 0.1f)
    {
        this.applicationInitializer = applicationInitializer;
        this.profileRepository = profileRepository;
        this.profileStore = profileStore;
        this.recordRepository = recordRepository;
        this.leaderboard = leaderboard;
        this.minimumSeconds = minimumSeconds;
    }

    public async Task<Result<RunRecordOutcome>> ExecuteAsync(
        RunRecord record,
        CancellationToken cancellationToken)
    {
        if (record == null || !record.IsMeaningful(minimumSeconds) || record.Map.IsEmpty)
        {
            return Result<RunRecordOutcome>.Success(RunRecordOutcome.Skipped);
        }

        var initializationResult = await applicationInitializer.InitializeAsync(cancellationToken);
        if (initializationResult.IsFailure)
        {
            return Result<RunRecordOutcome>.Failure(initializationResult.Error);
        }

        var profileSaveResult = await profileRepository.SaveAsync(profileStore.Current, cancellationToken);
        if (profileSaveResult.IsFailure)
        {
            return Result<RunRecordOutcome>.Failure(profileSaveResult.Error);
        }

        var saveResult = await recordRepository.SaveAsync(record, profileStore.Current.Nickname, cancellationToken);
        if (saveResult.IsFailure)
        {
            return saveResult;
        }

        // 리더보드 실패는 개인 기록 결과를 덮어쓰지 않는다.
        await leaderboard.SubmitAsync(record, profileStore.Current.Nickname, cancellationToken);
        return saveResult;
    }
}
