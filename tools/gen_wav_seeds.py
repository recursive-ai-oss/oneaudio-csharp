#!/usr/bin/env python3
"""Writes small, deterministic WAV files used as fuzzer seeds and parser fixtures."""
import math, struct, sys, pathlib

out = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else "testdata/wav")
out.mkdir(parents=True, exist_ok=True)

def wav(tag, ch, rate, bits, data, extensible=False, mask=0):
    fmt = struct.pack("<HHIIHH", 0xFFFE if extensible else tag, ch, rate, rate * ch * bits // 8, ch * bits // 8, bits)
    if extensible:
        fmt += struct.pack("<HHI", 22, bits, mask) + struct.pack("<H", tag) + bytes.fromhex("000000001000800000aa00389b71")
    body = b"WAVE" + b"fmt " + struct.pack("<I", len(fmt)) + fmt + b"data" + struct.pack("<I", len(data)) + data
    return b"RIFF" + struct.pack("<I", len(body)) + body

def sine(n, ch, amp):
    return [[amp * math.sin(2 * math.pi * 440 * i / 8000 + c) for c in range(ch)] for i in range(n)]

s = sine(64, 2, 1.0)
(out / "pcm16_stereo.wav").write_bytes(wav(1, 2, 8000, 16, b"".join(struct.pack("<h", int(v * 30000)) for f in s for v in f)))
(out / "pcm24_ext_stereo.wav").write_bytes(wav(1, 2, 8000, 24, b"".join(struct.pack("<i", int(v * 8000000))[:3] for f in s for v in f), True, 3))
(out / "float32_mono.wav").write_bytes(wav(3, 1, 8000, 32, b"".join(struct.pack("<f", f[0]) for f in sine(64, 1, 0.5))))
(out / "pcm8_mono.wav").write_bytes(wav(1, 1, 8000, 8, bytes(int(128 + 100 * f[0]) for f in sine(64, 1, 1))))
