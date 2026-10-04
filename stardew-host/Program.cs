using System;
using System.Reflection;
using System.Runtime.InteropServices;
using MonoGame.OpenGL;   // internal 命名空间：经 mg-patch 注入的 IVT("stardewhost") 可访问

namespace StardewHost;

internal static class Program
{
    [DllImport("libc.so", CallingConvention = CallingConvention.Cdecl)]
    private static extern int open(string path, int flags, int mode);
    [DllImport("libc.so", CallingConvention = CallingConvention.Cdecl)]
    private static extern int dup2(int oldfd, int newfd);
    [DllImport("libc.so", CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe int write(int fd, ref byte buf, int count);
    [DllImport("libc.so", CallingConvention = CallingConvention.Cdecl)]
    private static extern int _exit(int code);
    [DllImport("libc.so", CallingConvention = CallingConvention.Cdecl)]
    private static extern int mkdir(string path, int mode);

    static void CopyTree(string src, string dst)
    {
        System.IO.Directory.CreateDirectory(dst);
        foreach (var f in System.IO.Directory.GetFiles(src))
            System.IO.File.Copy(f, System.IO.Path.Combine(dst, System.IO.Path.GetFileName(f)), true);
        foreach (var d in System.IO.Directory.GetDirectories(src))
            CopyTree(d, System.IO.Path.Combine(dst, System.IO.Path.GetFileName(d)));
    }

    [DllImport("libc.so", CallingConvention = CallingConvention.Cdecl)]
    private static extern int close(int fd);
    [DllImport("libc.so", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr getenv(string name);
    [DllImport("libc.so", CallingConvention = CallingConvention.Cdecl)]
    private static extern int symlink(string target, string linkpath);

    static System.Type[] SubArray(System.Type[] a)
    {
        var r = new System.Type[a.Length - 1];
        for (int i = 1; i < a.Length; i++) r[i - 1] = a[i];
        return r;
    }
    [DllImport("libc.so", CallingConvention = CallingConvention.Cdecl)]
    private static extern int chdir(string path);
    [DllImport("libc.so", CallingConvention = CallingConvention.Cdecl)]
    private static extern uint getpid();

    static void RedirectFd(string path, int fd)
    {
        // O_APPEND：多次运行追加保留历史（崩溃诊断常需对比崩溃前后两次运行）
        int nfd = open(path, 0x441 /*O_WRONLY|O_CREAT|O_APPEND*/, 420 /*0644*/);
        if (nfd >= 0) dup2(nfd, fd);
    }

    // Linux/musl/glibc 的 struct sigaction 布局（aarch64 与 x86_64 相同）：
    // handler(8) + sigset_t sa_mask(128) + sa_flags(4+pad4) + sa_restorer(8) = 152
    unsafe struct SigAction
    {
        public delegate* unmanaged[Cdecl]<int, IntPtr, IntPtr, void> handler;
        public fixed byte mask[128];
        public int flags;
        public IntPtr restorer;
    }

    // 信号处理器：NativeAOT 下必须 [UnmanagedCallersOnly]+函数指针注册
    // （Marshal.GetFunctionPointerForDelegate 走委托 marshaling，之前在此触发 OOM）。
    // 处理器内仅做无分配操作：静态字节数组 + write 系统调用。
    static readonly byte[] FatalMsg4  = System.Text.Encoding.UTF8.GetBytes("SDV FATAL SIGILL(4)\n");
    static readonly byte[] FatalMsg5  = System.Text.Encoding.UTF8.GetBytes("SDV FATAL SIGTRAP(5)\n");
    static readonly byte[] FatalMsg6  = System.Text.Encoding.UTF8.GetBytes("SDV FATAL SIGABRT(6)\n");
    static readonly byte[] FatalMsg7  = System.Text.Encoding.UTF8.GetBytes("SDV FATAL SIGBUS(7)\n");
    static readonly byte[] FatalMsg11 = System.Text.Encoding.UTF8.GetBytes("SDV FATAL SIGSEGV(11)\n");

    [DllImport("libc.so", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sigaction(int sig, ref SigAction act, IntPtr old);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    static unsafe void FatalHandler(int sig, IntPtr si, IntPtr ctx)
    {
        var msg = sig == 11 ? FatalMsg11 : sig == 6 ? FatalMsg6 : sig == 7 ? FatalMsg7 : sig == 5 ? FatalMsg5 : FatalMsg4;
        fixed (byte* p = msg)
            _ = write(2, ref *p, msg.Length);
        _ = _exit(66);
    }

    static unsafe void InstallFatalHandlers()
    {
        // 函数指针字段直接赋 &Method：ILC 生成 native 入口地址。
        // （先前 cast 成 IntPtr 会让 ILC 生成"托管入口"→ 信号跳进 stub 抛
        //  "attempted to call a UnmanagedCallersOnly method from managed code"）
        SigAction sa;
        sa.handler = &FatalHandler;
        for (int i = 0; i < 128; i++) sa.mask[i] = 0;
        sa.flags = 0x4; // SA_SIGINFO
        sa.restorer = IntPtr.Zero;
        foreach (int s in new[] { 4, 5, 6, 7, 11 }) _ = sigaction(s, ref sa, IntPtr.Zero);
        Console.WriteLine("[SDV] fatal handlers installed");
    }

    [UnmanagedCallersOnly(EntryPoint = "SDL_main")]
    public static unsafe int Main(int argc, IntPtr argv)
    {
        // 入口第一拍：直接写探针文件（验证 ReversePInvoke/方法体是否到达）
        string probeStr = $"[SDV] SDL_main entered pid={getpid()} t={DateTime.Now:yyyy-MM-dd HH:mm:ss}\n";
        byte[] probe = System.Text.Encoding.ASCII.GetBytes(probeStr);
        int pfd = open("/data/storage/el2/base/haps/entry/files/probe.log", 0x441 /*O_WRONLY|O_CREAT|O_APPEND*/, 420);
        fixed (byte* pp = probe)
            _ = write(pfd, ref pp[0], probe.Length);
        var dir = "/data/storage/el2/base/haps/entry/files/game";
        _ = mkdir(dir, 0755);
        RedirectFd("/data/storage/el2/base/haps/entry/files/sdv-out.log", 1);
        RedirectFd("/data/storage/el2/base/haps/entry/files/sdv-err.log", 2);
        // FatalHandler 已弃用：NativeAOT/GC 在启动期本身会走 SIGSEGV 正常机制，
        // 我们的 handler 拦截后 ILC 给出的"托管入口 stub"路径反而杀死启动
        // （"Invalid Program: attempted to call a UnmanagedCallersOnly method"）。
        // 崩溃定位由探针承担（最后一条"进入"无"返回"即崩点）。
        // InstallFatalHandlers();
        Console.WriteLine($"[SDV] SDL_main pid={getpid()} t={DateTime.Now:yyyy-MM-dd HH:mm:ss} — Stardew Valley on OHOS starting");
        {
            // 从 hap rawfile 拷贝 Content（bundleCodeDir 由 ArkTS 启动时 setenv）
            var bundleDirPtr = getenv("SDV_BUNDLE_DIR");
            var bundleDir = bundleDirPtr == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(bundleDirPtr);
            Console.WriteLine($"[SDV] SDV_BUNDLE_DIR={bundleDir ?? "(null)"}");
            if (!string.IsNullOrEmpty(bundleDir))
            {
                var raw = bundleDir + "/entry/resources/rawfile/Content";
                var link = dir + "/Content";

                try
                {
                    // rawfile 不落盘（保留在 hap zip 内）——从 hap 提取 Content 到 el2。
                    // 智能同步：已存在且大小一致的文件跳过（首次全量 ~5 分钟；
                    // 之后仅同步差异 —— 这也是「mod 部署」机制：替换 rawfile 中资产后重启即生效）。
                    var hapPath = bundleDir + "/entry.hap";
                    Console.WriteLine($"[SDV] hap exists={System.IO.File.Exists(hapPath)}");
                    if (System.IO.File.Exists(hapPath))
                    {
                        Console.WriteLine("[SDV] syncing Content from hap zip ...");
                        using var zip = System.IO.Compression.ZipFile.OpenRead(hapPath);
                        var entries = zip.Entries;
                        int n = 0, skip = 0;
                        long bytes = 0;
                        var buf = new byte[8 * 1024 * 1024];
                        foreach (var e in entries)
                        {
                            if (!e.FullName.StartsWith("resources/rawfile/Content/", System.StringComparison.OrdinalIgnoreCase))
                                continue;
                            var rel = e.FullName.Substring("resources/rawfile/".Length);
                            var dst = System.IO.Path.Combine(dir, rel);
                            var fi = new System.IO.FileInfo(dst);
                            if (fi.Exists && fi.Length == e.Length) { skip++; continue; }
                            var dstDir = System.IO.Path.GetDirectoryName(dst);
                            if (!string.IsNullOrEmpty(dstDir) && !System.IO.Directory.Exists(dstDir))
                                _ = System.IO.Directory.CreateDirectory(dstDir);
                            using var es = e.Open();
                            using var fs = new System.IO.FileStream(dst, System.IO.FileMode.Create, System.IO.FileAccess.Write, System.IO.FileShare.None, 1 << 20);
                            int r;
                            while ((r = es.Read(buf, 0, buf.Length)) > 0)
                            {
                                fs.Write(buf, 0, r);
                                bytes += r;
                            }
                            n++;
                            if (n % 500 == 0) Console.WriteLine($"[SDV] synced {n} files ({bytes / 1048576}MB)");
                        }
                        Console.WriteLine($"[SDV] sync done: 写入 {n} 个 / 跳过 {skip} 个, {bytes / 1048576}MB");
                    }
                }
                catch (Exception je) { Console.WriteLine($"[SDV] extract FAIL: {je.Message}"); }
            }
        }
        try
        {
            chdir(dir);
            Environment.CurrentDirectory = dir;
            Console.WriteLine($"[SDV] cwd={Environment.CurrentDirectory}");
            try
            {
                var lt = typeof(System.Collections.Generic.List<object>);
                var addM = lt.GetMethod("Add", new[] { typeof(object) });
                Console.WriteLine($"[SDV] probe GetMethod(Add,{typeof(object)}) = {(addM == null ? "NULL(BUG)" : "OK")}");
                var addM2 = lt.GetMethod("Add");
                Console.WriteLine($"[SDV] probe GetMethod(Add) = {(addM2 == null ? "NULL" : "OK")}");
                var ms = lt.GetMethods();
                Console.WriteLine($"[SDV] probe GetMethods n={ms.Length} hasAdd={System.Array.Exists(ms, x => x.Name == "Add")}");
            }
            catch (Exception pe) { Console.WriteLine($"[SDV] probe EX: {pe.Message}"); }
            try
            {
                var lt2 = typeof(System.Collections.Generic.List<object>);
                foreach (var dp in lt2.GetDefaultMembers())
                {
                    if (dp is not System.Reflection.PropertyInfo pi) continue;
                    Console.WriteLine($"[SDV] defmember {pi.Name} DeclType={pi.DeclaringType?.FullName} eq={(pi.DeclaringType == lt2)} params=[{string.Join(",", System.Array.ConvertAll(pi.GetMethod!.GetParameters(), x => x.ParameterType.FullName ?? "?"))}]");
                }
                var icoll = typeof(System.Collections.ICollection);
                Console.WriteLine($"[SDV] ICollection assignable={icoll.IsAssignableFrom(lt2)}");
                var ifm = lt2.GetInterfaceMap(icoll);
                foreach (var im in ifm.InterfaceMethods)
                    if (im.Name == "Add")
                        Console.WriteLine($"[SDV] iface Add found, target={ifm.TargetMethods[System.Array.IndexOf(ifm.InterfaceMethods, im)].Name}");
            }
            catch (Exception pe2) { Console.WriteLine($"[SDV] probe2 EX: {pe2.Message}"); }
            // 预构造游戏全部 XmlSerializer：利用其静态缓存（(namespace,type)→TempAssembly），
            // 游戏代码运行时直接命中缓存，绕开被后续初始化破坏的反射路径
            // 按游戏的精确签名预构造（extraTypes 参与缓存 key）
            System.Type[][] pre =
            {
                new[] { typeof(StardewValley.SaveGame), typeof(StardewValley.Character), typeof(StardewValley.GameLocation), typeof(StardewValley.Item), typeof(StardewValley.Quests.Quest), typeof(StardewValley.TerrainFeatures.TerrainFeature) },
                new[] { typeof(StardewValley.Farmer), typeof(StardewValley.Item) },
                new[] { typeof(StardewValley.GameLocation), typeof(StardewValley.Character), typeof(StardewValley.Item), typeof(StardewValley.TerrainFeatures.TerrainFeature) },
                new[] { typeof(StardewValley.Quests.DescriptionElement), typeof(StardewValley.Character), typeof(StardewValley.Item) },
                new[] { typeof(StardewValley.SaveMigrations.SaveMigrator_1_6.LegacyDescriptionElement), typeof(StardewValley.Quests.DescriptionElement), typeof(StardewValley.Character), typeof(StardewValley.Item) },
            };
            foreach (var sig in pre)
            {
                try
                {
                    var s = new System.Xml.Serialization.XmlSerializer(sig[0], SubArray(sig));
                    Console.WriteLine($"[SDV] pre-xml {sig[0].Name}+{sig.Length - 1} OK");
                }
                catch (Exception pe) { Console.WriteLine($"[SDV] pre-xml {sig[0].Name} FAIL: {pe.InnerException?.Message ?? pe.Message}"); }
            }
            // 预验证游戏首个资产（暴露 manifest/xnb 真实错误）
            try
            {
                var jsonPath = dir + "/Content/ContentHashes.json";
                Console.WriteLine($"[SDV] manifest exists={System.IO.File.Exists(jsonPath)}");
                if (System.IO.File.Exists(jsonPath))
                {
                    var txt = System.IO.File.ReadAllText(jsonPath);
                    Console.WriteLine($"[SDV] manifest len={txt.Length} hasKey={txt.Contains("Data/BigCraftables.xnb") || txt.Contains("Data\\BigCraftables.xnb")}");
                }
                var xnbPath = dir + "/Content/Data/BigCraftables.xnb";
                Console.WriteLine($"[SDV] xnb exists={System.IO.File.Exists(xnbPath)} size={(System.IO.File.Exists(xnbPath) ? new System.IO.FileInfo(xnbPath).Length : -1)}");
            }
            catch (Exception pe3) { Console.WriteLine($"[SDV] preverify EX: {pe3.Message}"); }
            try
            {
                var locProp = typeof(Microsoft.Xna.Framework.TitleContainer).GetProperty("Location", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                var locVal = locProp?.GetValue(null, null) as string;
                Console.WriteLine($"[SDV] TitleContainer.Location={locVal ?? "(null)"}");
            }
            catch (Exception le) { Console.WriteLine($"[SDV] Location EX: {le.Message}"); }
            try
            {
                var dict = ContentManifest.ContentHashParser.ParseFromFile(dir + "/Content/ContentHashes.json");
                Console.WriteLine($"[SDV] CHJsonParser result: {(dict == null ? "NULL" : $"{dict.Count} keys")}, contains={dict != null && dict.ContainsKey("Data/BigCraftables.xnb")}");
            }
            catch (Exception pe4) { Console.WriteLine($"[SDV] CHJsonParser EX: {pe4}"); }
            try
            {
                var svc = new Microsoft.Xna.Framework.GameServiceContainer();
                var lcm = new StardewValley.LocalizedContentManager(svc, "Content");
                var exists = lcm.DoesAssetExist<System.Collections.Generic.Dictionary<string, string>>("Data\\BigCraftables");
                Console.WriteLine($"[SDV] host DoesAssetExist={exists}");
                if (exists)
                {
                    var loaded = lcm.Load<System.Collections.Generic.Dictionary<string, string>>("Data\\BigCraftables");
                    Console.WriteLine($"[SDV] host Load OK n={loaded.Count}");
                }
            }
            catch (Exception ce) { Console.WriteLine($"[SDV] host content EX: {ce}"); }
            // 音频预初始化（游戏侧未触发 OpenAL，host 主动拉起 controller）
            try
            {
                Microsoft.Xna.Framework.Audio.SoundEffect.Initialize();
                Console.WriteLine("[SDV] SoundEffect.Initialize OK");
            }
            catch (Exception ae)
            {
                Console.WriteLine($"[SDV] Sound init FAIL: {ae.GetType().Name}: {ae.Message}");
                var xi = ae.InnerException;
                while (xi != null)
                {
                    Console.WriteLine($"[SDV]   xi {xi.GetType().Name}: {xi.Message}");
                    Console.WriteLine($"[SDV]   xistack: {xi.StackTrace}");
                    xi = xi.InnerException;
                }
            }

            // 预置启动偏好：全屏（SDL OHOS 动态子窗口按 backbuffer 尺寸创建，全屏才铺满屏）
            try
            {
                var prefPath = dir + "/startup_preferences";
                // 全屏 + backbuffer=屏幕物理尺寸（OHOS 全屏窗口 3120x2080，backbuffer 同尺寸才铺满）
                var xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<StartupPreferences xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">\n  <windowMode>2</windowMode>\n  <fullscreenResolutionX>3120</fullscreenResolutionX>\n  <fullscreenResolutionY>2080</fullscreenResolutionY>\n</StartupPreferences>\n";
                System.IO.File.WriteAllText(prefPath, xml);
                Console.WriteLine("[SDV] startup_preferences written (fullscreen 3120x2080)");
            }
            catch (Exception pe5) { Console.WriteLine($"[SDV] pref write FAIL: {pe5.Message}"); }

            // 诊断：存档路径（应用自报——SELinux 挡住外部查看）
            try
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                Console.WriteLine($"[SDV] HOME='{Environment.GetEnvironmentVariable("HOME") ?? "(null)"}' XDG='{Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? "(null)"}' AppData='{appData}'");
                var gm = typeof(StardewValley.Program).GetMethod("GetSavesFolder",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                var sf = gm?.Invoke(null, null) as string;
                Console.WriteLine($"[SDV] GetSavesFolder='{sf}' exists={System.IO.Directory.Exists(sf)}");
                if (System.IO.Directory.Exists(sf))
                {
                    foreach (var d in System.IO.Directory.GetDirectories(sf))
                    {
                        Console.WriteLine($"[SDV] save slot: {System.IO.Path.GetFileName(d)}");
                        var info = System.IO.Path.Combine(d, "SaveGameInfo");
                        if (System.IO.File.Exists(info))
                        {
                            try
                            {
                                var fld = typeof(StardewValley.SaveGame).GetField("farmerSerializer",
                                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                                var ser = fld?.GetValue(null) as System.Xml.Serialization.XmlSerializer;
                                Console.WriteLine($"[SDV] farmerSerializer present={ser != null}");
                                if (ser != null)
                                {
                                    using (var fs2 = System.IO.File.OpenRead(info))
                                    {
                                        var fobj = ser.Deserialize(fs2);
                                        Console.WriteLine($"[SDV] SaveGameInfo deserialize OK -> {fobj?.GetType().Name}");
                                    }
                                }
                            }
                            catch (Exception dx)
                            {
                                Console.WriteLine($"[SDV] SaveGameInfo deserialize FAIL: {dx.GetType().Name}: {dx.Message}");
                                var ix = dx.InnerException; int n = 0;
                                while (ix != null && n++ < 3)
                                {
                                    Console.WriteLine($"[SDV]   inner{n}: {ix.GetType().Name}: {ix.Message}");
                                    if (ix.StackTrace != null)
                                        Console.WriteLine($"[SDV]   inner{n}-stack: {ix.StackTrace.Replace("\n", " | ")}");
                                    ix = ix.InnerException;
                                }
                            }
                            /* 复制到沙箱根供 host 外拉取分析 */
                            try
                            {
                                System.IO.File.Copy(info, "/data/storage/el2/base/haps/entry/files/SaveGameInfo.copy", true);
                                var mainSave = System.IO.Path.Combine(d, System.IO.Path.GetFileName(d));
                                if (System.IO.File.Exists(mainSave))
                                    System.IO.File.Copy(mainSave, "/data/storage/el2/base/haps/entry/files/save.copy", true);
                                Console.WriteLine("[SDV] save files copied to sandbox root");
                            }
                            catch (Exception cp) { Console.WriteLine($"[SDV] copy FAIL: {cp.Message}"); }
                        }
                        else Console.WriteLine($"[SDV] no SaveGameInfo in slot");
                        /* 主存档ファイル拷出（含大小，检查截断） */
                        try
                        {
                            var mainf = System.IO.Path.Combine(d, System.IO.Path.GetFileName(d));
                            if (System.IO.File.Exists(mainf))
                            {
                                var dest = "/data/storage/el2/base/haps/entry/files/save-" + System.IO.Path.GetFileName(d) + ".copy";
                                System.IO.File.Copy(mainf, dest, true);
                                Console.WriteLine($"[SDV] main copied: {System.IO.Path.GetFileName(d)} {new System.IO.FileInfo(mainf).Length}B");
                            }
                            else Console.WriteLine($"[SDV] no main file in {System.IO.Path.GetFileName(d)}");
                        }
                        catch (Exception mex) { Console.WriteLine($"[SDV] main copy FAIL: {mex.Message}"); }
                    }
                }
            }
            catch (Exception dpe) { Console.WriteLine($"[SDV] save-path probe FAIL: {dpe.Message}"); }

            /* 启动后延迟探测：复刻真实加载路径（Game1.content 已初始化），
             * 在后台线程跑，不阻塞游戏主循环 */
            new System.Threading.Thread(() =>
            {
                try
                {
                    System.Threading.Thread.Sleep(70000);
                    Console.WriteLine("[SDV] post-boot probe: 开始");
                    var sf = "/data/storage/el2/base/haps/entry/files/game/StardewValley/Saves";
                    if (System.IO.Directory.Exists(sf))
                    {
                        foreach (var d in System.IO.Directory.GetDirectories(sf))
                        {
                            var slot = System.IO.Path.GetFileName(d);
                            var info = System.IO.Path.Combine(d, "SaveGameInfo");
                            if (!System.IO.File.Exists(info)) { Console.WriteLine($"[SDV] post-boot: {slot} 无 SaveGameInfo"); continue; }
                            try
                            {
                                var fld = typeof(StardewValley.SaveGame).GetField("farmerSerializer",
                                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                                var ser = fld?.GetValue(null) as System.Xml.Serialization.XmlSerializer;
                                using (var fs2 = System.IO.File.OpenRead(info))
                                {
                                    var fobj = ser.Deserialize(fs2);
                                    var farmer = fobj as StardewValley.Farmer;
                                    Console.WriteLine($"[SDV] post-boot: {slot} 反序列化 OK -> {farmer?.Name}");
                                }
                            }
                            catch (Exception dx)
                            {
                                Console.WriteLine($"[SDV] post-boot: {slot} 反序列化 FAIL: {dx.GetType().Name}: {dx.Message}");
                                var ix = dx.InnerException; int n = 0;
                                while (ix != null && n++ < 3)
                                {
                                    Console.WriteLine($"[SDV]   pb-inner{n}: {ix.GetType().Name}: {ix.Message}");
                                    if (ix.StackTrace != null)
                                        Console.WriteLine($"[SDV]   pb-inner{n}-stack: {ix.StackTrace.Replace("\n", " | ")}");
                                    ix = ix.InnerException;
                                }
                            }
                        }
                    }
                }
                catch (Exception pe) { Console.WriteLine($"[SDV] post-boot probe FAIL: {pe.Message}"); }
            }) { IsBackground = true }.Start();

            StardewValley.Program.Main(new[] { "" });
            return 0;
        }
        catch (Exception e)
        {
            Console.WriteLine("[SDV] FATAL: " + e);
            Console.WriteLine("[SDV] STACK: " + e.StackTrace);
            var ie = e.InnerException;
            while (ie != null)
            {
                Console.WriteLine("[SDV] IMSG: " + ie.GetType().Name + ": " + ie.Message);
                Console.WriteLine("[SDV] ISTACK: " + ie.StackTrace);
                ie = ie.InnerException;
            }
            return 2;
        }
    }
}

// 存档路径根：固定沙箱绝对路径（Environment.GetFolderPath(ApplicationData)
// 在设备上返回 ""，HOME 指向 app 不可访问的 /storage/Users/currentUser）
internal static class SdvPaths
{
    public static string GetAppDataRoot()
    {
        return "/data/storage/el2/base/haps/entry/files/game";
    }
}

// GLES 无 glGetTexImage（桌面 GL 专属）：mg380 的 Texture2D/TextureCube.GetData 走
// 该委托会 NRE。mg-patch 已把 PlatformGetData 里的 GL.GetTexImage 调用重定向到
// GLES 无 glGetTexImage（桌面 GL 专属）：mg380 的 Texture2D/TextureCube.GetData 走
// 该委托会 NRE。mg-patch 已把 PlatformGetData 里的 GL.GetTexImage 调用重定向到
// 本类，并给 MonoGame 注入了 InternalsVisibleTo("stardewhost")——因此这里可以
// 强类型直调 internal GL 委托（反射 DynamicInvoke 在 NativeAOT 不回写 out 参数，
// 曾导致 fbo 恒为 0、attach 落到默认帧缓冲而失败）。
// 行序与 GetTexImage 一致（均自底行起），上层逐行拷贝逻辑不变。
internal static unsafe class GlEsShim
{
    static int _diag;

    public static void GetTexImageViaFbo(int glTexture, int level, int fmt, int typ, int face, int w, int h, object pixelsObj)
    {
        var pixels = (Array)pixelsObj;

        int prevFb = 0;
        GL.GetIntegerv(0x8CA6 /* GL_FRAMEBUFFER_BINDING */, &prevFb);

        int fbo = 0;
        GL.GenFramebuffers(1, out fbo);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                                (TextureTarget)face, glTexture, level);

        var gch = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            GL.ReadPixelsInternal(0, 0, w, h, (PixelFormat)fmt, (PixelType)typ, gch.AddrOfPinnedObject());
        }
        finally { gch.Free(); }

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, prevFb);
        GL.DeleteFramebuffers(1, ref fbo);

        if (_diag++ < 4)
            Console.WriteLine($"[SDV] GlEsShim read#{_diag} tex={glTexture} {w}x{h} fmt={fmt} prevFb={prevFb} fbo={fbo}");
    }
}
