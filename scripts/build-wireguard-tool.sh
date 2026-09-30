#!/bin/sh
# Build a pinned, static wg tool. This is a separate executable, not linked into the Agent.
set -eu
[ "$#" -eq 2 ] || { echo "Usage: $0 RID OUTPUT_DIRECTORY" >&2; exit 2; }
wg_rid=$1
wg_output=$2
case "$wg_rid" in linux-x64|linux-musl-x64) wg_machine=62 ;; linux-arm64|linux-musl-arm64) wg_machine=183 ;; *) echo "unsupported WireGuard RID" >&2; exit 2 ;; esac
wg_version=1.0.20260223
wg_sha256=af459827b80bfd31b83b08077f4b5843acb7d18ad9a33a2ef532d3090f291fbf
mkdir -p "$wg_output"
wg_output=$(CDPATH='' cd -- "$wg_output" && pwd)
wg_source="$wg_output/wireguard-tools-$wg_version.tar.xz"
if [ ! -f "$wg_source" ]; then
    curl --fail --location --retry 3 --connect-timeout 30 --max-time 180 --proto '=https' \
        "https://git.zx2c4.com/wireguard-tools/snapshot/wireguard-tools-$wg_version.tar.xz" -o "$wg_source.tmp"
    mv "$wg_source.tmp" "$wg_source"
fi
printf '%s  %s\n' "$wg_sha256" "$wg_source" | sha256sum -c -
wg_cc=${WIREGUARD_CC:-musl-gcc}
command -v "$wg_cc" >/dev/null || { echo "install musl-tools (or set WIREGUARD_CC to a musl compiler)" >&2; exit 1; }
tar -xJf "$wg_source" -C "$wg_output"
# musl-gcc on Debian/Ubuntu hides glibc's include tree, including the Linux UAPI headers.
# Expose only kernel headers through an isolated include directory, never glibc's C library headers.
wg_headers="$wg_output/kernel-headers"
rm -rf "$wg_headers"
mkdir -p "$wg_headers"
for wg_header in linux asm-generic; do
    [ -d "/usr/include/$wg_header" ] || { echo "install Linux UAPI headers" >&2; exit 1; }
    ln -s "/usr/include/$wg_header" "$wg_headers/$wg_header"
done
wg_target=$("$wg_cc" -dumpmachine)
if [ -d /usr/include/asm ]; then ln -s /usr/include/asm "$wg_headers/asm"
elif [ -d "/usr/include/$wg_target/asm" ]; then ln -s "/usr/include/$wg_target/asm" "$wg_headers/asm"
else echo "missing architecture Linux UAPI headers" >&2; exit 1
fi
make -C "$wg_output/wireguard-tools-$wg_version/src" clean
make -C "$wg_output/wireguard-tools-$wg_version/src" -j2 CC="$wg_cc" CPPFLAGS="-idirafter $wg_headers" LDFLAGS="-static -s"
cp "$wg_output/wireguard-tools-$wg_version/src/wg" "$wg_output/wg"
# Reject a wrong architecture or any dynamic loader; this wg runs on both glibc and musl hosts.
"${PYTHON:-python3}" - "$wg_output/wg" "$wg_machine" <<'PY'
import struct, sys
with open(sys.argv[1], "rb") as elf:
    header = elf.read(64)
    if header[:6] != b"\x7fELF\x02\x01" or struct.unpack_from("<H", header, 18)[0] != int(sys.argv[2]):
        raise SystemExit("wrong WireGuard ELF architecture")
    phoff, = struct.unpack_from("<Q", header, 32)
    phentsize, phnum = struct.unpack_from("<HH", header, 54)
    elf.seek(phoff)
    if any(struct.unpack("<I", elf.read(phentsize)[:4])[0] == 3 for _ in range(phnum)):
        raise SystemExit("wg must be statically linked")
PY
"$wg_output/wg" --version
sha256sum "$wg_output/wg" | cut -d ' ' -f 1 > "$wg_output/wg.sha256"
