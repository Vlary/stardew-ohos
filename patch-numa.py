#!/usr/bin/env python3
"""把 NativeAOT so 里的 syscall(__NR_get_mempolicy=236) 调用号 patch 成 getpid(172)。
OHOS seccomp 对 get_mempolicy 发 SIGSYS。改成 getpid 后返回正值，
.NET 的 NUMASupportInitialize 会转而探测 /sys/devices/system/node（不存在）→ 自动禁用 NUMA。"""
import sys, struct

so = sys.argv[1] if len(sys.argv) > 1 else "out/aot-test.so"
data = bytearray(open(so, "rb").read())

# movz w0, #236 = 0x52801D80 ; 目标 movz w0, #172 = 0x52801580
old = struct.pack("<I", 0x52801D80)
new = struct.pack("<I", 0x52801580)

# 唯一性锚定：movz w0,#236 + mov x1,xzr（syscall(NULL) 参数模式）
anchor = old + b"\xe1\x03\x1f\xaa"
idx = 0
hits = []
while True:
    i = data.find(anchor, idx)
    if i < 0:
        break
    hits.append(i)
    idx = i + 1

if len(hits) == 0:
    # 退化：只搜单条
    idx = 0
    while True:
        i = data.find(old, idx)
        if i < 0:
            break
        hits.append(i)
        idx = i + 1

if len(hits) != 1:
    print(f"锚定失败：找到 {len(hits)} 处 {[hex(h) for h in hits]}，拒绝 patch")
    sys.exit(1)

data[hits[0]:hits[0]+4] = new
open(so, "wb").write(data)
print(f"patched at file offset {hex(hits[0])}: movz w0,#236 -> movz w0,#172 (getpid)")
