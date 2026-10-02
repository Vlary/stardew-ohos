#!/bin/bash
# Stardew-OHOS NativeAOT 构建脚本：C# 工程 → OHOS aarch64 musl .so
# 用法: ./build-game.sh <csproj目录> <输出lib名>
set -e
ROOT="$(cd "$(dirname "$0")" && pwd)"
DIR="${1:-game-demo}"
OUTNAME="${2:-libmain.so}"

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
export PATH=$HOME/.dotnet:$PATH
export LD_LIBRARY_PATH=$HOME/harmony-toolchain/clt26/command-line-tools/sdk/default/openharmony/native/llvm/lib

NDK=$HOME/harmony-toolchain/clt26/command-line-tools/sdk/default/openharmony/native/llvm/bin
SR=$HOME/harmony-toolchain/clt26/command-line-tools/sdk/default/openharmony/native/sysroot

cd "$(dirname "$0")/$DIR"
rm -rf obj bin   # 强制全量 ilc（NuGet 包内 BCL 变化不触发增量）
dotnet publish -c Release -r linux-musl-arm64 \
  -p:SysRoot=$SR \
  -p:CppCompilerAndLinker="$ROOT/toolwrap/clang-ohos" \
  -p:ArName=$NDK/llvm-ar \
  -p:ObjCopyName=$NDK/llvm-objcopy \
  -p:RanLibName=$NDK/llvm-ranlib \
  -p:LinkerFlavor=lld \
  -o out

SO=$(ls out/*.so | head -1)
echo "产物: $SO"
mkdir -p ../hap/entry/libs/arm64-v8a
cp "$SO" "../hap/entry/libs/arm64-v8a/$OUTNAME"
echo "已拷贝 → hap/entry/libs/arm64-v8a/$OUTNAME"
