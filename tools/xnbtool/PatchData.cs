using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

// 数据 Mod 写端（试点）：按 MonoGame 反射读取器规范"切分-重放"对象区，
// 对 <键, 字段> 定位的值类型字段做替换，输出未压缩 xnb。
//
// 规范来源（ReverseXml 读端源码 + 实测字节，详见 docs/MODS.md）：
//   成员序列 = [基类(递归)] + Properties(DeclaredOnly 本类, 有非 override getter) + Fields(DeclaredOnly 本类)
//   值类型成员: 直读 reader 数据；引用类型成员: 7bit index(1-based,0=null) + reader 数据
//   Int32/Enum/Nullable-flag...: 4/4/1B；String: 7bit len + utf8
//   List/Array: int32 count + 元素；Dictionary: int32 count + 每项 K,V
public static class PatchData
{
    static List<ReaderInfo> _readers = new();
    static byte[] _d; static int _pos;
    class ReaderInfo { public string Name; public string Kind; public string TargetNorm; public int Index; }
    static Dictionary<string, Type> _typeByNorm = new();

    static void BuildTypeUniverse()
    {
        var asms = new[] { typeof(StardewValley.GameData.Weapons.WeaponData).Assembly,
                           typeof(Microsoft.Xna.Framework.Vector2).Assembly,
                           typeof(object).Assembly };
        foreach (var a in asms)
            foreach (var t in a.GetTypes())
            {
                var n = Norm(t.FullName ?? t.Name);
                if (!_typeByNorm.ContainsKey(n)) _typeByNorm[n] = t;
            }
        // 常用系统泛型
        foreach (var t in new[] { typeof(string), typeof(int), typeof(float), typeof(bool), typeof(Dictionary<,>), typeof(List<>), typeof(Nullable<>) })
            _typeByNorm[Norm(t.FullName)] = t;
    }

    // 规范把程序集限定/空白剥掉，仅按类型结构比较（mscorlib 版本限定无法 Type.GetType）
    static string Norm(string s)
    {
        var sb = new StringBuilder();
        bool inAsm = false;
        for (int i = 0; i < s.Length; i++)
        {
            char ch = s[i];
            if (ch == ',')
            {
                // 下一非空字符若是 '[' → 层间分隔（保留逗号）；否则为程序集段（舍弃）
                int j = i + 1;
                while (j < s.Length && char.IsWhiteSpace(s[j])) j++;
                if (j < s.Length && s[j] == '[') sb.Append(',');
                else inAsm = true;
                continue;
            }
            if (ch == ']' || ch == '[') { inAsm = false; sb.Append(ch); continue; }
            if (inAsm) continue;
            if (!char.IsWhiteSpace(ch)) sb.Append(ch);
        }
        return sb.ToString().Replace("mscorlib", "System.Private.CoreLib");
    }

    static string BasicMap(string readerName)
    {
        string[] basics = { "Int32Reader:int", "Int64Reader:long", "SingleReader:float", "DoubleReader:double",
            "BooleanReader:bool", "ByteReader:byte", "SByteReader:sbyte", "Int16Reader:short",
            "UInt16Reader:ushort", "UInt32Reader:uint", "UInt64Reader:ulong", "CharReader:char",
            "StringReader:string", "DecimalReader:decimal", "DateTimeReader:System.DateTime" };
        foreach (var b in basics)
        {
            var parts = b.Split(':');
            if (readerName.StartsWith("Microsoft.Xna.Framework.Content." + parts[0])) return parts[1];
        }
        return null;
    }

    static (string kind, string targetNorm) ReaderSemantics(string readerName)
    {
        var b = BasicMap(readerName);
        if (b != null) return ("basic:" + b, null);
        string core = "Microsoft.Xna.Framework.Content.";
        if (readerName.StartsWith(core + "ReflectiveReader`1[["))
            return ("reflective", Norm(readerName.Substring((core + "ReflectiveReader`1[[").Length).TrimEnd(']')));
        if (readerName.StartsWith(core + "ListReader`1[["))
            return ("list", Norm(readerName.Substring((core + "ListReader`1[[").Length).TrimEnd(']')));
        if (readerName.StartsWith(core + "ArrayReader`1[["))
            return ("array", Norm(readerName.Substring((core + "ArrayReader`1[[").Length).TrimEnd(']')));
        if (readerName.StartsWith(core + "NullableReader`1[["))
            return ("nullable", Norm(readerName.Substring((core + "NullableReader`1[[").Length).TrimEnd(']')));
        if (readerName.StartsWith(core + "EnumReader`1[["))
            return ("enum", Norm(readerName.Substring((core + "EnumReader`1[[").Length).TrimEnd(']')));
        if (readerName.StartsWith(core + "DictionaryReader`2[["))
        {
            var inner = readerName.Substring((core + "DictionaryReader`2[[").Length).TrimEnd(']');
            return ("dict", Norm(inner));
        }
        return ("other", Norm(readerName));
    }

    static Type MatchNorm(string norm) => norm != null && _typeByNorm.TryGetValue(norm, out var t) ? t : null;

    // 构造与反射 Type 对应的"目标规范名"（与 reader 名解析出的 TargetNorm 比较）
    static string NormOf(Type t)
    {
        if (t.IsGenericType)
        {
            var def = t.GetGenericTypeDefinition();
            var args = t.GetGenericArguments().Select(NormOf);
            return Norm(def.FullName.Split('[')[0]) + "[[" + string.Join("],[", args) + "]]";
        }
        return Norm(t.FullName);
    }

    static int R7() { int v = 0, sh = 0; while (true) { byte b = _d[_pos++]; v |= (b & 0x7f) << sh; if ((b & 0x80) == 0) break; sh += 7; } return v; }
    static string RStr() { int n = R7(); var s = Encoding.UTF8.GetString(_d, _pos, n); _pos += n; return s; }

    // ---- reader 表（语义解析） ----
    static void ParseReaderTable()
    {
        _readers.Clear();
        int n = R7();
        for (int i = 0; i < n; i++)
        {
            var name = RStr();
            int extra = BitConverter.ToInt32(_d, _pos); _pos += 4;
            var (kind, tnorm) = ReaderSemantics(name);
            _readers.Add(new ReaderInfo { Name = name, Kind = kind, TargetNorm = tnorm, Index = i });
        }
    }

    static Type ReaderTargetType(ReaderInfo r)
    {
        if (r.Kind.StartsWith("basic:"))
        {
            var t = Type.GetType(r.Kind.Substring(6) switch
            {
                "int" => "System.Int32", "long" => "System.Int64", "float" => "System.Single",
                "double" => "System.Double", "bool" => "System.Boolean", "byte" => "System.Byte",
                "sbyte" => "System.SByte", "short" => "System.Int16", "ushort" => "System.UInt16",
                "uint" => "System.UInt32", "ulong" => "System.UInt64", "char" => "System.Char",
                "string" => "System.String", "decimal" => "System.Decimal", _ => "System.DateTime"
            });
            return t;
        }
        if (r.Kind == "reflective" || r.Kind == "list" || r.Kind == "array" || r.Kind == "nullable" || r.Kind == "enum")
        {
            var inner = MatchNorm(r.TargetNorm);
            if (inner == null) throw new Exception("类型未匹配: " + r.TargetNorm);
            return r.Kind switch
            {
                "reflective" => inner,
                "list" => typeof(List<>).MakeGenericType(inner),
                "array" => inner.MakeArrayType(),
                "nullable" => typeof(Nullable<>).MakeGenericType(inner),
                "enum" => inner,
                _ => inner
            };
        }
        if (r.Kind == "dict")
        {
            // TargetNorm 形如 Dictionary`2[[K],[V]]（Norm 后）
            var inner = r.TargetNorm;   // 形如 K],[V（前缀 [[ 与尾部 ] 已在语义解析时剥除）
            var i1 = inner.IndexOf("],[");
            var k = MatchNorm(inner.Substring(0, i1));
            var v = MatchNorm(inner.Substring(i1 + 3));
            if (k == null || v == null) throw new Exception("字典类型未匹配: " + inner);
            return typeof(Dictionary<,>).MakeGenericType(k, v);
        }
        throw new Exception("未知 reader: " + r.Name);
    }

    // ---- 值 walker（Type 驱动） ----
    static void SkipValue(Type t, string path)
    {
        if (t == typeof(int) || t == typeof(uint) || t == typeof(float)) { _pos += 4; return; }
        if (t == typeof(long) || t == typeof(ulong) || t == typeof(double)) { _pos += 8; return; }
        if (t == typeof(short) || t == typeof(ushort) || t == typeof(char)) { _pos += 2; return; }
        if (t == typeof(bool) || t == typeof(byte) || t == typeof(sbyte)) { _pos += 1; return; }
        if (t == typeof(string)) { RStr(); return; }
        if (t.IsEnum) { _pos += 4; return; }
        var u = Nullable.GetUnderlyingType(t);
        if (u != null) { if (_d[_pos++] != 0) SkipValue(u, path); return; }
        if (t.IsArray)
        {
            uint cnt = BitConverter.ToUInt32(_d, _pos); _pos += 4;
            var et = t.GetElementType();
            for (uint i = 0; i < cnt; i++)
            {
                if (et.IsValueType) SkipValue(et, path);
                else { var r = ReadIndexed(); if (r != null) SkipByReader(r, path); }
            }
            return;
        }
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
        {
            int cnt = BitConverter.ToInt32(_d, _pos);
            if (cnt < 0 || cnt > 100000) { Console.WriteLine($"[diag] 异常 List count={cnt} @ {path} pos={_pos}"); foreach (var tt in _trace) Console.WriteLine("  " + tt); throw new Exception("List count 异常"); }
            _pos += 4;
            var et = t.GetGenericArguments()[0];
            for (int i = 0; i < cnt; i++)
            {
                if (et.IsValueType) SkipValue(et, path);
                else { var r = ReadIndexed(); if (r != null) SkipByReader(r, path); }
            }
            return;
        }
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Dictionary<,>))
        {
            int cnt = BitConverter.ToInt32(_d, _pos);
            if (cnt < 0 || cnt > 500000) { Console.WriteLine($"[diag] 异常 Dict count={cnt} @ {path} pos={_pos}"); foreach (var tt in _trace) Console.WriteLine("  " + tt); throw new Exception("Dict count 异常"); }
            _pos += 4;
            var ga = t.GetGenericArguments();
            for (int i = 0; i < cnt; i++)
            {
                SkipMemberValue(ga[0], path + ".K");
                SkipMemberValue(ga[1], path + ".V");
            }
            return;
        }
        // Reflective 对象：成员序（基类→Properties→Fields）
        SkipReflective(t, path, System.Array.Empty<string>(), 0, null, null);
    }

    // 成员值：值类型直读 / 引用类型 index+reader（与 ReadObject(reader) 规范一致）
    static void SkipMemberValue(Type mt, string path)
    {
        if (mt.IsValueType) SkipValue(mt, path);
        else { var r = ReadIndexed(); if (r != null) SkipByReader(r, path); }
    }

    static string _ctx = "";
    static System.Collections.Generic.List<string> _trace = new();
    static ReaderInfo ReadIndexed()
    {
        int idx = R7();
        if (idx > _readers.Count)
        {
            Console.WriteLine("[trace] 最近足迹:");
            foreach (var t in _trace) Console.WriteLine("   " + t);
            throw new Exception($"reader index {idx} 越界(共{_readers.Count}) @ {_ctx}");
        }
        return idx == 0 ? null : _readers[idx - 1];
    }

    static void SkipByReader(ReaderInfo r, string path)
    {
        var t = ReaderTargetType(r);
        SkipValue(t, path);
    }

    static void SkipReflective(Type t, string path, string[] wantPath, int depthIdx, string newValue, List<(int start, int end, byte[] repl)> hits)
    {
        var bt = t.BaseType;
        if (bt != null && bt != typeof(object)) SkipReflective(bt, path, wantPath, depthIdx, newValue, hits);
        const BindingFlags F = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var members = new List<MemberInfo>();
        foreach (var p in t.GetProperties(F))
        {
            var g = p.GetGetMethod(true);
            if (g == null || g != g.GetBaseDefinition()) continue;
            members.Add(p);
        }
        foreach (var f in t.GetFields(F)) members.Add(f);
        foreach (var m in members)
        {
            var mt = m is PropertyInfo p2 ? p2.PropertyType : ((FieldInfo)m).FieldType;
            bool isTarget = wantPath != null && depthIdx == wantPath.Length - 1 && m.Name == wantPath[depthIdx];
            int start = _pos;
            _ctx = path + "." + m.Name;
            int memberStart = _pos;
            SkipMemberValue(mt, path + "." + m.Name);
            int end = _pos;
            _trace.Add($"{_ctx} [{memberStart}-{end}] {mt.Name}");
            if (_trace.Count > 40) _trace.RemoveAt(0);
            if (isTarget)
            {
                if (!mt.IsValueType) throw new Exception($"试点仅支持值类型字段: {m.Name} ({mt.Name})");
                hits.Add((start, end, EncodeValue(mt, newValue)));
            }
        }
    }

    static byte[] EncodeValue(Type t, string s)
    {
        if (t == typeof(int)) return BitConverter.GetBytes(int.Parse(s));
        if (t == typeof(float)) return BitConverter.GetBytes(float.Parse(s));
        if (t == typeof(bool)) return new[] { (byte)((s == "true" || s == "1") ? 1 : 0) };
        if (t.IsEnum) return BitConverter.GetBytes(int.Parse(s));
        throw new Exception("试点暂支持 int/float/bool/enum");
    }

    public static int Run(string inXnb, string outXnb, string key, string field, string newValue)
    {
        BuildTypeUniverse();
        var raw = File.ReadAllBytes(inXnb);
        int flags = raw[5]; int totalSize = BitConverter.ToInt32(raw, 6);
        byte[] content;
        if ((flags & 0x80) != 0)
        {
            int dec = BitConverter.ToInt32(raw, 10);
            content = Decompress(raw, 14, totalSize - 14, dec);
        }
        else content = raw.Skip(10).Take(totalSize - 10).ToArray();

        _d = content; _pos = 0;
        ParseReaderTable();
        R7();
        var main = ReadIndexed();
        if (main == null) throw new Exception("主对象为空");
        var mt2 = ReaderTargetType(main);
        if (!mt2.IsGenericType || mt2.GetGenericTypeDefinition() != typeof(Dictionary<,>))
            throw new Exception("试点对象须为 Dictionary<,>: " + mt2?.FullName);
        var ga = mt2.GetGenericArguments();
        int cnt = BitConverter.ToInt32(_d, _pos); _pos += 4;
        var hits = new List<(int start, int end, byte[] repl)>();
        for (int i = 0; i < cnt; i++)
        {
            string k = null;
            var rk = ReadIndexed();
            if (rk != null)
            {
                var kt = ReaderTargetType(rk);
                if (kt == typeof(string)) k = RStr();
                else { SkipValue(kt, "(key)"); k = "(obj:" + kt.Name + ")"; }
            }
            var rv = ReadIndexed();
            if (rv == null) continue;
            int vStart = _pos;
            if (k == key)
            {
                var vt = ReaderTargetType(rv);   // WeaponData
                var wantPath = field.Split('.');
                SkipReflective(vt, vt.Name, wantPath, 0, newValue, hits);
            }
            else SkipByReader(rv, k ?? "?");
        }
        if (hits.Count == 0) throw new Exception($"未找到 {key}.{field}");
        var outp = new MemoryStream();
        int cur = 0;
        foreach (var (s0, e0, repl) in hits.OrderBy(h => h.start))
        {
            outp.Write(content, cur, s0 - cur);
            outp.Write(repl, 0, repl.Length);
            cur = e0;
        }
        outp.Write(content, cur, content.Length - cur);
        var packed = Wrap(outp.ToArray(), flags);
        File.WriteAllBytes(outXnb, packed);
        Console.WriteLine($"[xnbtool] patchdata {key}.{field} → {newValue}: {outXnb} ({packed.Length}B)");
        return 0;
    }

    static byte[] Decompress(byte[] d, int off, int compSize, int decSize)
    {
        var asm = typeof(Microsoft.Xna.Framework.Vector2).Assembly;
        var t = asm.GetTypes().First(x => x.Name == "LzxDecoderStream");
        var ctor = t.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            null, new[] { typeof(Stream), typeof(int), typeof(int) }, null);
        using var ms = new MemoryStream(d, off, compSize);
        using var lzx = (Stream)ctor.Invoke(new object[] { ms, decSize, compSize });
        var o = new byte[decSize];
        int rd = 0;
        while (rd < decSize) { int r = lzx.Read(o, rd, decSize - rd); if (r <= 0) break; rd += r; }
        if (rd != decSize) throw new IOException("LZX 解压不完整");
        return o;
    }

    static byte[] Wrap(byte[] content, int srcFlags)
    {
        int total = 10 + content.Length;
        var o = new byte[total];
        o[0] = (byte)'X'; o[1] = (byte)'N'; o[2] = (byte)'B'; o[3] = (byte)'w'; o[4] = 5;
        o[5] = (byte)(srcFlags & 0x01);
        BitConverter.GetBytes(total).CopyTo(o, 6);
        content.CopyTo(o, 10);
        return o;
    }
}
