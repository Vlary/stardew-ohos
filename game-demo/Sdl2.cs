using System.Runtime.InteropServices;

namespace StardewOhos;

// SDL2 最小绑定（仅验证管线所需子集，命名与 SDL2 C API 一致）
internal static partial class Sdl
{
    const string Lib = "libSDL2.so";

    public const uint SDL_INIT_VIDEO = 0x00000020;
    public const uint SDL_INIT_EVENTS = 0x00004000;
    public const uint SDL_INIT_AUDIO = 0x00000010;
    public const uint SDL_INIT_GAMECONTROLLER = 0x00002000;

    public const int SDL_WINDOWPOS_CENTERED = 0x2FFF0000;

    public const uint SDL_WINDOW_FULLSCREEN = 0x00000001;
    public const uint SDL_WINDOW_OPENGL = 0x00000002;
    public const uint SDL_WINDOW_SHOWN = 0x00000004;
    public const uint SDL_WINDOW_RESIZABLE = 0x00000020;
    public const uint SDL_WINDOW_FULLSCREEN_DESKTOP = 0x00001001;
    public const uint SDL_WINDOW_ALLOW_HIGHDPI = 0x00002000;

    public const int SDL_GL_DOUBLEBUFFER = 5;
    public const int SDL_GL_DEPTH_SIZE = 12;
    public const int SDL_GL_CONTEXT_MAJOR_VERSION = 17;
    public const int SDL_GL_CONTEXT_MINOR_VERSION = 18;
    public const int SDL_GL_CONTEXT_PROFILE_MASK = 21;
    public const int SDL_GL_CONTEXT_PROFILE_ES = 0x0004;

    public const uint SDL_QUIT = 0x100;

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int SDL_Init(uint flags);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void SDL_Quit();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr SDL_GetError();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr SDL_CreateWindow([MarshalAs(UnmanagedType.LPUTF8Str)] string title,
        int x, int y, int w, int h, uint flags);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void SDL_DestroyWindow(IntPtr window);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int SDL_GL_SetAttribute(int attr, int value);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr SDL_GL_CreateContext(IntPtr window);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int SDL_GL_MakeCurrent(IntPtr window, IntPtr ctx);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int SDL_GL_SwapWindow(IntPtr window);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int SDL_PollEvent(out SdlEvent e);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint SDL_GetTicks();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void SDL_Delay(uint ms);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int SDL_SetHint([MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void SDL_Log([MarshalAs(UnmanagedType.LPUTF8Str)] string fmt);

    public static void Log(string msg) => SDL_Log("[SDV-OHOS] " + msg);

    public static int SetHint(string name, string value) => SDL_SetHint(name, value);

    // SDL_Event：56 字节（SDL2 ABI 固定大小）
    [StructLayout(LayoutKind.Sequential, Size = 56)]
    public unsafe struct SdlEvent
    {
        public uint type;
        public uint timestamp;
        public fixed byte pad[48];
    }
}
