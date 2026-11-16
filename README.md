# 하베스트 (Harvest)

한국 서버 Dalamud API 15용 재배 작업자. TCToolbox의 재배 실행기와 필요한 공통 코드만 남겼다. 재배 외 도구상자 모듈과 기존 무소유 재배 IPC는 제공하지 않는다.

## 사용

1. WorkflowOrchestrator, GatherBuddyReborn, Lifestream, vnavmesh와 함께 로드한다.
2. 워크플로우의 **화단** 탭 또는 `/harvest`에서 작물·꽃·씨앗을 보고 선택한다. 목표 보유량은 품목마다 지정하며 여러 품목을 선택할 수 있다. 등록 화단 탭에서 사용할 칸을 선택하고 현재 상태와 경로를 확인한다.
3. 워크플로우 제작 재료 목록에서 재배할 품목을 선택하면 전체 확장팩의 미완료 기본 제작수첩 재료를 기준으로 계산한다.
4. 화단의 각 칸 또는 실내 화분을 선택하고 상호작용 가능한 위치에서 등록한다. 같은 화단은 같은 시설 이름과 시계 방향 칸 번호 0~7을 사용한다. 해당 시설까지 이동하는 Lifestream 경로도 지정한다.
5. 워크플로우의 **재배** 작업을 켜고 원하는 순위에 둔다. 신규 설치는 꺼짐이다. 목표가 없거나 목표 보유량을 충족하면 방문하지 않는다.

인벤토리·집사 재고를 먼저 사용하고, 부족한 기본 씨앗·흙·색상 비료는 GBR의 기존 보충 경로로 확보한다. 교배 씨앗은 선행 씨앗부터 생산한다. 교배는 인접한 두 등록 칸을 사용하며 한쪽 부모를 유지한다. 부모 씨앗이 소진되면 기존 작물을 수확한 뒤 선행 단계부터 다시 준비한다. 꽃 색상은 필요한 색상 비료를 순서대로 사용한다. 확률이 개입하는 교배·꽃 색상은 결과 재고를 확인하고 목표까지 반복한다. 횟수나 완료 날짜를 보장하지 않는다.

관리 간격은 12시간이다. 성장 중에는 워크플로우를 점유하지 않는다. 열린 조건부 낚시·채집 기회가 있으면 현재 한 칸의 조작을 마친 뒤 제어권을 넘긴다. 등록 위치와 현재 주택 식별이 일치하지 않거나, 재고·메뉴·작물 상태가 불명확하면 새 조작을 보내지 않는다. 기존에 심어진 외부 작물을 임의로 수확·폐기하지 않는다.

캐릭터별 목표·시설·단계·미확정 조작을 저장한다. 재로드 후에는 실제 칸과 재고를 재확인한다. 미확정 수확·비료 사용은 자동 재전송하지 않는다. 기존 `TCToolbox.json`이 있으면 백업 후 가져오되 기존 모듈 스위치로 자동 실행하지 않는다.

## 빌드와 검사

```powershell
$env:DALAMUD_HOME = 'C:\dev\dalamud\sources\Dalamud\bin\Debug'
dotnet build Harvest/Harvest.csproj -c Release
dotnet run --project checks/GardeningKR/GardeningKR.Checks.csproj -c Release
```

검사는 실제 한국 클라이언트 데이터 경로를 사용한다. 2026-11-16 기준 재배 결과 300종, 씨앗 108종, 교배 3,768행과 33개 호환성·계획 검사를 확인했다. 이는 게임 내 주택 이동·심기·관리·수확·장시간 교배를 검증한 결과가 아니다. 실제 주택 등록 후 현장 검증이 필요하다.

## 데이터와 라이선스

- 원본 재배 실행기: [ffxiv-tc-port/TCToolbox](https://github.com/ffxiv-tc-port/TCToolbox), `d6132a49a67d6921e6e34a28b0523a3dfc06759c`, MIT.
- 씨앗·성장 시간·꽃 변형: [FFXIV Teamcraft](https://github.com/ffxiv-teamcraft/ffxiv-teamcraft), `8c0af4563b714fdbf35fc5c293093613d60238ea`, MIT. 동봉한 `LICENSE-Teamcraft` 참조.
- 교배 표: 설치된 `LuminaSupplemental.Excel` 4.3.0의 `GardeningCrossbreed.csv`. 게임 공식 확률표가 아닌 보조 데이터이며 현재 한국 Item/GardeningSeed 행으로 검증한다.
- `Core/AutomationEndpoint.cs`는 작업공간 AutomationContracts 실행 계약의 스냅샷이다. 런타임에 다른 플러그인 DLL을 직접 참조하지 않는다.
- 원본 `LICENSE`와 `THIRD-PARTY-NOTICES.md`를 유지한다.
