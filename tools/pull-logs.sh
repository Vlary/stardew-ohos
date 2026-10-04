#!/bin/bash
# 拉取星露谷运行日志并只显示"最后一次启动"的段（O_APPEND 多段拼接）。
# 用法: pull-logs.sh [out|err|both]   默认 both
#   out 段: [SDV]/[NET]/[HLP]/[SDV-X] 探针日志
#   err 段: SDL/openal/信号(FATAL)/托管异常 等 stderr
set -e
HDC=${HDC:-$HOME/harmony-toolchain/clt26/command-line-tools/sdk/default/openharmony/toolchains/hdc}
BASE=/data/app/el2/100/base/com.stardewvalley.ohos/haps/entry/files
WHAT=${1:-both}

last_segment() { # 只输出最后一个 "SDL_main pid" 行之后的内容
  awk '/SDL_main pid/{n=NR} {a[NR]=$0} END{for(i=n;i<=NR;i++) print a[i]}' "$1"
}

if [ "$WHAT" = "out" ] || [ "$WHAT" = "both" ]; then
  $HDC file recv $BASE/sdv-out.log /tmp/sdv-out-latest.log 2>&1 | tail -1
  echo "===== OUT (最后一次启动) 尾部 300 行 ====="
  last_segment /tmp/sdv-out-latest.log | tail -300
  echo "===== NET/SDV-X 探针行 ====="
  last_segment /tmp/sdv-out-latest.log | grep -E '\[NET\]|\[SDV-X\]' | tail -120
fi
if [ "$WHAT" = "err" ] || [ "$WHAT" = "both" ]; then
  $HDC file recv $BASE/sdv-err.log /tmp/sdv-err-latest.log 2>&1 | tail -1
  echo "===== ERR 关键行 (FATAL/异常/Unhandled) ====="
  grep -aE 'FATAL|Unhandled|Exception|SIGSEGV|SIGABRT|abort' /tmp/sdv-err-latest.log | tail -40
  echo "===== ERR 尾部 40 行 ====="
  tail -40 /tmp/sdv-err-latest.log
fi
