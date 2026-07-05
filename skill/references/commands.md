# unity-cli 명령 레퍼런스

## Scene 명령

| 명령 | 필수 인자 | 선택 인자 | 설명 |
|------|-----------|-----------|------|
| `scene create` | `path` | `name` | 씬 생성 |
| `scene load` | `path` | | 씬 로드 |
| `scene save` | | `path` | 씬 저장 (기본: 활성 씬) |
| `scene info` | | `path` | 씬 정보 조회 |
| `scene delete` | `path` | | 씬 삭제 |
| `scene unload` | | `path` | 씬 언로드 |
| `scene set-lighting` | | `ambientMode`, `ambientColor`, `ambientIntensity`, `ambientSkyColor`, `ambientEquatorColor`, `ambientGroundColor`, `fog`, `fogColor`, `fogMode`, `fogDensity`, `fogStartDistance`, `fogEndDistance`, `skyboxMaterial` | 활성 씬 RenderSettings(앰비언트/포그/스카이박스) 설정 |
| `scene bake-navmesh` | | | 활성 씬 NavMesh 베이크 (동기) |

```bash
unity-cli scene create path=Assets/Scenes/Main.unity
unity-cli scene save
unity-cli scene delete path=Assets/Scenes/Old.unity
unity-cli scene set-lighting fog=true fogColor=#8899AAFF fogMode=Linear fogStartDistance=10 fogEndDistance=120
unity-cli scene set-lighting ambientMode=Flat ambientColor=#303040FF skyboxMaterial=Assets/Materials/Sky.mat
unity-cli scene bake-navmesh
```

`scene set-lighting`은 제공된 키만 활성 씬 RenderSettings에 적용하고 적용된 키를 `applied[]`로 반환한다. `ambientMode`는 `Skybox|Trilight|Flat|Color`, `fogMode`는 `Linear|Exponential|ExponentialSquared`, 색상은 hex(`#RRGGBBAA`), `skyboxMaterial`은 에셋 경로다.
`scene bake-navmesh`는 레거시 NavMesh 빌더로 현재 씬을 동기 베이크하고 `{baked:true}`를 반환한다(보통 수초 소요).
라이트맵/오클루전 베이크와 Terrain 오소링은 현재 범위 밖이다. 프로젝트에 전용 `MenuItem`을 만들고 `menu execute path=...`로 우회한다.

## GameObject 명령

| 명령 | 필수 인자 | 선택 인자 | 설명 |
|------|-----------|-----------|------|
| `gameobject create` | `name` | `scenePath`, `parentId`, `position`, `scale`, `primitive` | 생성 |
| `gameobject get` | | `id`, `name` | 조회 |
| `gameobject delete` | | `id`, `name` | 삭제 |
| `gameobject duplicate` | | `id`, `name` | 복제 |
| `gameobject reparent` | | `id`, `name`, `parentId` | 부모 변경 |
| `gameobject move` | | `id`, `name`, `position` | 이동 |
| `gameobject rotate` | | `id`, `name`, `rotation` | 회전 |
| `gameobject scale` | | `id`, `name`, `scale` | 스케일 |
| `gameobject set-transform` | | `id`, `name`, `position`, `rotation`, `scale` | 전체 변환 |
| `gameobject select` | | `id`, `name` | 선택 |
| `gameobject find` | | `tag`, `layer`, `component`, `nameContains`, `path`, `activeOnly`, `includeInactive`, `limit` | 필터 검색 (비활성/풀링 오브젝트 포함) |
| `gameobject set-properties` | | `id`, `name`, `active`, `tag`, `layer`, `static`, `newName`, `recursiveLayer` | 오브젝트 속성 일괄 변경 |

`gameobject find`의 모든 필터는 선택이며 AND로 결합된다. `layer`는 이름 또는 int, `component`는 타입 이름, `path`는 계층 경로 접미사 매칭(세그먼트별 `*` 와일드카드)이다. `activeOnly`는 bool(기본 `false`), `includeInactive`는 bool(기본 `true`), `limit`는 int(기본 `200`). 응답은 `{count,truncated,items:[...]}` 형태로 비활성/풀링된 오브젝트도 포함한다.

`gameobject set-properties`의 `tag`/`layer`는 검증되며 정의되지 않은 값이면 에러를 반환한다. `layer`는 이름 또는 int, `active`/`static`/`recursiveLayer`는 bool(기본 `false`), `recursiveLayer=false`면 layer를 자식까지 적용한다. 응답은 `{applied:[],gameObject}` 형태다.

```bash
unity-cli gameobject create name=Player primitive=Capsule position=0,1,0
unity-cli gameobject move name=Player position=5,1,3
unity-cli gameobject set-transform name=Player position=3,4,5 rotation=0,90,0 scale=1,2,1
unity-cli gameobject find component=Camera
unity-cli gameobject find tag=Enemy nameContains=Goblin
unity-cli gameobject set-properties name=Enemy active=false tag=Enemy layer=Default
unity-cli gameobject delete name=Player
```

`gameobject get`/`find` 등의 직렬화 결과에는 `tag`, `layer`, `layerName`, `isStatic`, `activeInHierarchy`, `childCount`가 포함된다. SpriteRenderer 오브젝트는 `spritePath`, `sortingLayerName`, `sortingOrder`, `flipX`, `flipY`를, UI 오브젝트는 `image`/`button`/`canvasGroup` 하위 오브젝트(`CanvasGroup.alpha`, `button.interactable`, `image.fillAmount`로 팝업 가시성 검증)를 추가로 노출한다.

## Sprite 명령

| 명령 | 필수 인자 | 선택 인자 | 설명 |
|------|-----------|-----------|------|
| `sprite create` | `name` | `position`, `color` | 2D 스프라이트 생성 |

```bash
unity-cli sprite create name=MySprite position=2,1,0 color=#FF8A00FF
```

## Component 명령

| 명령 | 필수 인자 | 선택 인자 | 설명 |
|------|-----------|-----------|------|
| `component list` | `id` 또는 `name` | `includeValues` | GameObject의 모든 컴포넌트 목록 (missing script는 `<missing>`로 노출) |
| `component get` | `type` + (`id` 또는 `name`) | | 컴포넌트의 직렬화 프로퍼티 조회 (`[SerializeField] private` 포함) |
| `component add` | `type` + (`id` 또는 `name`) | `values`, `allowDuplicate` | 컴포넌트 추가 후 값 적용 |
| `component update` | `type` | `id`, `name`, `values` | 컴포넌트 갱신 |
| `component remove` | `type` + (`id` 또는 `name`) | `index` | 컴포넌트 제거 (Transform/RectTransform 제거 거부) |

`component list`의 `includeValues`는 bool(기본 `false`)이며, `true`면 각 컴포넌트의 `properties`도 함께 반환한다. 응답은 `{id,name,count,components:[{type,fullType,enabled,instanceId,properties?}]}` 형태다.

`component get`/`component add`/`component remove`의 `type`은 짧은 이름(`Camera`, `Rigidbody2D`)과 전체 이름을 모두 해석한다.

`component get` 응답은 `{id,name,type,fullType,properties:{path:value}}` 형태이며 값은 SerializedObject 기준으로 인코딩된다.
- enum: `{enumValue,enumName}`
- color: `#hex`
- vector: 배열
- object reference: `{objectName,objectType,assetPath,instanceId}`
- array: `{isArray,length}`
- 미지원 타입: `{unsupported,propertyType}`

`component add`의 `values`는 `member=value` JSON 오브젝트이며, 응답은 `{id,name,type,applied:[],skipped:[{name,reason}]}` 형태다. 같은 컴포넌트가 이미 있으면(`allowDuplicate=false`일 때) 또는 Component 타입이 아니면 실패한다. `allowDuplicate`는 bool(기본 `false`).

`component remove`의 `index`는 int(기본 `0`)로 동일 타입이 여러 개일 때 대상을 지정한다. 응답은 `{id,name,removed,type,index}` 형태다.

```bash
unity-cli component list name=Player includeValues=true
unity-cli component get name=Player type=Camera
unity-cli component add name=Player type=Rigidbody2D values={"gravityScale":2}
unity-cli component update name=Player type=Rigidbody
unity-cli component remove name=Player type=BoxCollider2D index=0
```

### values JSON 확장

`component add` / `component update` / `asset create-scriptableobject`의 `values`는 각 멤버를 SerializedProperty로 재귀 기록한다. CLI는 값이 `{` 또는 `[`로 시작하면 JSON으로 파싱하므로 배열/중첩/참조를 그대로 전달할 수 있다. 기록한 값은 `component get` / `scriptableobject get`으로 그대로 되읽힌다.

- 씬 오브젝트 참조: `{"__ref":"<selector>","component":"<옵션 컴포넌트명>"}`. selector는 `name:Foo` | `path:Root/Child/Foo` | `id:12345` | 접두사 없으면 name으로 간주. 필드 기대 타입이 `GameObject`면 그 오브젝트, `Transform`이면 transform, `Component` 파생이면 `component` 명시 시 해당 컴포넌트(미지정 시 기대 타입)를 할당한다. 미발견 시 `ref_not_found`, 타입 불일치 시 `ref_type_mismatch`.
- 에셋 참조: `{"__asset":"Assets/..."}` 또는 문자열 경로 그대로. 미발견 시 `asset_not_found`.
- null 클리어: JSON `null` 또는 `{"__null":true}` -> 참조 제거.
- 배열/리스트: `[...]` -- 배열 크기를 원소 수로 맞춘 뒤 각 원소를 재귀 기록(원소에 ref-spec/에셋참조 가능).
- 중첩 struct/class: `{...}` -- 자식 필드를 재귀 기록. 점 표기 dotted key(`_stat.hp`)도 상위 경로로 동작.
- 신규 타입: `Vector4`=`[x,y,z,w]`, `Quaternion`=`[x,y,z,w]`(직접) 또는 `[x,y,z]`(Euler), `Rect`=`[x,y,w,h]`, `Bounds`=`{"center":[x,y,z],"size":[x,y,z]}`, `Vector2Int`=`[x,y]`, `Vector3Int`=`[x,y,z]`. 미지원 타입은 `unsupported_write: <type>`로 skip된다.

```bash
# 카메라 팔로우 타깃을 씬 오브젝트로 참조
unity-cli component update name=MainCamera type=CameraFollow values={"_target":{"__ref":"name:Player"}}

# 특정 컴포넌트를 참조 (경로 선택자 + Rigidbody)
unity-cli component update name=Launcher type=Cannon values={"_body":{"__ref":"path:Root/Player","component":"Rigidbody"}}

# 스포너의 프리팹 배열 (에셋 참조)
unity-cli component update name=Spawner type=WaveSpawner values={"_prefabs":[{"__asset":"Assets/Prefabs/Goblin.prefab"},{"__asset":"Assets/Prefabs/Orc.prefab"}]}

# 중첩 스탯 struct
unity-cli component update name=Enemy type=EnemyStats values={"_stat":{"hp":120,"speed":3.5}}

# 신규 타입 (Bounds + Vector2Int)
unity-cli component update name=Marker type=Zone values={"_bounds":{"center":[0,0,0],"size":[10,4,10]},"_cell":[2,3]}

# 참조 제거
unity-cli component update name=MainCamera type=CameraFollow values={"_target":null}
```

## Material 명령

| 명령 | 필수 인자 | 선택 인자 | 설명 |
|------|-----------|-----------|------|
| `material create` | `path` | `name`, `shader`, `color` | 생성 |
| `material assign` | `materialPath` | `id`, `name` | 할당 |
| `material modify` | `path` | `shader`, `color` | 수정 |
| `material info` | `path` | | 조회 |

```bash
unity-cli material create path=Assets/Materials/Red.mat shader=Standard color=#FF0000FF
unity-cli material assign name=Cube materialPath=Assets/Materials/Red.mat
unity-cli material modify path=Assets/Materials/Red.mat color=#00FF00FF
```

## Asset 명령

| 명령 | 필수 인자 | 선택 인자 | 설명 |
|------|-----------|-----------|------|
| `asset list` | | `filter` | 에셋 목록 |
| `asset add-to-scene` | `assetPath` | `scenePath`, `name` | 프리팹 인스턴스화 |
| `asset import-texture` | `path` | `textureType`, `spriteMode`, `maxTextureSize`, `filterMode` | 텍스처 임포트 설정 변경 |
| `asset set-addressable` | `path` | `address`, `group` | 에셋을 Addressable 엔트리로 등록/이동하고 address/group 설정 |
| `asset remove-addressable` | `path` | | 에셋의 Addressable 엔트리 제거 |

```bash
unity-cli asset list filter=t:Prefab
unity-cli asset add-to-scene assetPath=Assets/Prefabs/Enemy.prefab

# PNG를 UGUI Sprite로 임포트 (Figma 에셋 등)
unity-cli asset import-texture path=Assets/FigmaAssets/icon.png textureType=Sprite
unity-cli asset import-texture path=Assets/FigmaAssets/hero.png textureType=Sprite maxTextureSize=2048
```

`textureType` 옵션: `Sprite` (기본), `Default`, `NormalMap`, `Cursor`
`spriteMode`: `1` (Single, 기본), `2` (Multiple)
`filterMode`: `Bilinear` (기본), `Point`, `Trilinear`

```bash
unity-cli asset set-addressable path=Assets/Prefabs/Enemy.prefab address=enemy group=Enemies
unity-cli asset remove-addressable path=Assets/Prefabs/Enemy.prefab
```

`asset set-addressable`는 에셋 guid로 엔트리를 만들거나 이동하고 `{path, guid, address, group}`를 반환한다. `address` 미지정 시 기존/기본 주소를 유지하고, `group` 미지정 시 기본 그룹에 둔다. `asset remove-addressable`는 `{path, removed}`를 반환한다. Addressables 패키지가 없거나 설정이 없으면 실패한다(`addressables_unavailable` 등).

## Package 명령

| 명령 | 필수 인자 | 선택 인자 | 설명 |
|------|-----------|-----------|------|
| `package list` | | | 패키지 목록 |
| `package add` | `name` | `version` | 패키지 설치 |

```bash
unity-cli package list
unity-cli package add name=com.unity.inputsystem
```

## Project 명령

| 명령 | 필수 인자 | 선택 인자 | 설명 |
|------|-----------|-----------|------|
| `project add-tag` | `tag` | | TagManager에 태그 추가 (이미 있으면 유지). 멱등 |
| `project add-layer` | `layer` | `index` | user 레이어(8..31) 빈 슬롯 또는 지정 `index`에 이름 설정 |
| `project list-tags-layers` | | | 현재 태그 목록과 user 레이어(index->name) 조회 (읽기 전용) |

`project add-tag` 응답은 `{tag, added}`이며 `added`가 새로 추가 여부를 나타낸다(이미 있으면 `added:false`).
`project add-layer`는 `index` 미지정 시 8..31 중 첫 빈 슬롯을 쓴다. 같은 이름이 이미 있으면 그 index를 `added:false`로 반환하고, 슬롯이 가득 차면 실패한다. 응답은 `{layer, index, added}`.
`project list-tags-layers` 응답은 `{tags:[...], layers:[{index,name}...]}`.

```bash
unity-cli project add-tag tag=Enemy
unity-cli project add-layer layer=Water
unity-cli project add-layer layer=Interactable index=12
unity-cli project list-tags-layers
```

## Tests 명령

| 명령 | 필수 인자 | 선택 인자 | 설명 |
|------|-----------|-----------|------|
| `tests list` | | `mode` | 테스트 목록 |
| `tests run` | | `mode` | 테스트 실행 (완료까지 대기) |

mode: `EditMode`, `PlayMode`

```bash
unity-cli --timeout-ms=60000 tests list mode=EditMode
unity-cli --timeout-ms=60000 tests run mode=PlayMode
```

## Console 명령

| 명령 | 필수 인자 | 선택 인자 | 설명 |
|------|-----------|-----------|------|
| `console get` | | `level` | 로그 조회 |
| `console clear` | | | 로그 초기화 |
| `console send` | `message` | `level` | 로그 발행 |

```bash
unity-cli console send message=HelloWorld level=info
unity-cli console get
unity-cli console clear
```

## UI 명령

현재 UI 생성 계열 명령은 텍스트를 `TextMeshProUGUI`, 입력 필드를 `TMP_InputField`로 만든다. 첫 TMP UI 생성 시 프로젝트에 TMP Essential Resources가 없으면 자동 import가 실행될 수 있다.

### 생성

모든 UI 생성 명령은 `parentName` 또는 `parentId`로 부모 RectTransform을 지정할 수 있다. 미지정 시 Canvas 직속 자식으로 생성된다.
앵커/피봇 파라미터(`anchorMin`, `anchorMax`, `pivot`)를 지원한다. 미지정 시 center anchor(0.5,0.5).

| 명령 | 필수 인자 | 선택 인자 | 설명 |
|------|-----------|-----------|------|
| `ui canvas.create` | | `name`, `referenceResolution`, `screenMatchMode`, `matchWidthOrHeight` | Canvas 생성 (CanvasScaler 설정 포함) |
| `ui button.create` | | `canvasName`, `name`, `text`, `anchoredPosition`, `size`, `anchorMin`, `anchorMax`, `pivot`, `parentName`, `parentId`, `fontSize`, `fontStyle`, `alignment` | 버튼 생성 |
| `ui toggle.create` | | `canvasName`, `name`, `text`, `anchoredPosition`, `size`, `anchorMin`, `anchorMax`, `pivot`, `parentName`, `parentId` | 토글 생성 |
| `ui slider.create` | | `canvasName`, `name`, `anchoredPosition`, `size`, `anchorMin`, `anchorMax`, `pivot`, `parentName`, `parentId`, `minValue`, `maxValue`, `value` | 슬라이더 생성 |
| `ui scrollrect.create` | | `canvasName`, `name`, `anchoredPosition`, `size`, `anchorMin`, `anchorMax`, `pivot`, `parentName`, `parentId`, `itemCount` | 스크롤뷰 생성 |
| `ui inputfield.create` | | `canvasName`, `name`, `anchoredPosition`, `size`, `anchorMin`, `anchorMax`, `pivot`, `parentName`, `parentId`, `placeholder`, `text`, `multiline` | TMP 입력필드 생성 |
| `ui text.create` | | `canvasName`, `name`, `text`, `anchoredPosition`, `size`, `anchorMin`, `anchorMax`, `pivot`, `parentName`, `parentId`, `fontSize`, `fontStyle`, `alignment` | TMP 텍스트 생성 |
| `ui image.create` | | `canvasName`, `name`, `anchoredPosition`, `size`, `anchorMin`, `anchorMax`, `pivot`, `parentName`, `parentId`, `spritePath`, `imageType` | 이미지 생성 |
| `ui panel.create` | | `canvasName`, `name`, `anchoredPosition`, `size`, `anchorMin`, `anchorMax`, `pivot`, `parentName`, `parentId`, `color` | 빈 RectTransform 패널 생성 (color 지정 시 Image 추가) |

`fontStyle`: "Normal", "Bold", "Italic", "BoldAndItalic"
`alignment`: "UpperLeft", "UpperCenter", "UpperRight", "MiddleLeft", "MiddleCenter", "MiddleRight", "LowerLeft", "LowerCenter", "LowerRight"
`screenMatchMode`: "Expand"(기본), "Shrink", "MatchWidthOrHeight"
`imageType`: "Simple", "Sliced", "Tiled", "Filled"

### Layout

| 명령 | 필수 인자 | 선택 인자 | 설명 |
|------|-----------|-----------|------|
| `ui layout.add` | `name` 또는 `id` | `layoutType`, `spacing`, `childAlignment`, `childForceExpandWidth`, `childForceExpandHeight`, `childControlWidth`, `childControlHeight`, `paddingLeft`, `paddingRight`, `paddingTop`, `paddingBottom`, `cellSize`, `gridSpacing`, `horizontalFit`, `verticalFit` | 기존 GO에 Layout 컴포넌트 추가 |

`layoutType`: "Horizontal", "Vertical", "Grid", "ContentSizeFitter"

### RectTransform 수정

| 명령 | 필수 인자 | 선택 인자 | 설명 |
|------|-----------|-----------|------|
| `ui recttransform.modify` | `name` 또는 `id` | `anchorMin`, `anchorMax`, `pivot`, `anchoredPosition`, `size`, `offsetMin`, `offsetMax` | 기존 RectTransform 속성 수정 (제공된 속성만 변경) |

### 스크린샷

| 명령 | 필수 인자 | 선택 인자 | 설명 |
|------|-----------|-----------|------|
| `ui screenshot.capture` | `outputPath` | `width`, `height`, `source` | Game/Scene View 캡처 후 PNG 저장 |

`source`는 `game`(기본) 또는 `scene`이다. 캡처는 URP/SRP 호환 `RenderPipeline.SubmitRenderRequest`를 사용하므로 에디터 윈도우 포커스와 무관하게 동작하며, URP에서도 검은 프레임이 나오지 않는다. 응답에는 사용된 `mode` 필드가 포함된다.

### 상태 변경

| 명령 | 필수 인자 | 선택 인자 | 설명 |
|------|-----------|-----------|------|
| `ui toggle.set` | `name` | `isOn` | 토글 상태 변경 |
| `ui slider.set` | `name` | `value` | 슬라이더 값 변경 |
| `ui scrollrect.set` | `name` | `normalizedPosition` | 스크롤 위치 변경 |
| `ui inputfield.set-text` | `name` | `text` | 텍스트 입력 |
| `ui focus` | `name` | | UI 포커스 |
| `ui blur` | | | 포커스 해제 |

### 입력 시뮬레이션 (UI 좌표)

| 명령 | 인자 | 설명 |
|------|------|------|
| `ui click` | `name` 또는 `normalizedPosition`, `pointerId` | 클릭 |
| `ui double-click` | `normalizedPosition` | 더블클릭 |
| `ui long-press` | `normalizedPosition`, `durationMs` | 롱프레스 |
| `ui drag` | `name`, `from`, `to`, `pointerId` | 드래그 |
| `ui swipe` | `normalizedFrom`, `normalizedTo` | 스와이프 |

```bash
unity-cli ui canvas.create name=MyCanvas referenceResolution=1440,3040 screenMatchMode=Expand
unity-cli ui panel.create canvasName=MyCanvas name=Header anchorMin=0,1 anchorMax=1,1 pivot=0.5,1 size=0,180 color=#1A1A2EFF
unity-cli ui text.create canvasName=MyCanvas name=Title parentName=Header text="TMP Title" fontSize=48 fontStyle=Bold alignment=MiddleCenter
unity-cli ui button.create canvasName=MyCanvas name=Btn text=Click anchoredPosition=0,0 size=200,60
unity-cli ui image.create canvasName=MyCanvas name=Icon spritePath=Assets/Sprites/icon.png imageType=Simple size=64,64
unity-cli ui inputfield.create canvasName=MyCanvas name=MyInput placeholder="Type here" text="seed" anchoredPosition=0,40 size=420,56
unity-cli ui layout.add name=Header layoutType=Horizontal spacing=16 childAlignment=MiddleCenter paddingLeft=24 paddingRight=24
unity-cli ui recttransform.modify name=Header anchoredPosition=0,-10 size=0,200
unity-cli ui screenshot.capture outputPath=Assets/Screenshots/capture.png width=1440 height=3040
unity-cli ui screenshot.capture outputPath=Assets/Screenshots/scene.png source=scene
unity-cli ui click name=Btn pointerId=21
unity-cli ui focus name=MyInput
unity-cli ui blur
```

실제 생성 결과 검증:

```bash
unity-cli resource get ui/hierarchy
unity-cli resource get editor/state
```

- `ui/hierarchy`에서 입력 필드는 `selectableType=TMP_InputField`, `inputField.textComponentType=TextMeshProUGUI`로 보인다.
- 텍스트 오브젝트와 플레이스홀더/본문 텍스트도 `text` 값으로 조회된다.
- EditMode에서는 `ui focus` 직후 `inputField.isFocused`가 바로 `true`가 아닐 수 있으므로 `isSelected`와 `editor/state.eventSystemSelectedObjectName`도 함께 본다.

## Input 명령 (월드 좌표)

| 명령 | 인자 | 설명 |
|------|------|------|
| `input tap` | `worldPosition` | 탭 |
| `input double-tap` | `worldPosition`, `pointerId` | 더블탭 |
| `input long-press` | `worldPosition`, `durationMs`, `pointerId` | 롱프레스 |
| `input drag` | `worldFrom`, `worldTo`, `pointerId` | 드래그 |
| `input swipe` | `worldFrom`, `worldTo`, `pointerId` | 스와이프 |

```bash
unity-cli input tap worldPosition=2,1,0
unity-cli input swipe worldFrom=2,1,0 worldTo=2.75,1,0 pointerId=9
```

## Menu 명령

| 명령 | 필수 인자 | 설명 |
|------|-----------|------|
| `menu execute` | `path` | 메뉴 아이템 실행 |

```bash
unity-cli menu execute path=Assets/Refresh
```

## Editor 명령

| 명령 | 선택 인자 | 설명 |
|------|-----------|------|
| `editor play` | | Play 모드 진입 (완료 대기) |
| `editor stop` | | Play 모드 종료 (완료 대기) |
| `editor pause` | `enabled` | 일시정지 토글 |
| `editor refresh` | | 에디터 리프레시 |
| `editor compile` | | 스크립트 컴파일 (완료 대기) |
| `editor gameview.resize` | `width`, `height` | Game View 해상도 변경 |

```bash
unity-cli editor play
unity-cli editor pause enabled=true
unity-cli editor stop
unity-cli editor refresh
unity-cli --timeout-ms=120000 editor compile
unity-cli editor gameview.resize width=1440 height=3040
```

## Resource 목록

| 리소스 이름 | 설명 |
|------------|------|
| `editor/state` | 에디터 Play/Pause/Selection 상태 |
| `scene/active` | 활성 씬 요약 |
| `scene/hierarchy` | 활성 씬 계층구조 |
| `ui/hierarchy` | UI 계층구조 |
| `console/logs` | 콘솔 로그 |
| `tests/catalog` | 등록된 테스트 목록 |
| `packages/list` | 설치된 패키지 목록 |
| `project/info` | 프로젝트/빌드 설정 정보 |

`project/info`는 `{unityVersion, projectPath, productName, companyName, activeBuildTarget, buildTargetGroup, colorSpace, scriptingDefineSymbols, renderPipeline (URP/Built-in 감지), isPlaying, scenesInBuild:[{path,enabled}]}`를 반환한다.

```bash
unity-cli resource get editor/state
unity-cli resource get scene/hierarchy
unity-cli resource get ui/hierarchy
unity-cli resource get project/info
```

## Event 타입

| 이벤트 | 설명 |
|--------|------|
| `bridge.started` | 브리지 시작 |
| `scene.changed` | 씬 생성/삭제 |
| `scene.loaded` | 씬 로드 |
| `scene.saved` | 씬 저장 |
| `hierarchy.changed` | 게임오브젝트 변경 |
| `transform.changed` | Transform 변경 |
| `console.log` | 콘솔 로그 발행 |
| `tests.started` / `tests.completed` | 테스트 시작/완료 |
| `editor.compiled` | 컴파일 완료 |
| `editor.play_mode_changed` | Play 모드 전환 |
| `ui.focused` / `ui.blurred` | UI 포커스 변경 |
| `ui.double_clicked` / `ui.long_pressed` / `ui.swiped` | UI 입력 이벤트 |
| `input.double_tapped` / `input.long_pressed` / `input.swiped` | 월드 입력 이벤트 |

위 이벤트 타입은 `capabilities`에도 모두 광고된다(`bridge.started`, `scene.loaded`, `scene.saved`, `transform.changed` 포함). 브리지는 최대 5000개의 이벤트 링 버퍼를 유지하며, `events` 폴 응답의 `floor`(최저 보존 커서) 필드로 잘린 구간을 감지할 수 있다.
