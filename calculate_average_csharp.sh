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

if [ -x "$NATIVE" ] && [ "$PROJECT_DIR/Program.cs" -ot "$NATIVE" ] && [ "$PROJECT_DIR/OneBrc.CSharp.csproj" -ot "$NATIVE" ]; then
  exec "$NATIVE" "$INPUT"
fi

if [ ! -f "$DLL" ] || [ "$PROJECT_DIR/Program.cs" -nt "$DLL" ] || [ "$PROJECT_DIR/OneBrc.CSharp.csproj" -nt "$DLL" ]; then
  "$DOTNET" build "$PROJECT_DIR/OneBrc.CSharp.csproj" -c Release -v q >/dev/null
fi

exec "$DOTNET" "$DLL" "$INPUT"
