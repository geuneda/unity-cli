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

```bash
unity-cli scene create path=Assets/Scenes/Main.unity
unity-cli scene save
unity-cli scene delete path=Assets/Scenes/Old.unity
```

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

```bash
unity-cli gameobject create name=Player primitive=Capsule position=0,1,0
unity-cli gameobject move name=Player position=5,1,3
unity-cli gameobject set-transform name=Player position=3,4,5 rotation=0,90,0 scale=1,2,1
unity-cli gameobject delete name=Player
```

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
| `component update` | `type` | `id`, `name`, `values` | 컴포넌트 갱신 |

```bash
unity-cli component update name=Player type=Rigidbody
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

```bash
unity-cli asset list filter=t:Prefab
unity-cli asset add-to-scene assetPath=Assets/Prefabs/Enemy.prefab
```

## Package 명령

| 명령 | 필수 인자 | 선택 인자 | 설명 |
|------|-----------|-----------|------|
| `package list` | | | 패키지 목록 |
| `package add` | `name` | `version` | 패키지 설치 |

```bash
unity-cli package list
unity-cli package add name=com.unity.inputsystem
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
| `ui screenshot.capture` | `outputPath` | `width`, `height` | Game View 캡처 후 PNG 저장 |

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

```bash
unity-cli resource get editor/state
unity-cli resource get scene/hierarchy
unity-cli resource get ui/hierarchy
```

## Event 타입

| 이벤트 | 설명 |
|--------|------|
| `bridge.started` | 브리지 시작 |
| `scene.changed` | 씬 생성/삭제 |
| `hierarchy.changed` | 게임오브젝트 변경 |
| `console.log` | 콘솔 로그 발행 |
| `tests.started` / `tests.completed` | 테스트 시작/완료 |
| `editor.compiled` | 컴파일 완료 |
| `editor.play_mode_changed` | Play 모드 전환 |
| `ui.focused` / `ui.blurred` | UI 포커스 변경 |
| `ui.double_clicked` / `ui.long_pressed` / `ui.swiped` | UI 입력 이벤트 |
| `input.double_tapped` / `input.long_pressed` / `input.swiped` | 월드 입력 이벤트 |
