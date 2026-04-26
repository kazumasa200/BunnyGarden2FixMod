#!/usr/bin/env bash
# ConfigGen スモークテスト: input.yaml → 生成物が expected-output.cs と一致するか
set -euo pipefail
cd "$(dirname "$0")"

# Windows / Git Bash 環境で .NET 側に解釈させるため、cygpath があれば Windows パスに変換。
ACTUAL_POSIX=$(mktemp)
if command -v cygpath >/dev/null 2>&1; then
    ACTUAL=$(cygpath -w "$ACTUAL_POSIX")
else
    ACTUAL="$ACTUAL_POSIX"
fi
trap 'rm -f "$ACTUAL_POSIX"' EXIT

dotnet run --project ConfigGen.csproj -- \
    --yaml samples/input.yaml \
    --out "$ACTUAL"

if diff -u --strip-trailing-cr samples/expected-output.cs "$ACTUAL_POSIX"; then
    echo "[smoketest] PASS"
else
    echo "[smoketest] FAIL: output differs from expected"
    exit 1
fi
