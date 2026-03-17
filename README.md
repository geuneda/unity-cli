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
- `gameobject create|get|delete|duplicate|reparent|move|rotate|scale|set-transform|select`
- `sprite create`
- `component update`
- `material create|assign|modify|info`
- `asset list|add-to-scene`
- `package list|add`
- `tests list|run`
- `console get|clear|send`
- `ui canvas.create|button.create|text.create|image.create|click|drag`
- `input tap|drag`
- `menu execute`
- `editor play|stop|pause|refresh|compile`

브리지 레벨 명령:

- `status`
- `capabilities`
- `tool list`
- `tool call <name> ...`
- `resource list`
- `resource get <name>`
- `events tail`
- `batch run <file>`
- `workflow run <file>`
- `mock serve`

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

## Unity에 붙이기

`unity-connector` 폴더를 Unity 프로젝트의 `Packages/com.geuneda.unity-cli-connector`로 복사하거나 Git dependency로 추가합니다.

반복적인 로컬 개발/검증에는 `Packages/com.geuneda.unity-cli-connector -> /path/to/unity-connector` 형태의 embedded package 연결이 가장 안정적입니다.
이 방식으로 패키지 소스를 수정했다면 `editor refresh` 후 `editor compile`을 호출하는 흐름을 권장합니다.
`editor play`와 `editor stop`은 CLI에서 실제 상태가 전환될 때까지 기다린 뒤 반환합니다.

패키지가 로드되면 기본적으로 `http://127.0.0.1:52737` 에서 HTTP bridge를 열고, `~/.unity-cli/instances.json`에 현재 엔드포인트를 기록합니다.

CLI는 `--base-url`이 없으면 이 `instances.json`의 `default.baseUrl`을 먼저 사용하고, 파일이 없을 때만 `http://127.0.0.1:52737`로 fallback 합니다.

현재 구현은 Editor API 중심이며, 아래 흐름을 지원합니다.

- 씬 생성/로드/저장/삭제
- GameObject 생성/조회/복제/삭제/변환/선택
- 2D sprite 생성
- Canvas/Button/Text/Image 생성
- 클릭/탭/드래그 입력 디스패치
- 컴포넌트 갱신
- 머티리얼 생성/할당/수정
- 에셋 목록 조회 및 프리팹 인스턴스화
- 패키지 목록 조회 및 설치 요청
- 테스트 목록 조회 및 EditMode/PlayMode 실행
- 콘솔 로그 발행/조회/초기화
- 메뉴 실행, Play/Pause/Refresh/Compile 제어

## 실제 Editor 검증 예시

```bash
"/Applications/Unity/Hub/Editor/6000.3.11f1/Unity.app/Contents/MacOS/Unity" \
  -projectPath "/Users/geuneda/Documents/GitHub/unity-cli/manual-test-project" \
  -logFile "/Users/geuneda/Documents/GitHub/unity-cli/manual-test-project/Logs/unity-editor.log"

dotnet build src/UnityCli/UnityCli.csproj
scripts/verify-editor.sh
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
- `ui canvas.create|button.create|text.create|image.create|click|drag`
- `input tap|drag`
- `menu execute`
- `resource get editor/state|scene/active|scene/hierarchy|ui/hierarchy|console/logs|tests/catalog|packages/list`
- `events tail`
- `tests list|run`
- `workflow run samples/workflows/smoke-test.json`
- `workflow run samples/workflows/ui-touch-smoke.json`
- `batch run samples/workflows/bootstrap.json`
- `editor refresh|compile|play|pause|stop`

스크립트 안에는 play mode 중 `scene create`가 실패해야 한다는 검증도 포함되어 있어서, 브리지 에러 메시지가 CLI에서 그대로 노출되는지까지 확인합니다.

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

- 실제 Editor에서 확인됨:
  - `scene.*`
  - `gameobject.*`
  - `sprite.create`
  - `material.*`
  - `asset.list`, `asset.add-to-scene`
  - `package.list`, `package.add`
  - `tests.list`, `tests.run mode=EditMode`, `tests.run mode=PlayMode`
  - `console.get`, `console.clear`, `console.send`
  - `ui.canvas.create`, `ui.button.create`, `ui.text.create`, `ui.image.create`
  - `ui.click`, `ui.drag`, `input.tap`, `input.drag`
  - `menu.execute`
  - `editor.refresh`, `editor.compile`, `editor.play`, `editor.pause`, `editor.stop`
  - `resource list`, `resource get editor/state`, `resource get scene/hierarchy`, `resource get ui/hierarchy`, `resource get tests/catalog`, `resource get packages/list`
  - `events tail`
  - `batch run samples/workflows/bootstrap.json`
  - `workflow run samples/workflows/smoke-test.json`
  - `workflow run samples/workflows/ui-touch-smoke.json`
  - `scripts/verify-editor.sh` end-to-end pass on Unity `6000.3.11f1`

- 실제 Editor 검증에 사용한 현재 manual test project 상태:
  - embedded package: `Packages/com.geuneda.unity-cli-connector -> unity-connector`
  - registry package: `com.unity.inputsystem@1.19.0`
  - sample prefab: `Assets/Prefabs/CliPrefab.prefab`

- 아직 제한이 남아 있음:
  - `mock serve` 는 실기능 검증 경로가 아니라 CLI 회귀 테스트용입니다.
