#!/usr/bin/env python3
"""Writes synthetic AMR storage-format files (RFC 4867 section 5) for container tests.

The frames carry deterministic pseudo-random payload bytes of the right size, not speech: a container reader only
needs the table of frame sizes, and no AMR encoder is needed. The mix includes SID and NO_DATA frames.
Usage: gen-amr-testdata.py <out-dir>
"""
import os
import sys

# Speech payload bytes per frame type, excluding the one-byte table of contents (3GPP TS 26.101 table A.1b,
# TS 26.201 table 2); None marks reserved types.
NB = [12, 13, 15, 17, 19, 20, 26, 31, 5, None, None, None, None, None, None, 0]
WB = [17, 23, 32, 36, 40, 46, 50, 58, 60, 5, None, None, None, None, 0, 0]


def lcg(seed):
    state = seed
    while True:
        state = (state * 1103515245 + 12345) & 0x7FFFFFFF
        yield (state >> 16) & 0xFF


def frames(table, count, seed):
    rnd = lcg(seed)
    out = bytearray()
    valid = [i for i, n in enumerate(table) if n is not None]
    for i in range(count):
        ft = valid[next(rnd) % len(valid)] if i % 7 else valid[i // 7 % len(valid)]
        q = 1 if next(rnd) & 3 else 0
        out.append(((ft & 15) << 3) | (q << 2))  # F=0, FT, Q, padding
        out += bytes(next(rnd) for _ in range(table[ft]))
    return bytes(out)


def main():
    out = sys.argv[1]
    os.makedirs(out, exist_ok=True)
    with open(os.path.join(out, "synthetic-nb.amr"), "wb") as f:
        f.write(b"#!AMR\n" + frames(NB, 150, 1))
    with open(os.path.join(out, "synthetic-wb.awb"), "wb") as f:
        f.write(b"#!AMR-WB\n" + frames(WB, 150, 2))


main()
