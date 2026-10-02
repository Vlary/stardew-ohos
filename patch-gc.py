#!/usr/bin/env python3
"""NativeAOT so 的 OHOS 适配 patch（每次 dotnet publish 后运行）：
1. syscall(__NR_get_mempolicy=236) → getpid(172)：OHOS seccomp 对 236 SIGSYS
2. GC regions_range 常量 256GB → 64GB：OHOS app 域 mmap 256GB ENOMEM（128GB 实测上限内）
用法: python3 patch-gc.py <so路径>
"""
import struct, sys

so = sys.argv[1]
data = bytearray(open(so, "rb").read())
TEXT_VA2FILE = 0x1000  # .text 文件偏移 = vaddr - 0x1000

# --- patch 1: movz w0,#236 (0x52801D80) → movz w0,#172 (0x52801580) ---
old1 = struct.pack("<I", 0x52801D80)
new1 = struct.pack("<I", 0x52801580)
hits = []
i = 0
while True:
    i = data.find(old1, i)
    if i < 0:
        break
    hits.append(i)
    i += 1
if len(hits) == 1:
    data[hits[0]:hits[0]+4] = new1
    print(f"[1] get_mempolicy→getpid patched @ {hex(hits[0])}")
elif len(hits) == 0:
    print("[1] movz w0,#236 未找到（可能已 patch）")
else:
    print(f"[1] 警告：{len(hits)} 处匹配，跳过"); sys.exit(1)

# --- patch 2: movz x9,#0x4000000000 (0xD2C00809) → movz x9,#0x1000000000 (64GB) ---
old2 = struct.pack("<I", 0xD2C00809)
new2 = struct.pack("<I", 0xD2C02009)
hits = []
i = 0
while True:
    i = data.find(old2, i)
    if i < 0:
        break
    hits.append(i)
    i += 1
if len(hits) == 1:
    data[hits[0]:hits[0]+4] = new2
    print(f"[2] regions_range 256G→64G patched @ {hex(hits[0])}")
elif len(hits) == 0:
    print("[2] movz x9,#256G 未找到（可能已 patch）")
else:
    print(f"[2] 警告：{len(hits)} 处匹配（{[hex(h) for h in hits]}），全部替换")
    for h in hits:
        data[h:h+4] = new2

open(so, "wb").write(data)
print("done")
