#!/usr/bin/env bash

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CLI_DLL="${CLI_DLL:-$ROOT_DIR/src/UnityCli/bin/Debug/net10.0/UnityCli.dll}"
CLI_PROJECT="$ROOT_DIR/src/UnityCli/UnityCli.csproj"
DEFAULT_TIMEOUT_MS="${DEFAULT_TIMEOUT_MS:-120000}"

log() {
  printf '\n==> %s\n' "$*" >&2
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

ensure_cli

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

run_cli ui canvas.create name=CliCanvas >/dev/null
run_cli ui button.create canvasName=CliCanvas name=CliButton text=TapMe anchoredPosition=0,0 size=220,80 >/dev/null
run_cli ui text.create canvasName=CliCanvas name=CliText text=Coverage anchoredPosition=0,120 size=280,48 >/dev/null
run_cli ui image.create canvasName=CliCanvas name=CliImage anchoredPosition=0,-120 size=96,96 color=#66CCFFFF >/dev/null
run_cli component update name=CliButton type=CliUiProbe >/dev/null

run_cli resource get editor/state >/dev/null
run_cli resource get scene/active >/dev/null
run_cli resource get scene/hierarchy >/dev/null
run_cli resource get ui/hierarchy >/dev/null
run_cli resource get console/logs >/dev/null
run_cli resource get tests/catalog >/dev/null
run_cli resource get packages/list >/dev/null

ui_click_json="$(json_cli ui click normalizedPosition=0.5,0.5)"
assert_contains "$ui_click_json" "CliButton"
ui_drag_json="$(json_cli ui drag normalizedFrom=0.5,0.5 normalizedTo=0.72,0.64)"
assert_contains "$ui_drag_json" "CliButton"
input_tap_json="$(json_cli input tap worldPosition=2,1,0)"
assert_contains "$input_tap_json" "CliSprite"
input_drag_json="$(json_cli input drag worldFrom=2,1,0 worldTo=2.75,1,0)"
assert_contains "$input_drag_json" "CliSprite"

events_json="$(json_cli events tail after=0)"
assert_contains "$events_json" "ui.clicked"
assert_contains "$events_json" "input.dragged"

run_cli gameobject delete id="$duplicate_id" >/dev/null
run_cli menu execute path=Assets/Refresh >/dev/null

run_cli --timeout-ms=60000 tests list mode=EditMode >/dev/null
run_cli --timeout-ms=60000 tests list mode=PlayMode >/dev/null
run_cli --timeout-ms=60000 tests run mode=EditMode >/dev/null
run_cli --timeout-ms=240000 tests run mode=PlayMode >/dev/null

run_cli workflow run "$ROOT_DIR/samples/workflows/smoke-test.json" >/dev/null
run_cli workflow run "$ROOT_DIR/samples/workflows/ui-touch-smoke.json" >/dev/null
run_cli batch run "$ROOT_DIR/samples/workflows/bootstrap.json" >/dev/null

run_cli editor play >/dev/null
play_error=""
if ! play_error="$(run_cli scene create path=Assets/Scenes/ShouldFailInPlay.unity 2>&1)"; then
  true
else
  printf 'Expected scene.create to fail during play mode.\n' >&2
  exit 1
fi
assert_contains "$play_error" "This cannot be used during play mode"
run_cli editor pause enabled=true >/dev/null
paused_state="$(json_cli resource get editor/state)"
assert_contains "$paused_state" "\"isPaused\": true"
run_cli editor pause enabled=false >/dev/null
run_cli editor stop >/dev/null

temp_scene="Assets/Scenes/CliDeleteMe.unity"
run_cli scene create path="$temp_scene" >/dev/null
run_cli scene delete path="$temp_scene" >/dev/null

unload_scene="Assets/Scenes/CliUnload.unity"
run_cli scene create path="$unload_scene" >/dev/null
run_cli scene unload >/dev/null
run_cli scene create path=Assets/Scenes/CliCoverage.unity >/dev/null
run_cli scene delete path="$unload_scene" >/dev/null

log "Editor verification completed successfully."
