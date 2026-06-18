#!/bin/sh
set -eu

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
PROJECT_DIR="$SCRIPT_DIR/OneBrc.CSharp"
if [ "${DOTNET+x}" = "" ]; then
  LOCAL_DOTNET="$SCRIPT_DIR/../work/.dotnet/dotnet"
  if [ -x "$LOCAL_DOTNET" ]; then
    DOTNET="$LOCAL_DOTNET"
  else
    DOTNET=dotnet
  fi
fi
INPUT=${1:-measurements.txt}
DLL="$PROJECT_DIR/bin/Release/net10.0/OneBrc.CSharp.dll"
RID=$("$SCRIPT_DIR/detect_rid.sh")
NATIVE="$PROJECT_DIR/publish/$RID/OneBrc.CSharp"

sources_newer_than() {
  target=$1
  [ -n "$(find "$PROJECT_DIR" \
    -path "$PROJECT_DIR/bin" -prune -o \
    -path "$PROJECT_DIR/obj" -prune -o \
    -name '*.cs' -newer "$target" -print -quit)" ]
}

if [ -x "$NATIVE" ] && ! sources_newer_than "$NATIVE" && [ "$PROJECT_DIR/OneBrc.CSharp.csproj" -ot "$NATIVE" ]; then
  exec "$NATIVE" "$INPUT"
fi

if [ ! -f "$DLL" ] || sources_newer_than "$DLL" || [ "$PROJECT_DIR/OneBrc.CSharp.csproj" -nt "$DLL" ]; then
  "$DOTNET" build "$PROJECT_DIR/OneBrc.CSharp.csproj" -c Release -v q >/dev/null
fi

exec "$DOTNET" "$DLL" "$INPUT"
