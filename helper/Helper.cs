using System;
using System.Reflection;

namespace OHOS
{
    public static class ReflectionFix
    {
        // XmlSerializer 在 OHOS NativeAOT 真机上 GetMethod("Add", Type[]) 对部分
        // 泛型集合返回 null；先按原样查，null 则按名精确回退，最后无参版兜底。
        public static MethodInfo GetAddMethodSafe(Type type, Type elementType)
        {
            var m = type.GetMethod("Add", new Type[] { elementType });
            if (m != null)
                return m;
            try { Console.WriteLine($"[HLP] typed Add null: {type.FullName} elem={elementType?.FullName}"); } catch { }
            MethodInfo fallback = null;
            foreach (var x in type.GetMethods())
            {
                if (x.Name != "Add") continue;
                var ps = x.GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType == elementType)
                    return x;
                if (fallback == null)
                    fallback = x;
            }
            return fallback;
        }

        // 诊断：把 SDV catch 吞掉的存档读取异常打到控制台（含 inner 链）
        public static void LogException(Exception e)
        {
            try
            {
                Console.WriteLine("[SDV-X] caught: " + e);
                var ix = e.InnerException; int n = 0;
                while (ix != null && n++ < 6)
                {
                    Console.WriteLine("[SDV-X]   inner" + n + ": " + ix);
                    ix = ix.InnerException;
                }
            }
            catch { }
        }

        // 诊断：通用字符串日志
        public static void LogString(string s)
        {
            try { Console.WriteLine("[SDV-X] " + s); } catch { }
        }

        // 诊断：流长度 / 对象类型
        public static void LogStreamLen(System.IO.Stream s)
        {
            try { Console.WriteLine($"[SDV-X] stream len={(s == null ? -1L : s.Length)}"); } catch (Exception e) { Console.WriteLine("[SDV-X] len FAIL: " + e.Message); }
        }

        public static void LogObj(object o)
        {
            try { Console.WriteLine("[SDV-X] result=" + (o == null ? "(null)" : o.GetType().Name)); } catch { }
        }

        // Lidgren ComputeSHAHash 替代：NativeAOT 下 SHA256.ComputeHash 路径崩溃。
        // 用途仅为生成本机标识（非加密安全），用确定性 FNV-1a 双通道扩展成 32 字节。
        public static byte[] ComputeShaFallback(byte[] data, int offset, int count)
        {
            ulong h1 = 14695981039346656037UL;
            ulong h2 = 1099511628211UL ^ (ulong)count;
            int end = offset + count;
            for (int i = offset; i < end && i < data.Length; i++)
            {
                h1 = (h1 ^ data[i]) * 1099511628211UL;
                h2 = (h2 ^ (data[i] + (ulong)i)) * 14695981039346656037UL;
            }
            var res = new byte[32];
            for (int j = 0; j < 4; j++)
            {
                ulong v = (j % 2 == 0 ? h1 : h2) ^ (h1 << (j * 5 + 1)) ^ (h2 >> (j * 3));
                byte[] vb = BitConverter.GetBytes(v);
                Array.Copy(vb, 0, res, j * 8, 8);
            }
            return res;
        }

        // 集合保障修复配套：安全读取成员现有值（readonly 字段/无 setter 属性）
        public static object GetMemberValueSafe(object o, System.Reflection.MemberInfo mi)
        {
            try
            {
                if (o == null || mi == null) return null;
                var f = mi as System.Reflection.FieldInfo;
                if (f != null) return f.GetValue(o);
                var p = mi as System.Reflection.PropertyInfo;
                if (p != null) return p.GetValue(o);
            }
            catch { }
            return null;
        }

        // 集合保障收尾的安全写回：复用现有实例时 setter 可能因类型校验拒绝，
        // 若当前成员值就是该实例（无变化），吞掉异常等价于无操作；否则原样抛出。
        public static void SetMemberValueSafe(Delegate d, object o, object v, System.Reflection.MemberInfo mi)
        {
            try { d.DynamicInvoke(new object[] { o, v }); }
            catch (Exception)
            {
                var cur = GetMemberValueSafe(o, mi);
                if (ReferenceEquals(cur, v)) return;
                throw;
            }
        }

        // 读取侧第二处（ReflectionXmlSerializationReader.WriteLiteralStructMethod 的
        // get-only 列表属性 lambda）：GetMethod("Add") 同样多重重载歧义。
        // 名称解析优先单参，兜底首个（真正形态适配交给 AddItemSafe）。
        public static MethodInfo GetAddMethodForDict(Type type)
        {
            MethodInfo one = null, first = null;
            foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name != "Add" || m.IsStatic) continue;
                if (first == null) first = m;
                if (one == null && m.GetParameters().Length == 1) one = m;
            }
            var picked = (object)one != null ? one : first;
            try { Console.WriteLine($"[HLP] GetAddMethodForDict {type.Name}: picked={picked?.GetParameters().Length}param"); } catch { }
            return picked;
        }

        // 调用适配：add.Invoke(coll, args) — 当 args 是单个 KeyValuePair 而 add 是
        // 双参 Add(K,V) 时（字典场景，sgen 语义），拆 KVP 为两参调用。
        public static object AddItemSafe(MethodInfo add, object coll, object[] args)
        {
            try { return add.Invoke(coll, args); }
            catch (ArgumentException)
            {
                if (args != null && args.Length == 1 && args[0] != null)
                {
                    var vt = args[0].GetType();
                    var kp = vt.GetProperty("Key");
                    var vp = vt.GetProperty("Value");
                    if (kp != null && vp != null)
                    {
                        return add.Invoke(coll, new object[] { kp.GetValue(args[0]), vp.GetValue(args[0]) });
                    }
                }
                throw;
            }
        }

        // 读取侧（ReflectionXmlSerializationReader.AddObjectsIntoTargetCollection）：
        // Type.GetMethod("Add") 仅按名查询，多重重载在 AOT 下抛 AmbiguousMatchException。
        // reader 用单参 Invoke，优先选"参数能接收元素运行时类型"的单参 Add
        // （如 NetDescriptionElementList 有 Add(String) 干扰，须选 Add(T=DescriptionElement)）。
        public static MethodInfo GetAddMethodForItems(Type type, System.Collections.IList items)
        {
            Type elem = null;
            if (items != null && items.Count > 0 && items[0] != null) elem = items[0].GetType();
            MethodInfo first = null, one = null, match = null;
            foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name != "Add" || m.IsStatic) continue;
                if (first == null) first = m;
                var ps = m.GetParameters();
                if (ps.Length == 1)
                {
                    if (one == null) one = m;
                    if (elem != null && ps[0].ParameterType.IsAssignableFrom(elem)) { match = m; }
                }
            }
            var picked = (object)match != null ? match : ((object)one != null ? one : first);
            try { Console.WriteLine($"[HLP] GetAddMethodForItems {type.Name}: elem={elem?.Name} picked={picked?.GetParameters().Length}param {picked?.GetParameters()[0].ParameterType.Name}"); } catch { }
            return picked;
        }

    // 反射版 LocalMultiplayer 静态字段存取（替代 Reflection.Emit 版本，功能等价）：
    //   StaticSetDefault(holder): h[i] = defaults[i]
    //   StaticSave(holder):       h[i] = fields[i] 当前值
    //   StaticLoad(holder):       fields[i] = h[i]
    // holder 为 object[]（Emit 版用动态类型实例，语义相同）。
    internal class StaticVarsImpl
    {
        readonly System.Collections.Generic.List<System.Reflection.FieldInfo> _fields;
        readonly System.Collections.Generic.List<object> _defaults;
        internal StaticVarsImpl(System.Collections.Generic.List<System.Reflection.FieldInfo> f, System.Collections.Generic.List<object> d)
        { _fields = f; _defaults = d; }
        static string Key(System.Reflection.FieldInfo f, int i) { return f.DeclaringType.Name + "_" + f.Name; }
        internal void SetDefault(object holder)
        {
            var h = (System.Collections.Generic.Dictionary<string, object>)holder;
            for (int i = 0; i < _defaults.Count && i < _fields.Count; i++) h[Key(_fields[i], i)] = _defaults[i];
        }
        internal void Save(object holder)
        {
            var h = (System.Collections.Generic.Dictionary<string, object>)holder;
            for (int i = 0; i < _fields.Count; i++) h[Key(_fields[i], i)] = _fields[i].GetValue(null);
        }
        internal void Load(object holder)
        {
            var h = (System.Collections.Generic.Dictionary<string, object>)holder;
            for (int i = 0; i < _fields.Count; i++) { var k = Key(_fields[i], i); if (h.ContainsKey(k)) _fields[i].SetValue(null, h[k]); }
        }
    }

    public static void InitStaticVars()
    {
        var lm = System.Type.GetType("StardewValley.LocalMultiplayer, Stardew Valley");
        if (lm == null) return;
        const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        var fFields = lm.GetField("staticFields", F);
        var fDefaults = lm.GetField("staticDefaults", F);
        if (fFields == null || fDefaults == null) return;
        var fields = fFields.GetValue(null) as System.Collections.Generic.List<System.Reflection.FieldInfo>;
        var defaults = fDefaults.GetValue(null) as System.Collections.Generic.List<object>;
        if (fields == null) return;
        var impl = new StaticVarsImpl(fields, defaults ?? new System.Collections.Generic.List<object>());
        lm.GetField("StaticVarHolderType", F)?.SetValue(null, typeof(System.Collections.Generic.Dictionary<string, object>));
        var delegateType = lm.GetNestedType("StaticInstanceMethod");
        if (delegateType == null) return;
        lm.GetField("StaticSetDefault", F)?.SetValue(null, System.Delegate.CreateDelegate(delegateType, impl, typeof(StaticVarsImpl).GetMethod("SetDefault", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)));
        lm.GetField("StaticSave", F)?.SetValue(null, System.Delegate.CreateDelegate(delegateType, impl, typeof(StaticVarsImpl).GetMethod("Save", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)));
        lm.GetField("StaticLoad", F)?.SetValue(null, System.Delegate.CreateDelegate(delegateType, impl, typeof(StaticVarsImpl).GetMethod("Load", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)));
        System.Console.WriteLine("[HLP] InitStaticVars OK: " + fields.Count + " fields (reflection impl)");
    }

    // 反射版深/浅克隆（替代 DeepCloner 的表达式生成版，功能等价：字段递归 + 数组逐元素 + 循环引用检测）
    internal static class CloneShim
    {
        static readonly System.Collections.Generic.HashSet<Type> Simple = new System.Collections.Generic.HashSet<Type>
        {
            typeof(string), typeof(decimal), typeof(DateTime), typeof(TimeSpan), typeof(Guid), typeof(IntPtr)
        };
        static bool IsSimple(Type t)
        {
            return t.IsPrimitive || t.IsEnum || Simple.Contains(t) || (t.IsValueType && t.Namespace == "Microsoft.Xna.Framework");
        }
        static void CloneArrayDim(System.Array src, System.Array dst, long[] idx, int dim, bool deep, System.Collections.Generic.Dictionary<object, object> map)
        {
            if (dim == idx.Length)
            {
                var v = src.GetValue(idx);
                if (v != null && !IsSimple(v.GetType()))
                    dst.SetValue(CloneCore(v, deep, map), idx);
                return;
            }
            for (long i = 0; i < src.GetLongLength(dim); i++)
            {
                idx[dim] = i;
                CloneArrayDim(src, dst, idx, dim + 1, deep, map);
            }
        }

        public static object Clone(object obj, bool deep)
        {
            return CloneCore(obj, deep, new System.Collections.Generic.Dictionary<object, object>());
        }
        static object CloneCore(object obj, bool deep, System.Collections.Generic.Dictionary<object, object> map)
        {
            if (obj == null) return null;
            var t = obj.GetType();
            if (IsSimple(t)) return obj;
            object cached;
            if (map.TryGetValue(obj, out cached)) return cached;
            if (t.IsArray)
            {
                var arr = (System.Array)obj;
                var copy = (System.Array)arr.Clone();
                map[obj] = copy;
                if (deep && arr.Rank == 1)
                {
                    for (int i = 0; i < copy.Length; i++)
                    {
                        var v = copy.GetValue(i);
                        if (v != null && !IsSimple(v.GetType()))
                            copy.SetValue(CloneCore(v, deep, map), i);
                    }
                }
                else if (deep)
                {
                    // 多维数组：按每维下标递归遍历
                    var idx = new long[arr.Rank];
                    CloneArrayDim(arr, copy, idx, 0, deep, map);
                }
                return copy;
            }
            var mc = t.GetMethod("MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            object copyC = mc != null ? mc.Invoke(obj, null) : obj;
            map[obj] = copyC;
            if (deep)
            {
                for (var bt = t; bt != null && bt != typeof(object); bt = bt.BaseType)
                {
                    foreach (var f in bt.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                    {
                        var v = f.GetValue(obj);
                        if (v == null || IsSimple(v.GetType())) continue;
                        f.SetValue(copyC, CloneCore(v, deep, map));
                    }
                }
            }
            return copyC;
        }
    }
}
}
