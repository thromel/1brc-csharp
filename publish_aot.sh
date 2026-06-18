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
RID=${1:-$("$SCRIPT_DIR/detect_rid.sh")}

"$DOTNET" publish "$PROJECT_DIR/OneBrc.CSharp.csproj" \
  -c Release \
  -r "$RID" \
  --self-contained true \
  -v minimal \
  -o "$PROJECT_DIR/publish/$RID"
