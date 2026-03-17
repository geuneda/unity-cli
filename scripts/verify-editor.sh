#!/usr/bin/env bash

set -uo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CLI_DLL="${CLI_DLL:-$ROOT_DIR/src/UnityCli/bin/Debug/net10.0/UnityCli.dll}"
CLI_PROJECT="$ROOT_DIR/src/UnityCli/UnityCli.csproj"
DEFAULT_TIMEOUT_MS="${DEFAULT_TIMEOUT_MS:-120000}"
REPORT_PATH="${REPORT_PATH:-$ROOT_DIR/reports/verify-editor/latest.json}"

AVAILABLE_STAGES=(
  "core"
  "ui-input"
  "tests"
  "editor-lifecycle"
)

SELECTED_STAGES=()
OVERALL_STATUS="passed"
RESULTS_FILE="$(mktemp)"

log() {
  printf '\n==> %s\n' "$*" >&2
}

usage() {
  cat <<'EOF'
Usage: scripts/verify-editor.sh [options]

Options:
  --stage <name>       Run a single stage. Can be passed multiple times.
  --stages <a,b,c>     Run a comma-separated stage list.
  --report <path>      Write JSON report to the given path.
  --list-stages        Print available stages.
  -h, --help           Show this help.

Stages:
  core
  ui-input
  tests
  editor-lifecycle
EOF
}

require_tool() {
  local tool_name="$1"
  if ! command -v "$tool_name" >/dev/null 2>&1; then
    printf 'Missing required tool: %s\n' "$tool_name" >&2
    exit 1
  fi
}

iso_now() {
  date -u +"%Y-%m-%dT%H:%M:%SZ"
}

epoch_ms() {
  printf '%s000' "$(date +%s)"
}

json_array_from_args() {
  jq -n '$ARGS.positional' --args "$@"
}

ensure_cli() {
  if [[ ! -f "$CLI_DLL" ]]; then
    log "Building Unity CLI"
    dotnet build "$CLI_PROJECT" >&2
  fi
}

run_cli() {
  local timeout_ms="$DEFAULT_TIMEOUT_MS"
  if [[ "${1:-}" == --timeout-ms=* ]]; then
    timeout_ms="${1#--timeout-ms=}"
    shift
  fi

  log "unity-cli --timeout-ms=$timeout_ms $*"
  dotnet "$CLI_DLL" --timeout-ms="$timeout_ms" "$@"
}

json_cli() {
  local output
  output="$(run_cli "$@")"
  printf '%s\n' "$output" >&2
  printf '%s' "$output"
}

assert_contains() {
  local haystack="$1"
  local needle="$2"
  if [[ "$haystack" != *"$needle"* ]]; then
    printf 'Expected output to contain: %s\n' "$needle" >&2
    exit 1
  fi
}

append_stage_result() {
  local stage_name="$1"
  local status="$2"
  local started_at="$3"
  local finished_at="$4"
  local duration_ms="$5"
  local details="$6"

  jq -n \
    --arg stage "$stage_name" \
    --arg status "$status" \
    --arg startedAt "$started_at" \
    --arg finishedAt "$finished_at" \
    --argjson durationMs "$duration_ms" \
    --arg details "$details" \
    '{
      stage: $stage,
      status: $status,
      startedAt: $startedAt,
      finishedAt: $finishedAt,
      durationMs: $durationMs,
      details: (if $details == "" then null else $details end)
    }' >> "$RESULTS_FILE"
}

write_report() {
  local generated_at selected_json
  generated_at="$(iso_now)"
  selected_json="$(json_array_from_args "${SELECTED_STAGES[@]}")"
  mkdir -p "$(dirname "$REPORT_PATH")"

  jq -s \
    --arg generatedAt "$generated_at" \
    --arg overallStatus "$OVERALL_STATUS" \
    --arg reportPath "$REPORT_PATH" \
    --argjson selectedStages "$selected_json" \
    '{
      generatedAt: $generatedAt,
      overallStatus: $overallStatus,
      reportPath: $reportPath,
      selectedStages: $selectedStages,
      passedCount: map(select(.status == "passed")) | length,
      failedCount: map(select(.status == "failed")) | length,
      results: .
    }' "$RESULTS_FILE" > "$REPORT_PATH"
}

run_stage() {
  local stage_name="$1"
  local stage_function="$2"
  local started_at finished_at start_ms end_ms duration_ms status details

  started_at="$(iso_now)"
  start_ms="$(epoch_ms)"
  status="passed"
  details=""

  log "stage[$stage_name] started"
  if ! (
    set -euo pipefail
    "$stage_function"
  ); then
    status="failed"
    details="Stage execution failed."
    OVERALL_STATUS="failed"
  fi

  finished_at="$(iso_now)"
  end_ms="$(epoch_ms)"
  duration_ms="$((end_ms - start_ms))"
  append_stage_result "$stage_name" "$status" "$started_at" "$finished_at" "$duration_ms" "$details"
  log "stage[$stage_name] $status"
}

parse_args() {
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --stage)
        [[ $# -ge 2 ]] || { printf 'Missing value for --stage\n' >&2; exit 1; }
        SELECTED_STAGES+=("$2")
        shift 2
        ;;
      --stage=*)
        SELECTED_STAGES+=("${1#--stage=}")
        shift
        ;;
      --stages)
        [[ $# -ge 2 ]] || { printf 'Missing value for --stages\n' >&2; exit 1; }
        IFS=',' read -r -a parsed_stages <<<"$2"
        SELECTED_STAGES+=("${parsed_stages[@]}")
        shift 2
        ;;
      --stages=*)
        IFS=',' read -r -a parsed_stages <<<"${1#--stages=}"
        SELECTED_STAGES+=("${parsed_stages[@]}")
        shift
        ;;
      --report)
        [[ $# -ge 2 ]] || { printf 'Missing value for --report\n' >&2; exit 1; }
        REPORT_PATH="$2"
        shift 2
        ;;
      --report=*)
        REPORT_PATH="${1#--report=}"
        shift
        ;;
      --list-stages)
        printf '%s\n' "${AVAILABLE_STAGES[@]}"
        exit 0
        ;;
      -h|--help)
        usage
        exit 0
        ;;
      *)
        printf 'Unknown argument: %s\n' "$1" >&2
        usage >&2
        exit 1
        ;;
    esac
  done

  if [[ ${#SELECTED_STAGES[@]} -eq 0 ]]; then
    SELECTED_STAGES=("${AVAILABLE_STAGES[@]}")
  fi

  local stage_name valid
  for stage_name in "${SELECTED_STAGES[@]}"; do
    valid="false"
    for known_stage in "${AVAILABLE_STAGES[@]}"; do
      if [[ "$stage_name" == "$known_stage" ]]; then
        valid="true"
        break
      fi
    done

    if [[ "$valid" != "true" ]]; then
      printf 'Unknown stage: %s\n' "$stage_name" >&2
      exit 1
    fi
  done
}

stage_core() {
  local parent_json parent_id cube_json cube_id duplicate_json duplicate_id asset_list_json console_json

  run_cli status >/dev/null
  run_cli tool list >/dev/null
  run_cli resource list >/dev/null

  run_cli editor stop >/dev/null || true
  run_cli editor refresh >/dev/null
  run_cli --timeout-ms=180000 editor compile >/dev/null

  run_cli scene create path=Assets/Scenes/CliCoverage.unity >/dev/null
  run_cli scene info >/dev/null
  run_cli scene save >/dev/null

  parent_json="$(json_cli gameobject create name=CliParent)"
  parent_id="$(jq -r '.result.id' <<<"$parent_json")"

  cube_json="$(json_cli gameobject create name=CliCube primitive=Cube position=1,2,3)"
  cube_id="$(jq -r '.result.id' <<<"$cube_json")"
  run_cli gameobject get id="$cube_id" >/dev/null

  duplicate_json="$(json_cli gameobject duplicate id="$cube_id" name=CliCubeCopy)"
  duplicate_id="$(jq -r '.result.id' <<<"$duplicate_json")"

  run_cli gameobject reparent id="$duplicate_id" parentId="$parent_id" >/dev/null
  run_cli gameobject move name=CliCube position=2,3,4 >/dev/null
  run_cli gameobject rotate name=CliCube rotation=0,45,0 >/dev/null
  run_cli gameobject scale name=CliCube scale=2,2,2 >/dev/null
  run_cli gameobject set-transform name=CliCube position=3,4,5 rotation=0,90,0 scale=1,2,1 >/dev/null
  run_cli gameobject select id="$cube_id" >/dev/null
  run_cli component update id="$cube_id" type=CliUiProbe >/dev/null
  run_cli gameobject delete id="$duplicate_id" >/dev/null

  run_cli sprite create name=CliSprite position=2,1,0 color=#FF8A00FF >/dev/null
  run_cli component update name=CliSprite type=CliUiProbe >/dev/null

  run_cli material create path=Assets/Materials/CliCoverage.mat shader=Standard color=#00C2A8FF >/dev/null
  run_cli material info path=Assets/Materials/CliCoverage.mat >/dev/null
  run_cli material modify path=Assets/Materials/CliCoverage.mat color=#C24D00FF >/dev/null
  run_cli material assign name=CliCube materialPath=Assets/Materials/CliCoverage.mat >/dev/null

  asset_list_json="$(json_cli asset list filter=t:Prefab)"
  assert_contains "$asset_list_json" "Assets/Prefabs/CliPrefab.prefab"
  run_cli asset add-to-scene assetPath=Assets/Prefabs/CliPrefab.prefab >/dev/null

  run_cli package list >/dev/null
  run_cli package add name=com.unity.inputsystem >/dev/null

  run_cli console send message=CliCoverageLog >/dev/null
  console_json="$(json_cli console get)"
  assert_contains "$console_json" "CliCoverageLog"
  run_cli console clear >/dev/null

  run_cli resource get editor/state >/dev/null
  run_cli resource get scene/active >/dev/null
  run_cli resource get scene/hierarchy >/dev/null
  run_cli resource get tests/catalog >/dev/null
  run_cli resource get packages/list >/dev/null
  run_cli menu execute path=Assets/Refresh >/dev/null
}

stage_ui_input() {
  local ui_double_click_json ui_long_press_json ui_swipe_json input_double_tap_json input_long_press_json input_swipe_json
  local toggle_json slider_json scrollrect_json inputfield_json ui_hierarchy_json console_json events_json ui_scene

  ui_scene="Assets/Scenes/CliUiCoverage.unity"

  run_cli scene create path="$ui_scene" >/dev/null
  run_cli console clear >/dev/null

  run_cli ui canvas.create name=CliCanvas >/dev/null
  run_cli ui button.create canvasName=CliCanvas name=CliButton text=TapMe anchoredPosition=0,0 size=220,80 >/dev/null
  run_cli ui toggle.create canvasName=CliCanvas name=CliToggle text=EnableFeature anchoredPosition=0,180 size=260,40 >/dev/null
  run_cli ui slider.create canvasName=CliCanvas name=CliSlider anchoredPosition=0,260 size=320,40 minValue=0 maxValue=1 value=0.2 >/dev/null
  run_cli ui scrollrect.create canvasName=CliCanvas name=CliScroll anchoredPosition=320,0 size=260,200 itemCount=10 >/dev/null
  run_cli ui inputfield.create canvasName=CliCanvas name=CliInput anchoredPosition=0,-200 size=320,44 placeholder=TypeHere >/dev/null
  run_cli ui text.create canvasName=CliCanvas name=CliText text=Coverage anchoredPosition=0,120 size=280,48 >/dev/null
  run_cli ui image.create canvasName=CliCanvas name=CliImage anchoredPosition=0,-120 size=96,96 color=#66CCFFFF >/dev/null
  run_cli component update name=CliButton type=CliUiProbe >/dev/null

  run_cli sprite create name=CliSprite position=2,1,0 color=#FF8A00FF >/dev/null
  run_cli component update name=CliSprite type=CliUiProbe >/dev/null

  toggle_json="$(json_cli ui toggle.set name=CliToggle isOn=true)"
  assert_contains "$toggle_json" "\"isOn\": true"

  slider_json="$(json_cli ui slider.set name=CliSlider value=0.75)"
  assert_contains "$slider_json" "\"value\": 0.75"

  scrollrect_json="$(json_cli ui scrollrect.set name=CliScroll normalizedPosition=0,0.35)"
  assert_contains "$scrollrect_json" "\"normalizedPosition\": ["

  inputfield_json="$(json_cli ui inputfield.set-text name=CliInput text=CliTextValue)"
  assert_contains "$inputfield_json" "CliTextValue"

  ui_hierarchy_json="$(json_cli resource get ui/hierarchy)"
  assert_contains "$ui_hierarchy_json" "\"name\": \"CliToggle\""
  assert_contains "$ui_hierarchy_json" "\"name\": \"CliSlider\""
  assert_contains "$ui_hierarchy_json" "\"name\": \"CliScroll\""
  assert_contains "$ui_hierarchy_json" "\"name\": \"CliInput\""

  run_cli console clear >/dev/null

  ui_double_click_json="$(json_cli ui double-click normalizedPosition=0.5,0.5)"
  assert_contains "$ui_double_click_json" "\"clickCount\": 2"

  ui_long_press_json="$(json_cli ui long-press normalizedPosition=0.5,0.5 durationMs=700)"
  assert_contains "$ui_long_press_json" "\"durationMs\": 700"

  ui_swipe_json="$(json_cli ui swipe normalizedFrom=0.5,0.5 normalizedTo=0.72,0.64)"
  assert_contains "$ui_swipe_json" "CliButton"

  input_double_tap_json="$(json_cli input double-tap worldPosition=2,1,0)"
  assert_contains "$input_double_tap_json" "\"clickCount\": 2"

  input_long_press_json="$(json_cli input long-press worldPosition=2,1,0 durationMs=700)"
  assert_contains "$input_long_press_json" "\"durationMs\": 700"

  input_swipe_json="$(json_cli input swipe worldFrom=2,1,0 worldTo=2.75,1,0)"
  assert_contains "$input_swipe_json" "CliSprite"

  console_json="$(json_cli console get)"
  assert_contains "$console_json" "CliUiProbe click CliButton count=2 clickCount=2"
  assert_contains "$console_json" "CliUiProbe up CliButton"
  assert_contains "$console_json" "heldMs="
  assert_contains "$console_json" "CliUiProbe click CliSprite count=2 clickCount=2"

  events_json="$(json_cli events tail after=0)"
  assert_contains "$events_json" "ui.double_clicked"
  assert_contains "$events_json" "ui.long_pressed"
  assert_contains "$events_json" "ui.swiped"
  assert_contains "$events_json" "input.double_tapped"
  assert_contains "$events_json" "input.long_pressed"
  assert_contains "$events_json" "input.swiped"

  run_cli scene create path=Assets/Scenes/CliCoverage.unity >/dev/null
  run_cli scene delete path="$ui_scene" >/dev/null
}

stage_tests() {
  run_cli --timeout-ms=60000 tests list mode=EditMode >/dev/null
  run_cli --timeout-ms=60000 tests list mode=PlayMode >/dev/null
  run_cli --timeout-ms=60000 tests run mode=EditMode >/dev/null
  run_cli --timeout-ms=240000 tests run mode=PlayMode >/dev/null
  run_cli workflow run "$ROOT_DIR/samples/workflows/smoke-test.json" >/dev/null
  run_cli workflow run "$ROOT_DIR/samples/workflows/ui-touch-smoke.json" >/dev/null
  run_cli batch run "$ROOT_DIR/samples/workflows/bootstrap.json" >/dev/null
}

stage_editor_lifecycle() {
  local play_error paused_state editor_state temp_scene unload_scene

  run_cli editor play >/dev/null
  play_error=""
  if ! play_error="$(run_cli scene create path=Assets/Scenes/ShouldFailInPlay.unity 2>&1)"; then
    true
  else
    printf 'Expected scene.create to fail during play mode.\n' >&2
    return 1
  fi

  assert_contains "$play_error" "This cannot be used during play mode"

  run_cli editor pause enabled=true >/dev/null
  paused_state="$(json_cli resource get editor/state)"
  assert_contains "$paused_state" "\"isPaused\": true"

  run_cli editor pause enabled=false >/dev/null
  run_cli editor stop >/dev/null

  editor_state="$(json_cli resource get editor/state)"
  assert_contains "$editor_state" "\"isPlaying\": false"

  temp_scene="Assets/Scenes/CliDeleteMe.unity"
  run_cli scene create path="$temp_scene" >/dev/null
  run_cli scene delete path="$temp_scene" >/dev/null

  unload_scene="Assets/Scenes/CliUnload.unity"
  run_cli scene create path="$unload_scene" >/dev/null
  run_cli scene unload >/dev/null
  run_cli scene create path=Assets/Scenes/CliCoverage.unity >/dev/null
  run_cli scene delete path="$unload_scene" >/dev/null
}

main() {
  require_tool "dotnet"
  require_tool "jq"
  parse_args "$@"
  ensure_cli

  local stage_name
  for stage_name in "${SELECTED_STAGES[@]}"; do
    case "$stage_name" in
      core)
        run_stage "core" "stage_core"
        ;;
      ui-input)
        run_stage "ui-input" "stage_ui_input"
        ;;
      tests)
        run_stage "tests" "stage_tests"
        ;;
      editor-lifecycle)
        run_stage "editor-lifecycle" "stage_editor_lifecycle"
        ;;
    esac
  done

  write_report
  log "Report written: $REPORT_PATH"

  if [[ "$OVERALL_STATUS" == "passed" ]]; then
    log "Editor verification completed successfully."
    return 0
  fi

  log "Editor verification completed with failures."
  return 1
}

main "$@"
