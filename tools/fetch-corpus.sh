#!/usr/bin/env bash
# Downloads third-party test material into a cache directory outside the repository and verifies SHA-256.
# Nothing fetched here is ever committed. Usage: tools/fetch-corpus.sh [cache-dir] [entry-name ...]
# The cache directory defaults to $ONEAUDIO_CORPUS, then ../.oneaudio-corpus next to the checkout.
# Each entry is: name|url|sha256|archive-type (tgz, zip or raw). Add new corpora as new lines.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cache="${1:-${ONEAUDIO_CORPUS:-$here/../.oneaudio-corpus}}"
shift || true
wanted=("$@")

ENTRIES=(
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
  # EBU loudness test set v05 (Tech 3341 / 3342 minimum-requirement signals). The EBU site refuses some networks (HTTP 403 from its CDN),
  # so this pins the Internet Archive's copy of https://tech.ebu.ch/docs/testmaterial/ebu-loudness-test-setv05.zip; the SHA-256 is that of the EBU file.
  "ebu-loudness-v05|https://web.archive.org/web/20170331172519id_/https://tech.ebu.ch/docs/testmaterial/ebu-loudness-test-setv05.zip|9cc500b4df83f7c21855c74dce795ef5209a752bf884253ae57d0ce512efb062|zip"
)

mkdir -p "$cache/_dl"
for entry in "${ENTRIES[@]}"; do
  IFS='|' read -r name url sha kind <<<"$entry"
  if [ ${#wanted[@]} -gt 0 ] && [[ ! " ${wanted[*]} " == *" $name "* ]]; then continue; fi
  dest="$cache/$name"
  if [ -e "$dest/.verified" ] && [ "$(cat "$dest/.verified")" = "$sha" ]; then echo "$name: up to date"; continue; fi
  file="$cache/_dl/$(basename "$url")"
  [ -f "$file" ] || curl -fL --retry 3 -o "$file" "$url"
  echo "$sha  $file" | sha256sum -c - >/dev/null || { echo "$name: SHA-256 mismatch for $file" >&2; rm -f "$file"; exit 1; }
  rm -rf "$dest"; mkdir -p "$dest"
  case "$kind" in
    tgz) tar -xzf "$file" -C "$dest" ;;
    zip) unzip -q "$file" -d "$dest" ;;
    raw) cp "$file" "$dest/" ;;
    *) echo "unknown archive type $kind" >&2; exit 1 ;;
  esac
  echo "$sha" > "$dest/.verified"
  echo "$name: fetched"
done
