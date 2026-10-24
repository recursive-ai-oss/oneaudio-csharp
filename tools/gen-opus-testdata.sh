#!/usr/bin/env bash
# Regenerates testdata/opus/*.opus with ffmpeg's libopus encoder (used as a black-box tool; nothing of it is in the repo).
# The signals are synthetic, so the files are ours. Usage: tools/gen-opus-testdata.sh
set -euo pipefail
out="$(dirname "$0")/../testdata/opus"
mkdir -p "$out"
sig='0.3*sin(2*PI*220*t)+0.2*sin(2*PI*660*t)*sin(2*PI*3*t)'
# Stereo music-style (CELT only), 20 ms frames.
ffmpeg -v error -y -f lavfi -i "aevalsrc=${sig}|0.25*sin(2*PI*1000*t):s=48000:d=1" \
  -c:a libopus -b:a 64k -application audio -frame_duration 20 "$out/celt-stereo.opus"
# Mono voice-style at 12 kbit/s (SILK only, wideband).
ffmpeg -v error -y -f lavfi -i "aevalsrc=${sig}:s=48000:d=1" \
  -c:a libopus -b:a 12k -application voip -cutoff 8000 -frame_duration 20 "$out/silk-mono.opus"
# Mono voice-style at 32 kbit/s, full band (hybrid).
ffmpeg -v error -y -f lavfi -i "aevalsrc=${sig}:s=48000:d=1" \
  -c:a libopus -b:a 32k -application voip -cutoff 20000 -frame_duration 20 "$out/hybrid-mono.opus"
