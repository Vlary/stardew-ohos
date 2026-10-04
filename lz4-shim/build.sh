#!/bin/bash
# 编译 lz4 shim → liblwjgl_lz4.so（aarch64 OHOS）
# 提供 SDV LWJGL.LZ4 P/Invoke 所需的 Java_org_lwjgl_util_lz4_LZ4_* 符号
set -e
DIR="$(cd "$(dirname "$0")" && pwd)"
NDK=$HOME/harmony-toolchain/clt26/command-line-tools/sdk/default/openharmony/native

$NDK/llvm/bin/clang --target=aarch64-linux-ohos \
  --sysroot=$NDK/sysroot \
  -shared -fPIC -O2 -fvisibility=hidden \
  -I"$DIR" \
  "$DIR/shim.c" "$DIR/lz4.c" \
  -o "$DIR/liblwjgl_lz4.so"

ls -la "$DIR/liblwjgl_lz4.so"
echo "产物: $DIR/liblwjgl_lz4.so"
echo "=== 导出符号:"
$NDK/llvm/bin/llvm-nm -D --defined-only "$DIR/liblwjgl_lz4.so" 2>/dev/null | grep Java_ || true
