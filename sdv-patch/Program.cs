using System;
using System.Linq;
using Mono.Cecil;

// 用法: sdv-patch <Stardew Valley.dll> <输出>
// 给与顶级类型 XML 名冲突的嵌套类型加 XmlType 别名（sgen 反射扫描需要）
var input = args[0];
var helperPath2 = args.Length > 2 ? args[2] : (Environment.GetEnvironmentVariable("OHOS_HELPER") ?? "helper/bin/Release/netstandard2.0/OHOS.Helper.dll");
var output = args[1];
var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(input)));
var asm = AssemblyDefinition.ReadAssembly(input, new ReaderParameters { AssemblyResolver = resolver });
var mod = asm.MainModule;

// XmlTypeAttribute(string typeName) 的 ctor 引用：从已引用的 System.Xml 解析
TypeReference xmlTypeRef = null;
foreach (var ar in mod.AssemblyReferences)
{
    if (ar.Name == "System.Xml.XmlSerializer")
    {
        xmlTypeRef = new TypeReference("System.Xml.Serialization", "XmlTypeAttribute", mod, ar);
        break;
    }
}
if (xmlTypeRef == null)
{
    // 游戏可能经别的名字引用——直接造
    var sysXml = new AssemblyNameReference("System.Xml.XmlSerializer", new Version(9, 0, 0, 0));
    if (!mod.AssemblyReferences.Any(a => a.Name == sysXml.Name)) mod.AssemblyReferences.Add(sysXml);
    xmlTypeRef = new TypeReference("System.Xml.Serialization", "XmlTypeAttribute", mod, sysXml);
}
var ctor = new MethodReference(".ctor", mod.TypeSystem.Void, xmlTypeRef) { HasThis = true };
ctor.Parameters.Add(new ParameterDefinition(mod.TypeSystem.String));

int total = 0;
// 1. LocalMultiplayer.GenerateDynamicMethodsForStatics：NativeAOT 无 Reflection.Emit，
//    方法体改为调用 OHOS.ReflectionFix.InitStaticVars()（反射版等价实现，功能不阉割：
//    联机/存档的静态字段 Save/Load/SetDefault 全保留）
{
    var lm = mod.GetType("StardewValley.LocalMultiplayer");
    if (lm != null)
    {
        var gm = lm.Methods.FirstOrDefault(x => x.Name == "GenerateDynamicMethodsForStatics");
        if (gm != null && gm.HasBody)
        {
            var helperAsm2 = AssemblyDefinition.ReadAssembly(helperPath2);
            var hType = helperAsm2.MainModule.GetType("OHOS.ReflectionFix");
            var initM = hType.Methods.First(x => x.Name == "InitStaticVars");
            var initRef = mod.ImportReference(initM);
            var ilp = gm.Body.GetILProcessor();
            gm.Body.Instructions.Clear();
            gm.Body.Variables.Clear();
            gm.Body.ExceptionHandlers.Clear();
            ilp.Emit(Mono.Cecil.Cil.OpCodes.Call, initRef);
            ilp.Emit(Mono.Cecil.Cil.OpCodes.Ret);
            Console.WriteLine("LocalMultiplayer::GenerateDynamicMethodsForStatics → InitStaticVars(反射版)");
            total++;
        }
    }
}
foreach (var t in EnumAllTypes(mod))
{
    // 嵌套且是枚举/简单类型、与顶级类型短名冲突的：QuestContainerMenu.ChangeType 等
    if (t.IsNested && t.Name == "ChangeType")
    {
        var ca = new CustomAttribute(ctor);
        ca.ConstructorArguments.Add(new CustomAttributeArgument(mod.TypeSystem.String, "QuestContainerMenuChangeType"));
        t.CustomAttributes.Add(ca);
        Console.WriteLine($"XmlType alias: {t.FullName} → QuestContainerMenuChangeType");
        total++;
    }
}

// 2. DeepCloner 的表达式生成在 NativeAOT 不可行（MakeGenericMethod 无 native code）：
//    DeepClonerGenerator/ShallowClonerGenerator.CloneObject<T> 替换为反射克隆 shim（功能等价）
{
    var helperAsm3 = AssemblyDefinition.ReadAssembly(helperPath2);
    var shim = helperAsm3.MainModule.GetType("OHOS.ReflectionFix/CloneShim");
    var cloneM = shim.Methods.First(x => x.Name == "Clone");
    var cloneRef = mod.ImportReference(cloneM);

    foreach (var typeName in new[] { "Force.DeepCloner.Helpers.DeepClonerGenerator", "Force.DeepCloner.Helpers.ShallowClonerGenerator" })
    {
        var gen = mod.GetType(typeName);
        if (gen == null) continue;
        var m = gen.Methods.FirstOrDefault(x => x.Name == "CloneObject" && x.HasGenericParameters);
        if (m == null || !m.HasBody) continue;
        var gp = m.GenericParameters[0];
        var ilp = m.Body.GetILProcessor();
        m.Body.Instructions.Clear();
        m.Body.Variables.Clear();
        m.Body.ExceptionHandlers.Clear();
        ilp.Emit(Mono.Cecil.Cil.OpCodes.Ldarg_0);
        ilp.Emit(Mono.Cecil.Cil.OpCodes.Box, gp);
        ilp.Emit(Mono.Cecil.Cil.OpCodes.Ldc_I4, typeName.Contains("Deep") ? 1 : 0);
        ilp.Emit(Mono.Cecil.Cil.OpCodes.Call, cloneRef);
        ilp.Emit(Mono.Cecil.Cil.OpCodes.Unbox_Any, gp);
        ilp.Emit(Mono.Cecil.Cil.OpCodes.Ret);
        Console.WriteLine($"{typeName}::CloneObject<T> → CloneShim");
        total++;
    }
}


// 3. Game1 构造的 backbuffer 1280x720 → 屏幕原生 3120x2080
//    （OHOS 全屏窗口=EGL surface 3120x2080，backbuffer 同尺寸才铺满；星露谷 UI 自适应任意分辨率）
{
    foreach (var ty in mod.Types)
    foreach (var m2 in ty.Methods)
    {
            if (!m2.HasBody) continue;
            var ins2 = m2.Body.Instructions;
            for (int i3 = 0; i3 < ins2.Count; i3++)
            {
                if (ins2[i3].OpCode == Mono.Cecil.Cil.OpCodes.Callvirt && ins2[i3].Operand is MethodReference mr3 &&
                    (mr3.Name == "set_PreferredBackBufferWidth" || mr3.Name == "set_PreferredBackBufferHeight") &&
                    i3 > 0 && ins2[i3 - 1].OpCode == Mono.Cecil.Cil.OpCodes.Ldc_I4)
                {
                    int v = (int)ins2[i3 - 1].Operand;
                    if (v == 1280) { ins2[i3 - 1].Operand = 3120; Console.WriteLine($"Game1::{m2.Name}: Width 1280→3120"); total++; }
                    else if (v == 720) { ins2[i3 - 1].Operand = 2080; Console.WriteLine($"Game1::{m2.Name}: Height 720→2080"); total++; }
                }
            }
    }
}

// 4. Item 派生类注入 ShouldSerializeparentSheetIndex override：
//    .NET XmlSerializer 反射 writer（AOT 路径）对 CheckShouldPersist 成员用
//    GetType().GetMethod("ShouldSerializeXxx", DeclaredOnly) 现场反射，方法
//    声明在基类 StardewValley.Item 时派生类（Object/Axe/Chest/...）root 序列化
//    会拿到 null 直接 NRE（存档时 Inventory.WriteXml 崩溃）。给每个未声明
//    的派生类注入转发到基类的 override，DeclaredOnly 即可命中。
{
    MethodReference baseMethod = null;
    foreach (var ty in mod.Types)
    {
        if (ty.FullName != "StardewValley.Item") continue;
        foreach (var m2 in ty.Methods)
        {
            if (m2.Name == "ShouldSerializeparentSheetIndex" && !m2.HasParameters)
            {
                baseMethod = m2;
                break;
            }
        }
        break;
    }
    if (baseMethod == null)
    {
        Console.WriteLine("!! 未找到 StardewValley.Item::ShouldSerializeparentSheetIndex");
    }
    else
    {
        var objType = baseMethod.DeclaringType;
        foreach (var ty in mod.Types)
        {
            bool isDerived = false;
            for (var bt = ty.BaseType; bt != null; )
            {
                var btd = bt.Resolve();
                if (btd == null) break;
                if (btd == objType) { isDerived = true; break; }
                bt = btd.BaseType;
            }
            if (!isDerived) continue;
            bool hasOwn = false;
            foreach (var m2 in ty.Methods)
                if (m2.Name == "ShouldSerializeparentSheetIndex") { hasOwn = true; break; }
            if (hasOwn) continue;

            var nm = new Mono.Cecil.MethodDefinition("ShouldSerializeparentSheetIndex",
                Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Virtual |
                Mono.Cecil.MethodAttributes.HideBySig,
                mod.TypeSystem.Boolean);
            nm.Body.Instructions.Add(Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Ldarg_0));
            nm.Body.Instructions.Add(Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Call, baseMethod));
            nm.Body.Instructions.Add(Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Ret));
            ty.Methods.Add(nm);
            total++;
        }
        Console.WriteLine($"ShouldSerialize override 注入完成（累计 {total}）");
    }
}

// 4.5 诊断：TryReadSaveFile 的 catch (Exception ex2) { error = ex2.Message; return null; }
//     会把读取异常吞进 error 字符串，加载失败时无法诊断——在 handler 入口注入
//     dup + OHOS.ReflectionFix.LogException，异常原文（含 inner 链）进控制台。
{
    var sbType = asm.MainModule.GetType("StardewValley.SaveGame");
    MethodDefinition target = null;
    if (sbType != null)
        foreach (var m2 in sbType.Methods)
            if (m2.Name == "TryReadSaveFile" && m2.HasBody) { target = m2; break; }
    if (target == null) { Console.WriteLine("!! 未找到 TryReadSaveFile"); }
    else
    {
        var helperAsmRef = asm.MainModule.AssemblyReferences.FirstOrDefault(a => a.Name == "OHOS.Helper");
        if (helperAsmRef == null)
        {
            helperAsmRef = new AssemblyNameReference("OHOS.Helper", new Version(1, 0, 0, 0));
            asm.MainModule.AssemblyReferences.Add(helperAsmRef);
        }
        var rfType = new TypeReference("OHOS", "ReflectionFix", asm.MainModule, helperAsmRef);
        var logEx = new MethodReference("LogException", asm.MainModule.TypeSystem.Void, rfType) { HasThis = false };
        logEx.Parameters.Add(new ParameterDefinition(new TypeReference("System", "Exception", asm.MainModule, asm.MainModule.TypeSystem.CoreLibrary)));

        var ilp = target.Body.GetILProcessor();
        int n = 0;
        foreach (var eh in target.Body.ExceptionHandlers)
        {
            if (eh.HandlerType != Mono.Cecil.Cil.ExceptionHandlerType.Catch) continue;
            if (eh.CatchType == null || eh.CatchType.Name != "Exception") continue;
            /* 注意：不能 InsertBefore(HandlerStart)（Cecil 不会扩展 handler 范围，
             * 注入的指令落在 handler 外永不执行）。改插到 HandlerStart 之后：
             * 首指令为 stloc (VariableDefinition)，随后用 ldloc 取异常打日志。 */
            var hs = eh.HandlerStart;
            if (hs.Operand is Mono.Cecil.Cil.VariableDefinition vd)
            {
                ilp.InsertAfter(hs, Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Ldloc, vd));
                ilp.InsertAfter(hs.Next, Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Call, logEx));
                n++;
            }
            else
            {
                Console.WriteLine($"!! HandlerStart 非常规指令: {hs.OpCode} {hs.Operand}");
            }
        }
        Console.WriteLine($"TryReadSaveFile: catch 注入 LogException x{n}");
        total += n;
    }
}

// 4.6 诊断：TryReadSaveFile 路径探针（定位 FileDoesNotExist 时实际查找的路径）
{
    var sbType2 = asm.MainModule.GetType("StardewValley.SaveGame");
    MethodDefinition trg2 = null;
    foreach (var m2 in sbType2.Methods)
        if (m2.Name == "TryReadSaveFile" && m2.HasBody) { trg2 = m2; break; }
    if (trg2 == null) { Console.WriteLine("!! TryReadSaveFile 未找到"); }
    else
    {
        var helperAsmRef2 = asm.MainModule.AssemblyReferences.First(a => a.Name == "OHOS.Helper");
        var rfType2 = new TypeReference("OHOS", "ReflectionFix", asm.MainModule, helperAsmRef2);
        var logStr = new MethodReference("LogString", asm.MainModule.TypeSystem.Void, rfType2) { HasThis = false };
        logStr.Parameters.Add(new ParameterDefinition(asm.MainModule.TypeSystem.String));
        var stringType = new TypeReference("System", "String", asm.MainModule, asm.MainModule.TypeSystem.CoreLibrary);
        var concat3 = new MethodReference("Concat", stringType, stringType) { HasThis = false };
        concat3.Parameters.Add(new ParameterDefinition(stringType));
        concat3.Parameters.Add(new ParameterDefinition(stringType));
        concat3.Parameters.Add(new ParameterDefinition(stringType));

        var ilp2 = trg2.Body.GetILProcessor();
        int probes = 0;
        foreach (var ins in trg2.Body.Instructions.ToList())
        {
            if ((ins.OpCode == Mono.Cecil.Cil.OpCodes.Call) && ins.Operand is MethodReference mm2 &&
                mm2.Name == "Exists" && mm2.DeclaringType.Name == "File")
            {
                ilp2.InsertBefore(ins, Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Ldstr, "[SDV-X] TryReadSaveFile file="));
                ilp2.InsertBefore(ins, Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Ldarg_0));
                ilp2.InsertBefore(ins, Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Ldloc_1));
                ilp2.InsertBefore(ins, Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Call, concat3));
                ilp2.InsertBefore(ins, Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Call, logStr));
                probes++;
            }
        }
        Console.WriteLine($"TryReadSaveFile: 路径探针 x{probes}");
        total += probes;

        /* 追加：IOException 捕获也透传 */
        var logExRef = new MethodReference("LogException", asm.MainModule.TypeSystem.Void, rfType2) { HasThis = false };
        logExRef.Parameters.Add(new ParameterDefinition(new TypeReference("System", "Exception", asm.MainModule, asm.MainModule.TypeSystem.CoreLibrary)));
        foreach (var eh in trg2.Body.ExceptionHandlers)
        {
            if (eh.HandlerType != Mono.Cecil.Cil.ExceptionHandlerType.Catch) continue;
            if (eh.CatchType == null || eh.CatchType.Name != "IOException") continue;
            var hs2 = eh.HandlerStart;
            if (hs2.Operand is Mono.Cecil.Cil.VariableDefinition vd2)
            {
                ilp2.InsertAfter(hs2, Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Ldloc, vd2));
                ilp2.InsertAfter(hs2.Next, Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Call, logExRef));
                Console.WriteLine("TryReadSaveFile: IOException catch 注入(handler内)");
                total++;
            }
        }

        /* 追加：流长度 + 反序列化前后标记 */
        var streamType = new TypeReference("System.IO", "Stream", asm.MainModule, asm.MainModule.TypeSystem.CoreLibrary);
        var logLen = new MethodReference("LogStreamLen", asm.MainModule.TypeSystem.Void, rfType2) { HasThis = false };
        logLen.Parameters.Add(new ParameterDefinition(streamType));
        var logObj = new MethodReference("LogObj", asm.MainModule.TypeSystem.Void, rfType2) { HasThis = false };
        logObj.Parameters.Add(new ParameterDefinition(asm.MainModule.TypeSystem.Object));

        /* 先快照目标指令，再做插入（避免边遍历边插入造成死循环） */
        var targets = new System.Collections.Generic.List<(int kind, Mono.Cecil.Cil.Instruction ins)>();
        var insList2 = trg2.Body.Instructions;
        for (int i9 = 0; i9 < insList2.Count; i9++)
        {
            var ins9 = insList2[i9];
            if (ins9.OpCode == Mono.Cecil.Cil.OpCodes.Newobj && ins9.Operand is MethodReference ctor9 && ctor9.DeclaringType.Name == "MemoryStream"
                && i9 + 1 < insList2.Count && insList2[i9 + 1].OpCode == Mono.Cecil.Cil.OpCodes.Stloc_0)
                targets.Add((1, insList2[i9 + 1]));
            if (ins9.OpCode == Mono.Cecil.Cil.OpCodes.Call && ins9.Operand is MethodReference dm9 && dm9.Name == "Deserialize" && dm9.DeclaringType.Name == "SaveSerializer")
                targets.Add((2, ins9));
        }
        foreach (var tp in targets)
        {
            if (tp.kind == 1)
            {
                ilp2.InsertAfter(tp.ins, Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Ldloc_0));
                ilp2.InsertAfter(tp.ins.Next, Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Call, logLen));
                Console.WriteLine("TryReadSaveFile: 流长度标记注入");
                total++;
            }
            else
            {
                var tgt = tp.ins;
                ilp2.InsertBefore(tgt, Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Ldstr, "[SDV-X] Deserialize<SaveGame> 开始"));
                ilp2.InsertBefore(tgt, Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Call, logStr));
                var after9 = tgt.Next;
                ilp2.InsertBefore(after9, Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Dup));
                ilp2.InsertBefore(after9, Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Call, logObj));
                Console.WriteLine("TryReadSaveFile: Deserialize 前后标记注入");
                total++;
            }
        }
    }
}

// 4.9 诊断：联机服务器路径探针（LidgrenServer.initialize / GameServer.startServer / Game1.loadForNewGame）
{
    var probeNames = new (string type, string method)[]
    {
        ("LidgrenServer", "initialize"),
        ("GameServer", "startServer"),
        ("Game1", "loadForNewGame"),
    };
    var hRef = asm.MainModule.AssemblyReferences.First(a => a.Name == "OHOS.Helper");
    var rfT = new TypeReference("OHOS", "ReflectionFix", asm.MainModule, hRef);
    var logS = new MethodReference("LogString", asm.MainModule.TypeSystem.Void, rfT) { HasThis = false };
    logS.Parameters.Add(new ParameterDefinition(asm.MainModule.TypeSystem.String));
    foreach (var (tn, mn) in probeNames)
    {
        var ty = asm.MainModule.GetType("StardewValley.Network." + tn) ?? asm.MainModule.GetType("StardewValley." + tn);
        if (ty == null) { Console.WriteLine($"?? 未找到 {tn}"); continue; }
        foreach (var m in ty.Methods)
        {
            if (m.Name != mn || !m.HasBody) continue;
            var ilp = m.Body.GetILProcessor();
            var first = m.Body.Instructions[0];
            ilp.InsertBefore(first, Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Ldstr, $"[NET] {tn}::{mn} 进入"));
            ilp.InsertBefore(first, Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Call, logS));
            foreach (var ins in m.Body.Instructions.ToList())
            {
                if (ins.OpCode == Mono.Cecil.Cil.OpCodes.Ret)
                {
                    ilp.InsertBefore(ins, Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Ldstr, $"[NET] {tn}::{mn} 返回"));
                    ilp.InsertBefore(ins, Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Call, logS));
                }
            }
            Console.WriteLine($"[NET] 探针 {tn}::{mn}");
            total++;
        }
    }
}

// 5. 存档路径固定到沙箱绝对路径：GetAppDataFolder 里的
//    Environment.GetFolderPath(SpecialFolder.ApplicationData) 在设备上返回 ""
//    （HOME=/storage/Users/currentUser 但该目录对 app 不可访问），导致存档
//    落在依赖 cwd 的相对路径上、且读取端解析可能不稳定。替换为 host 的
//    SdvPaths.GetAppDataRoot()（固定返回 <files>/game 绝对路径）。
{
    var hostAsmRef = asm.MainModule.AssemblyReferences.FirstOrDefault(a => a.Name == "stardewhost");
    if (hostAsmRef == null)
    {
        hostAsmRef = new AssemblyNameReference("stardewhost", new Version(1, 0, 0, 0));
        asm.MainModule.AssemblyReferences.Add(hostAsmRef);
    }
    var pathsType = new TypeReference("StardewHost", "SdvPaths", asm.MainModule, hostAsmRef);
    var getRoot = new MethodReference("GetAppDataRoot", asm.MainModule.TypeSystem.String, pathsType) { HasThis = false };

    int replaced = 0;
    foreach (var ty in EnumAllTypes(asm.MainModule))
    {
        foreach (var m2 in ty.Methods)
        {
            if (!m2.HasBody) continue;
            var ilp5 = m2.Body.GetILProcessor();
            var insList = m2.Body.Instructions;
            for (int i5 = 0; i5 < insList.Count; i5++)
            {
                var ins = insList[i5];
                if ((ins.OpCode == Mono.Cecil.Cil.OpCodes.Call || ins.OpCode == Mono.Cecil.Cil.OpCodes.Callvirt) &&
                    ins.Operand is MethodReference mr2 && mr2.Name == "GetFolderPath" &&
                    mr2.DeclaringType.Name == "Environment")
                {
                    /* 新方法无参：把参数压栈指令（ldc.i4 SpecialFolder 枚举常量）改为 nop */
                    if (i5 > 0)
                    {
                        var prev = insList[i5 - 1];
                        if (prev.OpCode == Mono.Cecil.Cil.OpCodes.Ldc_I4 || prev.OpCode == Mono.Cecil.Cil.OpCodes.Ldc_I4_S)
                            ilp5.Replace(prev, ilp5.Create(Mono.Cecil.Cil.OpCodes.Nop));
                        else if (prev.OpCode.Code.ToString().StartsWith("Ldc_I4_"))
                            ilp5.Replace(prev, ilp5.Create(Mono.Cecil.Cil.OpCodes.Nop));
                    }
                    ins.Operand = getRoot;
                    replaced++;
                    Console.WriteLine($"{ty.Name}::{m2.Name}: GetFolderPath → SdvPaths.GetAppDataRoot");
                }
            }
        }
    }
    if (replaced == 0) Console.WriteLine("!! 未找到 Environment.GetFolderPath 调用");
    total += replaced;
}

asm.Write(output);
Console.WriteLine($"done: {total}");

static System.Collections.Generic.IEnumerable<TypeDefinition> EnumAllTypes(ModuleDefinition mod)
{
    var stack = new System.Collections.Generic.Stack<TypeDefinition>();
    foreach (var t in mod.Types) stack.Push(t);
    while (stack.Count > 0)
    {
        var t = stack.Pop();
        yield return t;
        foreach (var nt in t.NestedTypes) stack.Push(nt);
    }
}
