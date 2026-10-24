#!/usr/bin/env bash
# Reproducible build. Inputs NOT in git (game-derived or third-party binaries):
#   interop/      <- tools/InteropGen run against YOUR OWN copy of GameAssembly.dll + global-metadata.dat
#   third_party/  <- official BepInEx be.788 zip, unpacked to third_party/bepinex-be788 (sha256 in manifest/supported-builds.json)
# Other locations: INTEROP=<dir> BEPINEX_CORE=<dir>/BepInEx/core ./build.sh
# Directory.Build.props makes this the public flavour (no self-test harness, no bundled Unity libraries).
set -euo pipefail
cd "$(dirname "$0")"
export DOTNET_ROLL_FORWARD=Major
python3 -c "import json;json.load(open('manifest/supported-builds.json'))"
ARGS=()
[ -n "${INTEROP:-}" ] && ARGS+=(-p:Interop="$INTEROP")
[ -n "${BEPINEX_CORE:-}" ] && ARGS+=(-p:BepInExCore="$BEPINEX_CORE")
dotnet build src/OthLan.Plugin -c Release "${ARGS[@]}"
dotnet run --project tests/LogicTests "${ARGS[@]}"
dotnet publish src/OthLan.Installer -c Release -o out/installer "${ARGS[@]}"
dotnet publish src/OthLan.Launch -c Release -o out/launch "${ARGS[@]}"
sha256sum src/OthLan.Plugin/bin/Release/net6.0/OthLan.dll out/installer/OTH-LAN-Setup.exe out/launch/OTH-LAN-Launch.exe | tee out/SHA256SUMS.txt
