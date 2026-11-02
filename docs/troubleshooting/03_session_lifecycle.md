# 수명별 책임과 실제 종료 순서를 구분

[게임 소개](../../README.md) · [전체 사례](../case-studies.md) · [코드 가이드](../code-guide.md)

**S / 상황**

대기실의 참가자 이탈은 방을 유지할 수 있지만, 게임 중 이탈은 공유 조작을 중단해야 합니다. 종료 통지와 비동기 응답도 뒤늦게 도착할 수 있습니다.

**T / 과제**

자원의 소유 수명과 종료 순서를 분리하고, 이미 끝난 세션의 정리나 화면 전환이 반복되지 않는지 확인해야 했습니다.

**A / 책임 분리와 종료 시퀀스**

| 소유 수명 | 책임 |
|---|---|
| 앱 / AppScope | 프로필·공용 Store |
| 세션 / SessionScope | 방·중계·네트워크 |
| 씬 / EntryPoint | UI·입력 구독 |

종료 순서는 **방 이탈 요청 시작(응답 대기 안 함) → Runner.Shutdown → Store.Clear → SessionLifetime.End(현재 참조 해제·Scope 정리) → 종료 이유 보관 → 로비 로드**입니다.

종료 후 `Current`가 null이면 정리를 생략하고, `Scope.Dispose`는 `isDisposed`로 중복 호출을 차단합니다. 게임 시작 요청은 await 전후에 세션 수명 취소 여부와 `ReferenceEquals(요청 Scope, 현재 Scope)`를 확인해 이전 세션의 응답을 거부합니다.

[SessionLifetime](../../src/Session/SessionLifetime.cs) · [SessionScope](../../src/Session/SessionScope.cs) · [LeaveSessionUseCase](../../src/Session/LeaveSessionUseCase.cs) · [StartGameUseCase](../../src/Session/StartGameUseCase.cs)

| 이탈 주체와 화면 | 정책 |
|---|---|
| 게임 중 호스트·참가자 이탈 | 남은 플레이어도 세션 종료·로비 복귀 |
| 대기실 참가자 이탈 | 호스트는 방 유지 |
| 대기실 호스트 이탈 | 참가자는 세션 종료·로비 복귀 |

**R / 대표 검사 3개**

| 조건 | 기대 결과 | 확인 결과 |
|---|---|---|
| 방 생성 후 씬 전환 거절 | 호스트·세션 정리 후 재시도 가능 | 종료 1회, 재시도 성공 |
| 게임 시작 응답 대기 중 세션 교체 | 이전 요청의 씬 전환 차단·새 세션 유지 | 로드 0회, 새 Scope 유지 |
| 종료 대상 3분기에서 이탈 통지 2회 | 중복 정리와 중복 로비 이동 방지 | 종료·로비 로드 각 1회 |

프로필·저장 순서·세션 등 **17개 시나리오가 통과**했습니다. 외부 `CheckV2.csproj`가 제품 Domain/Application 소스를 직접 컴파일하며, 서비스·씬 로더는 대역을 사용합니다. Unity Play Mode·UGS 실통신·다중 기기 결과 일치 검증과 구분합니다.



## 코드에서 확인할 경계

| 코드 | 선택한 책임 |
|---|---|
| [SessionLifetime.End](../../src/Session/SessionLifetime.cs) | 현재 Scope 참조를 먼저 해제하고 종료 알림·자원 정리 진행 |
| [SessionScope.Dispose](../../src/Session/SessionScope.cs) | 세션 수명 취소와 어댑터 정리를 한 곳에 모음 |
| [LeaveSessionUseCase.ExecuteAsync](../../src/Session/LeaveSessionUseCase.cs) | 방 이탈 요청, 통신 종료, 상태 초기화와 로비 이동의 순서 조정 |
| [StartGameUseCase.ThrowIfSessionEnded](../../src/Session/StartGameUseCase.cs) | 응답 대기 전후 요청 Scope의 수명과 현재 Scope의 동일성 확인 |

### 이전 응답을 확인하는 코드

```csharp
cancellationToken.ThrowIfCancellationRequested();
if (!ReferenceEquals(scope, sessions.Current))
{
    throw new OperationCanceledException(
        "The room session has ended.", cancellationToken);
}
```

세션 A에서 시작한 요청이 대기하는 동안 A가 종료되고 B가 시작되면, 이전 응답을 B의 화면 전환에 사용하지 않도록 이 경계를 검사합니다. 방 구성 변경에 대한 Waiting 복원과 세션 자체의 교체 검사는 따로 다룹니다.
