using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

// 用法: lidgren-patch <Steam 原版 Lidgren.Network.dll> <输出>
// 输入必须是 Steam 原版（游戏目录 Stardew Valley/Lidgren.Network.dll），
// 切勿用中间产物做输入（补丁叠加会出问题）。
// 功能修复（始终启用）：
//   1) NetUtility::ComputeSHAHash(3参) → OHOS.Helper.ComputeShaFallback（NativeAOT 下
//      SHA256.ComputeHash 崩溃；用途仅本机标识，FNV-1a 确定性哈希等价）
//   2) NetUtility::.cctor 里 SHA256.Create() → ldnull：设备无可用 OpenSSL，创建
//      SHA256 会触发 .NET 的 libssl 探测 → 直接 abort（"No usable version of libssl"
//      是"其他 PC 连接时主机崩溃"的根因之一）。s_sha 仅被已替换的 ComputeSHAHash 读。
// 诊断探针（#if false 包裹，调试联机时可整体启用）：握手路径/逐调用进入返回日志。
class P
{
    static void Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("用法: lidgren-patch <输入 Lidgren.Network.dll> <输出>");
            return;
        }
        var input = args[0];
        var output = args[1];

        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(input)));
        var asm = AssemblyDefinition.ReadAssembly(input,
            new ReaderParameters { AssemblyResolver = resolver });
        var mod = asm.MainModule;
        var helperRef = new AssemblyNameReference("OHOS.Helper", new Version(1, 0, 0, 0));
        mod.AssemblyReferences.Add(helperRef);
        var rfType = new TypeReference("OHOS", "ReflectionFix", mod, helperRef);
        var logStr = new MethodReference("LogString", mod.TypeSystem.Void, rfType) { HasThis = false };
        logStr.Parameters.Add(new ParameterDefinition(mod.TypeSystem.String));

        // 1) ComputeSHAHash(byte[],int,int) 整体重写为 OHOS.Helper.ComputeShaFallback
        var helperSha = new MethodReference("ComputeShaFallback", new ArrayType(mod.TypeSystem.Byte), rfType) { HasThis = false };
        helperSha.Parameters.Add(new ParameterDefinition(new ArrayType(mod.TypeSystem.Byte)));
        helperSha.Parameters.Add(new ParameterDefinition(mod.TypeSystem.Int32));
        helperSha.Parameters.Add(new ParameterDefinition(mod.TypeSystem.Int32));
        foreach (var t in mod.GetTypes())
            foreach (var m in t.Methods)
            {
                if (m.Name == "ComputeSHAHash" && m.HasBody && m.Parameters.Count == 3)
                {
                    m.Body.Instructions.Clear();
                    m.Body.Variables.Clear();
                    m.Body.ExceptionHandlers.Clear();
                    var ip = m.Body.GetILProcessor();
                    ip.Emit(OpCodes.Ldarg_0);
                    ip.Emit(OpCodes.Ldarg_1);
                    ip.Emit(OpCodes.Ldarg_2);
                    ip.Emit(OpCodes.Call, helperSha);
                    ip.Emit(OpCodes.Ret);
                    Console.WriteLine($"重写 {t.Name}::ComputeSHAHash(3参) → ComputeShaFallback");
                }
            }

        // 1.5) NetUtility..cctor 里的 SHA256.Create() → ldnull（见文件头注释 2）
        foreach (var t in mod.GetTypes())
            if (t.Name == "NetUtility")
                foreach (var m in t.Methods)
                    if (m.Name == ".cctor" && m.HasBody)
                    {
                        var ilc = m.Body.GetILProcessor();
                        var create = m.Body.Instructions.FirstOrDefault(i =>
                            i.OpCode == OpCodes.Call && i.Operand is MethodReference mr
                            && mr.Name == "Create" && mr.DeclaringType.Name == "SHA256");
                        if (create != null)
                        {
                            ilc.Replace(create, ilc.Create(OpCodes.Ldnull));
                            Console.WriteLine("NetUtility::.cctor SHA256.Create → ldnull");
                        }
                    }

#if false // 诊断探针（正式版禁用，调试联机时启用）
        // 2) NetPeer::Start/InitializeNetwork 逐调用探针（网络线程）
        int n = 0;
        foreach (var t in mod.GetTypes())
            foreach (var m in t.Methods)
            {
                if (!m.HasBody) continue;
                bool target = (t.Name == "NetPeer" && (m.Name == "InitializeNetwork" || m.Name == "Start"))
                            || (t.Name == "NetServer" && m.Name == "Start");
                if (!target) continue;
                m.Body.SimplifyMacros(); // 短分支→长分支：插入指令后短偏移溢出会写出非法 IL
                m.Body.MaxStackSize += 16;
                var il = m.Body.GetILProcessor();
                var first = m.Body.Instructions[0];
                il.InsertBefore(first, il.Create(OpCodes.Ldstr, $"[NET] {t.Name}::{m.Name} 进入"));
                il.InsertBefore(first, il.Create(OpCodes.Call, logStr));
                foreach (var ins in m.Body.Instructions.ToList())
                {
                    if (ins.OpCode == OpCodes.Call || ins.OpCode == OpCodes.Callvirt || ins.OpCode == OpCodes.Newobj)
                    {
                        if (ins.Operand is MethodReference mref && mref.DeclaringType.Name == "ReflectionFix") continue;
                        string label = ins.Operand is MethodReference mmr ? $"{mmr.DeclaringType.Name}::{mmr.Name}" : "?";
                        var anchor = ins;
                        for (var pv = ins.Previous; pv != null && pv.OpCode.OpCodeType == OpCodeType.Prefix; pv = pv.Previous)
                            anchor = pv;
                        il.InsertBefore(anchor, il.Create(OpCodes.Ldstr, $"[NET]   → {label}"));
                        il.InsertBefore(anchor, il.Create(OpCodes.Call, logStr));
                        n++;
                    }
                    if (ins.OpCode == OpCodes.Ret)
                    {
                        il.InsertBefore(ins, il.Create(OpCodes.Ldstr, $"[NET] {t.Name}::{m.Name} 返回"));
                        il.InsertBefore(ins, il.Create(OpCodes.Call, logStr));
                    }
                }
                Console.WriteLine($"patched {t.Name}::{m.Name} (逐调用)");
            }

        // 2.5) 远端连接握手路径探针
        int nh = 0;
        foreach (var t in mod.GetTypes())
            foreach (var m in t.Methods)
            {
                if (!m.HasBody) continue;
                bool target =
                    (t.Name == "NetConnection" && (m.Name == "ReceivedHandshake" || m.Name == "SendConnectResponse"
                        || m.Name == "SendConnectionEstablished" || m.Name == "SendConnect"
                        || m.Name == "Approve" || m.Name == "Deny" || m.Name == "ReceivedMessage"))
                 || (t.Name == "NetPeer" && (m.Name == "ReceivedUnconnectedLibraryMessage"
                        || m.Name == "AcceptConnection" || m.Name == "HandleIncomingDiscoveryRequest"));
                if (!target) continue;
                m.Body.SimplifyMacros();
                m.Body.MaxStackSize += 16;
                var il2 = m.Body.GetILProcessor();
                var f2 = m.Body.Instructions[0];
                il2.InsertBefore(f2, il2.Create(OpCodes.Ldstr, $"[NET] {t.Name}::{m.Name}[{m.Parameters.Count}p] 进入"));
                il2.InsertBefore(f2, il2.Create(OpCodes.Call, logStr));
                foreach (var ins2 in m.Body.Instructions.ToList())
                {
                    if (ins2.OpCode != OpCodes.Ret) continue;
                    il2.InsertBefore(ins2, il2.Create(OpCodes.Ldstr, $"[NET] {t.Name}::{m.Name}[{m.Parameters.Count}p] 返回"));
                    il2.InsertBefore(ins2, il2.Create(OpCodes.Call, logStr));
                }
                Console.WriteLine($"patched handshake {t.Name}::{m.Name}({m.Parameters.Count}p)");
                nh++;
            }
        Console.WriteLine("handshake probes: " + nh);
        Console.WriteLine("逐调用探针: " + n);
#endif

        asm.Write(output);
        Console.WriteLine("done → " + output);
    }
}
