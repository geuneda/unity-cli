# unity-cli

Unity Editor를 HTTP 브리지로 노출하고, `scene`, `gameobject`, `asset`, `material`, `package`, `tests`, `console`, `menu`, `editor`, `resource`, `events`, `workflow`를 CLI만으로 제어할 수 있게 만드는 프로젝트입니다.

핵심 목표는 두 가지입니다.

1. Unity Editor가 붙어 있을 때는 실제 Editor API를 CLI로 호출한다.
2. Unity 없이도 동일한 프로토콜을 흉내내는 mock bridge로 CLI, workflow, 이벤트 기반 테스트를 전부 검증한다.

## 포함된 구성

- `src/UnityCli`: `.NET` 기반 CLI 본체
- `unity-connector`: Unity 패키지 형태의 Editor HTTP bridge
- `samples/workflows/smoke-test.json`: 이벤트 기반 smoke workflow
- `samples/workflows/bootstrap.json`: batch 호출 예시
- `tests/UnityCli.Tests`: mock bridge를 사용한 CLI 통합 테스트

## 지원 명령

직접 매핑되는 그룹 명령:

- `scene create|load|save|info|delete|unload`
- `gameobject create|get|delete|duplicate|reparent|move|rotate|scale|set-transform|select`
- `component update`
- `material create|assign|modify|info`
- `asset list|add-to-scene`
- `package list|add`
- `tests list|run`
- `console get|clear|send`
- `menu execute`
- `editor play|stop|pause|refresh`

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
dotnet test
```

mock bridge를 수동으로 띄우고 CLI를 때릴 수도 있습니다.

```bash
dotnet run --project src/UnityCli -- mock serve
dotnet run --project src/UnityCli -- status
dotnet run --project src/UnityCli -- scene create path=Assets/Scenes/Main.unity
dotnet run --project src/UnityCli -- gameobject create name=Player primitive=Capsule position=[0,1,0]
dotnet run --project src/UnityCli -- workflow run samples/workflows/smoke-test.json
```

벡터 인자는 셸 quoting 없이 `position=1,2,3`, `rotation=0,90,0`, `scale=2,2,2` 형태로 줄 수 있습니다.

## Unity에 붙이기

`unity-connector` 폴더를 Unity 프로젝트의 `Packages/com.geuneda.unity-cli-connector`로 복사하거나 Git dependency로 추가합니다.

패키지가 로드되면 기본적으로 `http://127.0.0.1:52737` 에서 HTTP bridge를 열고, `~/.unity-cli/instances.json`에 현재 엔드포인트를 기록합니다.

CLI는 `--base-url`이 없으면 이 `instances.json`의 `default.baseUrl`을 먼저 사용하고, 파일이 없을 때만 `http://127.0.0.1:52737`로 fallback 합니다.

현재 구현은 Editor API 중심이며, 아래 흐름을 지원합니다.

- 씬 생성/로드/저장/삭제
- GameObject 생성/조회/복제/삭제/변환/선택
- 컴포넌트 갱신
- 머티리얼 생성/할당/수정
- 에셋 목록 조회 및 프리팹 인스턴스화
- 패키지 목록 조회 및 설치 요청
- 테스트 목록/실행
- 콘솔 로그 발행/조회
- 메뉴 실행, Play/Pause/Refresh 제어

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
dotnet run --project src/UnityCli -- workflow run samples/workflows/smoke-test.json
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
