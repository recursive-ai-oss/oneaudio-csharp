#!/usr/bin/env python3
"""Appends an iTunes-style gapless tag (a '----' freeform atom named iTunSMPB, mean com.apple.iTunes) to the
moov/udta/meta/ilst of an MP4 whose moov box comes after mdat, so no chunk offsets move.
Usage: add-itunsmpb.py <file.m4a> '<iTunSMPB text>'"""
import struct
import sys


def box(kind, payload):
    return struct.pack('>I4s', 8 + len(payload), kind) + payload


def find(d, o, e, kind):
    while o + 8 <= e:
        size, k = struct.unpack('>I4s', d[o:o + 8])
        if k == kind:
            return o, size
        o += size
    raise SystemExit('box %r not found' % kind)


path, text = sys.argv[1], sys.argv[2].encode('ascii')
d = bytearray(open(path, 'rb').read())
moov, msz = find(d, 0, len(d), b'moov')
assert moov + msz == len(d), 'moov must be last'
udta, usz = find(d, moov + 8, moov + msz, b'udta')
meta, tsz = find(d, udta + 8, udta + usz, b'meta')
ilst, isz = find(d, meta + 12, meta + tsz, b'ilst')
assert ilst + isz == meta + tsz and meta + tsz == udta + usz and udta + usz == moov + msz
mean = box(b'mean', b'\0\0\0\0' + b'com.apple.iTunes')
name = box(b'name', b'\0\0\0\0' + b'iTunSMPB')
data = box(b'data', struct.pack('>II', 1, 0) + text)
add = box(b'----', mean + name + data)
d += add
for off in (ilst, meta, udta, moov):
    size = struct.unpack('>I', d[off:off + 4])[0] + len(add)
    d[off:off + 4] = struct.pack('>I', size)
open(path, 'wb').write(d)
