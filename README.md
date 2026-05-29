# unity-cli

Unity Editor를 HTTP 브리지로 노출하고, `scene`, `gameobject`, `sprite`, `ui`, `input`, `asset`, `material`, `package`, `tests`, `console`, `menu`, `editor`, `resource`, `events`, `workflow`를 CLI만으로 제어할 수 있게 만드는 프로젝트입니다.

현재 기준은 "실제 Unity Editor에 붙어서 동작하는가" 입니다. `mock serve`와 `tests/UnityCli.Tests`는 CLI 프로토콜 회귀 확인용 보조 수단으로만 유지합니다.

## 포함된 구성

- `src/UnityCli`: `.NET` 기반 CLI 본체
- `unity-connector`: Unity 패키지 형태의 Editor HTTP bridge
- `samples/workflows/smoke-test.json`: 이벤트 기반 smoke workflow
- `samples/workflows/ui-touch-smoke.json`: 2D/UI/touch/drag smoke workflow
- `samples/workflows/bootstrap.json`: batch 호출 예시
- `tests/UnityCli.Tests`: mock bridge를 사용한 CLI 통합 테스트

## 지원 명령

직접 매핑되는 그룹 명령:

- `scene create|load|save|info|delete|unload`
- `gameobject create|get|delete|duplicate|reparent|move|rotate|scale|set-transform|select|find|set-properties`
- `sprite create`
- `component list|get|add|update|remove`
- `material create|assign|modify|info`
- `asset list|add-to-scene`
- `package list|add`
- `tests list|run`
- `console get|clear|send`
- `ui canvas.create|button.create|toggle.create|slider.create|scrollrect.create|inputfield.create|text.create|image.create`
- `ui toggle.set|slider.set|scrollrect.set|inputfield.set-text|focus|blur`
- `ui click|double-click|long-press|drag|swipe`
- `input tap|double-tap|long-press|drag|swipe`
- `menu execute`
- `editor play|stop|pause|refresh|compile`

브리지 레벨 명령:

- `status`
- `capabilities`
- `doctor`
- `tool list`
- `tool call <name> ...`
- `resource list`
- `resource get <name>`
- `events tail`
- `batch run <file>`
- `workflow run <file>`
- `mock serve`

`doctor`는 브리지<->CLI 계약을 자가 점검합니다. bridge.reachable / capabilities / tools.parity(`/tools` vs `/capabilities`) / events.contract(CLI가 요구하는 이벤트 광고 여부) / version을 확인하고, 각 항목을 `[PASS]/[WARN]/[FAIL] <check>: <detail>`로 출력합니다. 전부 통과하면 종료코드 0, FAIL이 하나라도 있으면 1을 반환하며 `--json`/`--quiet`를 따릅니다.

## 로컬 테스트

```bash
dotnet test UnityCli.slnx
```

mock bridge는 보조 검증용입니다. 실제 기능 확인은 아래 "실제 Editor 검증" 흐름을 기준으로 합니다.

반복적인 실기능 검증은 먼저 CLI를 빌드한 뒤, 실제 Unity Editor에 붙어서 `scripts/verify-editor.sh`를 실행하는 흐름을 기준으로 합니다.

```bash
dotnet run --project src/UnityCli -- mock serve
dotnet run --project src/UnityCli -- status
dotnet run --project src/UnityCli -- scene create path=Assets/Scenes/Main.unity
dotnet run --project src/UnityCli -- gameobject create name=Player primitive=Capsule position=[0,1,0]
dotnet run --project src/UnityCli -- workflow run samples/workflows/smoke-test.json
```

벡터 인자는 셸 quoting 없이 `position=1,2,3`, `rotation=0,90,0`, `scale=2,2,2` 형태로 줄 수 있습니다.
기본 CLI 타임아웃은 `10000ms` 입니다. 테스트 실행이나 패키지 작업처럼 오래 걸릴 수 있는 명령은 `--timeout-ms=60000` 이상을 권장합니다.

글로벌 옵션:

- `--base-url=<url>`: 브리지 URL (없으면 instances.json 또는 `http://127.0.0.1:52737`)
- `--json`: JSON 출력 모드
- `--timeout-ms=<ms>`: 요청 타임아웃 (기본 `10000`)
- `--field=<jsonpath>`: 전체 JSON 대신 점/인덱스 경로로 해석한 스칼라만 출력 (`result.id`, `data.logs[0].data.level`, `data.renderPipeline`). 경로 미해석 시 종료코드 2. 예: `id=$(unity-cli gameobject create name=Hero --field=result.id)`
- `--quiet`: 성공 JSON 출력 억제 (종료코드로만 성공/실패 판단)
- `--strict`: 알 수 없는 도구(`unknown_tool`) 실패를 사용성 오류로 간주하여 종료코드 2로 격상 (기본은 1)

### 종료 코드와 오류 봉투

도구/리소스 실패 봉투는 `{ success, message, code, result, events }` 형태이며, 실패 시 `code`에 안정적 오류 코드(`not_found`, `missing_arg`, `bad_arg`, `unknown_tool`, `internal_error`)가 채워집니다(성공 시 `code`는 `null`). HTTP 상태는 의미를 가집니다: `not_found`->404, `missing_arg`/`bad_arg`->400, `internal_error`->500, `unknown_tool`은 HTTP 200(`success=false`)으로 유지됩니다.

| 종료 코드 | 의미 |
| --- | --- |
| 0 | 성공 |
| 1 | 도구 실패 또는 assert 실패 (도메인 실패, `not_found` 등) |
| 2 | 인자/경로 오류 (`missing_arg`/`bad_arg`, `--field` 미해석, 명령 그룹/액션 누락, `--strict`의 `unknown_tool`) |
| 3 | 브리지 도달 불가 (전송 오류/타임아웃) |

## Unity에 붙이기

`unity-connector` 폴더를 Unity 프로젝트의 `Packages/com.geuneda.unity-cli-connector`로 복사하거나 Git dependency로 추가합니다.

반복적인 로컬 개발/검증에는 `Packages/com.geuneda.unity-cli-connector -> /path/to/unity-connector` 형태의 embedded package 연결이 가장 안정적입니다.
이 방식으로 패키지 소스를 수정했다면 `editor refresh` 후 `editor compile`을 호출하는 흐름을 권장합니다.
`editor play`와 `editor stop`은 CLI에서 실제 상태가 전환될 때까지 기다린 뒤 반환합니다.

패키지가 로드되면 기본적으로 `http://127.0.0.1:52737` 에서 HTTP bridge를 열고, `~/.unity-cli/instances.json`에 현재 엔드포인트를 기록합니다.

CLI는 `--base-url`이 없으면 이 `instances.json`의 `default.baseUrl`을 먼저 사용하고, 파일이 없을 때만 `http://127.0.0.1:52737`로 fallback 합니다.

현재 구현은 Editor API 중심이며, 아래 흐름을 지원합니다.

- 씬 생성/로드/저장/삭제
- GameObject 생성/조회/복제/삭제/변환/선택/검색/속성 변경
- 2D sprite 생성
- Canvas/Button/Text/Image 생성
- Toggle/Slider/ScrollRect/InputField 생성 및 상태 변경
- 생성되는 UI 텍스트는 `TextMeshProUGUI`, 입력 필드는 `TMP_InputField` 기준
- EventSystem selection 기반 UI focus/blur 전환
- TMP Essential Resources가 없으면 첫 TMP UI 생성 시 자동 import
- 클릭/더블클릭/롱프레스/탭/드래그/스와이프 입력 디스패치
- `pointerId`를 포함한 멀티포인터 입력 시뮬레이션
- 컴포넌트 목록/조회/추가/갱신/제거 (`[SerializeField] private` 직렬화 프로퍼티 포함)
- 머티리얼 생성/할당/수정
- 에셋 목록 조회 및 프리팹 인스턴스화
- 패키지 목록 조회 및 설치 요청
- 테스트 목록 조회 및 EditMode/PlayMode 실행
- 콘솔 로그 발행/조회/초기화
- 메뉴 실행, Play/Pause/Refresh/Compile 제어
- 프로젝트/빌드 설정 조회 (`resource get project/info`)
- 브리지<->CLI 계약 자가 점검 (`doctor`)

## 실제 Editor 검증 예시

```bash
"/Applications/Unity/Hub/Editor/6000.3.11f1/Unity.app/Contents/MacOS/Unity" \
  -projectPath "/Users/geuneda/Documents/GitHub/unity-cli/manual-test-project" \
  -logFile "/Users/geuneda/Documents/GitHub/unity-cli/manual-test-project/Logs/unity-editor.log"

dotnet build src/UnityCli/UnityCli.csproj
scripts/verify-editor.sh
scripts/verify-editor.sh --stage ui-input --report reports/verify-editor/ui-input.json
```

`scripts/verify-editor.sh`는 실제 Editor에 대해 아래 항목을 직렬 검증합니다.

- `status`, `tool list`, `resource list`
- `scene create|info|save|delete|unload`
- `gameobject create|get|duplicate|reparent|move|rotate|scale|set-transform|select|delete`
- `sprite create`
- `component update`
- `material create|info|modify|assign`
- `asset list|add-to-scene`
- `package list|add`
- `console send|get|clear`
- `ui canvas.create|button.create|toggle.create|slider.create|scrollrect.create|inputfield.create|text.create|image.create`
- `ui toggle.set|slider.set|scrollrect.set|inputfield.set-text|focus|blur`
- `ui click|double-click|long-press|drag|swipe`
- `input tap|double-tap|long-press|drag|swipe`
- `menu execute`
- `resource get editor/state|scene/active|scene/hierarchy|ui/hierarchy|console/logs|tests/catalog|packages/list`
- `events tail`
- `tests list|run`
- `workflow run samples/workflows/smoke-test.json`
- `workflow run samples/workflows/ui-touch-smoke.json`
- `batch run samples/workflows/bootstrap.json`
- `editor refresh|compile|play|pause|stop`

스크립트 안에는 play mode 중 `scene create`가 실패해야 한다는 검증도 포함되어 있어서, 브리지 에러 메시지가 CLI에서 그대로 노출되는지까지 확인합니다.
기본 실행은 `core`, `ui-input`, `tests`, `editor-lifecycle`, `resilience` 다섯 stage를 모두 돌리고, `--stage`를 여러 번 주면 필요한 stage만 선택 실행할 수 있습니다.
실행 결과는 기본적으로 `reports/verify-editor/latest.json`에 JSON 리포트로 남습니다.
`resilience` stage는 compile/play/stop 전환 도중에도 `status`, `resource get`, `events tail`이 실제 Editor에서 계속 버티는지 확인합니다.

## 이벤트 기반 workflow 예시

`samples/workflows/smoke-test.json`은 아래 패턴을 보여줍니다.

1. 씬 생성
2. Player 오브젝트 생성
3. 로그 발행
4. `console.log` 이벤트를 기다림
5. 테스트 실행
6. `tests.completed` 이벤트를 기다림

이 구조로 "콜백이 오면 다음 스텝 실행" 형태의 CLI 기반 검증을 만들 수 있습니다.
현재 `workflow run`은 실행 시작 시점의 최신 이벤트 커서를 스냅샷하고, 각 tool 응답 안에 포함된 이벤트도 다음 `waitFor`에서 재사용합니다.
따라서 같은 workflow를 반복 실행해도 과거 실행의 `console.log`/`tests.completed`를 다시 잡지 않습니다.

`samples/workflows/ui-touch-smoke.json`은 매 실행마다 전용 씬을 새로 만들어서, 동일한 이름의 UI/GameObject가 누적돼도 안정적으로 반복 검증할 수 있게 해둔 상태입니다.

## 현재 검증 상태

- 실제 Editor에서 확인됨 (Unity `6000.3.11f1`, SpellDefense URP 프로젝트):
  - `scene.*`
  - `gameobject.*` (`gameobject.find`, `gameobject.set-properties` 포함)
  - `component.list`, `component.get`, `component.add`, `component.update`, `component.remove`
  - `sprite.create`
  - `material.*`
  - `asset.list`, `asset.add-to-scene`
  - `package.list`, `package.add`
  - `tests.list`, `tests.run mode=EditMode`, `tests.run mode=PlayMode`
  - `console.get`, `console.clear`, `console.send`
  - `ui.canvas.create`, `ui.button.create`, `ui.toggle.create`, `ui.slider.create`, `ui.scrollrect.create`, `ui.inputfield.create`, `ui.text.create`, `ui.image.create`
  - `ui.toggle.set`, `ui.slider.set`, `ui.scrollrect.set`, `ui.inputfield.set-text`, `ui.focus`, `ui.blur`
  - `ui.click`, `ui.double-click`, `ui.long-press`, `ui.drag`, `ui.swipe`
  - `input.tap`, `input.double-tap`, `input.long-press`, `input.drag`, `input.swipe`
  - `menu.execute`
  - `editor.refresh`, `editor.compile`, `editor.play`, `editor.pause`, `editor.stop`
  - `resource list`, `resource get editor/state`, `resource get scene/hierarchy`, `resource get ui/hierarchy`, `resource get tests/catalog`, `resource get packages/list`, `resource get project/info`
  - `doctor` (bridge<->CLI 계약 자가 점검)
  - `--field`, `--quiet` 글로벌 옵션
  - `ui.screenshot.capture` (`source=game|scene`, URP에서도 정상 캡처)
  - `events tail`
  - `batch run samples/workflows/bootstrap.json`
  - `workflow run samples/workflows/smoke-test.json`
  - `workflow run samples/workflows/ui-touch-smoke.json`
  - `scripts/verify-editor.sh` end-to-end pass on Unity `6000.3.11f1`
  - `status.sessionId`는 health 응답에 값이 비어 있어도 가장 최근 `bridge.started` 이벤트에서 복구

- BACKLOG Tier 2+3 추가분 (2026-05-29, 라이브 Unity `6000.3.11f1`/SpellDefense 에서 재컴파일 0 에러·0 경고 + `capabilities` 노출 확인):
  - 런타임 실행 확인(read-only): `scene.list-loaded`(buildIndex/isActive), `scriptableobject.get`, `scriptableobject.list`(실 1336개), `resource get addressables/list`(실 그룹), `console.logs`, CLI `assert`/`logs wait`/`instances list`
  - 컴파일·광고 확인(쓰기형이라 실 프로젝트 미실행, `dotnet test` mock 통합테스트로 검증): `prefab.create`/`prefab.instantiate`/`prefab.apply`/`prefab.unpack`, `asset.manage`, `asset.create-scriptableobject`, `sprite.set`, `scene.open-additive`/`scene.set-active`, `scene.unload path=`
  - 신규 리소스: `resource get tests/last-run`, `resource get addressables/list`
  - 계약: 통일 오류 봉투(`code`) + 종료코드 0/1/2/3 + `--strict`, `tests.run`에 `category=`/`regex=` 필터, 워크플로우 `assert`/`capture`/`waitFor`(resource)/`retry`/조건부 skip

- `ui-input` stage에서 현재 실제 검증하는 UI 상태:
  - Button `double-click`, `long-press`, `swipe`
  - Button `pointerId` click
  - Sprite `double-tap`, `long-press`, `swipe`
  - Sprite `pointerId` drag/swipe
  - Toggle 실제 `ui.click` 토글
  - Slider 실제 `ui.drag` 값 변경
  - Toggle `isOn` 변경
  - Slider `value` 변경
  - ScrollRect `normalizedPosition` 변경과 실제 drag 반응
  - TMP_InputField 텍스트 입력과 EventSystem selection 기반 focus/blur 전환

- 입력 시뮬레이션 참고:
  - `ui.click name=CliButton pointerId=21`
  - `input.swipe worldFrom=2,1,0 worldTo=2.75,1,0 pointerId=9`

- 최근 개선 사항:
  - `tool list`가 도구별 실제 설명과 `(required: ...)` 인자를 표시합니다(이전 placeholder 제거). `/tools` 엔드포인트는 `name/category/description/requiredArguments/optionalArguments/arguments`(인자별 type+description 포함)를 반환합니다.
  - 도구/리소스/이벤트 목록이 단일 출처(ToolCatalog/ResourceCatalog/EventTypes)에서 파생되어 더 이상 어긋나지 않습니다.
  - `capabilities`의 이벤트 목록이 완전해졌습니다(`bridge.started`, `scene.loaded`, `scene.saved`, `transform.changed` 광고).
  - 브리지가 최대 5000개의 이벤트 링 버퍼를 유지하고, `events` 폴 응답에 `floor`(최저 보존 커서) 필드가 포함되어 잘린 구간을 감지할 수 있습니다.
  - GameObject 직렬화가 강화되어 결과에 `tag`, `layer`, `layerName`, `isStatic`, `activeInHierarchy`, `childCount`가 포함됩니다. SpriteRenderer 오브젝트는 `spritePath`, `sortingLayerName`, `sortingOrder`, `flipX`, `flipY`를, UI 오브젝트는 `image`/`button`/`canvasGroup` 하위 오브젝트(팝업 가시성 검증용 `CanvasGroup.alpha`, `button.interactable`, `image.fillAmount`)를 추가로 노출합니다.

- 현재 EditMode 제한:
  - 입력 필드의 `isFocused`는 Unity EditMode에서 `ActivateInputField()` 이후에도 기대와 다르게 남을 수 있습니다. 현재 CLI 검증은 `eventSystemSelectedObjectName`, `isSelected`, 그리고 probe 로그를 기준으로 포커스 전환을 확인합니다.

- 실제 Editor 검증에 사용한 현재 manual test project 상태:
  - embedded package: `Packages/com.geuneda.unity-cli-connector -> unity-connector`
  - registry package: `com.unity.inputsystem@1.19.0`
  - sample prefab: `Assets/Prefabs/CliPrefab.prefab`

- 아직 제한이 남아 있음:
  - `mock serve` 는 실기능 검증 경로가 아니라 CLI 회귀 테스트용입니다.

## Claude Code 스킬

`skill/` 디렉토리에 Claude Code용 스킬이 포함되어 있습니다. 이 스킬을 설치하면 Claude Code에서 unity-cli의 모든 명령을 자연어로 요청할 수 있습니다.

### 설치

```bash
# 스킬 디렉토리를 Claude Code 스킬 경로에 복사
cp -r skill ~/.claude/skills/unity-cli
```

또는 `.claude/settings.json`에 직접 추가:

```json
{
  "skills": [
    "/path/to/unity-cli/skill"
  ]
}
```

### 포함 내용

- `skill/SKILL.md` -- 스킬 메타데이터 및 사용 가이드
- `skill/references/commands.md` -- 전체 명령 레퍼런스 (그룹별 인자, 예시 포함)
