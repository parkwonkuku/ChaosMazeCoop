# 공개 코드 가이드

[게임 소개](../README.md) · [문제 해결 과정](case-studies.md) · [아키텍처](architecture.md)

## 선정 기준

기능 수보다 **입력과 응답의 순서를 어떻게 다루는지, 책임을 어디에서 나누는지, 실패 시 어떤 상태를 남기는지**가 보이는 파일을 골랐습니다. 제품 코드 10개 파일은 개발 프로젝트에서 발췌했으며, 열람에 집중할 수 있도록 `using` 구문과 `namespace` 선언·감싸는 중괄호를 생략하고 들여쓰기를 정리했습니다. 긴 시간축 설명은 상세 문서로 옮기고, 코드에는 역할·불변 조건·복원 책임 중심의 주석만 남겼습니다. 실행 로직은 원본과 같습니다.

## 1. 공유 이동: 공통 규칙 → 예측 → 호스트 확정

| 파일 | 먼저 볼 메서드 | 읽을 때의 질문 |
|---|---|---|
| [SharedMovementRules](../src/Movement/SharedMovementRules.cs) | `Advance` | 이동 계산과 Unity 물리 조회를 어떻게 분리했는가? |
| [ClientMovePredictor](../src/Movement/ClientMovePredictor.cs) | `TryCreateCommand` → `ApplyConfirmed` → `TryPredict` | 확정 상태가 도착한 뒤 어떤 입력을 지우고 무엇을 재적용하는가? |
| [HostMoveAuthority](../src/Movement/HostMoveAuthority.cs) | `Receive` → `Step` → `ReplayFrames` → `TryCreateSnapshot` | 지연 입력의 적용 시점은 어디까지 허용하며 취소 시 무엇을 복원하는가? |

호출자는 `SharedCharacterController`입니다. RPC 수신 메타데이터의 송신자 ID를 `Receive`에 전달하고, 이동 틱마다 `Step`을 호출합니다. `Step`이 반환한 접촉 결과는 호출자가 별·위험 지형·골 이벤트로 전달합니다. 컨트롤러, RPC 연결과 화면 보정 구현은 공개 범위에서 생략했습니다.

### 함께 읽어야 하는 자료형의 의미

[MoveCommand](../src/Movement/Contracts/MoveCommand.cs)와 [ConfirmedMoveState](../src/Movement/Contracts/ConfirmedMoveState.cs)는 실제 정의를 함께 공개했습니다. 아래의 나머지 자료형·인터페이스는 계약을 요약합니다.

| 자료형·인터페이스 | 계약과 역할 |
|---|---|
| `MoveCommand` | `Epoch`, `Sequence`, `Key`, `ApplyTick`. 클라이언트의 위치나 최종 판정은 담지 않음 |
| `SharedMoveState` | 위치와 방향을 담는 이동 상태. 방향 변경·이동 결과는 새 상태로 생성 |
| `ConfirmedMoveState` | `Epoch`, `ServerTick`, `Revision`, 이동 상태·진행 상태, 참가자별 마지막 처리 번호 |
| `SequenceMath` | unsigned 순서 번호의 증가·최신 여부 비교. 단순 정수 크기 비교와 구분 |
| `AssignedKeysLookup` | 송신자가 참가자인지 조회하고 담당 키 목록을 반환 |
| `ISharedCharacterCollision` | 이동을 막는 거리 조회와 접촉 목록 조회. Unity 구현은 Physics2D BoxCast / OverlapBox를 사용 |
| `HostStepOutcome` | 현재 틱의 위험·골 진입 여부와 새로 진입한 별 ID 목록. 다음 Step 전 호출자가 즉시 소비 |
| `PendingMoveCommand` | 아직 호스트 처리 완료 번호를 받지 못한 자기 명령과 로컬 적용 틱 |

`Revision`은 확정 상태 순서, `Sequence`는 참가자별 입력 순서, `Epoch`는 상태 초기화로 구분하는 실행 구간(플레이 회차)입니다. 서로 다른 문제를 구분하는 번호이며 하나로 합치지 않았습니다.

## 2. 세션: 자원 소유 → 종료 → 이전 응답 차단

| 파일 | 먼저 볼 메서드 | 읽을 때의 질문 |
|---|---|---|
| [SessionLifetime](../src/Session/SessionLifetime.cs) | `Begin`, `End` | 현재 세션 참조는 언제 해제하는가? |
| [SessionScope](../src/Session/SessionScope.cs) | `Dispose` | 세션 수명 취소와 어댑터 해제를 어디서 묶는가? |
| [LeaveSessionUseCase](../src/Session/LeaveSessionUseCase.cs) | `ExecuteAsync` | 방 이탈, 통신 종료, 상태 초기화와 씬 전환의 순서는 무엇인가? |
| [StartGameUseCase](../src/Session/StartGameUseCase.cs) | `ExecuteAsync`, `ThrowIfSessionEnded` | 응답 대기 중 방 구성 또는 세션이 바뀌면 어떻게 처리하는가? |

`IRelayGateway`, `ILobbyDirectory`, `INetworkRunner`, 씬 로더와 Store의 구현은 생략했습니다. 네트워크·UGS·씬 로드를 인터페이스로 전달받는 호출 경계에 집중할 수 있도록 구성했습니다.

## 3. 기록: 저장 순서와 실패 전파

[SubmitRunRecordUseCase](../src/Records/SubmitRunRecordUseCase.cs)의 `ExecuteAsync`를 순서대로 읽으면 됩니다.

1. 의미 있는 기록인지 확인합니다.
2. 초기화 → 프로필 저장 → 개인 기록 저장을 차례로 기다립니다.
3. 앞 단계에서 실패 `Result`를 받으면 그 결과를 반환합니다.
4. 리더보드 제출의 `Result`와 개인 기록 저장 결과를 구분합니다.

저장소 어댑터·프로필 Store와 공통 `Result` 자료형은 생략했습니다. 이 코드는 실패 `Result`의 처리 순서를 보여줍니다. 예외나 취소까지 모두 성공 결과로 바꾸는 구조는 아닙니다.

## 이력서·포트폴리오의 구현 범위와 공개 코드

인증·저장·랭킹 제출의 단계별 실패 처리와 Google Play Games 계정 연동·해제를 구현했습니다. 공개 파일 중 `SubmitRunRecordUseCase`는 인증·저장·랭킹의 호출 순서를 보여주며, Google Play Games 계정 연동 어댑터는 공개 범위에서 생략했습니다.

## 공개 범위

실행 로직을 읽기 위한 발췌본입니다. Unity 씬·프리팹·아트, 전체 게임 흐름, SDK 어댑터, 서비스 설정과 인증 정보는 포함하지 않습니다. 독립 실행용 프로젝트나 전체 소스의 재구성을 목적으로 하지 않습니다.


## 취소 분기의 검사 코드

[ReplayCancellationCase.cs](../tests/ReplayCancellationCase.cs)는 제품 HostMoveAuthority와 충돌 조회 대역으로 재계산 취소를 검사합니다. [조건·예상 상태·실제 출력](troubleshooting/02_limited_recalculation.md)에 4개 시나리오·45개 검사 결과를 기록했습니다.
