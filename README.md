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

dotnet run --project src/UnityCli -- status
dotnet run --project src/UnityCli -- scene create path=Assets/CliScene.unity
dotnet run --project src/UnityCli -- gameobject create name=Enemy primitive=Cube position=1,2,3
dotnet run --project src/UnityCli -- material create path=Assets/CliMaterial.mat shader=Standard
dotnet run --project src/UnityCli -- material assign name=Enemy materialPath=Assets/CliMaterial.mat
dotnet run --project src/UnityCli -- resource get scene/hierarchy
dotnet run --project src/UnityCli -- --timeout-ms=180000 editor compile
dotnet run --project src/UnityCli -- workflow run samples/workflows/smoke-test.json
dotnet run --project src/UnityCli -- workflow run samples/workflows/ui-touch-smoke.json
dotnet run --project src/UnityCli -- --timeout-ms=60000 tests run mode=EditMode
dotnet run --project src/UnityCli -- --timeout-ms=240000 tests run mode=PlayMode
```

## 이벤트 기반 workflow 예시

`samples/workflows/smoke-test.json`은 아래 패턴을 보여줍니다.

1. 씬 생성
2. Player 오브젝트 생성
3. 로그 발행
4. `console.log` 이벤트를 기다림
5. 테스트 실행
6. `tests.completed` 이벤트를 기다림

이 구조로 "콜백이 오면 다음 스텝 실행" 형태의 CLI 기반 검증을 만들 수 있습니다.

## 현재 검증 상태

- 실제 Editor에서 확인됨:
  - `scene.*`
  - `gameobject.*`
  - `sprite.create`
  - `material.*`
  - `asset.list`
  - `package.list`, `package.add`
  - `tests.list`, `tests.run mode=EditMode`, `tests.run mode=PlayMode`
  - `console.get`, `console.clear`, `console.send`
  - `ui.canvas.create`, `ui.button.create`, `ui.text.create`, `ui.image.create`
  - `ui.click`, `ui.drag`, `input.tap`, `input.drag`
  - `editor.play`, `editor.stop`, `editor.pause`, `editor.refresh`, `editor.compile`
  - `resource get scene/hierarchy`, `resource get ui/hierarchy`, `resource get tests/catalog`, `resource get packages/list`
  - `workflow run samples/workflows/smoke-test.json`
  - `workflow run samples/workflows/ui-touch-smoke.json`

- 아직 제한이 남아 있음:
  - `mock serve` 는 실기능 검증 경로가 아니라 CLI 회귀 테스트용입니다.
