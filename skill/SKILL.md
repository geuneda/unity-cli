---
name: unity-cli
description: Unity Editor를 CLI로 제어하는 unity-cli 도구 사용 스킬. 씬/게임오브젝트/UI/입력/머티리얼/에셋/패키지/테스트/콘솔/에디터를 CLI 명령으로 조작할 때 사용한다. 'unity-cli', 'Unity CLI', 'CLI로 유니티', '씬 생성 CLI', 'gameobject CLI', 'UI CLI 테스트' 등의 키워드가 포함된 요청 시 트리거된다.
---

# unity-cli

Unity Editor를 HTTP 브리지로 노출하고 CLI만으로 제어하는 .NET 도구.

## 사전 조건

- .NET SDK (net10.0 이상)
- Unity Editor에 `unity-connector` 패키지가 설치되어 있어야 함
- 브리지 기본 주소: `http://127.0.0.1:52737`
- 자동 검색: `~/.unity-cli/instances.json`의 `default.baseUrl`

## 빌드 및 실행

```bash
# 빌드
dotnet build src/UnityCli/UnityCli.csproj

# 실행 (프로젝트 직접)
dotnet run --project src/UnityCli -- <command>

# 실행 (빌드된 DLL)
dotnet src/UnityCli/bin/Debug/net10.0/UnityCli.dll <command>
```

## 글로벌 옵션

| 옵션 | 설명 | 기본값 |
|------|------|--------|
| `--base-url=<url>` | 브리지 URL | instances.json 또는 `http://127.0.0.1:52737` |
| `--json` | JSON 출력 모드 | off |
| `--timeout-ms=<ms>` | 요청 타임아웃 | 10000 |

긴 작업(테스트, 패키지, 컴파일)에는 `--timeout-ms=60000` 이상을 사용한다.

## 명령 체계

### 직접 매핑 명령 (group action key=value...)

씬, 게임오브젝트, UI, 입력, 머티리얼, 에셋, 패키지, 테스트, 콘솔, 에디터 그룹을 `<group> <action> [key=value...]` 형태로 호출한다.

벡터 인자는 `position=1,2,3` 형태(콤마 구분, 괄호 불필요)로 전달한다.

전체 명령 목록과 인자 상세는 `references/commands.md`를 참조한다.

### 브리지 레벨 명령

- `status` -- 브리지 상태 확인
- `capabilities` -- 지원 기능 목록
- `tool list` / `tool call <name> [key=value...]` -- 도구 직접 호출
- `resource list` / `resource get <name>` -- 리소스 조회
- `events tail [after=0] [waitMs=1000]` -- 이벤트 폴링
- `batch run <file>` -- JSON 배치 파일 실행
- `workflow run <file>` -- 이벤트 기반 워크플로우 실행
- `mock serve [host=127.0.0.1] [port=52737]` -- 테스트용 mock 서버

### 비동기 대기 명령

아래 명령은 자동으로 완료를 기다린다:
- `tests run` -- `tests.completed` 이벤트까지 폴링 (최소 60초 타임아웃)
- `editor compile` -- `editor.compiled` 이벤트까지 폴링 (최소 120초 타임아웃)
- `editor play` / `editor stop` -- 상태 전환 완료까지 폴링 (최소 30초 타임아웃)

## 워크플로우 파일 형식

이벤트 기반 순차 실행을 위한 JSON 형식:

```json
{
  "variables": { "scenePath": "Assets/Scenes/Test.unity" },
  "steps": [
    { "id": "scene", "call": "scene.create", "args": { "path": "${scenePath}" } },
    { "id": "wait-log", "waitFor": { "type": "console.log", "contains": "ready", "timeoutMs": 2000 } }
  ]
}
```

- `call`: 도구 호출 (`group.action` 형태)
- `waitFor`: 이벤트 대기 (`type`, `contains`, `timeoutMs`)
- `variables`: `${key}` 형태로 args에서 치환

## 배치 파일 형식

순차 도구 호출만 하는 간단한 형식:

```json
{
  "calls": [
    { "name": "scene.create", "arguments": { "path": "Assets/Scenes/Batch.unity" } },
    { "name": "gameobject.create", "arguments": { "name": "Cube", "primitive": "Cube" } }
  ]
}
```

## Editor 검증

`scripts/verify-editor.sh`로 실제 Editor 대상 end-to-end 검증을 실행한다:

```bash
# 전체 stage 실행
scripts/verify-editor.sh

# 특정 stage만 실행
scripts/verify-editor.sh --stage core --stage ui-input

# 리포트 경로 지정
scripts/verify-editor.sh --report reports/verify-editor/custom.json
```

사용 가능한 stage: `core`, `ui-input`, `tests`, `editor-lifecycle`, `resilience`

## Unity 커넥터 설치

`unity-connector` 폴더를 Unity 프로젝트의 `Packages/com.geuneda.unity-cli-connector`로 복사하거나 embedded package로 연결한다. 패키지 로드 시 `http://127.0.0.1:52737`에서 HTTP 브리지를 열고 `~/.unity-cli/instances.json`에 엔드포인트를 기록한다.

## 일반적인 사용 흐름

1. Unity Editor를 열고 커넥터 패키지가 로드되었는지 확인
2. `unity-cli status`로 브리지 연결 확인
3. CLI 명령으로 씬/오브젝트/UI 조작
4. 필요시 workflow 파일로 이벤트 기반 자동화
5. `verify-editor.sh`로 end-to-end 검증
