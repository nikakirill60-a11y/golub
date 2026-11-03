#!/bin/sh
# Renders the Godot reference frames of the combat effects
# (Tools/godot_combat_fx_render.gd) with the Mobile renderer.
# The script is copied into the Godot project for the run and removed after;
# user:// is redirected so the game's autoloads do not touch real saves.
set -u
GODOT_ROOT="${BLITZ_GODOT_ROOT:-/home/smh01/projects/blitz-legacy-godot}"
OUT="${BLITZ_COMBAT_FX_RENDER_OUT:-/tmp/blitz_combat_fx_render}"
HERE="$(cd "$(dirname "$0")" && pwd)"
TMP_SCRIPT="_combat_fx_render_tmp.gd"

cp "$HERE/godot_combat_fx_render.gd" "$GODOT_ROOT/$TMP_SCRIPT"
trap 'rm -f "$GODOT_ROOT/$TMP_SCRIPT" "$GODOT_ROOT/$TMP_SCRIPT.uid"' EXIT
rm -rf "$OUT"
cd "$GODOT_ROOT"
env XDG_DATA_HOME=/tmp/godot_fx_userdata BLITZ_COMBAT_FX_RENDER_OUT="$OUT" DISPLAY="${DISPLAY:-:0}" \
  timeout 300 "${GODOT:-godot}" --path . --rendering-method mobile --fixed-fps 60 \
  --resolution 960x540 --script "res://$TMP_SCRIPT"
rc=$?
echo "[combat-fx-render] godot exit $rc, $(ls "$OUT" 2>/dev/null | wc -l) frames in $OUT"
exit $rc
