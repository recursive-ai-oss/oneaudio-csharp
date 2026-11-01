# HE-AAC signalling samples

Five tiny M4A files, each a plain AAC-LC stream (synthetic tone, 22,050 Hz; mono except `sbrps-explicit.m4a`, which is stereo) whose AudioSpecificConfig was rewritten to signal
HE-AAC in a different way. They carry no SBR payload, so they test the signalling only; the decoder expands the core to the signalled rate and channel count. They were written with ffmpeg 7.0.2 and MP4Box (GPAC 26.07) and are licensed MIT OR Apache-2.0 like the rest of the repository.

| File | AudioSpecificConfig | Signalling |
|---|---|---|
| `sbr-implied.m4a` | `13 88` | none in the config (plain AAC-LC) |
| `sbr-compatible.m4a` | `13 88 56 e5 a0` | backward-compatible SBR extension (sync 0x2b7) |
| `sbr-explicit.m4a` | `2b 8a 08` | explicit SBR (object type 5), no trailing extensionFlag |
| `sbrps-explicit.m4a` | `eb 92 08` | explicit PS (object type 29, channelConfiguration 2) over frames that carry a stereo core (CPE): decodes as SBR stereo, no parametric stereo applied |
| `sbrps-mono-core.m4a` | `eb 92 08` | the same config over the mono frames of `sbr-explicit.m4a` (SCE): parametric stereo expands them to two channels |
