# 핵심 구현 시스템과 트러블슈팅

[게임 소개](../README.md) · [코드 가이드](code-guide.md) · [아키텍처](architecture.md)

각 사례는 `docs/troubleshooting/`의 개별 문서에 상황·과제·구현 과정·결과와 관련 코드를 정리했습니다.

| 문서 | 주제 |
|---|---|
| [01. 입력 예측과 호스트 확정](troubleshooting/01_input_prediction.md) | 참가자·입력 검증, Revision·Sequence·Epoch, 확정 상태 기반 재적용 |
| [02. 지연 입력의 제한적 재계산](troubleshooting/02_limited_recalculation.md) | 판정 경계, 상태 복원, 취소 후 현재 틱 적용, 취소 분기 4개 시나리오·45개 검사 |
| [03. 세션 수명과 지연 응답](troubleshooting/03_session_lifecycle.md) | 수명별 책임, 종료 순서, 이전 응답 차단, 대표 검사 3개 |
| [04. 인증·기록 저장·계정 연동](troubleshooting/04_account_and_records.md) | 단계별 실패 전파, 개인 기록 결과 유지, 계정 연동의 설명 범위 |
