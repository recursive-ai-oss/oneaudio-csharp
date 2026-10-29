#!/usr/bin/env bash
# Downloads third-party test material into a cache directory outside the repository and verifies SHA-256.
# Nothing fetched here is ever committed. Usage: tools/fetch-corpus.sh [cache-dir] [entry-name ...]
# The cache directory defaults to $ONEAUDIO_CORPUS, then ../.oneaudio-corpus next to the checkout.
# Each entry is: name|url|sha256|archive-type (tgz, tgz1, zip or raw). Add new corpora as new lines.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cache="${1:-${ONEAUDIO_CORPUS:-$here/../.oneaudio-corpus}}"
shift || true
wanted=("$@")

ENTRIES=(
  # IETF CELLAR FLAC decoder test files (CC0), pinned by commit; tgz1 strips the archive's top directory.
  "flac-cellar|https://github.com/ietf-wg-cellar/flac-test-files/archive/aa7b0c6cf32994c106ae517a08134c28a96ff5b2.tar.gz|36de2310155b4084011fbd56f24603dfef91a26c5433e3f92bd21995b45089c3|tgz1"
  "opus-rfc8251|https://opus-codec.org/static/testvectors/opus_testvectors-rfc8251.tar.gz|6b26a22f9ba87b2b836906a9bb7afec5f8e54d49553b1200382520ee6fedfa55|tgz"
  # Xiph Vorbis test vectors, one directory per file under the cache.
  "vorbis-xiph-1.0.1-test|https://people.xiph.org/~xiphmont/test-vectors/vorbis/1.0.1-test.ogg|8c9423e00826d6d2457d78c09d6e2a94bcdf9216af796bb22a4c4f450c2e72bb|raw"
  "vorbis-xiph-1.0-test|https://people.xiph.org/~xiphmont/test-vectors/vorbis/1.0-test.ogg|9a882710314bcc1d2b4cdefb7f89911f4375acd1feb9e64a12eca9f7202377d3|raw"
  "vorbis-xiph-48k-mono|https://people.xiph.org/~xiphmont/test-vectors/vorbis/48k-mono.ogg|f51459d9bdd04ca3ec6f6732b8a01efcdc83e5c6fc79c7d5347527bfd4948e9e|raw"
  "vorbis-xiph-beta3-test|https://people.xiph.org/~xiphmont/test-vectors/vorbis/beta3-test.ogg|7fc791a4d5a0d3b7cef2448093ccf2ae54600acc81667be90c6b209c366c32ea|raw"
  "vorbis-xiph-beta4-test|https://people.xiph.org/~xiphmont/test-vectors/vorbis/beta4-test.ogg|bc367c0d4dcdbf1f0a2f81abf74cc52c8f0ab8366d150a75830782ca2dba080a|raw"
  "vorbis-xiph-bimS-silence|https://people.xiph.org/~xiphmont/test-vectors/vorbis/bimS-silence.ogg|e2a38871d390ed651faf0fec5253a6873530da4f2503d85021c99325dfd23813|raw"
  "vorbis-xiph-chain-test1|https://people.xiph.org/~xiphmont/test-vectors/vorbis/chain-test1.ogg|d9c37533a1f456d2a996755a43d112ef46b6ef953319913907bc79f1c014d79e|raw"
  "vorbis-xiph-chain-test2|https://people.xiph.org/~xiphmont/test-vectors/vorbis/chain-test2.ogg|5b5bf834e93e9a93b7114be084161e972f33d2031963518492ba2c91dc4418a7|raw"
  "vorbis-xiph-chain-test3|https://people.xiph.org/~xiphmont/test-vectors/vorbis/chain-test3.ogg|ed039ba775d1b31e805d26d6413a3ef2c6663bf4c5ea47ce1462f8f23c84d8dc|raw"
  "vorbis-xiph-combustion|https://people.xiph.org/~xiphmont/test-vectors/vorbis/combustion.ogg|5f58888fd5061c1202a4a93ae13a13f73a5c096a62fee17918a87c6627dabfd8|raw"
  "vorbis-xiph-highrate-test|https://people.xiph.org/~xiphmont/test-vectors/vorbis/highrate-test.ogg|0942c88369f84b125388cc8437575b66d67bc97baaeeb997b6602144821509df|raw"
  "vorbis-xiph-lsp-test2|https://people.xiph.org/~xiphmont/test-vectors/vorbis/lsp-test2.ogg|7b00f893a93071bdf243b8a22c1758a6ab09c1ab8eee89ca21659d98ec0f53ea|raw"
  "vorbis-xiph-lsp-test3|https://people.xiph.org/~xiphmont/test-vectors/vorbis/lsp-test3.ogg|7a5c4064fc31285f6fea0b2424a0d415a8b18ef13d1aa1ef931a429ab9a593d1|raw"
  "vorbis-xiph-lsp-test4|https://people.xiph.org/~xiphmont/test-vectors/vorbis/lsp-test4.ogg|cb0b28931dfc8ef8b2d320f3028fab548171e7c6e94006f0c23532478a8d3596|raw"
  "vorbis-xiph-lsp-test|https://people.xiph.org/~xiphmont/test-vectors/vorbis/lsp-test.ogg|ad1b07b68576ae2c85178475ef3607e1a96c09e96296cae83ee7a9726f8311eb|raw"
  "vorbis-xiph-make|https://people.xiph.org/~xiphmont/test-vectors/vorbis/make.ogg|28d14448873bc08aa2d57ead5ef4705ed3f00a730a377e2f637dc85233ec17b7|raw"
  "vorbis-xiph-mono|https://people.xiph.org/~xiphmont/test-vectors/vorbis/mono.ogg|d8abca95445a07186c9a158d4f573f38918985dd2498a9bc8d1811bd72fe1d1a|raw"
  "vorbis-xiph-moog|https://people.xiph.org/~xiphmont/test-vectors/vorbis/moog.ogg|bd5b51bb1d6855e0e990e3ebdd230fc16265407809bd6db44aea691ada498943|raw"
  "vorbis-xiph-one-entry-codebook-test|https://people.xiph.org/~xiphmont/test-vectors/vorbis/one-entry-codebook-test.ogg|789b5146f2a7c0864a228ee4a870606a32c4169e22097be65129623705c54749|raw"
  "vorbis-xiph-out-of-spec-blocksize|https://people.xiph.org/~xiphmont/test-vectors/vorbis/out-of-spec-blocksize.ogg|0970e66291744815f2ca0dec7523f5bbc112907c5c75978a0f91d3b0ed6f03b2|raw"
  "vorbis-xiph-rc1-test|https://people.xiph.org/~xiphmont/test-vectors/vorbis/rc1-test.ogg|ccfac6ac7c75615bec0632b94bcf088c027c0b112cd136c997d04f037252cd3d|raw"
  "vorbis-xiph-rc2-test2|https://people.xiph.org/~xiphmont/test-vectors/vorbis/rc2-test2.ogg|24ade471eefe1c7642f73a6810e56266b34c2d7d30a3b5ad05a34c098128226c|raw"
  "vorbis-xiph-rc2-test|https://people.xiph.org/~xiphmont/test-vectors/vorbis/rc2-test.ogg|4adcda786dfeea4188d7b1df35571dbe29c687f2d1cac680ed115353abfe1637|raw"
  "vorbis-xiph-rc3-test|https://people.xiph.org/~xiphmont/test-vectors/vorbis/rc3-test.ogg|edd984e84c7c2a59af7801f3e8d2db11a6619134fd8d0274d1289e70bf60cde7|raw"
  "vorbis-xiph-singlemap-test|https://people.xiph.org/~xiphmont/test-vectors/vorbis/singlemap-test.ogg|50d8077608a4192b8f8505aec0217be8b6c25def4068899afc19c17f30e1d521|raw"
  "vorbis-xiph-sleepzor|https://people.xiph.org/~xiphmont/test-vectors/vorbis/sleepzor.ogg|01c67ecaf7a58b5ac5f1fe3bd060b5d61536595e97927a1b1cf0129a62b5cfcf|raw"
  "vorbis-xiph-test-short2|https://people.xiph.org/~xiphmont/test-vectors/vorbis/test-short2.ogg|6bd3f59e0fa77904edd35c73dadd6558e2a36d78c9d7bc5db223862bf092fcc2|raw"
  "vorbis-xiph-test-short|https://people.xiph.org/~xiphmont/test-vectors/vorbis/test-short.ogg|183510552403021fb90ce43796fcc88e16c8bb4ae5d0d72a316b8e51afc395fa|raw"
  "vorbis-xiph-unused-mode-test|https://people.xiph.org/~xiphmont/test-vectors/vorbis/unused-mode-test.ogg|e27ae6fc2f7c0037c28328801c46d966abcee3050e3ebd40bf097f7986f50f94|raw"
  # ISO/IEC 11172-4 Layer III compliance streams and their reference decodes, as mirrored by the FFmpeg FATE sample suite (data files only).
  "mp3-iso-compl-bit|https://fate-suite.ffmpeg.org/mp3-conformance/compl.bit|3b93f3675821a5af4826abcff7097fa2e42efcc46154e02c992995f7e31dbc8e|raw"
  "mp3-iso-compl-pcm|https://fate-suite.ffmpeg.org/mp3-conformance/compl.pcm|b33610eba44b3c1c0eb2dbc90bd55aba8c57e82c71b133266780d7182e2d994e|raw"
  "mp3-iso-he_32khz-bit|https://fate-suite.ffmpeg.org/mp3-conformance/he_32khz.bit|965065cdcac3f45a61a9f54b4f94d59d0334f4b5cb74840a5aec7d405e2e050f|raw"
  "mp3-iso-he_32khz-pcm|https://fate-suite.ffmpeg.org/mp3-conformance/he_32khz.pcm|61c5a85e3057905e47402bb85b36fadeba23be9a04b8090e42670eab621acf08|raw"
  "mp3-iso-he_44khz-bit|https://fate-suite.ffmpeg.org/mp3-conformance/he_44khz.bit|a64212281a8896d90e00f06c9b21f2c8f21ef9cb7367cae692a92ea421984441|raw"
  "mp3-iso-he_44khz-pcm|https://fate-suite.ffmpeg.org/mp3-conformance/he_44khz.pcm|fa9a4f4ab1e4dd8b2d08211d8c20a35c08237a0615172e8572d4b43645a9db2c|raw"
  "mp3-iso-he_48khz-bit|https://fate-suite.ffmpeg.org/mp3-conformance/he_48khz.bit|dd0383075710570648b8961f3cd0ce692f07a679b265fc4997e50883b3958299|raw"
  "mp3-iso-he_48khz-pcm|https://fate-suite.ffmpeg.org/mp3-conformance/he_48khz.pcm|61c5a85e3057905e47402bb85b36fadeba23be9a04b8090e42670eab621acf08|raw"
  "mp3-iso-he_free-bit|https://fate-suite.ffmpeg.org/mp3-conformance/he_free.bit|b55afc2a21492c4b3035d423f36fe87560ef34f65eb997da60af9b01f300362c|raw"
  "mp3-iso-he_free-pcm|https://fate-suite.ffmpeg.org/mp3-conformance/he_free.pcm|9e89d9481c68261e070e575174c85739b630553ddc9f7feaf1c61605fb15b3d2|raw"
  "mp3-iso-he_mode-bit|https://fate-suite.ffmpeg.org/mp3-conformance/he_mode.bit|fb90396d9bb13311be35284d061e19196830d213d823e05297b18172c88d90ce|raw"
  "mp3-iso-he_mode-pcm|https://fate-suite.ffmpeg.org/mp3-conformance/he_mode.pcm|524abbe4b47988df2ecd9a2943532b9528cf3732a0376bafd14fea82967c34e5|raw"
  "mp3-iso-hecommon-bit|https://fate-suite.ffmpeg.org/mp3-conformance/hecommon.bit|21999b77716aeba047c7f55c2c1ce17b54c780b6dc007912f251b7ea189676ef|raw"
  "mp3-iso-hecommon-pcm|https://fate-suite.ffmpeg.org/mp3-conformance/hecommon.pcm|5af4952d4451dc977b1344f836975e7545a66e38425de1764f3c28994cc38397|raw"
  "mp3-iso-si-bit|https://fate-suite.ffmpeg.org/mp3-conformance/si.bit|4f3382de1d9265013d42249ff108023d4a90ecf3c0136489d199243a90a5d0f9|raw"
  "mp3-iso-si-pcm|https://fate-suite.ffmpeg.org/mp3-conformance/si.pcm|331c1e57fd6161ee63eac62650337d5f73cbf70dcdd76e50a11acb8016d8669d|raw"
  "mp3-iso-si_block-bit|https://fate-suite.ffmpeg.org/mp3-conformance/si_block.bit|1d12956840638bf6b81164fc510c3e8269eeb038a51dc4548469e21a71e52b69|raw"
  "mp3-iso-si_block-pcm|https://fate-suite.ffmpeg.org/mp3-conformance/si_block.pcm|7ed181c7dad40e7033d73303131345ac57b914fab9eb1ae8c00a263aa9b0b7cb|raw"
  "mp3-iso-si_huff-bit|https://fate-suite.ffmpeg.org/mp3-conformance/si_huff.bit|fda6d8a7d4e1df3cb17d3560b57013ef98abbf9c60d940455c2e3ebf63eae0ef|raw"
  "mp3-iso-si_huff-pcm|https://fate-suite.ffmpeg.org/mp3-conformance/si_huff.pcm|468cd5d833fa3e017300128d3a80126412e6d77bdbdcca9e97abace6752cc0be|raw"
  "mp3-iso-sin1k0db-bit|https://fate-suite.ffmpeg.org/mp3-conformance/sin1k0db.bit|64775e23c29aa84e27f68777460f88e0717af2ed64e732d287a20c5a92c9fa3e|raw"
  "mp3-iso-sin1k0db-pcm|https://fate-suite.ffmpeg.org/mp3-conformance/sin1k0db.pcm|4a31caa4ec395b4f4c8efbb1b7fe739f7390a83b78320d078ef53bcb84939e63|raw"
  # 3GPP digital test sequences for the AMR (TS 26.074) and AMR-WB (TS 26.174) speech decoders. zipr unzips nested archives too.
  # 3GPP's CDN refuses curl's default user agent, hence the -A below.
  "amr-nb-ts26074|https://www.3gpp.org/ftp/Specs/archive/26_series/26.074/26074-j00.zip|e4d8771a807023738d7648e374913f0337c1c92e688bd5f4b572d44c5231e881|zipr"
  "amr-wb-ts26174|https://www.3gpp.org/ftp/Specs/archive/26_series/26.174/26174-j00.zip|bb2eb62db39263bdf047b41d4a3693ad3785b559c29e116043a2cb4f082d908d|zipr"
  # EBU loudness test set v05 (Tech 3341 / 3342 minimum-requirement signals). The EBU site refuses some networks (HTTP 403 from its CDN),
  # so this pins the Internet Archive's copy of https://tech.ebu.ch/docs/testmaterial/ebu-loudness-test-setv05.zip; the SHA-256 is that of the EBU file.
  "ebu-loudness-v05|https://web.archive.org/web/20170331172519id_/https://tech.ebu.ch/docs/testmaterial/ebu-loudness-test-setv05.zip|9cc500b4df83f7c21855c74dce795ef5209a752bf884253ae57d0ce512efb062|zip"
)

mkdir -p "$cache/_dl"
# macOS ships shasum rather than sha256sum.
check_sha() { if command -v sha256sum >/dev/null; then sha256sum -c -; else shasum -a 256 -c -; fi; }

for entry in "${ENTRIES[@]}"; do
  IFS='|' read -r name url sha kind <<<"$entry"
  if [ ${#wanted[@]} -gt 0 ] && [[ ! " ${wanted[*]} " == *" $name "* ]]; then continue; fi
  dest="$cache/$name"
  if [ -e "$dest/.verified" ] && [ "$(cat "$dest/.verified")" = "$sha" ]; then echo "$name: up to date"; continue; fi
  file="$cache/_dl/$(basename "$url")"
  [ -f "$file" ] || curl -fL --retry 3 -A "oneaudio-fetch/1.0" -o "$file" "$url"
  echo "$sha  $file" | check_sha >/dev/null || { echo "$name: SHA-256 mismatch for $file" >&2; rm -f "$file"; exit 1; }
  rm -rf "$dest"; mkdir -p "$dest"
  case "$kind" in
    tgz) tar -xzf "$file" -C "$dest" ;;
    tgz1) tar -xzf "$file" -C "$dest" --strip-components=1 ;;
    zip) unzip -q "$file" -d "$dest" ;;
    zipr)
      unzip -q "$file" -d "$dest"
      # Unpack archives inside archives (each into a directory named after it) until none are left.
      while [ -n "$(find "$dest" -name '*.zip' -print -quit)" ]; do
        find "$dest" -name '*.zip' | while read -r z; do unzip -q -o "$z" -d "${z%.zip}" && rm "$z"; done
      done ;;
    raw) cp "$file" "$dest/" ;;
    *) echo "unknown archive type $kind" >&2; exit 1 ;;
  esac
  echo "$sha" > "$dest/.verified"
  echo "$name: fetched"
done
