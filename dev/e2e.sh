#!/usr/bin/env bash
# Runs the end-to-end tests: builds the page and the server, then uses the page in a real browser
# at desktop and at phone size, against servers the tests start themselves. Each test has a
# server of its own on a free port of this computer, with an empty data folder that is removed
# afterwards; a pretend game sends it lines the way the iOS app does.
#
#   dev/e2e.sh [arguments for "playwright test"]
#
# It needs the .NET SDK, Node.js and ffmpeg. Set
#   VN_MINING_E2E_FFMPEG    ffmpeg, when it is not on the PATH (ffprobe is looked for beside it)
#   VN_MINING_E2E_CHROMIUM  a Chromium to use instead of Playwright's own
#   VN_MINING_E2E_WORKERS   how many tests run side by side (2 by default)
#   VN_MINING_E2E_KEEP=1    keeps each server's data folder and prints where it is
#
# The browser is Playwright's Chromium. Where it is not installed yet:
#   (cd web && npx playwright install chromium)
set -euo pipefail

cd "$(dirname "$0")/.."

if [ -z "${VN_MINING_E2E_FFMPEG:-}" ] && ! command -v ffmpeg >/dev/null; then
  echo "The tests make their voices and screenshots with ffmpeg. Install it, or set VN_MINING_E2E_FFMPEG to its path." >&2
  exit 1
fi

# The compiler's and the build's helper processes can outlive this script. They are switched
# off, so nothing of this run stays behind.
export UseSharedCompilation=false
export MSBUILDDISABLENODEREUSE=1

# The page first: the server carries it inside itself.
(cd web && { [ -d node_modules ] || npm ci; } && npm run build)
dotnet build VnMiningServer/VnMiningServer.csproj -c Release --nologo -v quiet

export VN_MINING_E2E_SERVER="${PWD}/VnMiningServer/bin/Release/net10.0/VnMiningServer.dll"
export VN_MINING_E2E_DOTNET="$(command -v dotnet)"

cd web
exec npx playwright test "$@"
