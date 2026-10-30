#!/usr/bin/env bash
# Prepare the GitHub release asset 6000.0.75.zip (same bytes BepInEx downloads from unity.bepinex.dev).
# Copy into the public release folder and list it in SHA256SUMS.txt next to OTH-LAN-*.zip.
# Usage: ./tools/pack-unity-libs.sh [outdir]
set -euo pipefail
cd "$(dirname "$0")/.."
SRC="third_party/unity-libs-link/6000.0.75.zip"
ALT="third_party/unity-libs-6000.0.75.zip"
OUT="${1:-out/unity-libs}"
mkdir -p "$OUT"
if [[ -f "$ALT" ]]; then src="$ALT"
elif [[ -f "$SRC" ]]; then src="$SRC"
else echo "missing $SRC (or $ALT)"; exit 1; fi
cp -f "$src" "$OUT/6000.0.75.zip"
( cd "$OUT" && sha256sum 6000.0.75.zip )
echo "Upload $OUT/6000.0.75.zip as a GitHub release asset (name must be exactly 6000.0.75.zip)."
echo "Add its sha256 line to SHA256SUMS.txt (must match manifest unityLibsSha256)."
