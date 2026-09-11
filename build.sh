#!/usr/bin/env bash
# Builds Mashed.exe with the Windows .NET SDK. F# has no in-box compiler the way
# C# does, so this needs the SDK - it is already installed (dotnet 10).
set -euo pipefail

SRC_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DOTNET="/mnt/c/Program Files/dotnet/dotnet.exe"
[ -x "$DOTNET" ] || { echo "dotnet.exe not found at $DOTNET" >&2; exit 1; }

# Where a per-user Windows app goes:
#   %LOCALAPPDATA%\Programs\<App>   the install - the convention VS Code and Teams use
#   %LOCALAPPDATA%\<App>            local data: build scratch here, the log at runtime
#   %APPDATA%\<App>                 roaming data: the config
# Both are resolved from Windows, so no username is baked into this script.
STAGE_WIN="$(cd /mnt/c && cmd.exe /c 'echo %LOCALAPPDATA%\Mashed Potato\build' 2>/dev/null | tr -d '\r')"
INSTALL_WIN="$(cd /mnt/c && cmd.exe /c 'echo %LOCALAPPDATA%\Programs\Mashed Potato' 2>/dev/null | tr -d '\r')"
STAGE_DIR="$(wslpath -u "$STAGE_WIN")"

# dotnet.exe must run from a real drive, not a \\wsl.localhost path, hence staging.
mkdir -p "$STAGE_DIR"
cp "$SRC_DIR"/*.fs "$SRC_DIR/MashedPotato.fsproj" "$SRC_DIR/mashedpotato.json" "$STAGE_DIR/"

cd "$STAGE_DIR"
"$DOTNET" publish MashedPotato.fsproj -c Release -o "$INSTALL_WIN" --nologo

echo
echo "Built: $INSTALL_WIN\\Mashed.exe"
echo "Run:   cmd.exe /c start \"\" \"$INSTALL_WIN\\Mashed.exe\""
