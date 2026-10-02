#!/usr/bin/env python3
"""鲁棒版 GC patch：movz w0,#236 后 24 字节内出现 mov x1,xzr 且 mov x2,xzr → 判定 get_mempolicy 调用点"""
import struct, sys
so = sys.argv[1]
data = bytearray(open(so, "rb").read())
movz236 = struct.pack("<I", 0x52801D80)
movx1zr = struct.pack("<I", 0xAA1F03E1)
movx2zr = struct.pack("<I", 0xAA1F03E2)
movz172 = struct.pack("<I", 0x52801580)
hits = []
i = 0
while True:
    i = data.find(movz236, i)
    if i < 0: break
    win = data[i:i+28]
    if movx1zr in win and movx2zr in win:
        hits.append(i)
    i += 1
print("candidate hits:", [hex(h) for h in hits])
if len(hits) == 1:
    data[hits[0]:hits[0]+4] = movz172
    open(so, "wb").write(data)
    print(f"GC patched @ {hex(hits[0])}")
else:
    print(f"跳过：{len(hits)} 候选"); sys.exit(1)
