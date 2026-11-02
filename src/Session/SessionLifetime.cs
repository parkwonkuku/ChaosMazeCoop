/// <summary>
/// 활성 세션 하나의 생성과 종료를 관리합니다.
/// End는 현재 참조 해제, 종료 알림, Scope 해제 순으로 처리합니다.
/// </summary>
public sealed class SessionLifetime : ISessionLifetime
{
    private readonly Func<SessionScope> scopeFactory;
    private SessionScope current;

    public SessionLifetime(Func<SessionScope> scopeFactory)
    {
        this.scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    public bool IsActive => current != null;
    public SessionScope Current => current;

    public event Action<SessionScope> Started;
    public event Action Ended;

    public SessionScope Begin()
    {
        if (current != null)
        {
            return current;
        }

        current = scopeFactory()
            ?? throw new InvalidOperationException("Session scope factory returned null.");
        Started?.Invoke(current);
        return current;
    }

    public void End()
    {
        if (current == null)
        {
            return;
        }

        var scope = current;
        current = null;
        Ended?.Invoke();
        scope.Dispose();
    }
}
