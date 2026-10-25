#!/usr/bin/env python3
"""
Writes small, deterministic FLAC fixtures by encoding generated signals with the reference `flac` encoder
(black box; the version used is printed). Usage: tools/gen_flac_seeds.py [flac-binary] [out-dir]
The signals are sums of sines with a little seeded noise, so the encoder picks real predictors and residual coding.
"""
import math, random, struct, subprocess, sys, pathlib, tempfile

flac = sys.argv[1] if len(sys.argv) > 1 else "flac"
out = pathlib.Path(sys.argv[2] if len(sys.argv) > 2 else "testdata/flac")
out.mkdir(parents=True, exist_ok=True)
print(subprocess.run([flac, "--version"], capture_output=True, text=True).stdout.strip())

def wav(ch, rate, bits, frames):
    data = bytearray()
    for f in frames:
        for v in f:
            data += struct.pack("<i", v)[: bits // 8] if bits != 8 else struct.pack("<B", (v + 128) & 255)
    ext = ch > 2
    fmt = struct.pack("<HHIIHH", 0xFFFE if ext else 1, ch, rate, rate * ch * bits // 8, ch * bits // 8, bits)
    if ext:  # WAVE_FORMAT_EXTENSIBLE, PCM sub-format, default speaker layout
        fmt += struct.pack("<HHI", 22, bits, {3: 7, 4: 0x33, 6: 0x3F}[ch]) + struct.pack("<H", 1) + bytes.fromhex("000000001000800000aa00389b71")
    body = b"WAVE" + b"fmt " + struct.pack("<I", len(fmt)) + fmt + b"data" + struct.pack("<I", len(data)) + bytes(data)
    return b"RIFF" + struct.pack("<I", len(body)) + body

def signal(n, ch, bits, seed):
    rnd = random.Random(seed)
    peak = (1 << (bits - 1)) - 1
    frames = []
    for i in range(n):
        row = []
        for c in range(ch):
            v = 0.45 * math.sin(2 * math.pi * 330 * (c + 1) * i / 22050) + 0.3 * math.sin(2 * math.pi * 1250 * i / 22050 + c)
            v += rnd.uniform(-0.02, 0.02)
            row.append(int(v * peak))
        frames.append(row)
    return frames

cases = [
    # name, channels, rate, bits, frames, extra encoder arguments
    ("stereo16_seektable", 2, 22050, 16, 66000, ["-S", "12x", "-b", "4096"]),
    ("stereo16_noseektable", 2, 22050, 16, 66000, ["--no-seektable", "-b", "4096"]),
    ("mono24_lpc", 1, 22050, 24, 40000, ["--no-seektable", "-b", "1152", "-l", "12", "-e"]),
    ("mono8", 1, 22050, 8, 30000, ["--no-seektable", "-b", "576"]),
    ("stereo16_midside_small", 2, 22050, 16, 9000, ["--no-seektable", "-b", "192", "-m"]),
    ("six16", 6, 22050, 16, 12000, ["--no-seektable", "-b", "1024"]),
]
for name, ch, rate, bits, n, extra in cases:
    with tempfile.TemporaryDirectory() as t:
        w = pathlib.Path(t) / (name + ".wav")
        w.write_bytes(wav(ch, rate, bits, signal(n, ch, bits, sum(map(ord, name)))))
        subprocess.run([flac, "-s", "-f", "--no-padding", *extra, "-o", str(out / (name + ".flac")), str(w)], check=True)
    print(name, (out / (name + ".flac")).stat().st_size)
