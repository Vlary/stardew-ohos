#!/bin/bash
# 星露谷 OHOS 一键构建部署脚本
# 用法：
#   ./build-all.sh            # 全链构建 + 覆盖安装 + 启动
#   ./build-all.sh deploy     # 只部署（hap 已构建）
#   ./build-all.sh host       # 只重编 host（AOT）+ 打包部署
# 输入约定：mg-patch / sdv-patch 的输入必须是 Steam 原版 dll
#   ~/.local/share/Steam/steamapps/common/Stardew Valley/
# 切勿用中间产物做输入（脏 IL 会叠加出 RasterizerState InvalidProgram 等）
set -e
cd "$(dirname "$0")"
ROOT=$PWD
STEAM="$HOME/.local/share/Steam/steamapps/common/Stardew Valley"
HDC=${HDC:-$HOME/harmony-toolchain/clt26/command-line-tools/sdk/default/openharmony/toolchains/hdc}
export PATH=$HOME/.dotnet:$PATH

MODE=${1:-all}

patch_mg() {
    dotnet mg-patch/bin/Release/net9.0/mg-patch.dll "$STEAM/MonoGame.Framework.dll" \
        game-files/MonoGame.Framework.dll
}
patch_sdv() {
    dotnet sdv-patch/bin/Release/net9.0/sdv-patch.dll "$STEAM/Stardew Valley.dll" \
        game-files/Stardew Valley.dll helper/bin/Release/netstandard2.0/OHOS.Helper.dll
}
build_patches() {
    dotnet build mg-patch -c Release | grep -E '已成功| error ' || true
    dotnet build sdv-patch -c Release | grep -E '已成功| error ' || true
    dotnet build helper -c Release | grep -E '已成功| error ' || true
}

case $MODE in
all)
    build_patches
    patch_mg
    patch_sdv
    ./openal-shim/build.sh
    cp openal-shim/libopenal.so hap/entry/libs/arm64-v8a/libopenal.so
    ./build-game.sh stardew-host libmain.so
    ;&
host)
    ./build-game.sh stardew-host libmain.so
    ;&
hap)
    pushd hap >/dev/null
    source $HOME/harmony-toolchain/env26.sh
    hvigorw --mode module -p module=entry@default -p product=default assembleHap --no-daemon | tail -1
    popd >/dev/null
    ;&
deploy)
    HAP=hap/entry/build/default/outputs/default/entry-default-signed.hap
    $HDC shell "aa force-stop com.stardewvalley.ohos" 2>/dev/null || true
    $HDC file send "$HAP" /data/local/tmp/sdv-entry.hap
    $HDC shell "bm install -r -p /data/local/tmp/sdv-entry.hap"
    $HDC shell "aa start -b com.stardewvalley.ohos -a EntryAbility"
    echo "已部署并启动"
    ;;
*)
    echo "用法: $0 [all|host|hap|deploy]"
    ;;
esac
