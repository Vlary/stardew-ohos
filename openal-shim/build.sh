#!/bin/bash
# 编译 OpenAL shim → libopenal.so（aarch64 OHOS，OHAudio 直连后端）
set -e
DIR="$(cd "$(dirname "$0")" && pwd)"
NDK=$HOME/harmony-toolchain/clt26/command-line-tools/sdk/default/openharmony/native

$NDK/llvm/bin/clang --target=aarch64-linux-ohos \
  --sysroot=$NDK/sysroot \
  -shared -fPIC -O2 -fvisibility=default \
  -I"$DIR" \
  "$DIR/openal_shim.c" \
  -l:libohaudio.so \
  -o "$DIR/libopenal.so"

ls -la "$DIR/libopenal.so"
echo "产物: $DIR/libopenal.so"
