using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

// 用法: mg-patch <输入 MonoGame.Framework.dll> <输出>
// 对星露谷自带 MonoGame 3.8.0.1641 做 OHOS 适配（二进制 IL 级 patch）：
//   1. DllImport 模块名: libc→libc.so, libdl.so.2→libc.so（musl 无 lib 前缀变换）
//   2. FuncLoader 库名: libSDL2-2.0.so.0→libSDL2.so, openal→libopenal.so
//   3. GL.LoadPlatformEntryPoints: BoundApi GL→ES（走 GLES float 函数路径）
//   4. GraphicsDevice.PlatformSetup: 版本解析 Substring(0,1)/(2,1)→(10,1)/(12,1)
//      （"OpenGL ES 3.2 ..." 的主/次版本在 index 10/12）
//   5. RasterizerState: 删除 glPolygonMode 调用（GLES 无此函数，仅支持 Fill）
var input = args[0];
var output = args[1];

var asm = AssemblyDefinition.ReadAssembly(input, new ReaderParameters { ReadWrite = false });
var mod = asm.MainModule;
int total = 0;

// 1. PInvoke 模块引用改名
foreach (var mr in mod.ModuleReferences)
{
    if (mr.Name == "libc") { mr.Name = "libc.so"; Console.WriteLine($"ModuleRef: libc → libc.so"); total++; }
    else if (mr.Name == "libdl.so.2") { mr.Name = "libc.so"; Console.WriteLine("ModuleRef: libdl.so.2 → libc.so"); total++; }
    else if (mr.Name == "openal") { mr.Name = "libopenal.so"; Console.WriteLine("ModuleRef: openal → libopenal.so"); total++; }
}

foreach (var type in mod.GetTypeHierarchy())
{
    foreach (var m in type.Methods)
    {
        if (!m.HasBody) continue;
        var il = m.Body.GetILProcessor();

        // 2. 库名字符串
        foreach (var ins in m.Body.Instructions)
        {
            if (ins.OpCode == OpCodes.Ldstr)
            {
                var s = (string)ins.Operand;
                if (s == "libSDL2-2.0.so.0") { ins.Operand = "libSDL2.so"; Console.WriteLine($"{type.Name}::{m.Name}: SDL2 库名"); total++; }
                else if (s == "libopenal.so.1") { ins.Operand = "libopenal.so"; Console.WriteLine($"{type.Name}::{m.Name}: openal.so.1 库名"); total++; }
                else if (s == "openal") { ins.Operand = "libopenal.so"; Console.WriteLine($"{type.Name}::{m.Name}: openal 库名"); total++; }
            }
        }

        // 2.5 CurrentPlatform: uname=="Linux" 判定改为 "HarmonyOS"
        //     （sysname 是 HarmonyOS，否则误判 macOS 走 libSystem.dylib 崩）
        if (type.Name == "CurrentPlatform")
        {
            foreach (var ins in m.Body.Instructions)
            {
                if (ins.OpCode == OpCodes.Ldstr && (string)ins.Operand == "Linux")
                {
                    ins.Operand = "HarmonyOS";
                    Console.WriteLine($"{type.Name}::{m.Name}: uname 判定 Linux→HarmonyOS"); total++;
                }
            }
        }

        // 2.55 TitleContainer.PlatformInit 整体重写为 Location = Directory.GetCurrentDirectory()
        //     （原 IL 在 NativeAOT 下 InvalidProgram；Location 须指向游戏目录（cwd））
        if (type.Name == "TitleContainer" && m.Name == "PlatformInit")
        {
            AssemblyNameReference coreRef = null;
            foreach (var ar in mod.AssemblyReferences) { if (ar.Name == "System.Runtime") { coreRef = ar; break; } }
            if (coreRef == null) { coreRef = mod.AssemblyReferences[0]; }
            var dirType = new TypeReference("System.IO", "Directory", mod, coreRef);
            var gcm = new MethodReference("GetCurrentDirectory", mod.TypeSystem.String, dirType);
            var tcType = m.DeclaringType;
            var setLocation = new MethodReference("set_Location", mod.TypeSystem.Void, tcType) { HasThis = false };
            setLocation.Parameters.Add(new ParameterDefinition(mod.TypeSystem.String));
            m.Body.Instructions.Clear();
            m.Body.Variables.Clear();
            m.Body.ExceptionHandlers.Clear();
            var ilp2 = m.Body.GetILProcessor();
            ilp2.Emit(OpCodes.Call, gcm);
            ilp2.Emit(OpCodes.Call, setLocation);
            ilp2.Emit(OpCodes.Ret);
            Console.WriteLine("TitleContainer::PlatformInit 重写为 GetCurrentDirectory"); total++;
        }

        // 2.6 GLES 无 glPolygonMode / glGetCompressedTexImage：重定向到 glLineWidth / glGetError
        //     （委托非 null 即不 NRE；SDV 主流程无压缩纹理 GetData，返回空数据仅影响该罕见路径）
        if (m.IsStatic)
        {
            foreach (var ins in m.Body.Instructions)
            {
                if (ins.OpCode == OpCodes.Ldstr && (string)ins.Operand == "glPolygonMode")
                {
                    ins.Operand = "glLineWidth";
                    Console.WriteLine($"{type.Name}::{m.Name}: glPolygonMode → glLineWidth"); total++;
                }
                else if (ins.OpCode == OpCodes.Ldstr && (string)ins.Operand == "glGetCompressedTexImage")
                {
                    ins.Operand = "glGetError";
                    Console.WriteLine($"{type.Name}::{m.Name}: glGetCompressedTexImage → glGetError"); total++;
                }
            }
        }

        // 3. LoadPlatformEntryPoints: BoundApi = GL(12450) → ES(12448)
        if (m.Name == "LoadPlatformEntryPoints" && m.IsPrivate && m.IsStatic)
        {
            foreach (var ins in m.Body.Instructions)
            {
                if (ins.OpCode == OpCodes.Ldc_I4 && (int)ins.Operand == 12450)
                {
                    ins.Operand = 12448;
                    Console.WriteLine($"{type.Name}::{m.Name}: BoundApi GL→ES"); total++;
                }
            }
        }

        // 4. PlatformSetup: Substring(0,1)→(10,1) / Substring(2,1)→(12,1)
        if (m.Name == "PlatformSetup")
        {
            var instrs = m.Body.Instructions;
            for (int i = 0; i + 2 < instrs.Count; i++)
            {
                var a = instrs[i]; var b = instrs[i + 1]; var c = instrs[i + 2];
                if (c.OpCode == OpCodes.Callvirt && c.Operand is MethodReference mr2 &&
                    mr2.DeclaringType.FullName == "System.String" && mr2.Name == "Substring" &&
                    b.OpCode == OpCodes.Ldc_I4_1)
                {
                    int start = -1, newStart = -1;
                    if (a.OpCode == OpCodes.Ldc_I4_0) { start = 0; newStart = 10; }
                    else if (a.OpCode == OpCodes.Ldc_I4_2) { start = 2; newStart = 12; }
                    if (start >= 0)
                    {
                        il.Replace(a, il.Create(OpCodes.Ldc_I4, newStart));
                        Console.WriteLine($"{type.Name}::{m.Name}: Substring({start},1)→({newStart},1)"); total++;
                    }
                }
            }
        }

        // 5. RasterizerState: 删 glPolygonMode 委托调用（GLES 无此函数）
        // IL 模式: ldsfield GL::PolygonMode; ldc; ldc; callvirt Invoke
        if (false) // nop 方案废弃：引发 InvalidProgram，改用 glLineWidth 重定向
        {
            var instrs = m.Body.Instructions;
            for (int i = 0; i < instrs.Count; i++)
            {
                if (instrs[i].OpCode == OpCodes.Callvirt && instrs[i].Operand is MethodReference mr3 &&
                    mr3.Name == "Invoke" && mr3.DeclaringType.Name == "PolygonModeDelegate" && i >= 3 &&
                    instrs[i - 3].OpCode == OpCodes.Ldsfld &&
                    ((FieldReference)instrs[i - 3].Operand).Name == "PolygonMode" &&
                    IsLdc(instrs[i - 1]) && IsLdc(instrs[i - 2]))
                {
                    for (int k = i - 3; k <= i; k++)
                        il.Replace(instrs[k], il.Create(OpCodes.Nop));
                    Console.WriteLine($"{type.Name}::{m.Name}: 删除 PolygonMode 调用"); total++;
                    i++;
                }
            }
        }
    }
}

// 6. GLES 无 glGetTexImage：Texture2D/TextureCube 的 PlatformGetData 里
//    GL.GetTexImage 调用重定向到 StardewHost.GlEsShim（host 内实现 FBO+ReadPixels）。
//    host 侧经 IVT 强类型直调 GL 委托（见第 7 项），无反射。
{
    var hostAsmRef = mod.AssemblyReferences.FirstOrDefault(a => a.Name == "stardewhost");
    if (hostAsmRef == null)
    {
        hostAsmRef = new AssemblyNameReference("stardewhost", new Version(1, 0, 0, 0));
        mod.AssemblyReferences.Add(hostAsmRef);
    }
    var shimType = new TypeReference("StardewHost", "GlEsShim", mod, hostAsmRef);
    var shimCall = new MethodReference("GetTexImageViaFbo", mod.TypeSystem.Void, shimType) { HasThis = false };
    for (int pi = 0; pi < 8; pi++)
        shimCall.Parameters.Add(new ParameterDefinition(pi == 7 ? (TypeReference)mod.TypeSystem.Object : mod.TypeSystem.Int32));

    foreach (var (typeName, isCube) in new[] { ("Texture2D", false), ("TextureCube", true) })
    {
        var ty = mod.GetTypeHierarchy().FirstOrDefault(t => t.Name == typeName && t.Namespace == "Microsoft.Xna.Framework.Graphics");
        var m = ty?.Methods.FirstOrDefault(x => x.Name == "PlatformGetData" && x.HasGenericParameters);
        if (m == null) { Console.WriteLine($"!! 未找到 {typeName}.PlatformGetData"); continue; }

        // glTexture 字段（在基类 Texture）
        FieldReference fldGlTex = null;
        for (var t2 = (TypeReference)ty; t2 != null; )
        {
            var td = t2.Resolve();
            var f2 = td?.Fields.FirstOrDefault(f => f.Name == "glTexture");
            if (f2 != null) { fldGlTex = f2; break; }
            t2 = td?.BaseType;
        }
        if (fldGlTex == null) { Console.WriteLine($"!! {typeName}: 未找到 glTexture 字段"); continue; }
        MethodReference mathMax = null;
        foreach (var i2 in m.Body.Instructions)
            if (i2.OpCode == OpCodes.Call && i2.Operand is MethodReference mm && mm.Name == "Max" && mm.DeclaringType.Name == "Math")
                { mathMax = mm; break; }
        MethodReference FindGetter(string name)
        {
            for (var t3 = (TypeReference)ty; t3 != null; )
            {
                var td3 = t3.Resolve();
                var g = td3?.Methods.FirstOrDefault(x => x.Name == name);
                if (g != null) return g;
                t3 = td3?.BaseType;
            }
            return null;
        }
        // cube 的尺寸是字段 size；Texture2D 是属性 ActualWidth/Height
        FieldReference fldSize = null;
        if (isCube)
        {
            for (var t3 = (TypeReference)ty; t3 != null; )
            {
                var td3 = t3.Resolve();
                var f3 = td3?.Fields.FirstOrDefault(f => f.Name == "size");
                if (f3 != null) { fldSize = f3; break; }
                t3 = td3?.BaseType;
            }
            if (fldSize == null) { Console.WriteLine("!! TextureCube: 未找到 size 字段"); continue; }
        }
        var mGetW = isCube ? null : FindGetter("get_ActualWidth");
        var mGetH = isCube ? null : FindGetter("get_ActualHeight");
        if (!isCube && (mGetW == null || mGetH == null)) { Console.WriteLine("!! Texture2D: 未找到尺寸属性"); continue; }

        // 定位 call GL::GetTexImage<T>
        var ins = m.Body.Instructions.FirstOrDefault(i =>
            (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) &&
            i.Operand is GenericInstanceMethod gim && gim.Name == "GetTexImage" && gim.DeclaringType.Name == "GL");
        if (ins == null) { Console.WriteLine($"!! {typeName}.PlatformGetData 未找到 GetTexImage 调用"); continue; }
        var oldGim = (GenericInstanceMethod)ins.Operand;
        var tFmt = oldGim.Parameters[2].ParameterType;   // PixelFormat
        var tTyp = oldGim.Parameters[3].ParameterType;   // PixelType
        var tFace = oldGim.Parameters[0].ParameterType;  // TextureTarget

        // 新局部变量
        var gp = m.GenericParameters[0];
        var vLvl = new VariableDefinition(mod.TypeSystem.Int32);
        var vFmt = new VariableDefinition(tFmt);
        var vTyp = new VariableDefinition(tTyp);
        var vArr = new VariableDefinition(new ArrayType(gp));
        VariableDefinition vFace = null;
        foreach (var v in new[] { vLvl, vFmt, vTyp, vArr }) m.Body.Variables.Add(v);
        if (isCube)
        {
            vFace = new VariableDefinition(tFace);
            m.Body.Variables.Add(vFace);
        }

        var ilp = m.Body.GetILProcessor();
        var list = new List<Instruction>();
        void E(OpCode op, object operand = null)
        {
            Instruction i;
            if (operand == null) i = ilp.Create(op);
            else if (operand is Instruction t) i = ilp.Create(op, t);
            else if (operand is MethodReference mth) i = ilp.Create(op, mth);
            else if (operand is FieldReference fld) i = ilp.Create(op, fld);
            else if (operand is TypeReference tr) i = ilp.Create(op, tr);
            else if (operand is VariableDefinition vd) i = ilp.Create(op, vd);
            else if (operand is int n) i = ilp.Create(op, n);
            else throw new Exception("E: 不支持的 operand 类型 " + operand.GetType());
            list.Add(i);
        }

        // 弹出原调用参数（栈顶→底: arr, typ, fmt, lvl, face）
        E(OpCodes.Stloc, vArr);
        E(OpCodes.Stloc, vTyp);
        E(OpCodes.Stloc, vFmt);
        E(OpCodes.Stloc, vLvl);
        if (isCube) E(OpCodes.Stloc, vFace); else E(OpCodes.Pop);

        // GetTexImageViaFbo(glTexture, lvl, fmt, typ, face, w, h, arr)
        E(OpCodes.Ldarg_0); E(OpCodes.Ldfld, fldGlTex);
        E(OpCodes.Ldloc, vLvl);
        E(OpCodes.Ldloc, vFmt); E(OpCodes.Box, tFmt); E(OpCodes.Unbox_Any, mod.TypeSystem.Int32);
        E(OpCodes.Ldloc, vTyp); E(OpCodes.Box, tTyp); E(OpCodes.Unbox_Any, mod.TypeSystem.Int32);
        if (isCube) { E(OpCodes.Ldloc, vFace); E(OpCodes.Box, tFace); E(OpCodes.Unbox_Any, mod.TypeSystem.Int32); }
        else E(OpCodes.Ldc_I4, 3553);
        E(OpCodes.Ldarg_0);
        if (isCube) E(OpCodes.Ldfld, fldSize); else E(OpCodes.Call, mGetW);
        E(OpCodes.Ldloc, vLvl); E(OpCodes.Shr); E(OpCodes.Ldc_I4_1); E(OpCodes.Call, mathMax);
        E(OpCodes.Ldarg_0);
        if (isCube) E(OpCodes.Ldfld, fldSize); else E(OpCodes.Call, mGetH);
        E(OpCodes.Ldloc, vLvl); E(OpCodes.Shr); E(OpCodes.Ldc_I4_1); E(OpCodes.Call, mathMax);
        E(OpCodes.Ldloc, vArr);
        E(OpCodes.Call, shimCall);

        ilp.Replace(ins, list[0]);
        var cur = list[0];
        for (int k = 1; k < list.Count; k++) { ilp.InsertAfter(cur, list[k]); cur = list[k]; }
        // 注入序列栈峰值 7（shim 参数），原方法 MaxStack 可能不足
        m.Body.MaxStackSize = Math.Max(m.Body.MaxStackSize, 8);
        Console.WriteLine($"{typeName}::PlatformGetData: GetTexImage → GlEsShim(FBO+ReadPixels)"); total++;
    }
}

// 7. InternalsVisibleTo("stardewhost")：host 的 GlEsShim 需强类型访问 internal GL
//    （FBO 读回、枚举、out/ref 参数——反射 DynamicInvoke 在 NativeAOT 不回写 out）
{
    var coreRef = mod.AssemblyReferences.FirstOrDefault(a => a.Name == "System.Runtime") ?? mod.AssemblyReferences[0];
    var ivtType = new TypeReference("System.Runtime.CompilerServices", "InternalsVisibleToAttribute", mod, coreRef);
    var ivtCtor = new MethodReference(".ctor", mod.TypeSystem.Void, ivtType) { HasThis = true };
    ivtCtor.Parameters.Add(new ParameterDefinition(mod.TypeSystem.String));
    if (!asm.CustomAttributes.Any(ca => ca.Constructor.DeclaringType.Name == "InternalsVisibleToAttribute" &&
                                        ca.ConstructorArguments.Count == 1 &&
                                        (ca.ConstructorArguments[0].Value as string) == "stardewhost"))
    {
        var ca = new CustomAttribute(ivtCtor);
        ca.ConstructorArguments.Add(new CustomAttributeArgument(mod.TypeSystem.String, "stardewhost"));
        asm.CustomAttributes.Add(ca);
        Console.WriteLine("Assembly: +InternalsVisibleTo(stardewhost)"); total++;
    }
}

asm.Write(output);
Console.WriteLine($"完成：{total} 处 patch → {output}");

static bool IsLdc(Instruction ins) =>
    ins.OpCode == OpCodes.Ldc_I4 || ins.OpCode.Code.ToString().StartsWith("Ldc_I4_");

public static class TypeHierarchyExt
{
    public static IEnumerable<TypeDefinition> GetTypeHierarchy(this ModuleDefinition mod)
    {
        var stack = new Stack<TypeDefinition>();
        foreach (var t in mod.Types) stack.Push(t);
        while (stack.Count > 0)
        {
            var t = stack.Pop();
            yield return t;
            foreach (var nt in t.NestedTypes) stack.Push(nt);
        }
    }
}
