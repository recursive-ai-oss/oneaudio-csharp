#!/usr/bin/env bash
# Builds, instruments and runs a SharpFuzz target under libFuzzer, with a time budget.
#
#   tools/fuzz.sh setup                       install the tools (once per machine)
#   tools/fuzz.sh run <target> [seconds] [-- <libFuzzer flags>]
#   tools/fuzz.sh replay <target> <file>...   run saved inputs through the plain (uninstrumented) build
#
# <target> is the suffix of a project under fuzz/ (fuzz/OneAudio.Fuzz.<Target>), for example wav, ogg, vorbis, mp3, mp4, adts, aac, amr, amrnb, amrwb.
# Environment:
#   ONEAUDIO_FUZZ_HOME  tools and per-target work directories (default ~/.cache/oneaudio-fuzz)
#   ONEAUDIO_CORPUS     fetched corpus (tools/fetch-corpus.sh) used to seed the Ogg-based targets
#   FUZZ_JOBS           parallel libFuzzer workers (default 1)
#   FUZZ_MAX_LEN        longest input in bytes (default 262144)
# Needs the .NET SDK on PATH, curl and sha256sum. No compiler: libfuzzer-dotnet comes as a pinned release binary.
# Crashing, hanging or leaking inputs land in $ONEAUDIO_FUZZ_HOME/work/<target>/crashes; copy minimized repros
# into testdata/ with a regression test.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
home="${ONEAUDIO_FUZZ_HOME:-$HOME/.cache/oneaudio-fuzz}"
tools="$home/tools"
driver="$tools/libfuzzer-dotnet"
sharpfuzz="$tools/sharpfuzz"

# Pinned third-party tooling (fuzzing drivers only; nothing here is shipped).
DRIVER_URL="https://github.com/Metalnem/libfuzzer-dotnet/releases/download/v2025.05.02.0904/libfuzzer-dotnet-ubuntu"
DRIVER_SHA256="c2c2a90d94c409a4af339a0d4f244e0442c5a5d249be0f1252ba07871f285958"
SHARPFUZZ_CLI_VERSION="2.3.0"

cmd="${1:-}"; shift || true

setup() {
  mkdir -p "$tools"
  if [ ! -x "$driver" ]; then
    curl -fL --retry 3 -o "$driver.part" "$DRIVER_URL"
    echo "$DRIVER_SHA256  $driver.part" | sha256sum -c - >/dev/null || { echo "libfuzzer-dotnet: SHA-256 mismatch" >&2; rm -f "$driver.part"; exit 1; }
    chmod +x "$driver.part"; mv "$driver.part" "$driver"
  fi
  if [ ! -x "$sharpfuzz" ]; then
    dotnet tool install SharpFuzz.CommandLine --version "$SHARPFUZZ_CLI_VERSION" --tool-path "$tools" >/dev/null
  fi
  echo "tools ready in $tools"
}

project_for() {
  local name
  name="$(ls "$here/fuzz" | grep -i -x "OneAudio.Fuzz.$1" || true)"
  [ -n "$name" ] || { echo "no fuzz project for target '$1' (see fuzz/)" >&2; exit 2; }
  echo "$here/fuzz/$name/$name.csproj"
}

# Copies seed inputs into the working corpus. Small hand-made inputs from testdata/ go to every target;
# fetched files go to the targets that parse them.
seed() {
  local target="$1" corpus="$2" work="$3"
  mkdir -p "$corpus"
  # Earlier crashing inputs (already fixed) stay in the corpus as regression seeds.
  if [ -d "$here/testdata/fuzz/$target" ]; then cp -n "$here/testdata/fuzz/$target"/* "$corpus/"; fi
  case "$target" in
    wav)         find "$here/testdata" -type f \( -iname '*.wav' -o -iname '*.wave' \) -size -64k -exec cp -n {} "$corpus/" \; ;;
    ogg|vorbis)  find "$here/testdata" -type f -iname '*.ogg' -size -1024k -exec cp -n {} "$corpus/" \; 2>/dev/null || true
                 if [ -n "${ONEAUDIO_CORPUS:-}" ]; then
                   # Only the first bytes matter for header and setup coverage; whole files make runs slow.
                   find "$ONEAUDIO_CORPUS" -type f -iname '*.ogg' -size -2048k | while read -r f; do
                     head -c 65536 "$f" > "$corpus/$(basename "$f" .ogg).seed"
                   done
                 fi ;;
    mp3)         find "$here/testdata" -type f -iname '*.mp3' -size -1024k -exec cp -n {} "$corpus/" \; 2>/dev/null || true
                 if [ -n "${ONEAUDIO_CORPUS:-}" ]; then
                   # The ISO/IEC 11172-4 streams (all block types, stereo modes, free format) and the encoder output.
                   find "$ONEAUDIO_CORPUS" -type f \( -path '*mp3-iso-*' -iname '*.bit' -o -path '*mp3-gen*' -iname '*.mp3' \) -size -2048k | while read -r f; do
                     head -c 65536 "$f" > "$corpus/$(basename "$f").seed"
                   done
                 fi ;;
    flac)        # The first byte picks the stream type (bit 1: no seeking); native and Ogg-wrapped inputs are sniffed.
                 find "$here/testdata" -type f \( -iname '*.flac' -o -iname '*.oga' \) -size -1024k | while read -r f; do
                   { printf '\000'; cat "$f"; } > "$corpus/$(basename "${f%.*}").seed"
                   { printf '\002'; head -c 40000 "$f"; } > "$corpus/$(basename "${f%.*}")-fwd.seed"
                 done
                 if [ -n "${ONEAUDIO_CORPUS:-}" ] && [ -d "$ONEAUDIO_CORPUS/flac-cellar" ]; then
                   find "$ONEAUDIO_CORPUS/flac-cellar" -type f -iname '*.flac' | while read -r f; do
                     { printf '\000'; head -c 65536 "$f"; } > "$corpus/cellar-$(basename "$f" .flac | tr -c 'A-Za-z0-9' _).seed"
                   done
                 fi ;;
    opus)        # The first byte picks the mode; odd means "the rest is an Ogg Opus file".
                 find "$here/testdata" -type f -iname '*.opus' -size -1024k | while read -r f; do
                   { printf '\001'; head -c 65536 "$f"; } > "$corpus/$(basename "$f" .opus).seed"
                 done
                 head -c 600 /dev/urandom | { printf '\000'; cat; } > "$corpus/random-packets.seed" ;;
    mp4|adts|amr) # The first byte picks the stream type (bit 0: forward-only); the rest is the file.
                 local pat
                 case "$target" in mp4) pat='*.m4a *.mp4 *.3gp';; adts) pat='*.aac';; amr) pat='*.amr *.awb';; esac
                 for ext in $pat; do
                   find "$here/testdata" ${ONEAUDIO_CORPUS:+"$ONEAUDIO_CORPUS"} -type f -iname "$ext" -size -256k 2>/dev/null | while read -r f; do
                     { printf '\000'; cat "$f"; } > "$corpus/$(basename "${f%.*}").seed"
                     { printf '\001'; cat "$f"; } > "$corpus/$(basename "${f%.*}")-fwd.seed"
                   done
                 done ;;
    aac)         # Real AAC files turned into the target's input format (config bytes, then length-prefixed access units).
                 for ext in '*.m4a' '*.mp4' '*.aac'; do
                   find "$here/testdata" ${ONEAUDIO_CORPUS:+"$ONEAUDIO_CORPUS/aac-iso-lc/mp4"} -type f -iname "$ext" -size -2048k 2>/dev/null | head -n 120 | while read -r f; do
                     dotnet "$work/bin-plain/OneAudio.Fuzz.Aac.dll" --seed "$f" "$corpus/$(basename "${f%.*}").seed" 2>/dev/null || true
                   done
                 done ;;
    amrnb|amrwb) # Raw runs of storage-format frames: the AMR files without their magic line.
                 local ext=amr; [ "$target" = amrwb ] && ext=awb
                 find "$here/testdata" -type f -iname "*.$ext" -size -256k | while read -r f; do
                   tail -c +$(( $(head -n 1 "$f" | wc -c) + 1 )) "$f" > "$corpus/$(basename "${f%.*}").seed"
                 done ;;
  esac
  if [ -z "$(ls -A "$corpus")" ]; then printf 'RIFF' > "$corpus/empty.seed"; fi
}

build() {
  local target="$1" work="$2" proj
  proj="$(project_for "$target")"
  rm -rf "$work/bin" "$work/bin-plain"
  # Plain build for replays, instrumented copy for the fuzzer.
  dotnet publish "$proj" -c Release -o "$work/bin-plain" -v quiet -clp:NoSummary >/dev/null
  cp -r "$work/bin-plain" "$work/bin"
  "$sharpfuzz" "$work/bin/OneAudio.dll" >/dev/null
}

case "$cmd" in
  setup) setup ;;
  run)
    target="${1:?usage: fuzz.sh run <target> [seconds] [-- libFuzzer flags]}"; shift
    seconds=600
    if [ $# -gt 0 ] && [ "$1" != "--" ]; then seconds="$1"; shift; fi
    [ "${1:-}" = "--" ] && shift
    setup
    work="$home/work/$target"
    mkdir -p "$work/crashes"
    build "$target" "$work"
    seed "$target" "$work/corpus" "$work"
    exe="$work/bin/$(basename "$(project_for "$target")" .csproj)"
    jobs="${FUZZ_JOBS:-1}"
    echo "fuzzing $target for ${seconds}s, $jobs worker(s); corpus $work/corpus, crashes $work/crashes"
    # -timeout turns a hang into a saved input; -rss_limit_mb 0 because the .NET child process, not the driver, owns memory.
    # libFuzzer writes fuzz-<n>.log for parallel workers into the working directory, so each target gets its own.
    rm -f "$work"/fuzz-*.log
    ( cd "$work" && "$driver" --target_path="$exe" \
      -max_total_time="$seconds" -timeout=20 -rss_limit_mb=0 -max_len="${FUZZ_MAX_LEN:-262144}" \
      -artifact_prefix="$work/crashes/" -jobs="$jobs" -workers="$jobs" \
      "$@" "$work/corpus" ) > "$work/last-run.log" 2>&1 || true
    # Summary: the final status line and run count of every worker (also when there is only one).
    for log in "$work"/fuzz-*.log; do [ -f "$log" ] || continue; echo "-- $(basename "$log")"; grep -E '^#[0-9]+' "$log" | tail -n 1; grep -E '^Done|ERROR|SUMMARY' "$log" | head -n 3; done
    echo "crashes:"; ls -1 "$work/crashes" | sed 's/^/  /'
    ;;
  replay)
    target="${1:?usage: fuzz.sh replay <target> <file>...}"; shift
    work="$home/work/$target"
    mkdir -p "$work"
    [ -d "$work/bin-plain" ] || build "$target" "$work"
    "$work/bin-plain/$(basename "$(project_for "$target")" .csproj)" "$@"
    ;;
  *) sed -n '2,19p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 2 ;;
esac
