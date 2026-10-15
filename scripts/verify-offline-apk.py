"""Reject an offline APK missing the ARM64 database required at startup."""
import struct
import sys
import zipfile

with zipfile.ZipFile(sys.argv[1]) as apk:
    name = 'lib/arm64-v8a/libsqlite3.so'
    if apk.namelist().count(name) != 1:
        raise SystemExit(f'Expected exactly one {name}')
    library = apk.read(name)
    if library[:6] != b'\x7fELF\x02\x01' or struct.unpack_from('<H', library, 18)[0] != 183:
        raise SystemExit('SQLite must be a little-endian ARM64 ELF library')
    for symbol in (b'sqlite3_open_v2\0', b'sqlite3_libversion\0', b'sqlite3_close_v2\0'):
        if symbol not in library:
            raise SystemExit(f'Missing SQLite API: {symbol!r}')
    print(f'Verified bundled ARM64 SQLite: {len(library)} bytes')
