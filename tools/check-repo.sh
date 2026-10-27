#!/usr/bin/env bash
# Repository rules that the compiler does not enforce:
#  - the libraries reference nothing but the base class library (no PackageReference in src/);
#  - unsafe code, Unsafe.*, MemoryMarshal, fixed and pointer stackalloc live only in src/OneAudio/Kernels/
#    and src/OneAudio.Devices/Interop/.
set -euo pipefail
cd "$(dirname "$0")/.."
status=0

if grep -rn 'PackageReference' src --include='*.csproj'; then
  echo "error: a library project has a PackageReference" >&2
  status=1
fi

hits=$(grep -rnE '\bunsafe\b|\bUnsafe\.|MemoryMarshal|\bfixed\s*\(|\w\*\s*\w+\s*=\s*stackalloc' src --include='*.cs' \
  | grep -v '^src/OneAudio/Kernels/' | grep -v '^src/OneAudio.Devices/Interop/' || true)
if [ -n "$hits" ]; then
  echo "$hits"
  echo "error: unsafe constructs outside the allowed folders" >&2
  status=1
fi

exit $status
