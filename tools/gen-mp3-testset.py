#!/usr/bin/env python3
"""Generates the wider MP3 test set: deterministic synthetic signals encoded by the LAME command-line encoder
(and ffmpeg's libmp3lame) as black boxes. Nothing here is committed; the output goes to the cache directory.

Usage: tools/gen-mp3-testset.py <output-dir>
Needs `lame` and, for the ffmpeg cases, `ffmpeg` on PATH. Reruns skip files that exist.
"""
import math
import os
import random
import struct
import subprocess
import sys
import wave

out = sys.argv[1]
os.makedirs(out, exist_ok=True)


def signal(rate, channels, seconds, seed):
    """Tones, a sweep, white noise bursts and clicks: tonal and transient material so long and short blocks both occur."""
    rng = random.Random(seed)
    n = int(rate * seconds)
    frames = []
    for i in range(n):
        t = i / rate
        base = 0.25 * math.sin(2 * math.pi * 440 * t) + 0.15 * math.sin(2 * math.pi * 1234.5 * t)
        sweep = 0.15 * math.sin(2 * math.pi * (200 * t + 600 * t * t))
        burst = rng.uniform(-0.4, 0.4) if (t % 0.7) < 0.05 else 0.0
        click = 0.8 if i % (rate // 3) < 2 else 0.0
        fs = min(0.45 * rate, 7000)
        hi = 0.1 * math.sin(2 * math.pi * min(fs, rate * 0.4) * t)
        left = base + sweep + burst + click + hi
        right = 0.8 * base - 0.5 * sweep + burst * 0.5 + click * 0.5 + 0.7 * hi
        frames.append((left, right))
    samples = []
    for left, right in frames:
        for v in ((left,) if channels == 1 else (left, right)):
            samples.append(int(max(-1.0, min(1.0, v)) * 30000))
    return samples


def write_wav(path, rate, channels, samples):
    with wave.open(path, "wb") as w:
        w.setnchannels(channels)
        w.setsampwidth(2)
        w.setframerate(rate)
        w.writeframes(struct.pack("<%dh" % len(samples), *samples))


def run(cmd):
    subprocess.run(cmd, check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


rates = [8000, 11025, 12000, 16000, 22050, 24000, 32000, 44100, 48000]
for rate in rates:
    for channels in (1, 2):
        wav = os.path.join(out, f"src_{rate}_{channels}.wav")
        if not os.path.exists(wav):
            write_wav(wav, rate, channels, signal(rate, channels, 2.5, rate + channels))
        mpeg1 = rate >= 32000
        br = "128" if mpeg1 else "64"
        modes = [("cbr", ["-b", br]), ("vbr", ["-V", "4"]), ("abr", ["--abr", br])]
        if channels == 2:
            modes += [("joint", ["-m", "j", "-b", br]), ("stereo", ["-m", "s", "-b", br])]
        # -t leaves out the Xing/LAME header, so those files have no gapless information.
        modes += [("noshort", ["-b", br, "--noshort"]), ("notag", ["-b", br, "-t"])]
        for name, opts in modes:
            mp3 = os.path.join(out, f"lame_{rate}_{channels}_{name}.mp3")
            if os.path.exists(mp3):
                continue
            cmd = ["lame", "--quiet", "-q", "2", *opts, "-S", wav, mp3]
            if rate in (8000, 11025, 12000):
                cmd = ["lame", "--quiet", "-q", "2", *opts, "--resample", str(rate / 1000), "-S", wav, mp3]
            try:
                run(cmd)
            except subprocess.CalledProcessError:
                print("skipped", mp3)
        if channels == 2 and mpeg1:
            ff = os.path.join(out, f"lame_{rate}_{channels}_freeformat.mp3")
            if not os.path.exists(ff):
                try:
                    run(["lame", "--quiet", "--freeformat", "-b", "128", "-S", wav, ff])
                except subprocess.CalledProcessError:
                    print("skipped", ff)

# ffmpeg's libmp3lame writes its own Xing/Info header ("Lavc" encoder string).
for rate, channels in ((44100, 2), (22050, 1)):
    wav = os.path.join(out, f"src_{rate}_{channels}.wav")
    mp3 = os.path.join(out, f"ffmpeg_{rate}_{channels}.mp3")
    if not os.path.exists(mp3):
        run(["ffmpeg", "-y", "-loglevel", "error", "-i", wav, "-c:a", "libmp3lame", "-b:a", "96k", mp3])
print("done", len(os.listdir(out)), "files")
