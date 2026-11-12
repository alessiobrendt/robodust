using System;
using System.Runtime.InteropServices;
using System.Text;

namespace RoboDust;

/// Just the slice of OpenGL 3.3 RoboDust needs, loaded by hand so there are no dependencies.
public static unsafe class Gl
{
    public const uint COLOR_BUFFER_BIT = 0x4000, TRIANGLES = 4, BLEND = 0x0BE2, ONE = 1, ZERO = 0, SRC_ALPHA = 0x0302, ONE_MINUS_SRC_ALPHA = 0x0303;
    public const uint TEXTURE_2D = 0x0DE1, TEXTURE_2D_ARRAY = 0x8C1A, TEXTURE0 = 0x84C0, RGBA = 0x1908, RGBA8 = 0x8058, BGRA = 0x80E1, UNSIGNED_BYTE = 0x1401, FLOAT = 0x1406;
    public const uint TEXTURE_MIN_FILTER = 0x2801, TEXTURE_MAG_FILTER = 0x2800, TEXTURE_WRAP_S = 0x2802, TEXTURE_WRAP_T = 0x2803, LINEAR = 0x2601, LINEAR_MIPMAP_LINEAR = 0x2703, CLAMP_TO_EDGE = 0x812F;
    public const uint TEXTURE_MAX_ANISOTROPY = 0x84FE, TEXTURE_LOD_BIAS = 0x8501;
    public const uint VERTEX_SHADER = 0x8B31, FRAGMENT_SHADER = 0x8B30, COMPILE_STATUS = 0x8B81, LINK_STATUS = 0x8B82, INFO_LOG_LENGTH = 0x8B84;
    public const uint ARRAY_BUFFER = 0x8892, STATIC_DRAW = 0x88E4, STREAM_DRAW = 0x88E0;
    public const uint FRAMEBUFFER = 0x8D40, COLOR_ATTACHMENT0 = 0x8CE0, FRAMEBUFFER_COMPLETE = 0x8CD5;
    public const uint FUNC_ADD = 0x8006, FUNC_REVERSE_SUBTRACT = 0x800B;
    public const uint PACK_ALIGNMENT = 0x0D05, UNPACK_ALIGNMENT = 0x0CF5, VERSION = 0x1F02, RENDERER = 0x1F01, MULTISAMPLE = 0x809D, FRAMEBUFFER_SRGB = 0x8DB9;

    [DllImport("opengl32.dll")] static extern IntPtr wglCreateContext(IntPtr hdc);
    [DllImport("opengl32.dll")] public static extern bool wglMakeCurrent(IntPtr hdc, IntPtr ctx);
    [DllImport("opengl32.dll")] public static extern bool wglDeleteContext(IntPtr ctx);
    [DllImport("opengl32.dll")] static extern IntPtr wglGetProcAddress(string name);
    [DllImport("kernel32.dll")] static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr LoadLibrary(string name);
    [DllImport("gdi32.dll")] static extern int ChoosePixelFormat(IntPtr hdc, ref PIXELFORMATDESCRIPTOR pfd);
    [DllImport("gdi32.dll")] static extern bool SetPixelFormat(IntPtr hdc, int format, ref PIXELFORMATDESCRIPTOR pfd);
    [DllImport("gdi32.dll")] public static extern bool SwapBuffers(IntPtr hdc);

    [StructLayout(LayoutKind.Sequential)]
    struct PIXELFORMATDESCRIPTOR
    {
        public ushort nSize, nVersion; public uint dwFlags; public byte iPixelType, cColorBits, cRedBits, cRedShift, cGreenBits, cGreenShift, cBlueBits, cBlueShift, cAlphaBits, cAlphaShift, cAccumBits, cAccumRedBits, cAccumGreenBits, cAccumBlueBits, cAccumAlphaBits, cDepthBits, cStencilBits, cAuxBuffers, iLayerType, bReserved; public uint dwLayerMask, dwVisibleMask, dwDamageMask;
    }

    public static delegate* unmanaged<int, int, int, int, void> Viewport;
    public static delegate* unmanaged<float, float, float, float, void> ClearColor;
    public static delegate* unmanaged<uint, void> Clear, Enable, Disable;
    public static delegate* unmanaged<uint, uint, void> BlendFunc;
    public static delegate* unmanaged<uint, uint, uint, uint, void> BlendFuncSeparate;
    public static delegate* unmanaged<uint, void> BlendEquation;
    public static delegate* unmanaged<byte, byte, byte, byte, void> ColorMask;
    public static delegate* unmanaged<uint, int, int, void> DrawArrays;
    public static delegate* unmanaged<uint, int, int, int, void> DrawArraysInstanced;
    public static delegate* unmanaged<int, uint*, void> GenTextures, GenBuffers, GenVertexArrays, GenFramebuffers;
    public static delegate* unmanaged<int, uint*, void> DeleteTextures, DeleteBuffers, DeleteVertexArrays, DeleteFramebuffers;
    public static delegate* unmanaged<uint, uint, void> BindTexture, BindBuffer, BindFramebuffer;
    public static delegate* unmanaged<uint, void> BindVertexArray, ActiveTexture, GenerateMipmap;
    public static delegate* unmanaged<uint, uint, int, void> TexParameteri;
    public static delegate* unmanaged<uint, uint, float, void> TexParameterf;
    public static delegate* unmanaged<uint, int, int, int, int, int, uint, uint, void*, void> TexImage2D;
    public static delegate* unmanaged<uint, int, int, int, int, int, int, uint, uint, void*, void> TexImage3D;
    public static delegate* unmanaged<uint, int, int, int, int, int, int, int, uint, uint, void*, void> TexSubImage3D;
    public static delegate* unmanaged<uint, uint, uint, uint, int, void> FramebufferTexture2D;
    public static delegate* unmanaged<uint, uint> CheckFramebufferStatus;
    public static delegate* unmanaged<uint, nint, void*, uint, void> BufferData;
    public static delegate* unmanaged<uint, nint, nint, void*, void> BufferSubData;
    public static delegate* unmanaged<uint, int, uint, byte, int, nint, void> VertexAttribPointer;
    public static delegate* unmanaged<uint, void> EnableVertexAttribArray;
    public static delegate* unmanaged<uint, uint, void> VertexAttribDivisor;
    public static delegate* unmanaged<uint, uint> CreateShader;
    public static delegate* unmanaged<uint, int, byte**, int*, void> ShaderSource;
    public static delegate* unmanaged<uint, void> CompileShader, LinkProgram, UseProgram, DeleteShader;
    public static delegate* unmanaged<uint, uint, int*, void> GetShaderiv, GetProgramiv;
    public static delegate* unmanaged<uint, int, int*, byte*, void> GetShaderInfoLog, GetProgramInfoLog;
    public static delegate* unmanaged<uint> CreateProgram;
    public static delegate* unmanaged<uint, uint, void> AttachShader;
    public static delegate* unmanaged<uint, uint, byte*, void> BindAttribLocation;
    public static delegate* unmanaged<uint, byte*, int> GetUniformLocation;
    public static delegate* unmanaged<int, float, void> Uniform1f;
    public static delegate* unmanaged<int, float, float, void> Uniform2f;
    public static delegate* unmanaged<int, float, float, float, float, void> Uniform4f;
    public static delegate* unmanaged<int, int, void> Uniform1i;
    public static delegate* unmanaged<int, int, float*, void> Uniform4fv, Uniform3fv;
    public static delegate* unmanaged<int, int, int, int, uint, uint, void*, void> ReadPixels;
    public static delegate* unmanaged<uint, int, void> PixelStorei;
    public static delegate* unmanaged<uint> GetError;
    public static delegate* unmanaged<uint, byte*> GetString;
    public static delegate* unmanaged<int, int> SwapIntervalEXT;
    public static delegate* unmanaged<void> Finish;

    static IntPtr lib;

    static IntPtr Proc(string name)
    {
        var p = wglGetProcAddress(name);
        if (p == IntPtr.Zero || p == (IntPtr)1 || p == (IntPtr)2 || p == (IntPtr)3 || p == (IntPtr)(-1))
            p = GetProcAddress(lib, name);
        return p;
    }

    /// Creates a context on the window and makes it current. Returns (hdc, context).
    public static (IntPtr dc, IntPtr ctx) CreateContext(IntPtr hwnd)
    {
        var dc = Native.GetDC(hwnd);
        var pfd = new PIXELFORMATDESCRIPTOR
        {
            nSize = (ushort)Marshal.SizeOf<PIXELFORMATDESCRIPTOR>(), nVersion = 1,
            dwFlags = 0x4 | 0x20 | 0x1, // draw to window, support OpenGL, double buffer
            iPixelType = 0, cColorBits = 32, cAlphaBits = 8, iLayerType = 0,
        };
        var fmt = ChoosePixelFormat(dc, ref pfd);
        if (fmt == 0 || !SetPixelFormat(dc, fmt, ref pfd)) throw new Exception("Kein passendes Pixelformat für OpenGL.");
        var ctx = wglCreateContext(dc);
        if (ctx == IntPtr.Zero || !wglMakeCurrent(dc, ctx)) throw new Exception("OpenGL ist auf diesem PC nicht verfügbar.");
        Load();
        var ver = Marshal.PtrToStringAnsi((IntPtr)GetString(VERSION)) ?? "0";
        if (!int.TryParse(ver.Split('.')[0], out var major) || major < 3)
            throw new Exception($"RoboDust braucht OpenGL 3.3, der Grafiktreiber bietet {ver}.");
        return (dc, ctx);
    }

    public static string Version => Marshal.PtrToStringAnsi((IntPtr)GetString(VERSION)) + " / " + Marshal.PtrToStringAnsi((IntPtr)GetString(RENDERER));

    static void Load()
    {
        lib = LoadLibrary("opengl32.dll");
        Viewport = (delegate* unmanaged<int, int, int, int, void>)Proc("glViewport");
        ClearColor = (delegate* unmanaged<float, float, float, float, void>)Proc("glClearColor");
        Clear = (delegate* unmanaged<uint, void>)Proc("glClear");
        Enable = (delegate* unmanaged<uint, void>)Proc("glEnable");
        Disable = (delegate* unmanaged<uint, void>)Proc("glDisable");
        BlendFunc = (delegate* unmanaged<uint, uint, void>)Proc("glBlendFunc");
        BlendFuncSeparate = (delegate* unmanaged<uint, uint, uint, uint, void>)Proc("glBlendFuncSeparate");
        BlendEquation = (delegate* unmanaged<uint, void>)Proc("glBlendEquation");
        ColorMask = (delegate* unmanaged<byte, byte, byte, byte, void>)Proc("glColorMask");
        DrawArrays = (delegate* unmanaged<uint, int, int, void>)Proc("glDrawArrays");
        DrawArraysInstanced = (delegate* unmanaged<uint, int, int, int, void>)Proc("glDrawArraysInstanced");
        GenTextures = (delegate* unmanaged<int, uint*, void>)Proc("glGenTextures");
        GenBuffers = (delegate* unmanaged<int, uint*, void>)Proc("glGenBuffers");
        GenVertexArrays = (delegate* unmanaged<int, uint*, void>)Proc("glGenVertexArrays");
        GenFramebuffers = (delegate* unmanaged<int, uint*, void>)Proc("glGenFramebuffers");
        DeleteTextures = (delegate* unmanaged<int, uint*, void>)Proc("glDeleteTextures");
        DeleteBuffers = (delegate* unmanaged<int, uint*, void>)Proc("glDeleteBuffers");
        DeleteVertexArrays = (delegate* unmanaged<int, uint*, void>)Proc("glDeleteVertexArrays");
        DeleteFramebuffers = (delegate* unmanaged<int, uint*, void>)Proc("glDeleteFramebuffers");
        BindTexture = (delegate* unmanaged<uint, uint, void>)Proc("glBindTexture");
        BindBuffer = (delegate* unmanaged<uint, uint, void>)Proc("glBindBuffer");
        BindFramebuffer = (delegate* unmanaged<uint, uint, void>)Proc("glBindFramebuffer");
        BindVertexArray = (delegate* unmanaged<uint, void>)Proc("glBindVertexArray");
        ActiveTexture = (delegate* unmanaged<uint, void>)Proc("glActiveTexture");
        GenerateMipmap = (delegate* unmanaged<uint, void>)Proc("glGenerateMipmap");
        TexParameteri = (delegate* unmanaged<uint, uint, int, void>)Proc("glTexParameteri");
        TexParameterf = (delegate* unmanaged<uint, uint, float, void>)Proc("glTexParameterf");
        TexImage2D = (delegate* unmanaged<uint, int, int, int, int, int, uint, uint, void*, void>)Proc("glTexImage2D");
        TexImage3D = (delegate* unmanaged<uint, int, int, int, int, int, int, uint, uint, void*, void>)Proc("glTexImage3D");
        TexSubImage3D = (delegate* unmanaged<uint, int, int, int, int, int, int, int, uint, uint, void*, void>)Proc("glTexSubImage3D");
        FramebufferTexture2D = (delegate* unmanaged<uint, uint, uint, uint, int, void>)Proc("glFramebufferTexture2D");
        CheckFramebufferStatus = (delegate* unmanaged<uint, uint>)Proc("glCheckFramebufferStatus");
        BufferData = (delegate* unmanaged<uint, nint, void*, uint, void>)Proc("glBufferData");
        BufferSubData = (delegate* unmanaged<uint, nint, nint, void*, void>)Proc("glBufferSubData");
        VertexAttribPointer = (delegate* unmanaged<uint, int, uint, byte, int, nint, void>)Proc("glVertexAttribPointer");
        EnableVertexAttribArray = (delegate* unmanaged<uint, void>)Proc("glEnableVertexAttribArray");
        VertexAttribDivisor = (delegate* unmanaged<uint, uint, void>)Proc("glVertexAttribDivisor");
        CreateShader = (delegate* unmanaged<uint, uint>)Proc("glCreateShader");
        ShaderSource = (delegate* unmanaged<uint, int, byte**, int*, void>)Proc("glShaderSource");
        CompileShader = (delegate* unmanaged<uint, void>)Proc("glCompileShader");
        LinkProgram = (delegate* unmanaged<uint, void>)Proc("glLinkProgram");
        UseProgram = (delegate* unmanaged<uint, void>)Proc("glUseProgram");
        DeleteShader = (delegate* unmanaged<uint, void>)Proc("glDeleteShader");
        GetShaderiv = (delegate* unmanaged<uint, uint, int*, void>)Proc("glGetShaderiv");
        GetProgramiv = (delegate* unmanaged<uint, uint, int*, void>)Proc("glGetProgramiv");
        GetShaderInfoLog = (delegate* unmanaged<uint, int, int*, byte*, void>)Proc("glGetShaderInfoLog");
        GetProgramInfoLog = (delegate* unmanaged<uint, int, int*, byte*, void>)Proc("glGetProgramInfoLog");
        CreateProgram = (delegate* unmanaged<uint>)Proc("glCreateProgram");
        AttachShader = (delegate* unmanaged<uint, uint, void>)Proc("glAttachShader");
        BindAttribLocation = (delegate* unmanaged<uint, uint, byte*, void>)Proc("glBindAttribLocation");
        GetUniformLocation = (delegate* unmanaged<uint, byte*, int>)Proc("glGetUniformLocation");
        Uniform1f = (delegate* unmanaged<int, float, void>)Proc("glUniform1f");
        Uniform2f = (delegate* unmanaged<int, float, float, void>)Proc("glUniform2f");
        Uniform4f = (delegate* unmanaged<int, float, float, float, float, void>)Proc("glUniform4f");
        Uniform1i = (delegate* unmanaged<int, int, void>)Proc("glUniform1i");
        Uniform4fv = (delegate* unmanaged<int, int, float*, void>)Proc("glUniform4fv");
        Uniform3fv = (delegate* unmanaged<int, int, float*, void>)Proc("glUniform3fv");
        ReadPixels = (delegate* unmanaged<int, int, int, int, uint, uint, void*, void>)Proc("glReadPixels");
        PixelStorei = (delegate* unmanaged<uint, int, void>)Proc("glPixelStorei");
        GetError = (delegate* unmanaged<uint>)Proc("glGetError");
        GetString = (delegate* unmanaged<uint, byte*>)Proc("glGetString");
        Finish = (delegate* unmanaged<void>)Proc("glFinish");
        SwapIntervalEXT = (delegate* unmanaged<int, int>)Proc("wglSwapIntervalEXT");
    }

    public static uint Program(string vs, string fs, params string[] attribs)
    {
        uint Compile(uint type, string src)
        {
            var s = CreateShader(type);
            var bytes = Encoding.UTF8.GetBytes(src + "\0");
            fixed (byte* p = bytes) { var pp = p; ShaderSource(s, 1, &pp, null); }
            CompileShader(s);
            int ok; GetShaderiv(s, COMPILE_STATUS, &ok);
            if (ok == 0)
            {
                var log = new byte[8192]; int len;
                fixed (byte* l = log) GetShaderInfoLog(s, log.Length, &len, l);
                throw new Exception("Shader: " + Encoding.UTF8.GetString(log, 0, len) + "\n" + src);
            }
            return s;
        }
        var prog = CreateProgram();
        var v = Compile(VERTEX_SHADER, vs); var f = Compile(FRAGMENT_SHADER, fs);
        AttachShader(prog, v); AttachShader(prog, f);
        for (uint i = 0; i < attribs.Length; i++)
        {
            var b = Encoding.ASCII.GetBytes(attribs[i] + "\0");
            fixed (byte* p = b) BindAttribLocation(prog, i, p);
        }
        LinkProgram(prog);
        int linked; GetProgramiv(prog, LINK_STATUS, &linked);
        if (linked == 0)
        {
            var log = new byte[8192]; int len;
            fixed (byte* l = log) GetProgramInfoLog(prog, log.Length, &len, l);
            throw new Exception("Link: " + Encoding.UTF8.GetString(log, 0, len));
        }
        DeleteShader(v); DeleteShader(f);
        return prog;
    }

    public static int Loc(uint prog, string name)
    {
        var b = Encoding.ASCII.GetBytes(name + "\0");
        fixed (byte* p = b) return GetUniformLocation(prog, p);
    }

    public static uint Gen(delegate* unmanaged<int, uint*, void> f) { uint id; f(1, &id); return id; }
}
