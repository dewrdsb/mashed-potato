#!/usr/bin/env bash
# Runs the tests with the Windows .NET SDK, and fails if coverage drops below the
# threshold in tests/MashedPotato.Tests.fsproj. Staged the same way build.sh is,
# for the same reason: dotnet.exe must run from a real drive, not \\wsl.localhost.
set -euo pipefail

SRC_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DOTNET="/mnt/c/Program Files/dotnet/dotnet.exe"
[ -x "$DOTNET" ] || { echo "dotnet.exe not found at $DOTNET" >&2; exit 1; }

STAGE_WIN="$(cd /mnt/c && cmd.exe /c 'echo %LOCALAPPDATA%\Mashed Potato\test' 2>/dev/null | tr -d '\r')"
STAGE_DIR="$(wslpath -u "$STAGE_WIN")"

mkdir -p "$STAGE_DIR/tests"
cp "$SRC_DIR"/*.fs "$SRC_DIR/MashedPotato.fsproj" "$SRC_DIR/mashedpotato.json" \
   "$SRC_DIR/mashed.ico" "$STAGE_DIR/"
cp "$SRC_DIR"/tests/*.fs "$SRC_DIR/tests/MashedPotato.Tests.fsproj" "$STAGE_DIR/tests/"

cd "$STAGE_DIR"
status=0
"$DOTNET" test tests/MashedPotato.Tests.fsproj --nologo "$@" || status=$?

# The report comes back beside the sources, for an editor or a coverage viewer to
# read. tests/TestResults/ is ignored by git.
if [ -f tests/TestResults/coverage.cobertura.xml ]; then
    mkdir -p "$SRC_DIR/tests/TestResults"
    cp tests/TestResults/coverage.cobertura.xml "$SRC_DIR/tests/TestResults/"
fi

exit $status
