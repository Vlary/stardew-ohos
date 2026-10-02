#!/usr/bin/env python3
"""扫描 Content 下全部 xnb 的 reader 类型表，输出泛型 root 候选（TSV: readerName）"""
import os, struct, sys

def read_7bit(f):
    result = 0; shift = 0
    while True:
        b = f.read(1)
        if not b: return None
        b = b[0]
        result |= (b & 0x7F) << shift
        if not (b & 0x80): return result
        shift += 7

def readers_of(path):
    try:
        with open(path, 'rb') as f:
            hdr = f.read(4)
            if hdr[:3] != b'XNB': return []
            f.read(1)  # version? 实际: XNB + platform(w) + version(1) + flags(1)
            ver = f.read(1)[0]
            flags = f.read(1)[0]
            if flags & 0x80:  # HiDef? 0x80 hidef 0x40 compressed
                pass
            if flags & 0x40:
                f.read(4)  # compressed size
                # 解压才能读 reader 表——跳过（LZ4/XMem？MonoGame 用 LZ4
                return None
            f.read(4)  # file size
            n = f.read(1)[0]
            out = []
            for _ in range(n):
                ln = read_7bit(f)
                if ln is None or ln > 4096: return out
                name = f.read(ln).decode('utf-8', 'replace')
                f.read(4)  # reader version
                out.append(name)
            return out
    except Exception:
        return []

roots = set()
base = 'game-files/Content'
for root, dirs, files in os.walk(base):
    for fn in files:
        if not fn.endswith('.xnb'): continue
        rs = readers_of(os.path.join(root, fn))
        if rs:
            for r in rs:
                if '`' in r or '[' in r:
                    roots.add(r)
open('/tmp/xnb-readers.txt', 'w').write('\n'.join(sorted(roots)))
print(f"{len(roots)} 个泛型 reader")
