#!/bin/bash
# 把 mods/*/out/ 覆盖到 hap rawfile 的 Content（构建前执行）
set -e
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
RAW="$ROOT/hap/entry/src/main/resources/rawfile/Content"
n=0
for mod in "$ROOT"/mods/*/; do
  [ -d "$mod/out" ] || continue
  name=$(basename "$mod")
  echo "[mods] 应用: $name"
  cp -rv "$mod/out/." "$RAW/" | tail -3
  n=$((n+1))
done
echo "[mods] 共应用 $n 个 mod → $RAW"
