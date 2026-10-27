#!/usr/bin/env bash
# Publishes the browser-wasm check app, serves it on a free local port and runs it in headless Chrome.
# Exit status 0 when every step reads zero bytes allocated by the render callback on the Mono runtime.
#   tools/wasm-alloc-check/run.sh
# Environment: CHROME (default google-chrome), WORK (scratch folder for the publish output and the browser profile).
# Needs the .NET SDK, Chrome or Chromium, and python3 (only for its static file server).
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
CHROME=${CHROME:-google-chrome}
WORK=${WORK:-$(mktemp -d)}
mkdir -p "$WORK"

dotnet publish "$here/OneAudio.WasmAllocCheck.csproj" -c Release -o "$WORK/app" -v quiet -clp:NoSummary
root="$WORK/app/wwwroot"
[ -d "$root" ] || root="$WORK/app"

port=$(python3 -c 'import socket; s=socket.socket(); s.bind(("127.0.0.1",0)); print(s.getsockname()[1]); s.close()')
rm -f "$WORK/result.txt"
python3 "$here/serve.py" "$root" "$port" "$WORK/result.txt" &
server=$!
"$CHROME" --headless=new --no-sandbox --disable-gpu --user-data-dir="$WORK/profile" "http://127.0.0.1:$port/index.html" >/dev/null 2>&1 &
browser=$!
trap 'kill "$browser" "$server" 2>/dev/null || true' EXIT

# The page posts its result when the run ends; give the interpreter up to ten minutes.
for _ in $(seq 1 600); do
  [ -s "$WORK/result.txt" ] && break
  sleep 1
done
out=$(cat "$WORK/result.txt" 2>/dev/null || true)
printf '%s\n' "$out"
if printf '%s\n' "$out" | grep -q '^VERDICT PASS'; then
  echo "wasm-alloc-check: ok"
else
  echo "wasm-alloc-check: FAILED" >&2
  exit 1
fi
