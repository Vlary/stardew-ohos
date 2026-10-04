#!/bin/bash
# 鸿蒙设备交互工具（星露谷联机调试用）
# 用法:
#   devctl.sh shot [本地文件]      截图（默认 /tmp/scr.jpeg）
#   devctl.sh click <x> <y>        绝对坐标点击（鼠标归零→相对移动→左键点击）
#   devctl.sh move <x> <y>         绝对坐标移动
#   devctl.sh home                 鼠标归零（大幅左上移动）
#   devctl.sh query                查询鼠标状态
#   devctl.sh pull <设备路径> <本地路径>   拉文件（走 el2 视角）
set -e
HDC=${HDC:-$HOME/harmony-toolchain/clt26/command-line-tools/sdk/default/openharmony/toolchains/hdc}
case "$1" in
  shot)
    OUT=${2:-/tmp/scr.jpeg}
    $HDC shell "snapshot_display -f /data/local/tmp/scr.jpeg" >/dev/null 2>&1
    $HDC file recv /data/local/tmp/scr.jpeg "$OUT" >/dev/null 2>&1
    echo "saved $OUT";;
  home)
    $HDC shell "uinput -M -m -6000 -4000" ;;
  move)
    $HDC shell "uinput -M -m -6000 -4000"
    sleep 0.3
    $HDC shell "uinput -M -m $2 $3"
    echo "moved to ($2,$3)";;
  click)
    # uinput -M -g 用绝对坐标做"拖 1px"，产生带正确 button state 的按下/抬起
    # （-c click 的 down 事件 state=0 游戏不认；drag 可以）
    $HDC shell "uinput -M -g $2 $3 $(($2+1)) $(($3+1)) 100"
    echo "clicked ($2,$3)";;
  dclick)
    $HDC shell "uinput -M -m -6000 -4000"
    sleep 0.3
    $HDC shell "uinput -M -m $2 $3"
    sleep 0.3
    $HDC shell "uinput -M -c 0"
    sleep 0.15
    $HDC shell "uinput -M -c 0"
    echo "double-clicked ($2,$3)";;
  query)
    $HDC shell "uinput -M -q" 2>&1 | head -6;;
  pull)
    $HDC file recv "$2" "$3" 2>&1 | tail -1;;
  *)
    echo "用法: $0 shot|move x y|click x y|dclick x y|home|query|pull dev local";;
esac
