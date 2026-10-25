#!/usr/bin/env python3
"""Regenerates the short-transient-then-silence files in testdata/transients/.

Each signal is synthesised here (a windowed sine burst or a broadband click, followed by digital silence, or silence
followed by a burst that ends exactly at the end of the file), written as 16-bit PCM and encoded with ffmpeg's
libvorbis, libopus and flac encoders, used as black-box tools. Nothing of those tools is in the repo; the signals
are ours (MIT OR Apache-2.0, like the rest of the repository).

File names carry what the tests check: <kind>_<channels>ch_<rate>_<start>_<length>[_q<q>|_<bitrate>k].<ext>
where the burst occupies frames [start, start + length). Usage: tools/gen-transient-testdata.py
"""
import math
import os
import struct
import subprocess
import sys
import tempfile

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "testdata", "transients")
DURATION = 0.5  # seconds of signal in every file
AMP = 0.8

# (kind, channels, rate, start (frames, or -1 = so the burst ends at the last frame), length, Vorbis qualities)
VORBIS = [
    ("sine2k", 1, 48000, 0, 200, [-1, 0, 4, 10]),   # the shape that was found failing: a click at sample 0
    ("sine1k", 1, 44100, 0, 1000, [0, 4]),
    ("click", 2, 44100, 0, 16, [-1, 4, 10]),
    ("sine1k", 2, 48000, -1, 500, [0, 4]),          # burst at the very end
    ("sine2k", 1, 48000, -1, 300, [4]),
    ("click", 1, 44100, 11025, 64, [4]),            # burst in the middle of silence
]
# Opus is always 48 kHz here: (kind, channels, start, length, bitrates in kbit/s)
OPUS = [
    ("sine2k", 1, 0, 200, [24, 64]),
    ("click", 2, 0, 16, [64]),
    ("sine1k", 2, -1, 500, [32]),
    ("click", 1, 12000, 64, [24]),
]
FLAC = [
    ("sine2k", 1, 48000, 0, 200),
    ("click", 2, 44100, 0, 16),
    ("sine1k", 2, 48000, -1, 500),
    ("click", 1, 44100, 11025, 64),
]


def burst(kind, i, length, rng):
    if kind == "click":
        # Deterministic broadband noise with a fast decay (linear congruential generator).
        rng[0] = (rng[0] * 1103515245 + 12345) & 0x7FFFFFFF
        return ((rng[0] / 0x3FFFFFFF) - 1.0) * math.exp(-4.0 * i / length)
    f = 2000.0 if kind == "sine2k" else 1000.0
    fade = min(1.0, (length - i) / 16.0)  # a short fade-out so the burst does not end on a step
    return fade * math.sin(2 * math.pi * f * (i + 0.5) / RATE[0])


RATE = [48000]


def write_wav(path, kind, ch, rate, start, length):
    RATE[0] = rate
    frames = int(rate * DURATION)
    if start < 0:
        start = frames - length
    rng = [12345]
    data = bytearray()
    for n in range(frames):
        v = 0.0
        if start <= n < start + length:
            v = AMP * burst(kind, n - start, length, rng)
        s = max(-32768, min(32767, int(round(v * 32767))))
        data += struct.pack("<h", s) * ch
    with open(path, "wb") as f:
        f.write(b"RIFF" + struct.pack("<I", 36 + len(data)) + b"WAVEfmt ")
        f.write(struct.pack("<IHHIIHH", 16, 1, ch, rate, rate * ch * 2, ch * 2, 16))
        f.write(b"data" + struct.pack("<I", len(data)) + bytes(data))
    return start


def ffmpeg(*args):
    subprocess.run(["ffmpeg", "-v", "error", "-y", "-bitexact", *args], check=True)


def main():
    os.makedirs(OUT, exist_ok=True)
    with tempfile.TemporaryDirectory(dir=os.environ.get("TMPDIR")) as tmp:
        for kind, ch, rate, start, length, qs in VORBIS:
            wav = os.path.join(tmp, "in.wav")
            s = write_wav(wav, kind, ch, rate, start, length)
            for q in qs:
                name = f"{kind}_{ch}ch_{rate}_{s}_{length}_q{q}.ogg".replace("_q-", "_qm")
                ffmpeg("-i", wav, "-c:a", "libvorbis", "-q:a", str(q), "-fflags", "+bitexact", os.path.join(OUT, "vorbis_" + name))
        for kind, ch, start, length, rates in OPUS:
            wav = os.path.join(tmp, "in.wav")
            s = write_wav(wav, kind, ch, 48000, start, length)
            for k in rates:
                ffmpeg("-i", wav, "-c:a", "libopus", "-b:a", f"{k}k", "-application", "audio", "-frame_duration", "20",
                       "-fflags", "+bitexact", os.path.join(OUT, f"opus_{kind}_{ch}ch_48000_{s}_{length}_{k}k.opus"))
        for kind, ch, rate, start, length in FLAC:
            wav = os.path.join(tmp, "in.wav")
            s = write_wav(wav, kind, ch, rate, start, length)
            subprocess.run(["flac", "-s", "-f", "-8", "-o", os.path.join(OUT, f"flac_{kind}_{ch}ch_{rate}_{s}_{length}.flac"), wav], check=True)


if __name__ == "__main__":
    sys.exit(main())
