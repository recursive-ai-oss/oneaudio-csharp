# Format detection regressions

Two files that older releases opened as MP3 ("No MPEG audio frame found"), because the MP3 sniffer, which accepts a
frame-header-shaped pattern anywhere in the first bytes, was asked before the MP4 and AMR formats.

| File | Size | Made with |
|---|---|---|
| `voice.amr` | 9,638 B | `ffmpeg -i voice.wav -ar 8000 -ac 1 -c:a libopencore_amrnb -b:a 12.2k voice.amr` (ffmpeg 7.0.2) |
| `voice.3gp` | 10,250 B | the same command with a `.3gp` output |

`voice.wav` is a synthetic speech-like signal (16 kHz mono, 6.000 s, a periodic 0.5 s envelope, peak 0.30, energy around 720 Hz),
not a recording, with no third-party material. Both files are AMR-NB 12.2 kbit/s at 8 kHz mono, 6 s long. Licensed MIT OR Apache-2.0 like
the rest of the repository; the encoder's own identification strings inside them are not third-party content.
