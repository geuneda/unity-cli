# unity-cli BACKLOG

설계 워크플로우(4-lens)로 도출한 우선순위 스펙 중, 1차 구현(introspection/catalog/doctor/screenshot 등)에서 **의도적으로 다음으로 미룬 항목**. 각 항목은 콜드 스타트로 이어갈 수 있게 스펙/대상 파일/난이도를 적어둠.

구현 규칙 (1차에서 확립):
- 신규 도구 = `unity-connector/Editor/UnityCliBridge.Catalog.cs`의 `ToolCatalog` + `ExecuteToolAsync` switch arm 둘 다. `BridgeCatalogConsistencyTests`가 드리프트 검출.
- connector 변경은 `editor refresh` 후 도메인 리로드(~20s)로만 반영. `editor compile` 단독은 변경 미반영.
- CLI 측 변경은 `dotnet test UnityCli.slnx`로 즉시 회귀(연결자/실에디터 불필요), mock 핸들러도 같이 추가.
- 공유 자산: `JsonPathResolver`(이미 구현, `--field`/assert가 재사용), `ResolveComponentType`(짧은/전체 타입명 해석), SerializedProperty read/write 매퍼(`ReadSerializedProperty`/`TryWriteSerializedProperty`).

---

## Tier 2 — 정확성/견고성/콘텐츠 (다음 우선)

### T2-1. 통일 에러 계약 + HTTP 상태 + CLI 종료코드 (effort M, risk med)
- 브리지: `BridgeException : Exception { string Code; int Status }` 도입. `FindGameObject`/`scene.delete`/`component.update` 등이 `not_found`/`missing_arg`(404/400)로 throw. `HandleRequestAsync` catch에서 `Failure`와 **동일한 envelope**(`{success,message,code,result,events}`) + `BridgeException.Status` 사용. unknown-tool default arm은 `code:unknown_tool` @200 유지.
- CLI: `BridgeClient.EnsureSuccessAsync`가 200/500 모두 통일 envelope로 파싱(현재 raw 500 문자열 rethrow 제거). 종료코드 0 성공 / 1 도구·assert 실패 / 2 인자·경로 오류 / 3 전송 불가. `--strict` 옵션 고려.
- 주의: 종료코드 의미 변경 = 기존 스크립트 호환 영향. `code` 필드는 additive.

### T2-2. component.update 피드백 + 필드 쓰기 (effort M, risk med)
- 기존 `component.update`(L382 부근)는 writable 프로퍼티만 set하고 미지/필드는 조용히 무시. `applied:[]`/`skipped:[{name,reason}]` 반환 + `ResolveComponentType`(짧은 타입명) 사용 + `ApplyValuesToComponent`(이미 `component.add`용으로 존재, SerializedProperty 경로) 재사용해 `[SerializeField] private` 필드까지 쓰기.

### T2-3. 멀티 인스턴스 레지스트리 (effort M, risk low)
- 브리지 `RegisterInstance`: `instances.json`을 read-merge, `project:port` 키로 upsert(`{baseUrl,projectPath,port,unityVersion,sessionId,updatedAt,alive}`), `default` 별칭=최근 시작 인스턴스. `Stop`/`quitting`에서 best-effort `alive:false`.
- CLI `InstanceRegistry.ResolveBaseUrl(selector)` + `--project`/`--instance` 글로벌 + `instances list` 명령. 현재는 단일 `default` 키라 다중 에디터가 서로 덮어씀.

### T2-4. console.logs 도구 + CLI `logs wait` (effort M, risk low)
- 브리지 `console.logs`: `sinceCursor:long, level:LogType?, contains:string?` → `{logs[], cursor, errorCount, warningCount}` (기존 console.log Data의 level/stackTrace 활용).
- CLI `logs wait level=Error [contains=] [timeoutMs=]` (tests.run 폴링 패턴 재사용) + `expectNone=true`(매칭 로그 있으면 exit 1 = "이 시나리오에서 에러 없음" 게이트).

### T2-5. tests/last-run 리소스 + tests.run 필터 (effort M, risk low)
- 브리지: `EmitTestRunCompleted`에서 마지막 결과를 static 보관 → `tests/last-run` 리소스(`{runId,mode,passed,failed,skipped,inconclusive,finishedAt,failures:[{fullName,message}]}`). `ResourceCatalog` 등록.
- `tests.run`에 `category=`(Filter.categoryNames) + `regex=`(클라이언트 필터) 추가. `assert resource tests/last-run path=failed equals=0` 가능해짐.

### T2-6. prefab.create/instantiate/apply/unpack (effort M, risk med)
- `PrefabUtility.SaveAsPrefabAssetAndConnect`/`InstantiatePrefab`/`ApplyPrefabInstance`/`UnpackPrefabInstance` (이미 import됨). 새 파일 `UnityCliBridge.Prefab.cs`. variant 분기만 risk med. `FindGameObject`/`EnsureParentDirectory`/`Emit` 재사용.

### T2-7. asset.manage (effort M, risk low)
- 단일 도구 `op=create-folder|move|delete|rename|duplicate` → `AssetDatabase.CreateFolder/Validate+MoveAsset/DeleteAsset(s)/RenameAsset/CopyAsset`. 에러 문자열 → `Failure`. 새 파일 `UnityCliBridge.Asset.cs`.

### T2-8. CLI assert + workflow assert/capture/waitFor-resource (effort M, risk low)
- `JsonPathResolver`(구현됨) 위에 단일 `AssertEvaluator`(ops: equals/contains/exists/gt/lt/matches).
- CLI `assert resource|tool|event ... path=<p> <op>=<expected>` (exit 0/1/2).
- `Models.cs` WorkflowStep 확장: `Assert`, `Capture`(newVar←jsonpath, 기존 `${var}` 치환과 연결), `WaitFor`에 `{Resource,Path,Op,Expected,PollMs}` 추가(CliApplication L470 부근 editor/state 폴링 일반화).

### T2-9. sprite.create 실스프라이트 + sprite.set + sorting (effort M, risk low)
- `CreateSprite`(L1667 부근)에 `sprite=` 경로 로드(`AssetDatabase.LoadAssetAtPath<Sprite>`, `path::name` 서브스프라이트), `sortingLayer/sortingOrder/flipX/flipY/color`. 신규 `sprite.set`로 기존 SpriteRenderer 변경.

---

## Tier 3 — 고위험/대형/버전 민감

### T3-1. addressables/list 리소스 (effort M, risk high)
- `AddressableAssetSettingsDefaultObject.Settings`를 **리플렉션**으로(하드 의존 금지) 순회 → `{groups:[{name,entries:[{address,guid,assetPath,labels}]}]}`. 패키지 없으면 `{available:false}`. SpellDefense는 com.unity.addressables 2.7.x, audio.md 규칙이 주소=CSV 컬럼 일치 요구.

### T3-2. asset.create-scriptableobject + scriptableobject/get + scriptableobject.list (effort M, risk med)
- `ScriptableObject.CreateInstance(type)`+`CreateAsset`+`TryWriteSerializedProperty`로 값 주입. `scriptableobject/get?path=`는 `ReadSerializedProperty` 매퍼 재사용. SpellDefense는 ConfigAsset 다수 → CSV→Sheet→ConfigsProvider 후 값 검증에 유용. component.get 매퍼 안정화 후 진행.

### T3-3. 멀티 씬 제어 (effort M, risk low)
- `scene.open-additive`/`scene.set-active`/`scene.list-loaded`. `SceneObject`에 `buildIndex`/`isActive` 추가. `scene.unload`에 path 인자. 방어게임 Bootstrap/Lobby/Gameplay 분리 구성 검증용.

### T3-4. host/port env + 결정적 JSON 직렬화 (effort S, risk low)
- 브리지 Start: `UNITY_CLI_PORT` env → EditorPrefs → 52737. CLI: `--base-url` > `--project/--instance` > `UNITY_CLI_BASE_URL` > instances.json > 기본.
- 단일 `JsonSerializerSettings`(Indented, StringEscapeHandling=Default, ISO UTC 'Z')로 모든 `JsonConvert.SerializeObject` 통일 → timestamp `+`의 `+` 이스케이프 정리.

### T3-5. 워크플로우 retry/poll + 조건부 skip (effort M, risk low)
- WorkflowStep에 `Retry{MaxAttempts,DelayMs}` + 조건부 `{Resource|FromVar,Path,Op,Expected}`(false면 skip). T2-8의 AssertEvaluator 재사용. compile/play 전환 race 완화.

---

## 참고
- 1차 구현 전체 스펙·근거는 design workflow 결과(4 lens → synthesis)에서 도출. 핵심 사실 검증 완료(Unity 6000.3.11f1, SpellDefense URP).
- 스크린샷: 현재 `SubmitRenderRequest`로 ScreenSpaceOverlay UI는 main 카메라 경유로 캡처. 카메라 스택/멀티 카메라 합성은 단순 케이스 위주 — 복잡 구성 시 추가 검증 필요.
