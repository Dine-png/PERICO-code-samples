# PERI.CO 코드 샘플

Unity 6 URP · C# · 멀티플레이 협동 액션 게임 **PERI.CO**(BugForge 팀, Steam 출시 준비 중)에서
제가 직접 작성한 코드 일부입니다. 프로젝트 전체가 아니라 설명용 발췌이므로 이 저장소만으로는 컴파일되지 않습니다.

- Steam 상점: https://store.steampowered.com/app/5191120/
- 작성자: 이다인 (BugForge 팀장)

## 파일

| 파일 | 내용 |
|---|---|
| [`network/NetworkTransformSync.cs`](network/NetworkTransformSync.cs) | 스냅샷 버퍼 기반 위치 동기화. 최근 좌표 하나로 Lerp 하던 방식의 "툭툭 끊김"을 실측 패킷 간격에 맞춘 보간 지연으로 해결 (보간 계산은 프로젝트 안의 `TransformSnapshotBuffer` 담당) |
| [`reward/KillCoinDropper.cs`](reward/KillCoinDropper.cs) | 처치 보상 코인. 코인마다 네트워크 객체를 만들지 않고 이벤트 3개(드랍·줍기 요청·확정)로 처리, 금액은 마스터 장부 기준이라 동시에 주워도 한 번만 지급 |
| [`skill/SkillEffectBase.cs`](skill/SkillEffectBase.cs) | Q키 스킬 효과의 기반 클래스. 상태 변경은 소유 클라이언트에서만, 연출은 모든 클라이언트에서 재생하도록 나눈 구조 |
| [`diagnostics/FrameSpikeLogger.cs`](diagnostics/FrameSpikeLogger.cs) | 프레임 끊김 진단 도구. 임계값을 넘긴 프레임마다 정황을 한 줄로 남겨 물리 캐치업 / GC / 네트워크 재전송 중 원인을 로그로 가름 |
