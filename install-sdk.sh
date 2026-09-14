#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")"
sdk_script=$(mktemp "${TMPDIR:-/tmp}/player-service-sdk.XXXXXX")
trap 'rm -f -- "$sdk_script"' EXIT
curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$sdk_script"
bash "$sdk_script" --version 10.0.401 --install-dir "$PWD/.dotnet" --no-path
./.dotnet/dotnet --version
