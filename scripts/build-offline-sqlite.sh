#!/usr/bin/env bash
set -euo pipefail
ndk="${ANDROID_NDK_HOME:-}"
if [ -z "$ndk" ]; then
  ndk=$(find "$ANDROID_HOME/ndk" -mindepth 1 -maxdepth 1 -type d | sort -V | tail -n 1)
fi
test -n "$ndk"
build=$(mktemp -d)
trap 'rm -rf "$build"' EXIT
curl --fail --location --retry 3 https://www.sqlite.org/2025/sqlite-amalgamation-3500400.zip -o "$build/sqlite.zip"
printf '%s  %s\n' '1d3049dd0f830a025a53105fc79fd2ab9431aea99e137809d064d8ee8356b032' "$build/sqlite.zip" | sha256sum -c -
unzip -q "$build/sqlite.zip" -d "$build"
mkdir -p Content.Android/lib/arm64-v8a
"$ndk/toolchains/llvm/prebuilt/linux-x86_64/bin/aarch64-linux-android26-clang" \
  -shared -fPIC -O2 -DSQLITE_THREADSAFE=1 -DSQLITE_ENABLE_FTS5 -DSQLITE_ENABLE_RTREE \
  -Wl,-soname,libsqlite3.so -Wl,-z,max-page-size=16384 \
  "$build/sqlite-amalgamation-3500400/sqlite3.c" -lm -ldl \
  -o Content.Android/lib/arm64-v8a/libsqlite3.so
