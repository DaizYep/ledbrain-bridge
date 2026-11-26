// LEDBRAIN Bridge — ядро без Node.js: приём NDI и Spout, сжатие в JPEG (GDI+), локальный сервер HTTP + WebSocket.
//   GET  http://127.0.0.1:47800/        → { app, version, sources: ["NDI · …", "Spout · …"] }
//   GET  http://127.0.0.1:47800/status  → состояние для окна
//   WS   ws://127.0.0.1:47800/ws?src=…  → бинарные кадры JPEG; текстом — { w, h, fps, src }
// Слушает только 127.0.0.1 и принимает страницы ledbrain.pro (и прежний led.gladkovegor.ru) и localhost. Ничего никуда не отправляет.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

// ---------- NDI SDK: библиотека из NDI Tools / NDI Runtime ----------
static class Ndi
{
    const string Dll = "Processing.NDI.Lib.x64.dll";
    [StructLayout(LayoutKind.Sequential)] public struct FindCreate { [MarshalAs(UnmanagedType.U1)] public bool show_local_sources; public IntPtr p_groups; public IntPtr p_extra_ips; }
    [StructLayout(LayoutKind.Sequential)] public struct Source { public IntPtr p_ndi_name; public IntPtr p_url_address; }
    [StructLayout(LayoutKind.Sequential)] public struct RecvCreate { public Source source; public int color_format; public int bandwidth; [MarshalAs(UnmanagedType.U1)] public bool allow_video_fields; public IntPtr p_ndi_recv_name; }
    [StructLayout(LayoutKind.Sequential)] public struct Video { public int xres, yres; public uint FourCC; public int frame_rate_N, frame_rate_D; public float picture_aspect_ratio; public int frame_format_type; public long timecode; public IntPtr p_data; public int line_stride_in_bytes; public IntPtr p_metadata; public long timestamp; }

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.U1)] public static extern bool NDIlib_initialize();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr NDIlib_find_create_v2(ref FindCreate p);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr NDIlib_find_get_current_sources(IntPtr inst, out uint n);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr NDIlib_recv_create_v3(ref RecvCreate p);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void NDIlib_recv_destroy(IntPtr inst);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int NDIlib_recv_capture_v2(IntPtr inst, ref Video v, IntPtr a, IntPtr m, uint timeout);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void NDIlib_recv_free_video_v2(IntPtr inst, ref Video v);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr AddDllDirectory(string dir);

    public const int BGRX_BGRA = 0, BW_HIGHEST = 100, FRAME_VIDEO = 1;
    public static string Path = null;
    static IntPtr finder = IntPtr.Zero;
    static readonly object gate = new object();

    // найти библиотеку и запустить NDI; false — NDI недоступен (Spout работает и без него)
    public static bool Init()
    {
        var dirs = new List<string>();
        foreach (var v in new[] { "NDI_RUNTIME_DIR_V6", "NDI_RUNTIME_DIR_V5" }) { var e = Environment.GetEnvironmentVariable(v); if (!string.IsNullOrEmpty(e)) dirs.Add(e); }
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        dirs.AddRange(new[] { System.IO.Path.Combine(pf, @"NDI\NDI 6 Tools\Runtime"), System.IO.Path.Combine(pf, @"NDI\NDI 6 Runtime\v6"), System.IO.Path.Combine(pf, @"NDI\NDI 5 Runtime\v5"), System.IO.Path.Combine(pf, @"NDI\NDI 5 Tools\Runtime") });
        foreach (var d in dirs)
        {
            var f = System.IO.Path.Combine(d, Dll);
            if (!File.Exists(f)) continue;
            AddDllDirectory(d); // в поиск — только эта папка; папка exe не участвует (DefaultDllImportSearchPaths в App.cs)
            try
            {
                if (!NDIlib_initialize()) return false;
                var fc = new FindCreate { show_local_sources = true };
                finder = NDIlib_find_create_v2(ref fc);
                Path = f;
                return true;
            }
            catch { return false; }
        }
        return false;
    }
    public static List<KeyValuePair<string, string>> Sources()
    {
        var list = new List<KeyValuePair<string, string>>();
        if (finder == IntPtr.Zero) return list;
        lock (gate)
        {
            uint n; var p = NDIlib_find_get_current_sources(finder, out n);
            int sz = Marshal.SizeOf(typeof(Source));
            for (int i = 0; i < n && p != IntPtr.Zero; i++)
            {
                var s = (Source)Marshal.PtrToStructure(new IntPtr(p.ToInt64() + i * sz), typeof(Source));
                list.Add(new KeyValuePair<string, string>(Utf8(s.p_ndi_name), Utf8(s.p_url_address)));
            }
        }
        return list;
    }
    // строка UTF-8 в неуправляемой памяти (имена NDI-источников бывают не латиницей); освобождать FreeHGlobal
    public static IntPtr Utf8Alloc(string s)
    {
        var b = Encoding.UTF8.GetBytes(s ?? ""); var p = Marshal.AllocHGlobal(b.Length + 1);
        Marshal.Copy(b, 0, p, b.Length); Marshal.WriteByte(p, b.Length, 0); return p;
    }
    public static string Utf8(IntPtr p)
    {
        if (p == IntPtr.Zero) return "";
        int n = 0; while (Marshal.ReadByte(p, n) != 0) n++;
        var b = new byte[n]; Marshal.Copy(p, b, 0, n); return Encoding.UTF8.GetString(b);
    }
}

// ---------- Spout без SDK: имена и описание текстуры — общая память Windows, кадр — общая текстура DirectX 11 ----------
static class Spout
{
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] static extern IntPtr OpenFileMappingA(uint access, bool inherit, string name);
    [DllImport("kernel32.dll")] static extern IntPtr MapViewOfFile(IntPtr map, uint access, uint hi, uint lo, UIntPtr n);
    [DllImport("kernel32.dll")] static extern bool UnmapViewOfFile(IntPtr p);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] static extern UIntPtr VirtualQuery(IntPtr p, byte[] info, UIntPtr n);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] public static extern IntPtr OpenMutexA(uint access, bool inherit, string name);
    [DllImport("kernel32.dll")] public static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll")] public static extern bool ReleaseMutex(IntPtr h);

    // прочитать до n байт общей памяти по имени; размер области — у Windows (в Spout он зависит от MaxSenders)
    static byte[] ReadShared(string name, int n)
    {
        var h = OpenFileMappingA(4, false, name);
        if (h == IntPtr.Zero) return null;
        try
        {
            var p = MapViewOfFile(h, 4, 0, 0, UIntPtr.Zero);
            if (p == IntPtr.Zero) return null;
            try
            {
                var mbi = new byte[48];
                if (VirtualQuery(p, mbi, new UIntPtr(48)) == UIntPtr.Zero) return null;
                long size = BitConverter.ToInt64(mbi, 24);
                var b = new byte[Math.Min(n, size)];
                Marshal.Copy(p, b, 0, b.Length);
                return b;
            }
            finally { UnmapViewOfFile(p); }
        }
        finally { CloseHandle(h); }
    }
    static string CStr(byte[] b, int off, int n)
    {
        if (off >= b.Length) return "";
        int end = off; while (end < b.Length && end < off + n && b[end] != 0) end++;
        return Encoding.Default.GetString(b, off, end - off);
    }
    // список отправителей: SpoutSenderNames — подряд имена по 256 байт
    public static List<string> Senders()
    {
        var list = new List<string>();
        var b = ReadShared("SpoutSenderNames", 256 * 64);
        if (b == null) return list;
        for (int i = 0; i < 64; i++) { var n = CStr(b, i * 256, 256); if (n.Length == 0) break; if (Info(n) != null) list.Add(n); }
        return list;
    }
    public class TexInfo { public uint Handle, W, H, Format; }
    // SharedTextureInfo: shareHandle, width, height, format (по uint32)
    public static TexInfo Info(string name)
    {
        var b = ReadShared(name, 20);
        if (b == null || b.Length < 20) return null;
        var t = new TexInfo { Handle = BitConverter.ToUInt32(b, 0), W = BitConverter.ToUInt32(b, 4), H = BitConverter.ToUInt32(b, 8), Format = BitConverter.ToUInt32(b, 12) };
        return t.W > 0 && t.H > 0 ? t : null;
    }

    // ---- D3D11 через таблицы виртуальных функций COM ----
    [DllImport("d3d11.dll")] static extern int D3D11CreateDevice(IntPtr adapter, int type, IntPtr soft, uint flags, IntPtr levels, uint n, uint sdk, out IntPtr dev, out int fl, out IntPtr ctx);
    [DllImport("dxgi.dll")] static extern int CreateDXGIFactory1(ref Guid iid, out IntPtr f);
    [StructLayout(LayoutKind.Sequential)] public struct TexDesc { public uint Width, Height, MipLevels, ArraySize, Format, Count, Quality, Usage, BindFlags, CPUAccessFlags, MiscFlags; }
    [StructLayout(LayoutKind.Sequential)] public struct Mapped { public IntPtr pData; public uint RowPitch, DepthPitch; }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate uint ReleaseFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int OpenSharedFn(IntPtr self, IntPtr handle, ref Guid iid, out IntPtr res);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateTexFn(IntPtr self, ref TexDesc desc, IntPtr init, out IntPtr tex);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void GetDescFn(IntPtr self, out TexDesc desc);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int MapFn(IntPtr self, IntPtr res, uint sub, int type, uint flags, out Mapped m);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void UnmapFn(IntPtr self, IntPtr res, uint sub);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void CopyFn(IntPtr self, IntPtr dst, IntPtr src);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int EnumAdaptersFn(IntPtr self, uint i, out IntPtr a);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int AdapterDescFn(IntPtr self, byte[] desc);
    static T VT<T>(IntPtr obj, int i) where T : class
    {
        var vt = Marshal.ReadIntPtr(obj);
        return (T)(object)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(vt, i * IntPtr.Size), typeof(T));
    }
    static void Release(IntPtr o) { if (o != IntPtr.Zero) VT<ReleaseFn>(o, 2)(o); }
    static Guid IID_Texture2D = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c"), IID_Factory1 = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");

    public class Dev { public IntPtr Device, Ctx; public string Name; }
    static List<Dev> devs;
    static readonly object devGate = new object();
    // общую текстуру открывает только устройство на той же видеокарте, где рисует отправитель (ноутбук: Intel + NVIDIA)
    public static List<Dev> Devices()
    {
        lock (devGate)
        {
            if (devs != null) return devs;
            devs = new List<Dev>();
            IntPtr f;
            if (CreateDXGIFactory1(ref IID_Factory1, out f) < 0) return devs;
            for (uint i = 0; i < 8; i++)
            {
                IntPtr a;
                if (VT<EnumAdaptersFn>(f, 12)(f, i, out a) < 0 || a == IntPtr.Zero) break;
                var desc = new byte[512];
                VT<AdapterDescFn>(a, 10)(a, desc);
                var name = Encoding.Unicode.GetString(desc, 0, 256); int z = name.IndexOf('\0'); if (z >= 0) name = name.Substring(0, z);
                IntPtr d, c; int fl;
                if (D3D11CreateDevice(a, 0, IntPtr.Zero, 0, IntPtr.Zero, 0, 7, out d, out fl, out c) >= 0 && d != IntPtr.Zero) devs.Add(new Dev { Device = d, Ctx = c, Name = name });
                Release(a);
            }
            Release(f);
            return devs;
        }
    }

    // приёмник одного отправителя
    public class Receiver : IDisposable
    {
        readonly string name; IntPtr src, stage, mutex; uint handle, w, h, fmt; Dev D;
        public string Gpu { get { return D == null ? "" : D.Name; } }
        public Receiver(string name) { this.name = name; mutex = OpenMutexA(0x00100001, false, name + "_SpoutAccessMutex"); }
        void Close() { Release(stage); Release(src); stage = src = IntPtr.Zero; }
        bool Open(TexInfo I)
        {
            Close();
            var hv = new IntPtr((int)I.Handle); // 32-битный дескриптор Spout расширяется со знаком, как LongToPtr
            D = null;
            foreach (var x in Devices())
            {
                IntPtr r;
                if (VT<OpenSharedFn>(x.Device, 28)(x.Device, hv, ref IID_Texture2D, out r) >= 0 && r != IntPtr.Zero) { D = x; src = r; break; }
            }
            if (D == null) return false;
            TexDesc desc; VT<GetDescFn>(src, 10)(src, out desc);
            // 87/88/91 — BGRA/BGRX, 28/29 — RGBA (8 бит на канал): остальное (16/32 бит, сжатые) не читаем
            if (desc.Format != 87 && desc.Format != 88 && desc.Format != 91 && desc.Format != 28 && desc.Format != 29) { Server.Log("Spout: " + name + " — формат текстуры " + desc.Format + " не поддерживается"); Close(); return false; }
            var sd = new TexDesc { Width = desc.Width, Height = desc.Height, MipLevels = 1, ArraySize = 1, Format = desc.Format, Count = 1, Usage = 3, CPUAccessFlags = 0x20000 };
            IntPtr s;
            if (VT<CreateTexFn>(D.Device, 5)(D.Device, ref sd, IntPtr.Zero, out s) < 0) { Close(); return false; }
            stage = s; handle = I.Handle; w = desc.Width; h = desc.Height; fmt = desc.Format;
            return true;
        }
        // снять кадр: пиксели BGRA (или RGBA — rgba = true) в buf со строкой pitch
        public bool Grab(ref byte[] buf, out int W, out int H, out int pitch, out bool rgba)
        {
            W = H = pitch = 0; rgba = false;
            var I = Info(name);
            if (I == null) return false;
            if (I.Handle != handle || src == IntPtr.Zero) if (!Open(I)) return false;
            // immediate context D3D11 не потокобезопасен, а устройство общее для всех Spout-приёмников (debugger)
            lock (D)
            {
                bool locked = mutex != IntPtr.Zero && WaitForSingleObject(mutex, 40) == 0;
                VT<CopyFn>(D.Ctx, 47)(D.Ctx, stage, src);
                if (locked) ReleaseMutex(mutex);
                Mapped m;
                if (VT<MapFn>(D.Ctx, 14)(D.Ctx, stage, 0, 1, 0, out m) < 0) return false;
                long nl = (long)m.RowPitch * h;
                if (nl > int.MaxValue) { VT<UnmapFn>(D.Ctx, 15)(D.Ctx, stage, 0); return false; }
                int n = (int)nl;
                if (buf == null || buf.Length < n) buf = new byte[n];
                Marshal.Copy(m.pData, buf, 0, n);
                VT<UnmapFn>(D.Ctx, 15)(D.Ctx, stage, 0);
                pitch = (int)m.RowPitch;
            }
            W = (int)w; H = (int)h; rgba = fmt == 28 || fmt == 29;
            return true;
        }
        public void Dispose() { Close(); if (mutex != IntPtr.Zero) CloseHandle(mutex); mutex = IntPtr.Zero; }
    }
}

// ---------- JPEG: кадр шире MaxW уменьшается (для предпросмотра хватает, а сжатие быстрее) ----------
static class Jpeg
{
    public static int MaxW = 2048;
    static readonly ImageCodecInfo codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
    // уменьшение — своё (среднее 2×2 в точке выборки, unsafe): GDI+ DrawImage медленный и выполняет вызовы по одному.
    // GDI+ остаётся только JPEG уже маленького кадра
    public static unsafe byte[] Encode(IntPtr px, int w, int h, int stride, bool rgba)
    {
        int tw = Math.Min(w, MaxW), th = Math.Max(1, (int)Math.Round(h * (double)tw / w));
        using (var dst = new Bitmap(tw, th, PixelFormat.Format24bppRgb))
        {
            var bd = dst.LockBits(new Rectangle(0, 0, tw, th), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            try
            {
                double sx = (double)w / tw, sy = (double)h / th;
                bool box = tw < w;
                int ri = rgba ? 2 : 0, bi = rgba ? 0 : 2; // в памяти BGRA (Windows) или RGBA
                byte* src = (byte*)px;
                for (int y = 0; y < th; y++)
                {
                    int y0 = Math.Min(h - 1, (int)(y * sy)), y1 = box ? Math.Min(h - 1, y0 + 1) : y0;
                    byte* r0 = src + (long)y0 * stride, r1 = src + (long)y1 * stride;
                    byte* d = (byte*)bd.Scan0 + (long)y * bd.Stride;
                    for (int x = 0; x < tw; x++, d += 3)
                    {
                        int x0 = Math.Min(w - 1, (int)(x * sx)), x1 = box ? Math.Min(w - 1, x0 + 1) : x0;
                        byte* a = r0 + x0 * 4; byte* b = r0 + x1 * 4; byte* c = r1 + x0 * 4; byte* e = r1 + x1 * 4;
                        d[0] = (byte)((a[ri] + b[ri] + c[ri] + e[ri] + 2) >> 2);
                        d[1] = (byte)((a[1] + b[1] + c[1] + e[1] + 2) >> 2);
                        d[2] = (byte)((a[bi] + b[bi] + c[bi] + e[bi] + 2) >> 2);
                    }
                }
            }
            finally { dst.UnlockBits(bd); }
            using (var ms = new MemoryStream())
            using (var ep = new EncoderParameters(1))
            {
                ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 82L);
                dst.Save(ms, codec, ep);
                return ms.ToArray();
            }
        }
    }
}

// ---------- клиент WebSocket: отправка без очереди — пока шлётся кадр, новые пропускаются ----------
class WsClient
{
    readonly TcpClient tcp; readonly Stream stream; readonly object sendGate = new object();
    public volatile bool Closed; public bool NeedMeta = true; int sending;
    public WsClient(TcpClient t, Stream s) { tcp = t; stream = s; }
    void Write(int opcode, byte[] data)
    {
        var head = new List<byte> { (byte)(0x80 | opcode) };
        if (data.Length < 126) head.Add((byte)data.Length);
        else if (data.Length < 65536) { head.Add(126); head.Add((byte)(data.Length >> 8)); head.Add((byte)data.Length); }
        else { head.Add(127); for (int i = 7; i >= 0; i--) head.Add((byte)((long)data.Length >> (8 * i))); }
        lock (sendGate) { stream.Write(head.ToArray(), 0, head.Count); stream.Write(data, 0, data.Length); stream.Flush(); }
    }
    public void SendText(string s) { try { Write(1, Encoding.UTF8.GetBytes(s)); } catch { Close(); } }
    // кадр: если предыдущий ещё уходит — этот пропускаем (медленный клиент не копит очередь)
    // false — кадр пропущен (предыдущий ещё уходит): тогда размер кадра надо прислать со следующим (debugger)
    public bool SendFrame(string meta, byte[] jpg)
    {
        if (Closed || Interlocked.CompareExchange(ref sending, 1, 0) != 0) return false;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { if (meta != null) Write(1, Encoding.UTF8.GetBytes(meta)); Write(2, jpg); }
            catch { Close(); }
            finally { Interlocked.Exchange(ref sending, 0); }
        });
        return true;
    }
    // читаем кадры клиента: закрытие и ping
    public void ReadLoop()
    {
        try
        {
            var hdr = new byte[14];
            while (!Closed)
            {
                if (!ReadN(hdr, 0, 2)) break;
                int op = hdr[0] & 15; bool masked = (hdr[1] & 128) != 0; long len = hdr[1] & 127;
                if (len == 126) { if (!ReadN(hdr, 2, 2)) break; len = (hdr[2] << 8) | hdr[3]; }
                else if (len == 127) { if (!ReadN(hdr, 2, 8)) break; len = 0; for (int i = 0; i < 8; i++) len = (len << 8) | hdr[2 + i]; }
                var mask = new byte[4]; if (masked && !ReadN(mask, 0, 4)) break;
                if (len > 1 << 20) break;
                var data = new byte[len]; if (!ReadN(data, 0, (int)len)) break;
                if (masked) for (int i = 0; i < data.Length; i++) data[i] ^= mask[i & 3];
                if (op == 8) break;
                if (op == 9) { try { Write(10, data); } catch { break; } }
            }
        }
        catch { }
        Close();
    }
    bool ReadN(byte[] b, int off, int n) { while (n > 0) { int r = stream.Read(b, off, n); if (r <= 0) return false; off += r; n -= r; } return true; }
    public void Close() { if (Closed) return; Closed = true; try { tcp.Close(); } catch { } }
}

// ---------- приёмник источника: один на источник, кадры — всем подписанным ----------
abstract class Feed
{
    public string Id; public readonly List<WsClient> Clients = new List<WsClient>();
    public volatile bool Stop; public int W, H, Rate; public double SrcFps; public string Gpu = "";
    protected int inflight, sentN, lastN; protected long seq, sentSeq; string meta = "";
    protected const int Pipe = 3; // сколько кадров сжимается одновременно
    public abstract void Start();
    public int ClientCount { get { lock (Clients) return Clients.Count; } }
    public void Tick() { Rate = sentN - lastN; lastN = sentN; }
    // сжать и разослать; кадр, дожатый позже более нового, выбрасывается
    protected void Publish(IntPtr px, int w, int h, int stride, bool rgba, double fps)
    {
        long my = Interlocked.Increment(ref seq);
        byte[] jpg;
        try { jpg = Jpeg.Encode(px, w, h, stride, rgba); } catch (Exception ex) { Server.Log("JPEG: " + ex.Message); return; }
        lock (this) { if (my < sentSeq) return; sentSeq = my; }
        var m = new JavaScriptSerializer().Serialize(new Dictionary<string, object> { { "w", w }, { "h", h }, { "fps", Math.Round(fps, 2) }, { "src", Id } });
        bool changed = m != meta; meta = m;
        W = w; H = h; SrcFps = fps; Interlocked.Increment(ref sentN);
        WsClient[] list; lock (Clients) list = Clients.ToArray();
        foreach (var c in list) { var send = changed || c.NeedMeta ? m : null; if (c.SendFrame(send, jpg)) c.NeedMeta = false; else if (send != null) c.NeedMeta = true; }
    }
}
class NdiFeed : Feed
{
    readonly string name, url;
    public NdiFeed(string name, string url) { this.name = name; this.url = url; }
    public override void Start() { new Thread(Loop) { IsBackground = true, Name = "NDI " + name }.Start(); }
    void Loop()
    {
        var rc = new Ndi.RecvCreate { color_format = Ndi.BGRX_BGRA, bandwidth = Ndi.BW_HIGHEST, p_ndi_recv_name = Marshal.StringToHGlobalAnsi("LEDBRAIN Bridge") };
        var nm = Ndi.Utf8Alloc(name); var ur = Ndi.Utf8Alloc(url);
        rc.source = new Ndi.Source { p_ndi_name = nm, p_url_address = ur };
        var inst = Ndi.NDIlib_recv_create_v3(ref rc);
        if (inst == IntPtr.Zero) { Server.Log("NDI: " + name + " — не открылся"); return; }
        while (!Stop)
        {
            var v = new Ndi.Video();
            int type = Ndi.NDIlib_recv_capture_v2(inst, ref v, IntPtr.Zero, IntPtr.Zero, 200);
            if (type != Ndi.FRAME_VIDEO || v.p_data == IntPtr.Zero) continue;
            // ClientCount читаем один раз: зритель может уйти между проверками (debugger: счётчик уходил в −1,
            // NDIlib_recv_destroy вызывался, пока последний кадр ещё сжимался)
            bool inc = ClientCount > 0;
            if (inc && Interlocked.Increment(ref inflight) <= Pipe)
            {
                var fr = v;
                // кадр читается прямо из памяти NDI и возвращается NDI после сжатия
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { Publish(fr.p_data, fr.xres, fr.yres, fr.line_stride_in_bytes, false, fr.frame_rate_D != 0 ? (double)fr.frame_rate_N / fr.frame_rate_D : 0); }
                    finally { Ndi.NDIlib_recv_free_video_v2(inst, ref fr); Interlocked.Decrement(ref inflight); }
                });
            }
            else { if (inc) Interlocked.Decrement(ref inflight); Ndi.NDIlib_recv_free_video_v2(inst, ref v); }
        }
        while (Thread.VolatileRead(ref inflight) > 0) Thread.Sleep(10);
        Ndi.NDIlib_recv_destroy(inst);
        Marshal.FreeHGlobal(nm); Marshal.FreeHGlobal(ur); Marshal.FreeHGlobal(rc.p_ndi_recv_name);
    }
}
class SpoutFeed : Feed
{
    readonly string name;
    public SpoutFeed(string name) { this.name = name; }
    public override void Start() { new Thread(Loop) { IsBackground = true, Name = "Spout " + name }.Start(); }
    // у Spout без SDK нет «ждать новый кадр» — снимаем ~30 раз в секунду
    void Loop()
    {
        using (var rx = new Spout.Receiver(name))
        {
            var bufs = new byte[Pipe][]; int slot = 0;
            while (!Stop)
            {
                var t0 = Environment.TickCount;
                if (ClientCount > 0 && Thread.VolatileRead(ref inflight) < Pipe)
                {
                    int w, h, pitch; bool rgba;
                    var buf = bufs[slot];
                    if (rx.Grab(ref buf, out w, out h, out pitch, out rgba))
                    {
                        bufs[slot] = buf; slot = (slot + 1) % Pipe;
                        if (Gpu.Length == 0) { Gpu = rx.Gpu; Server.Log("  " + Server.T("Spout на видеокарте", "Spout on GPU") + ": " + Gpu); }
                        Interlocked.Increment(ref inflight);
                        var b = buf;
                        ThreadPool.QueueUserWorkItem(_ =>
                        {
                            var hnd = GCHandle.Alloc(b, GCHandleType.Pinned);
                            try { Publish(hnd.AddrOfPinnedObject(), w, h, pitch, rgba, 0); }
                            finally { hnd.Free(); Interlocked.Decrement(ref inflight); }
                        });
                    }
                }
                int dt = Environment.TickCount - t0;
                Thread.Sleep(Math.Max(1, 33 - dt));
            }
            while (Thread.VolatileRead(ref inflight) > 0) Thread.Sleep(10);
        }
    }
}

// ---------- сервер ----------
static class Server
{
    public const string Version = "0.4.1";
    public static int Port = 47800;
    public static bool EN;
    public static string T(string ru, string en) { return EN ? en : ru; }
    public static Action<string> OnLog = s => { };
    public static void Log(string s) { try { OnLog(s); } catch { } }
    const string NdiP = "NDI · ", SpoutP = "Spout · ";
    // старый led.gladkovegor.ru не доверяем: он только перенаправляет на ledbrain.pro (Redirect Rule в Cloudflare);
    // потеря домена дала бы новому владельцу видео со всех Bridge (debugger)
    static readonly Regex Origins = new Regex(@"^(https://(www\.)?ledbrain\.pro|http://(localhost|127\.0\.0\.1):(8792|8787|8793))$"); // localhost — только порты разработки LEDBRAIN (debugger: любой локальный веб-интерфейс получал видео)
    static readonly Dictionary<string, Feed> feeds = new Dictionary<string, Feed>();
    static TcpListener listener;
    public static volatile bool Running;
    static bool ndiOk;
    static readonly DateTime t0 = DateTime.Now;

    // false — порт занят (уже работает другой Bridge)
    public static bool Start()
    {
        ndiOk = Ndi.Init();
        Log(ndiOk ? "NDI: " + Ndi.Path : T("NDI не найден — поставьте NDI Tools (https://ndi.video/tools/). Spout работает и без него.", "NDI not found — install NDI Tools (https://ndi.video/tools/). Spout works without it."));
        try { listener = new TcpListener(IPAddress.Loopback, Port); listener.Start(); }
        catch (SocketException) { Log(T("Порт " + Port + " занят — Bridge уже запущен?", "Port " + Port + " is in use — is Bridge already running?")); return false; }
        Log("LEDBRAIN Bridge " + Version + " — http://127.0.0.1:" + Port);
        Running = true;
        new Thread(Accept) { IsBackground = true, Name = "accept" }.Start();
        new Thread(() => { while (true) { Thread.Sleep(1000); lock (feeds) foreach (var f in feeds.Values) f.Tick(); } }) { IsBackground = true }.Start();
        ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(2500); var s = AllSources(); Log(s.Count > 0 ? T("Источники", "Sources") + ": " + string.Join("  |  ", s.ToArray()) : T("Источников пока нет — включите в Resolume выход NDI или Spout", "No sources yet — turn on NDI or Spout output in Resolume")); });
        return true;
    }
    public static void StopAll() { try { if (listener != null) listener.Stop(); } catch { } lock (feeds) foreach (var f in feeds.Values) f.Stop = true; }

    public static List<string> AllSources()
    {
        var l = new List<string>();
        try { foreach (var s in Ndi.Sources()) l.Add(NdiP + s.Key); } catch { }
        try { foreach (var s in Spout.Senders()) l.Add(SpoutP + s); } catch { }
        return l;
    }
    public static Dictionary<string, object> Status()
    {
        var active = new List<object>();
        lock (feeds) foreach (var f in feeds.Values) active.Add(new Dictionary<string, object> { { "src", f.Id }, { "clients", f.ClientCount }, { "fps", f.Rate }, { "srcFps", f.SrcFps }, { "w", f.W }, { "h", f.H }, { "gpu", f.Gpu } });
        return new Dictionary<string, object> { { "app", "LEDBRAIN Bridge" }, { "version", Version }, { "port", Port }, { "uptime", (int)(DateTime.Now - t0).TotalSeconds }, { "ndi", Ndi.Path ?? "" }, { "sources", AllSources() }, { "active", active } };
    }

    static void Accept()
    {
        while (true)
        {
            TcpClient c;
            try { c = listener.AcceptTcpClient(); } catch { return; }
            new Thread(() => Handle(c)) { IsBackground = true }.Start();
        }
    }
    static int conns; // открытых соединений: больше 64 — новые закрываются (локальный процесс не исчерпает потоки)
    static void Handle(TcpClient c)
    {
        if (Interlocked.Increment(ref conns) > 64) { Interlocked.Decrement(ref conns); try { c.Close(); } catch { } return; }
        try
        {
            c.NoDelay = true;
            // заголовки — за 5 с, иначе соединение закрывается; после перехода на WebSocket таймаут снимается (debugger)
            c.ReceiveTimeout = 5000;
            var s = c.GetStream();
            var head = ReadHead(s);
            if (head == null) { c.Close(); return; }
            var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
            var req = lines[0].Split(' ');
            if (req.Length < 2) { c.Close(); return; }
            string method = req[0], path = req[1];
            var hd = new Dictionary<string, string>();
            foreach (var l in lines.Skip(1)) { int i = l.IndexOf(':'); if (i > 0) hd[l.Substring(0, i).Trim().ToLowerInvariant()] = l.Substring(i + 1).Trim(); }
            string host; hd.TryGetValue("host", out host);
            if (host != "127.0.0.1:" + Port && host != "localhost:" + Port) { Reply(s, "403 Forbidden", "", "text/plain", ""); c.Close(); return; }
            string origin; hd.TryGetValue("origin", out origin);
            bool ok = string.IsNullOrEmpty(origin) || Origins.IsMatch(origin);
            string cors = ok && !string.IsNullOrEmpty(origin) ? "Access-Control-Allow-Origin: " + origin + "\r\nVary: Origin\r\nAccess-Control-Allow-Private-Network: true\r\n" : "";
            if (!ok) { Reply(s, "403 Forbidden", "", "text/plain", ""); c.Close(); return; }
            if (method == "OPTIONS") { Reply(s, "204 No Content", cors + "Access-Control-Allow-Methods: GET\r\nAccess-Control-Allow-Headers: *\r\n", "text/plain", ""); c.Close(); return; }
            var json = new JavaScriptSerializer();
            if (path == "/" || path.StartsWith("/?")) { Reply(s, "200 OK", cors, "application/json; charset=utf-8", json.Serialize(new Dictionary<string, object> { { "app", "LEDBRAIN Bridge" }, { "version", Version }, { "sources", AllSources() } })); c.Close(); return; }
            if (path == "/status") { Reply(s, "200 OK", cors, "application/json; charset=utf-8", json.Serialize(Status())); c.Close(); return; }
            string key;
            if (path.StartsWith("/ws") && hd.TryGetValue("sec-websocket-key", out key))
            {
                var acc = Convert.ToBase64String(SHA1.Create().ComputeHash(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                var hs = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + acc + "\r\n\r\n";
                var hb = Encoding.ASCII.GetBytes(hs); s.Write(hb, 0, hb.Length);
                var q = path.IndexOf("src=") >= 0 ? Uri.UnescapeDataString(path.Substring(path.IndexOf("src=") + 4).Split('&')[0].Replace('+', ' ')) : "";
                var ws = new WsClient(c, s);
                c.ReceiveTimeout = 0;
                var feed = Subscribe(q, ws);
                if (feed == null) { ws.SendText(json.Serialize(new Dictionary<string, object> { { "error", "nosource" }, { "src", q } })); ws.Close(); return; }
                ws.ReadLoop(); // до закрытия
                Unsubscribe(feed, ws);
                return;
            }
            Reply(s, "404 Not Found", cors, "application/json", "{}"); c.Close();
        }
        catch { try { c.Close(); } catch { } }
        finally { Interlocked.Decrement(ref conns); }
    }
    static string ReadHead(Stream s)
    {
        var buf = new List<byte>(); var one = new byte[1];
        while (buf.Count < 16384)
        {
            if (s.Read(one, 0, 1) <= 0) return null;
            buf.Add(one[0]);
            int n = buf.Count;
            if (n >= 4 && buf[n - 4] == 13 && buf[n - 3] == 10 && buf[n - 2] == 13 && buf[n - 1] == 10) return Encoding.UTF8.GetString(buf.ToArray(), 0, n - 4);
        }
        return null;
    }
    static void Reply(Stream s, string code, string extra, string type, string body)
    {
        var b = Encoding.UTF8.GetBytes(body);
        var h = Encoding.ASCII.GetBytes("HTTP/1.1 " + code + "\r\nContent-Type: " + type + "\r\nContent-Length: " + b.Length + "\r\nCache-Control: no-store\r\nConnection: close\r\n" + extra + "\r\n");
        s.Write(h, 0, h.Length); s.Write(b, 0, b.Length); s.Flush();
    }
    static Feed Subscribe(string id, WsClient ws)
    {
        lock (feeds)
        {
            Feed f;
            if (!feeds.TryGetValue(id, out f))
            {
                if (id.StartsWith(SpoutP)) { var n = id.Substring(SpoutP.Length); if (!Spout.Senders().Contains(n)) return null; f = new SpoutFeed(n); }
                else
                {
                    var n = id.StartsWith(NdiP) ? id.Substring(NdiP.Length) : id;
                    var src = Ndi.Sources().FirstOrDefault(x => x.Key == n);
                    if (src.Key == null) return null;
                    f = new NdiFeed(src.Key, src.Value);
                }
                f.Id = id; feeds[id] = f;
                lock (f.Clients) f.Clients.Add(ws);
                f.Start();
                Log("▶ " + T("приём", "receiving") + ": " + id);
                return f;
            }
            lock (f.Clients) f.Clients.Add(ws);
            return f;
        }
    }
    static void Unsubscribe(Feed f, WsClient ws)
    {
        lock (feeds)
        {
            lock (f.Clients) f.Clients.Remove(ws);
            if (f.ClientCount == 0 && !f.Stop) { f.Stop = true; feeds.Remove(f.Id); Log("■ " + T("стоп", "stopped") + ": " + f.Id); }
        }
    }
}
