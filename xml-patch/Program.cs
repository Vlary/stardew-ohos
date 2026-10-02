using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// 用法: xml-patch <System.Private.Xml.dll> <helper.dll> <输出>
var input = args[0];
var helperPath = args[1];
var output = args[2];

var asm = AssemblyDefinition.ReadAssembly(input);
var helperAsm = AssemblyDefinition.ReadAssembly(helperPath);
var mod = asm.MainModule;

var helperType = helperAsm.MainModule.GetType("OHOS.ReflectionFix");
var helperMethod = helperType.Methods.First(m => m.Name == "GetAddMethodSafe");
var helperRef = mod.ImportReference(helperMethod);
var helperSingle = helperType.Methods.First(m => m.Name == "GetAddMethodForItems");
var helperSingleRef = mod.ImportReference(helperSingle);

var typeScope = mod.GetType("System.Xml.Serialization.TypeScope");
if (typeScope == null) { Console.WriteLine("TypeScope not found!"); Environment.Exit(1); }

int total = 0;
foreach (var m in typeScope.Methods)
{
    if (!m.HasBody) continue;
    var il = m.Body.GetILProcessor();
    var instrs = m.Body.Instructions;
    for (int i = 0; i < instrs.Count; i++)
    {
        if (instrs[i].OpCode == OpCodes.Callvirt && instrs[i].Operand is MethodReference mr &&
            mr.Name == "GetMethod" && mr.Parameters.Count == 2 &&
            mr.Parameters[1].ParameterType is ArrayType)
        {
            int ldstrIdx = -1;
            for (int k = i - 1; k >= Math.Max(0, i - 12); k--)
            {
                if (instrs[k].OpCode == OpCodes.Ldstr && (string)instrs[k].Operand == "Add") { ldstrIdx = k; break; }
            }
            if (ldstrIdx < 0) continue;

            // nop 掉数组构造（ldc/newarr/dup/ldc/stelem），保留 stelem 前压 elementType 的最后一条指令
            for (int k = ldstrIdx + 1; k < i; k++)
            {
                var op = instrs[k].OpCode;
                bool isArr =
                    op == OpCodes.Ldc_I4_1 || op == OpCodes.Ldc_I4_0 ||
                    op == OpCodes.Newarr || op == OpCodes.Dup ||
                    op == OpCodes.Stelem_Any || op == OpCodes.Stelem_Ref;
                // 找 stelem 的位置：最后一条 Stelem_Any
                if (isArr)
                {
                    // ldc.i4.0 与 stelem 必须成对 nop（dup 后的 ldc.i4.0）
                    il.Replace(instrs[k], il.Create(OpCodes.Nop));
                }
            }
            il.Replace(instrs[ldstrIdx], il.Create(OpCodes.Nop));
            il.Replace(instrs[i], il.Create(OpCodes.Call, helperRef));
            Console.WriteLine($"{m.Name}: GetMethod(Add,Type[]) -> GetAddMethodSafe @ il{i}");
            total++;
        }
    }
}
// 读取侧第二处：WriteLiteralStructMethod 的 get-only 列表属性 lambda 中
// GetMethod("Add")（多重重载歧义）→ GetAddMethodForDict；并把
// MethodBase::Invoke(Object,Object[]) 调用换成 AddItemSafe（KVP→双参拆解）。
{
    var helperDict = helperType.Methods.First(m => m.Name == "GetAddMethodForDict");
    var helperDictRef = mod.ImportReference(helperDict);
    var helperAddItem = helperType.Methods.First(m => m.Name == "AddItemSafe");
    var helperAddItemRef = mod.ImportReference(helperAddItem);

    foreach (var t in mod.GetTypes())
    {
        /* 含嵌套 display class（lambda 编译进 <>c__DisplayClass 等嵌套类型） */
        foreach (var m in t.Methods)
        {
            if (!m.HasBody) continue;
            // AddObjectsIntoTargetCollection 由后段用元素类型匹配版（GetAddMethodForItems）处理
            bool skipNaive = m.Name == "AddObjectsIntoTargetCollection";
            var instrs = m.Body.Instructions;
            var il = m.Body.GetILProcessor();
            for (int i = 1; i < instrs.Count; i++)
            {
                // A) ldstr "Add" + callvirt Type::GetMethod(String)
                if (skipNaive || !t.FullName.Contains("ReflectionXmlSerializationReader")) { } else
                if (instrs[i].OpCode == OpCodes.Callvirt && instrs[i].Operand is MethodReference mra &&
                    mra.Name == "GetMethod" && mra.DeclaringType.Name == "Type" && mra.Parameters.Count == 1 &&
                    instrs[i - 1].OpCode == OpCodes.Ldstr && (string)instrs[i - 1].Operand == "Add")
                {
                    il.Replace(instrs[i - 1], il.Create(OpCodes.Nop));
                    il.Replace(instrs[i], il.Create(OpCodes.Call, helperDictRef));
                    Console.WriteLine($"{t.Name}::{m.Name}: GetMethod(Add) → GetAddMethodForDict @ il{i}");
                    total++;
                }
                // B) MethodBase::Invoke(Object,Object[]) → AddItemSafe(MethodInfo,Object,Object[])
                if (instrs[i].OpCode == OpCodes.Callvirt && instrs[i].Operand is MethodReference mrb &&
                    mrb.Name == "Invoke" && mrb.DeclaringType.Name == "MethodBase" && mrb.Parameters.Count == 2)
                {
                    il.Replace(instrs[i], il.Create(OpCodes.Call, helperAddItemRef));
                    Console.WriteLine($"{t.Name}::{m.Name}: MethodBase::Invoke → AddItemSafe @ il{i}");
                    total++;
                }
            }
        }
    }
}

// 读取侧第三处：WriteLiteralStructMethod 尾部的"集合保障"循环从 null 新建集合——
// OverlaidDictionary 等无参构造缺失的类型直接崩。改为从成员现有值开始
// （ctor 已初始化，如 GameLocation.objects），既有值则填充复用，无则维持原新建逻辑。
{
    var helperMv = helperType.Methods.First(m => m.Name == "GetMemberValueSafe");
    var helperMvRef = mod.ImportReference(helperMv);
    var rxsr = mod.GetType("System.Xml.Serialization.ReflectionXmlSerializationReader");
    if (rxsr != null)
    {
        foreach (var m in rxsr.Methods)
        {
            if (m.Name != "WriteLiteralStructMethod" || !m.HasBody) continue;
            var ins = m.Body.Instructions;
            var il = m.Body.GetILProcessor();
            for (int i = 0; i < ins.Count; i++)
            {
                if (!(ins[i].Operand is MethodReference mr && mr.Name == "SetCollectionObjectWithCollectionMember")) continue;
                // 回看：i-9 ldnull / i-8 stloc V / i-7 ldloca V
                if (i < 12) continue;
                if (ins[i - 9].OpCode != OpCodes.Ldnull) continue;
                if (!ins[i - 8].OpCode.Code.ToString().StartsWith("Stloc")) continue;
                if (!ins[i - 7].OpCode.Code.ToString().StartsWith("Ldloca")) continue;
                var vloc = ins[i - 8].Operand;
                if (ins[i - 7].Operand != vloc) continue;
                // 找到 display 类字段 o（从 i-18 附近的 ldfld o）
                FieldReference oField = null;
                for (int k = i - 20; k < i - 9; k++)
                    if (k >= 0 && ins[k].OpCode == OpCodes.Ldfld && ins[k].Operand is FieldReference fr && fr.Name == "o")
                        oField = fr;
                if (oField == null) { Console.WriteLine("!! 未找到 display.o 字段"); continue; }
                var memberInfoLoc = ins[i - 10].Operand;   // V_27 (memberInfo)
                // 649 处 ldnull → ldloc.0
                il.Replace(ins[i - 9], il.Create(OpCodes.Ldloc_0));
                // 其后按顺序插入：ldfld o; ldloc V27; call GetMemberValueSafe
                // （InsertAfter 链式推进，避免连续 InsertAfter(cur,..) 造成倒序）
                var cur = ins[i - 9];
                foreach (var newIns in new[]
                {
                    il.Create(OpCodes.Ldfld, oField),
                    il.Create(OpCodes.Ldloc, (VariableDefinition)memberInfoLoc),
                    il.Create(OpCodes.Call, helperMvRef)
                })
                {
                    il.InsertAfter(cur, newIns);
                    cur = newIns;
                }
                Console.WriteLine($"WriteLiteralStructMethod: 集合保障 → 复用现有成员值 @ il{i}");
                total++;
            }
        }
    }
}

// 读取侧第四处：集合保障循环的 setMemberValue(o, collection) →
// SetMemberValueSafe(delegate, o, collection, memberInfo)（同实例=无操作）。
{
    var helperSv = helperType.Methods.First(m => m.Name == "SetMemberValueSafe");
    var helperSvRef = mod.ImportReference(helperSv);
    var rxsr2 = mod.GetType("System.Xml.Serialization.ReflectionXmlSerializationReader");
    if (rxsr2 != null)
    {
        foreach (var m in rxsr2.Methods)
        {
            if (m.Name != "WriteLiteralStructMethod" || !m.HasBody) continue;
            var ins = m.Body.Instructions;
            var il = m.Body.GetILProcessor();
            /* 快照目标后统一修补（防止边遍历边改造成链式注入） */
            var sites = new System.Collections.Generic.List<(Mono.Cecil.Cil.Instruction call, Mono.Cecil.Cil.VariableDefinition vmi)>();
            for (int i = 0; i < ins.Count; i++)
            {
                if (ins[i].OpCode == OpCodes.Callvirt && ins[i].Operand is MethodReference mv &&
                    mv.Name == "Invoke" && mv.DeclaringType.Name == "SetMemberValueDelegate")
                {
                    Mono.Cecil.Cil.VariableDefinition vmi = null;
                    for (int k = i - 1; k >= 0 && k > i - 30; k--)
                        if (ins[k].OpCode.Code.ToString().StartsWith("Stloc") && ins[k].Operand is Mono.Cecil.Cil.VariableDefinition vd && vd.VariableType.Name == "MemberInfo") { vmi = vd; break; }
                    if (vmi != null) sites.Add((ins[i], vmi));
                }
            }
            foreach (var (callIns, vmi) in sites)
            {
                il.InsertBefore(callIns, il.Create(OpCodes.Ldloc, vmi));
                il.Replace(callIns, il.Create(OpCodes.Call, helperSvRef));
                Console.WriteLine($"WriteLiteralStructMethod: setMemberValue → SetMemberValueSafe");
                total++;
            }
            Console.WriteLine($"WriteLiteralStructMethod: setMemberValue 站点数={sites.Count}");
        }
    }
}

// 读取侧：ReflectionXmlSerializationReader.AddObjectsIntoTargetCollection 的
// GetMethod("Add")（仅按名）在 AOT 对多重重载集合抛 AmbiguousMatchException
// （存档读取闪退根因）。替换为 GetSingleAddMethod（优先单参 Add）。
{
    var readerType = mod.GetType("System.Xml.Serialization.ReflectionXmlSerializationReader");
    if (readerType == null) { Console.WriteLine("!! ReflectionXmlSerializationReader not found"); }
    else
    {
        foreach (var m in readerType.Methods)
        {
            if (m.Name != "AddObjectsIntoTargetCollection" || !m.HasBody) continue;
            var il = m.Body.GetILProcessor();
            var instrs = m.Body.Instructions;
            for (int i = 1; i < instrs.Count; i++)
            {
                if (instrs[i].OpCode == OpCodes.Callvirt && instrs[i].Operand is MethodReference mr &&
                    mr.Name == "GetMethod" && mr.DeclaringType.Name == "Type" &&
                    instrs[i - 1].OpCode == OpCodes.Ldstr && (string)instrs[i - 1].Operand == "Add")
                {
                    /* ldstr "Add" → ldarg.1（items 列表）：栈变 [targetType][items] 供双参 helper */
                    il.Replace(instrs[i - 1], il.Create(OpCodes.Ldarg_1));
                    il.Replace(instrs[i], il.Create(OpCodes.Call, helperSingleRef));
                    Console.WriteLine($"AddObjectsIntoTargetCollection: GetMethod(Add) -> GetAddMethodForItems @ il{i}");
                    total++;
                }
            }
        }
    }
}

asm.Write(output);
Console.WriteLine($"done: {total} -> {output}");
