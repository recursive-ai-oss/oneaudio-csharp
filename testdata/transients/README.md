# Transient test files

Short bursts (sine bursts and broadband clicks) followed by, or preceded by, digital silence. The signals are synthesised by
`tools/gen-transient-testdata.py` and encoded with ffmpeg's libvorbis, libopus and flac encoders, run as black-box tools; the
files are our own synthetic material, licensed MIT OR Apache-2.0 like the rest of the repository (© Recursive AI, LLC).

File names read `<codec>_<kind>_<channels>ch_<rate>_<burst start frame>_<burst length>_<quality>`. Each file is half a second long.
Regenerate with `tools/gen-transient-testdata.py` (needs ffmpeg with libvorbis and libopus, and flac, on the PATH).
