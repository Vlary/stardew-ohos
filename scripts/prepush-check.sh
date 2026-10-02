#!/bin/bash
# 推送前敏感信息检查（pre-push gate）
# 用法: ./scripts/prepush-check.sh
# 命中任一模式即退出码 1，需人工确认或清洗后再推送。
set -u
cd "$(dirname "$0")/.."

FAIL=0
red() { printf '\033[31m%s\033[0m\n' "$1"; }
ok()  { printf '\033[32m%s\033[0m\n' "$1"; }

# 只扫 git 跟踪的文件
FILES=$(git ls-files)

# 排除第三方上游源码/文档（SDL2 官方代码、示例工程），仅扫描本项目自有代码
EXCLUDE='sdl2-ohos/(include|configure|acinclude|VisualC-WinRT|VisualC|android-project|android-project-ant|Xcode|cmake|docs|test|src/core/winrt|src/video/(winrt|windows|uikit|cocoa|x11|wayland|kmsdrm|raspberry|vivante|android|directfb|dummy|khronos)|src/audio/(coreaudio|directsound|winmm|android|pulseaudio|alsa|dummy|netbsd|emscripten|haiku|nacl|paudio|pipewire|psp|sndio|vita|wasapi|openslES)|src/misc/(mac|dummy|unix/.*mac)|src/haptic/(android|darwin|dummy|windows|hidapi|linux)|src/thread/(windows|pthread|stdcpp|generic)/|src/(hidapi|render/opengl|test|main|filesystem/unix)/|visualtest/|src/joystick/(windows|darwin|bsd|haiku|hidapi|iphoneos|android|steam|emscripten|psp))|toolwrap/|scripts/'

hit() {
    local name="$1" pattern="$2"
    local out
    out=$(echo "$FILES" | grep -vE "$EXCLUDE" | xargs -r grep -nIE -- "$pattern" 2>/dev/null \
          | grep -vE 'prepush-check\.sh|build-profile\.json5\.template|prepush-allow' | head -8)
    if [ -n "$out" ]; then
        red "✗ [$name]"
        echo "$out" | sed 's/^/    /'
        FAIL=1
    else
        ok "✓ $name"
    fi
}

hit "口令/密钥类关键词"      'password|passwd|secret|token|api[_-]?key|private[-_ ]?key|BEGIN [A-Z ]*PRIVATE'
hit "内网 IP / 调试端口"     '192\.168\.[0-9]|:35995|10\.[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}'
# 说明性文字与公开算法常量（README 提及 UDID、FNV 素数、下载校验和）不进白名单会误报，
# 但真实 UDID/账号 ID 出现时仍应命中——保留模式，仅排除已确认的说明行。
hit "设备 UDID / 64位十六进制串" 'UDID|[0-9a-fA-F]{64}'
hit "签名材料文件引用"        '\.(p12|p7b|cer|keystore)\b'

# 大文件/游戏资源误入检查
BIG=$(git ls-files -z | xargs -0 -r du -k 2>/dev/null | awk '$1>5120 {print $2}' | head -5)
if [ -n "$BIG" ]; then
    red "✗ [大文件 >5MB 入库]"; echo "$BIG" | sed 's/^/    /'; FAIL=1
else
    ok "✓ 无大文件（>5MB）入库"
fi

# mods/ 下的 .xnb/.png 为 Mod 产物（自制），放行；其余位置照拦
GAME=$(echo "$FILES" | grep -v '^mods/' | grep -iE 'game-files/|rawfile/|\.dll$|\.xnb$|\.hap$' | head -5)
if [ -n "$GAME" ]; then
    red "✗ [疑似游戏文件/产物入库]"; echo "$GAME" | sed 's/^/    /'; FAIL=1
else
    ok "✓ 无游戏文件/产物入库"
fi

echo
if [ "$FAIL" -eq 0 ]; then
    ok "== 全部通过，可以推送 =="
else
    red "== 发现可疑内容，请清洗后再推送 =="
fi
exit $FAIL
