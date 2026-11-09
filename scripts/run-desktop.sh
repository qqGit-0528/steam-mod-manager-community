#!/usr/bin/env bash
set -euo pipefail
ck3_project_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
if command -v dotnet >/dev/null 2>&1; then
    ck3_dotnet="$(command -v dotnet)"
else
    ck3_dotnet="${HOME}/.local/share/ck3-mod-manager/dotnet/dotnet"
fi
if [[ ! -x "$ck3_dotnet" ]]; then
    echo "请先安装 .NET 10 SDK：https://dotnet.microsoft.com/download/dotnet/10.0" >&2
    exit 1
fi
exec "$ck3_dotnet" run --project "$ck3_project_root/src/Ck3ModManager.Desktop/Ck3ModManager.Desktop.csproj" "$@"
