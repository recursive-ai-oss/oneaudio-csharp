#!/usr/bin/env python3
"""Writes the package icon (src/OneAudio/icon.png): a sine wave inside a rounded tile, 128x128, no dependencies."""
import math, struct, sys, zlib

N = 128
SS = 4  # supersampling per axis
BG = (24, 32, 48)
FG = (96, 200, 255)
AXIS = (60, 76, 104)

def inside_tile(x, y):
    r = 22.0
    cx = min(max(x, r), N - r)
    cy = min(max(y, r), N - r)
    return (x - cx) ** 2 + (y - cy) ** 2 <= r * r

def wave_dist(x, y):
    # Distance from (x, y) to a sine whose amplitude falls off toward the edges, sampled densely.
    best = 1e9
    for i in range(-40, 41):
        t = x + i * 0.25
        u = (t - 16) / (N - 32)
        if u < 0 or u > 1:
            continue
        amp = 34 * math.sin(math.pi * u) ** 0.7
        wy = N / 2 + amp * math.sin(2 * math.pi * 2.5 * u)
        best = min(best, math.hypot(t - x, wy - y))
    return best

rows = []
for py in range(N):
    row = bytearray([0])
    for px in range(N):
        acc = [0, 0, 0, 0]
        for sy in range(SS):
            for sx in range(SS):
                x = px + (sx + 0.5) / SS
                y = py + (sy + 0.5) / SS
                if not inside_tile(x, y):
                    continue
                col = BG
                if abs(y - N / 2) < 0.6 and 16 <= x <= N - 16:
                    col = AXIS
                if wave_dist(x, y) < 3.2:
                    col = FG
                acc[0] += col[0]; acc[1] += col[1]; acc[2] += col[2]; acc[3] += 255
        n = SS * SS
        a = acc[3] // n
        if acc[3]:
            cnt = acc[3] // 255
            row += bytes([acc[0] // cnt, acc[1] // cnt, acc[2] // cnt, a])
        else:
            row += bytes([0, 0, 0, 0])
    rows.append(bytes(row))

def chunk(tag, data):
    c = struct.pack('>I', len(data)) + tag + data
    return c + struct.pack('>I', zlib.crc32(tag + data) & 0xFFFFFFFF)

png = b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', N, N, 8, 6, 0, 0, 0)) \
    + chunk(b'IDAT', zlib.compress(b''.join(rows), 9)) + chunk(b'IEND', b'')
open(sys.argv[1] if len(sys.argv) > 1 else 'src/OneAudio/icon.png', 'wb').write(png)
