# TC Gardening KR

한국 서버용 Dalamud API 15 재배 빌드다. 기존 TCToolbox의 AutoGardensWork와 필요한 공통 코드를 링크한다. 다른 도구상자 모듈은 빌드·등록하지 않는다. 원본 MIT 라이선스와 THIRD-PARTY-NOTICES.md를 함께 배포한다.

## 변경 범위

- API 15의 AtkValueType, uint 지역 변경 이벤트, HousingEventObject 종류에 맞춘다.
- 한섭 게임 데이터에서 재배 메뉴와 작물 상태 문구를 읽는다. 필수 행이 없으면 활성화를 거부한다. 대만어 기본값으로 동작을 계속하지 않는다.
- 기존 심기·관리·비료·수확·상태 조회·재파종·IPC 실행을 재사용한다.
- 신규 설정은 모듈과 자동 반복 모두 꺼짐이다. 기존 재배 설정과 이 빌드에 포함하지 않은 모듈의 JSON 설정은 보존한다.
- 원본의 UI 중복 클릭 방지, YesAlready/TextAdvance 조정, 이동 정리 코드를 포함한다.

워크플로우의 다중 목표·수량·다단계 교배·예약 방문 연결은 이 호환성 설치에 포함되지 않는다. 기존 재배 실행기를 준비하는 단계다. 설정 화면의 기존 상세 항목과 메시지는 대부분 번체 중국어를 유지한다.

## 빌드와 검사

현재 한섭 Dalamud DLL이 있는 경로를 `DALAMUD_HOME`에 지정한다.

```powershell
$env:DALAMUD_HOME = 'C:\dev\dalamud\sources\Dalamud\bin\Debug'
dotnet build GardeningKR/GardeningKR.csproj -c Release
dotnet run --project checks/GardeningKR/GardeningKR.Checks.csproj -c Release
```

전체 도구상자의 `TCToolbox/TCToolbox.csproj`가 아니라 `GardeningKR/GardeningKR.csproj`를 사용한다. 출력의 TCToolbox.dll·TCToolbox.json·TCToolbox.deps.json과 라이선스 고지를 함께 배치한다. Dalamud가 제공하는 DLL을 플러그인 폴더에 복사하지 않는다.

검사는 실제 빌드의 재배 판단 함수와 설정 직렬화를 호출한다. 실제 메뉴 로더 소스도 링크해 한국 문구·상태 앞 공백 보존과 누락/빈 행 거부를 검사한다. 미확인 칸과 다른 성숙 작물을 건드리지 않는 기존 판단, 관리와 목표 작물 재파종, 설정 왕복 보존을 포함해 13개 조건을 확인한다. 네이티브 게임 조작을 실행하는 검사가 아니다.

## 설치와 사용

버전은 `7.25.0.45`, InternalName은 기존 IPC·설정 식별자와 같은 `TCToolbox`, 표시 이름은 `TC Gardening KR`다. 원본 TCToolbox와 동시에 로드하지 않는다.

1. 기존 플러그인이 로드돼 있다면 해당 플러그인만 비활성화하고 파일·설정을 백업한다.
2. 빌드 파일을 개발 플러그인 폴더에 복사한다.
3. `/xlsettings`에서 개발 플러그인 DLL 경로를 등록하고 `/xlplugins`에서 TC Gardening KR을 로드한다. 새 DLL의 현재 세션 등록은 파일 복사만으로 완료되지 않는다.
4. `/tctoolbox`로 설정을 연다. 재배 모듈 사용을 켜고 씨앗·흙·대상 시설을 준비한 뒤 기존 조작을 사용한다. 자동 반복은 별도로 선택하지 않는 한 꺼져 있다.

`/tcgarden ui`는 설정, `/tcgarden status`는 상태, `/tcgarden stop`은 기존 재배 큐 중지다. 인수 없는 `/tcgarden`은 현장 자동 관리 한 회를 시작하므로 단순 상태 조회에 사용하지 않는다. 기존 Stop은 워크플로우 소유 토큰을 지원하는 새 안전 종료 계약이 아니다.

## 설치 기록과 검증 한계

2026년 11월 16일 한섭 환경의 API 15 DLL로 빌드하고 호환성 검사 13개를 통과했다. 한국 sqpack에서 재배 메뉴·상태 0~10행도 별도로 확인했다. 기존 MemoryHelper/AtkValueType 이름에 대한 사용 중단 경고는 남아 있다.

설치 대상은 `C:/Users/kooli/AppData/Roaming/XIVLauncher/devPlugins/TCToolbox`다. 배포 전 설정 백업은 `C:/dev/dalamud/runtime/plugin-backups/TCToolbox-20261116-155057`에 보관했다. 세 빌드 파일을 복사하고 원본 대비 SHA-256 일치를 확인했다. 현재 세션의 새 개발 DLL 등록·로드와 실제 시설 조작은 별도 확인 대상이다.

실제 게임의 개별 칸 식별·심기·비료·수확, 다른 자동화와의 동시 실행, 교배와 다일 관찰은 아직 검증하지 않았다. 빌드 통과나 정적 데이터 판독을 게임 내 재배 검증으로 간주하지 않는다.

## 복구

TC Gardening KR만 비활성화하고 해당 개발 경로를 제거하면 신규 설치를 해제할 수 있다. 기존 TCToolbox를 교체한 환경에서는 백업의 파일과 설정을 복원한다. 게임 전체 재시작은 일반 복구 절차로 요구하지 않는다. 이 빌드는 다른 도구상자 모듈을 등록하지 않으며 보존된 기존 설정을 지우지 않는다.
