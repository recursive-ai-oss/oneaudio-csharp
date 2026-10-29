#!/usr/bin/env bash
# Regenerates testdata/containers/* with ffmpeg used as a black-box muxer/encoder. The signals are synthetic, so the
# files are ours. AMR has no encoder here: tools/gen-amr-testdata.py writes frames of the right sizes and ffmpeg only
# re-wraps them into 3GP (-c copy). Usage: tools/gen-container-testdata.sh
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
out="$here/../testdata/containers"
mkdir -p "$out"
sig='0.3*sin(2*PI*220*t)+0.2*sin(2*PI*660*t)*sin(2*PI*3*t)'
src=(-f lavfi -i "aevalsrc=${sig}|0.25*sin(2*PI*1000*t):s=44100:d=1.5")
mono=(-f lavfi -i "aevalsrc=${sig}:s=16000:d=1.5")
ff() { ffmpeg -v error -y -bitexact "$@"; }

# AAC-LC in MP4 with the movie box last (the muxer's default) and first (faststart).
ff "${src[@]}" -c:a aac -b:a 64k "$out/aac-moov-last.m4a"
ff "${src[@]}" -c:a aac -b:a 64k -movflags +faststart "$out/aac-moov-first.m4a"
ff "${src[@]}" -c:a aac -b:a 64k -f mp4 "$out/aac-stereo.mp4"
ff "${mono[@]}" -c:a aac -b:a 24k "$out/aac-mono.3gp"
ff "${src[@]}" -c:a aac -b:a 64k -f adts "$out/aac.aac"
# Two audio tracks (stereo 44.1 kHz and mono 16 kHz AAC), and an ALAC track ahead of an AAC one / alone.
ff -i "$out/aac-stereo.mp4" -i "$out/aac-mono.3gp" -map 0:a -map 1:a -c copy "$out/aac-two-tracks.mp4"
ff "${mono[@]}" -c:a alac "$out/alac-only.m4a"
ff "${mono[@]}" -f lavfi -i "aevalsrc=${sig}:s=16000:d=1.5" -map 0:a -map 1:a -c:a:0 alac -c:a:1 aac -b:a:1 24k "$out/alac-then-aac.mp4"
# Fragmented MP4: recognised and rejected with a typed error.
ff "${src[@]}" -c:a aac -b:a 64k -movflags +frag_keyframe+empty_moov "$out/aac-fragmented.mp4"
# iTunes-style gapless m4a: the ipod muxer, then an iTunSMPB freeform atom added to ilst (delay 2112, padding 472,
# 65000 frames) the way iTunes writes it.
ff "${src[@]}" -c:a aac -b:a 64k -f ipod -metadata title="Gapless test" -metadata artist="Test Artist" "$out/aac-itunes-gapless.m4a"
python3 "$here/add-itunsmpb.py" "$out/aac-itunes-gapless.m4a" \
  " 00000000 00000840 000001D8 000000000000FDE8 00000000 00000000 00000000 00000000 00000000 00000000 00000000 00000000"

# AMR: storage-format files, then the same frames in 3GP.
python3 "$here/gen-amr-testdata.py" "$out"
ff -i "$out/synthetic-nb.amr" -c:a copy "$out/amrnb.3gp"
ff -i "$out/synthetic-wb.awb" -c:a copy "$out/amrwb.3gp"
