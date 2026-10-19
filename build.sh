#!/bin/bash
# Compile, teste et empaquette Reco+. Utilisable tel quel dans l'image mcr.microsoft.com/dotnet/sdk:10.0
# ou sur une machine avec le SDK .NET 10 (c'est ce que fait la CI GitHub).
set -euo pipefail
cd "$(dirname "$0")"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 HOME="${HOME:-/tmp}"
VERSION=$(grep -oP '"version":\s*"\K[^"]+' meta.json)
CSPROJ_VERSION=$(grep -oP '<Version>\K[^<]+' src/Jellyfin.Plugin.RecoPlus/Jellyfin.Plugin.RecoPlus.csproj)
if [ "$VERSION" != "$CSPROJ_VERSION" ]; then
  echo "Versions incohérentes : meta.json=$VERSION, csproj=$CSPROJ_VERSION" >&2
  exit 1
fi
echo "== Reco+ $VERSION — SDK $(dotnet --version)"
echo "== tests"
dotnet test tests/RecoPlus.Tests -c Release --nologo -v q 2>&1 | tail -25
echo "== compilation du plugin"
dotnet build src/Jellyfin.Plugin.RecoPlus -c Release --nologo -v q 2>&1 | tail -5
OUT="out/RecoPlus_$VERSION"
rm -rf out && mkdir -p "$OUT"
BIN=src/Jellyfin.Plugin.RecoPlus/bin/Release/net10.0
cp "$BIN/Jellyfin.Plugin.RecoPlus.dll" "$BIN/RecoPlus.Engine.dll" "$OUT/"
cp meta.json "$OUT/"
# garde-fou : aucune DLL de Jellyfin ni d'autre dépendance ne doit partir avec le plugin
EXTRA=$(ls "$BIN"/*.dll | grep -v -e '/Jellyfin.Plugin.RecoPlus.dll$' -e '/RecoPlus.Engine.dll$' || true)
[ -z "$EXTRA" ] || { echo "DLL inattendues dans la sortie : $EXTRA" >&2; exit 1; }
echo "== paquet"
ls -l "$OUT"
(cd out && sha256sum "RecoPlus_$VERSION"/*)
