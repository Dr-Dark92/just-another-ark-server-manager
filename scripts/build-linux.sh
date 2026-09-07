#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ARCH="$(uname -m)"
case "$ARCH" in
  x86_64) RID="linux-x64" ;;
  aarch64|arm64) RID="linux-arm64" ;;
  *) echo "Unsupported architecture: $ARCH" >&2; exit 1 ;;
esac
OUT="$ROOT/dist/$RID"
dotnet restore "$ROOT/JAASM.slnx"
dotnet publish "$ROOT/src/JAASM.App/JAASM.App.csproj" -c Release -r "$RID" --self-contained true -p:PublishSingleFile=true -o "$OUT"
echo "JAASM Linux build: $OUT"
