using System;
using System.IO;
using System.Linq;
using System.Reflection;

// XNB 工具（本地 JIT 运行）：
//   unpack  <in.xnb> <out.raw>                     解出内容区（LZX/未压缩均支持）
//   packtex <template.xnb> <rgba.bin> <w> <h> <out.xnb>
//           以 template 的纹理元数据为骨架，替换像素数据，输出「未压缩」xnb（游戏可直接读）
//   replace <in.xnb> <out.xnb> <old> <new>         字符串定点替换（7bit-len 前缀重编码），输出未压缩 xnb
class P
{
    static byte[] LzxDecompress(byte[] d, int offset, int compressedSize, int decompressedSize)
    {
        var asm = typeof(Microsoft.Xna.Framework.Vector2).Assembly;
        var t = asm.GetTypes().First(x => x.Name == "LzxDecoderStream");
        var ctor = t.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            null, new[] { typeof(Stream), typeof(int), typeof(int) }, null);
        using var ms = new MemoryStream(d, offset, compressedSize);
        using var lzx = (Stream)ctor.Invoke(new object[] { ms, decompressedSize, compressedSize });
        var outp = new byte[decompressedSize];
        int rd = 0;
        while (rd < decompressedSize) { int r = lzx.Read(outp, rd, decompressedSize - rd); if (r <= 0) break; rd += r; }
        if (rd != decompressedSize) throw new IOException($"LZX 解压不完整 {rd}/{decompressedSize}");
        return outp;
    }

    static (byte[] content, int flags) Unpack(byte[] d)
    {
        if (d[0] != 'X' || d[1] != 'N' || d[2] != 'B') throw new IOException("非 XNB");
        int flags = d[5];
        int totalSize = BitConverter.ToInt32(d, 6);
        if ((flags & 0x80) != 0)
        {
            int dec = BitConverter.ToInt32(d, 10);
            return (LzxDecompress(d, 14, totalSize - 14, dec), flags);
        }
        if ((flags & 0x40) != 0) throw new IOException("LZ4 暂不支持");
        return (d.Skip(10).Take(totalSize - 10).ToArray(), flags);
    }

    static byte[] Wrap(byte[] content, int srcFlags)
    {
        int total = 10 + content.Length;
        var o = new byte[total];
        o[0] = (byte)'X'; o[1] = (byte)'N'; o[2] = (byte)'B'; o[3] = (byte)'w'; o[4] = 5;
        o[5] = (byte)(srcFlags & 0x01);              // 保留 HiDef 位，去掉压缩位 → 未压缩
        BitConverter.GetBytes(total).CopyTo(o, 6);
        content.CopyTo(o, 10);
        return o;
    }

    // 在内容区首个 reader 表之后扫描「7bit长度 + utf8 串」做替换（长度前缀自动重编码）
    static byte[] ReplaceString(byte[] content, string oldS, string newS)
    {
        var ob = System.Text.Encoding.UTF8.GetBytes(oldS);
        var nb = System.Text.Encoding.UTF8.GetBytes(newS);
        using var ms = new MemoryStream();
        int i = 0, hits = 0;
        while (i < content.Length)
        {
            // 尝试匹配 7bit(len=ob.Length) + ob
            int p = i; long v = 0; int sh = 0; bool ok = true;
            while (true)
            {
                if (p >= content.Length) { ok = false; break; }
                byte b = content[p++];
                v |= (long)(b & 0x7f) << sh;
                if ((b & 0x80) == 0) break;
                sh += 7;
                if (sh > 28) { ok = false; break; }
            }
            if (ok && v == ob.Length && p + ob.Length <= content.Length
                && content.Skip(p).Take(ob.Length).SequenceEqual(ob))
            {
                // 写新长度（7bit）+ 新串
                long nv = nb.Length; int q = p - 1;
                // 回退到长度字段起点重写（原长度字段可能多字节——极端情况长度编码位数变化：全部重写该串起点起）
                int lenStart = i;
                ms.Write(content, 0, 0); // noop
                // 重建：把 content[..lenStart] 已写过；这里直接重编码
                var lenBytes = Encode7Bit(nv);
                ms.Write(lenBytes, 0, lenBytes.Length);
                ms.Write(nb, 0, nb.Length);
                i = p + ob.Length;
                hits++;
                // 先把 ms 里补上 lenStart 之前未写部分——用整体策略：改用列表式处理
            }
            else
            {
                ms.WriteByte(content[i]);
                i++;
            }
        }
        // 上面逐字节写法在命中时漏写了 [lenStart, 长度字段) 的字节（因为 i 是逐字节推进的），
        // 简化：重写为两段式（见下方 ReplaceString2）
        return ms.ToArray();
    }

    static byte[] Encode7Bit(long v)
    {
        using var ms = new MemoryStream();
        while (v >= 0x80) { ms.WriteByte((byte)(v | 0x80)); v >>= 7; }
        ms.WriteByte((byte)v);
        return ms.ToArray();
    }

    static byte[] ReplaceString2(byte[] content, string oldS, string newS)
    {
        var ob = System.Text.Encoding.UTF8.GetBytes(oldS);
        var nb = System.Text.Encoding.UTF8.GetBytes(newS);
        using var ms = new MemoryStream();
        int i = 0, hits = 0;
        while (i < content.Length)
        {
            int p = i; long v = 0; int sh = 0; bool ok = true;
            while (true)
            {
                if (p >= content.Length) { ok = false; break; }
                byte b = content[p++];
                v |= (long)(b & 0x7f) << sh;
                if ((b & 0x80) == 0) break;
                sh += 7; if (sh > 28) { ok = false; break; }
            }
            if (ok && v == ob.Length && p + ob.Length <= content.Length
                && content.Skip(p).Take(ob.Length).SequenceEqual(ob))
            {
                var lenBytes = Encode7Bit(nb.Length);
                ms.Write(lenBytes, 0, lenBytes.Length);
                ms.Write(nb, 0, nb.Length);
                i = p + ob.Length;
                hits++;
            }
            else { ms.WriteByte(content[i]); i++; }
        }
        Console.WriteLine($"[xnbtool] 字符串替换 {hits} 处: \"{oldS}\" -> \"{newS}\"");
        return ms.ToArray();
    }

    // 纹理对象区：int32 format, uint32 w, uint32 h, int32 levels, 然后每 level: int32 len + data
    static (int fmt, uint w, uint h, int levels, int posData) ParseTextureHead(byte[] c)
    {
        // 跳过 reader 表
        int pos = 0;
        int n = c[pos++];
        for (int i = 0; i < n; i++)
        {
            long v = 0; int sh = 0;
            while (true) { byte b = c[pos++]; v |= (long)(b & 0x7f) << sh; if ((b & 0x80) == 0) break; sh += 7; }
            pos += (int)v;
            pos += 4; // extra int32
        }
        long sshared = 0; { int sh = 0; while (true) { byte b = c[pos++]; sshared |= (long)(b & 0x7f) << sh; if ((b & 0x80) == 0) break; sh += 7; } }
        pos++; // 主对象 readerIndex (通常 1)
        int fmt = BitConverter.ToInt32(c, pos); pos += 4;
        uint w = BitConverter.ToUInt32(c, pos); pos += 4;
        uint h = BitConverter.ToUInt32(c, pos); pos += 4;
        int levels = BitConverter.ToInt32(c, pos); pos += 4;
        uint rw = (w & 0xFFFF0000u) == 0 ? w : (w & 0xFFFF);
        uint rh = (h & 0xFFFF0000u) == 0 ? h : (h & 0xFFFF);
        return (fmt, rw, rh, levels, pos);
    }

    static int Main(string[] args)
    {
        if (args.Length < 2) { Console.WriteLine("用法: unpack|packtex|replace ..."); return 2; }
        switch (args[0])
        {
            case "unpack":
            {
                var d = File.ReadAllBytes(args[1]);
                var (c, f) = Unpack(d);
                File.WriteAllBytes(args[2], c);
                Console.WriteLine($"[xnbtool] unpack {args[1]} -> {args[2]} ({c.Length}B, flags=0x{f:X2})");
                return 0;
            }
            case "packtex":
            {
                // packtex <template.xnb> <rgba.bin> <w> <h> <out.xnb>
                var d = File.ReadAllBytes(args[1]);
                var (c, f) = Unpack(d);
                var (fmt, w0, h0, levels, posData) = ParseTextureHead(c);
                int lv0Len = BitConverter.ToInt32(c, posData);
                int w = int.Parse(args[3]), h = int.Parse(args[4]);
                var rgba = File.ReadAllBytes(args[2]);
                if (rgba.Length != w * h * 4) { Console.WriteLine($"像素数据应为 {w*h*4}B，实际 {rgba.Length}"); return 2; }
                // 重建内容：头部原样（保留 reader 表与 meta），替换尺寸+像素
                using var ms = new MemoryStream();
                ms.Write(c, 0, posData - 16);           // 到 format 之前
                ms.Write(BitConverter.GetBytes(fmt), 0, 4);
                ms.Write(BitConverter.GetBytes((uint)w), 0, 4);
                ms.Write(BitConverter.GetBytes((uint)h), 0, 4);
                ms.Write(BitConverter.GetBytes(1), 0, 4); // mip 数 → 1（简化：丢 mipmap）
                ms.Write(BitConverter.GetBytes(w * h * 4), 0, 4);
                ms.Write(rgba, 0, rgba.Length);
                var packed = Wrap(ms.ToArray(), f);
                File.WriteAllBytes(args[5], packed);
                Console.WriteLine($"[xnbtool] packtex {args[5]} {w}x{h} fmt={fmt} (原 {w0}x{h0} levels={levels}) → {packed.Length}B");
                return 0;
            }
            case "patchdata":
            {
                // patchdata <in.xnb> <out.xnb> <键> <字段> <新值>
                return PatchData.Run(args[1], args[2], args[3], args[4], args[5]);
            }
            case "replace":
            {
                var d = File.ReadAllBytes(args[1]);
                var (c, f) = Unpack(d);
                var c2 = ReplaceString2(c, args[3], args[4]);
                var packed = Wrap(c2, f);
                File.WriteAllBytes(args[2], packed);
                Console.WriteLine($"[xnbtool] replace -> {args[2]} ({packed.Length}B)");
                return 0;
            }
        }
        return 2;
    }
}
