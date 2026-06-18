#!/bin/sh
set -eu

os=$(uname -s)
arch=$(uname -m)

case "$os:$arch" in
  Darwin:arm64) echo "osx-arm64" ;;
  Darwin:x86_64) echo "osx-x64" ;;
  Linux:aarch64) echo "linux-arm64" ;;
  Linux:arm64) echo "linux-arm64" ;;
  Linux:x86_64) echo "linux-x64" ;;
  *)
    echo "Unsupported runtime identifier for $os/$arch" >&2
    exit 1
    ;;
esac
