using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework.Audio;

namespace StardewOhos;

// MonoGame on OHOS 验证 demo：Game 框架 + SpriteBatch（内嵌 shader→GLES）+ 输入 + 计时
public class OhosGame : Game
{
    GraphicsDeviceManager _gfx;
    SpriteBatch _sb;
    Texture2D _white;
    double _t;
    int _frames;
    int _mouseX, _mouseY;
    string _lastKey = "-";
    bool _lmb;
    int _lastScroll;

    public OhosGame()
    {
        _gfx = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = 1280,
            PreferredBackBufferHeight = 800,
            SynchronizeWithVerticalRetrace = true,
            HardwareModeSwitch = false,
        };
        _gfx.IsFullScreen = true;   // OHOS: 触发 SDL fullscreen → XComponent 铺满整屏
        Window.AllowUserResizing = false;
        IsMouseVisible = true;
        IsFixedTimeStep = false;
        Log("Game constructed");
    }

    static void Log(string m) => Console.WriteLine($"[SDV-MG] {m}");

    protected override void Initialize()
    {
        Log($"Initialize: GL es? device={GraphicsDevice?.GraphicsProfile}");
        base.Initialize();
        Log("Initialize done");
    }

    protected override void LoadContent()
    {
        _sb = new SpriteBatch(GraphicsDevice);
        _white = new Texture2D(GraphicsDevice, 4, 4);
        _white.SetData(new[] { Color.White, Color.White, Color.White, Color.White,
                               Color.White, Color.White, Color.White, Color.White,
                               Color.White, Color.White, Color.White, Color.White,
                               Color.White, Color.White, Color.White, Color.White });
        Log($"LoadContent done: adapter={GraphicsDevice.Adapter.Description}");
        InitAudioTest();
        try
        {
            var ser = new System.Xml.Serialization.XmlSerializer(typeof(StardewValley.Farmer));
            Log($"[XML] XmlSerializer(Farmer) OK: {ser != null}");
            var ser2 = new System.Xml.Serialization.XmlSerializer(typeof(StardewValley.GameLocation));
            Log($"[XML] XmlSerializer(GameLocation) OK: {ser2 != null}");
        }
        catch (Exception xe)
        {
            Log($"[XML] FAIL {xe.GetType().Name}: {xe.Message}");
            var ie = xe.InnerException;
            while (ie != null) { Log($"[XML] inner: {ie.Message}"); ie = ie.InnerException; }
        }
        base.LoadContent();
    }

    SoundEffectInstance _sfx;
    DynamicSoundEffectInstance _dyn;

    void InitAudioTest()
    {
        try
        {
            // 静态 SoundEffect：0.5s 440Hz 正弦
            int sr = 44100;
            var mono = new byte[sr * 2]; // 0.5s mono16（字节数必须为 2 的倍数）
            for (int i = 0; i < sr / 2; i++)
            {
                short v = (short)(System.Math.Sin(2 * System.Math.PI * 440 * i / sr) * 0.3 * 32767);
                System.Buffer.BlockCopy(new[] { v }, 0, mono, i * 2, 2);
            }
            var se = new SoundEffect(mono, 0, mono.Length, sr, AudioChannels.Mono, 0, 0);
            _sfx = se.CreateInstance();
            _sfx.Volume = 0.8f;
            _sfx.Play();
            Log($"[AUD] SoundEffect play: state={_sfx.State} dur={se.Duration.TotalMilliseconds:F0}ms");

            // 流式 DynamicSoundEffectInstance（Song 路径）：1s 660Hz 立体声
            _dyn = new DynamicSoundEffectInstance(sr, AudioChannels.Stereo);
            var st = new byte[44100 * 4]; // 0.5s stereo16
            for (int i = 0; i < 44100; i++)
            {
                short v = (short)(System.Math.Sin(2 * System.Math.PI * 660 * i / sr) * 0.25 * 32767);
                System.Buffer.BlockCopy(new[] { v, v }, 0, st, i * 4, 4);
            }
            _dyn.SubmitBuffer(st);
            _dyn.Volume = 0.6f;
            _dyn.Play();
            Log($"[AUD] Dynamic play: state={_dyn.State} pending={_dyn.PendingBufferCount}");
        }
        catch (Exception e)
        {
            Log($"[AUD] audio test failed: {e.GetType().Name}: {e.Message}");
        }
    }

    protected override void Update(GameTime gameTime)
    {
        _t = gameTime.TotalGameTime.TotalSeconds;
        _frames++;
        var ms = Mouse.GetState();
        _mouseX = ms.X; _mouseY = ms.Y;
        var ks = Keyboard.GetState();
        var keys = ks.GetPressedKeys();
        if (keys.Length > 0 && keys[0].ToString() != _lastKey)
        {
            _lastKey = keys[0].ToString();
            Log($"key pressed: {_lastKey}");
            try { _sfx?.Play(); } catch { }
        }
        if (ms.LeftButton == ButtonState.Pressed && !_lmb)
        {
            _lmb = true;
            Log($"mouse LMB down at ({ms.X},{ms.Y})");
        }
        if (ms.LeftButton == ButtonState.Released) _lmb = false;
        if (ms.ScrollWheelValue != _lastScroll)
        {
            Log($"scroll: {ms.ScrollWheelValue} (delta {ms.ScrollWheelValue - _lastScroll})");
            _lastScroll = ms.ScrollWheelValue;
        }
        if (_frames % 600 == 0)
        {
            Log($"alive t={_t:F1}s mouse=({_mouseX},{_mouseY}) GC={GC.GetTotalMemory(false)}" +
                (_sfx != null ? $" sfx={_sfx.State}" : "") +
                (_dyn != null ? $" dyn={_dyn.State}/{_dyn.PendingBufferCount}" : ""));
        }
        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
        float t = (float)gameTime.TotalGameTime.TotalSeconds;
        var c = new Microsoft.Xna.Framework.Color(
            (int)(128 + 127 * Math.Sin(t)),
            (int)(128 + 127 * Math.Sin(t + 2.09)),
            (int)(128 + 127 * Math.Sin(t + 4.18)), 255);
        GraphicsDevice.Clear(c);
        // SpriteBatch → 内嵌 Effect → GLSL(转 ES) → GLES 全链路
        _sb.Begin();
        int n = 8;
        for (int i = 0; i < n; i++)
        {
            float k = (float)i / n;
            var bc = new Microsoft.Xna.Framework.Color((int)(255 * k), (int)(255 * (1 - k)), 128, 220);
            var pos = new Vector2(40 + k * 1100, 200 + 180 * MathF.Sin(t * (1 + k) + i));
            _sb.Draw(_white, pos, null, bc, 0f, Vector2.Zero, new Vector2(120, 120), SpriteEffects.None, 0f);
        }
        // 鼠标跟随块（输入可视化）
        _sb.Draw(_white, new Vector2(_mouseX - 30, _mouseY - 30), null, Color.Yellow, 0f, Vector2.Zero, new Vector2(60, 60), SpriteEffects.None, 0f);
        _sb.End();
        base.Draw(gameTime);
    }
}

internal static class Program
{
    [DllImport("libc", CallingConvention = CallingConvention.Cdecl)]
    private static extern int open(string path, int flags, int mode);
    [DllImport("libc", CallingConvention = CallingConvention.Cdecl)]
    private static extern int dup2(int oldfd, int newfd);

    [DllImport("libc.so", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sigaction(int sig, ref SigAction sa, IntPtr old);

    [StructLayout(LayoutKind.Sequential)]
    private struct SigAction
    {
        public IntPtr handler;
        public ulong flags;
        public IntPtr restorer;
        public IntPtr mask;
    }

    [UnmanagedCallersOnly]
    private static unsafe void FatalHandler(int sig, IntPtr si, IntPtr ctx)
    {
        // ucontext：arm64 regs[32] 在偏移 0x1A8（mcontext）—— pc=regs[32] sp=regs[31]
        byte* p = (byte*)ctx;
        ulong pc = *(ulong*)(p + 0x1A8 + 32 * 8);
        ulong sp = *(ulong*)(p + 0x1A8 + 31 * 8);
        int fd = 2;
        var msg = $"[SDV-MG] FATAL sig={sig} pc=0x{pc:x} sp=0x{sp:x}\n";
        byte[] b = new byte[128]; int n = 0;
        foreach (char c in msg) b[n++] = (byte)c;
        _ = write(fd, b[0], n);
        _exit(9);
    }

    [DllImport("libc.so", CallingConvention = CallingConvention.Cdecl)]
    private static extern int write(int fd, byte buf, int count);
    [DllImport("libc.so", CallingConvention = CallingConvention.Cdecl)]
    private static extern void _exit(int code);

    static void RedirectFd(string path, int fd)
    {
        int nfd = open(path, 0x41 /*O_WRONLY|O_CREAT*/, 420 /*0644*/);
        if (nfd >= 0) dup2(nfd, fd);
    }

    [UnmanagedCallersOnly(EntryPoint = "SDL_main")]
    public static unsafe int Main(int argc, IntPtr argv)
    {
        // fd 级重定向（1=stdout 2=stderr）
        var dir = "/data/storage/el2/base/haps/entry/files";
        RedirectFd(dir + "/mg-out.log", 1);
        RedirectFd(dir + "/mg-err.log", 2);
        // InstallFatalHandlers(); // 疑似 ABI 问题，先禁用排查
        Console.WriteLine("[SDV-MG] SDL_main — MonoGame on OHOS starting");
        try
        {
            using var g = new OhosGame();
            g.Run();
            return 0;
        }
        catch (Exception e)
        {
            Console.WriteLine("[SDV-MG] FATAL: " + e);
            return 2;
        }
    }

    static unsafe void InstallFatalHandlers()
    {
        delegate* unmanaged<int, IntPtr, IntPtr, void> fp = &FatalHandler;
        var sa = new SigAction { handler = (IntPtr)fp, flags = 0x4 };
        foreach (int sig in new[] { 4, 5, 6, 7, 11 }) _ = sigaction(sig, ref sa, IntPtr.Zero);
    }
}


