using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace LumenThumbs;

/// <summary>A decoded frame in 32-bit BGRX, top row first.</summary>
internal sealed class Frame
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required byte[] Pixels { get; init; }

    /// <summary>Average brightness 0–255, sampled; used to skip black fade-ins and scene cuts.</summary>
    public double Brightness()
    {
        long sum = 0, count = 0;
        for (var i = 0; i + 2 < Pixels.Length; i += 4 * 7)
        {
            sum += (Pixels[i] * 29 + Pixels[i + 1] * 150 + Pixels[i + 2] * 77) >> 8;
            count++;
        }
        return count == 0 ? 0 : (double)sum / count;
    }

    public IntPtr ToHBitmap()
    {
        var header = new Gdi.BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<Gdi.BITMAPINFOHEADER>(),
            biWidth = Width,
            biHeight = -Height, // top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        var hbmp = Gdi.CreateDIBSection(IntPtr.Zero, ref header, 0, out var bits, IntPtr.Zero, 0);
        if (hbmp == IntPtr.Zero) return IntPtr.Zero;
        for (var i = 3; i < Pixels.Length; i += 4) Pixels[i] = 0xFF;
        Marshal.Copy(Pixels, 0, bits, Pixels.Length);
        return hbmp;
    }
}

/// <summary>
/// Plays the video silently into memory with libvlc (the copy bundled with Lumen Player), seeks to a
/// representative spot and keeps one frame. Works for every codec VLC can decode, independent of the
/// codecs Windows itself has installed.
/// </summary>
internal static unsafe class FrameGrabber
{
    private static readonly object InitGate = new();
    private static IntPtr _instance;

    // Spots to try, as a fraction of the duration; the first one that isn't nearly black wins.
    private static readonly double[] Spots = { 0.2, 0.33, 0.5, 0.1 };
    private const double DarkThreshold = 28;

    private static readonly string[] InstanceArgs =
    {
        "--intf=dummy", "--ignore-config", "--quiet", "--no-stats", "--no-osd", "--no-video-title-show",
        "--no-snapshot-preview", "--no-lua", "--no-media-library", "--aout=none",
    };

    private static readonly string[] MediaOptions =
    {
        ":no-audio", ":no-spu", ":no-sub-autodetect-file", ":avcodec-hw=none", ":input-fast-seek",
    };

    private static IntPtr Instance
    {
        get
        {
            lock (InitGate)
            {
                if (_instance != IntPtr.Zero) return _instance;
                Vlc.Load();
                _instance = Vlc.libvlc_new(InstanceArgs.Length, InstanceArgs);
                if (_instance == IntPtr.Zero) throw new InvalidOperationException("libvlc_new failed");
                return _instance;
            }
        }
    }

    public static Frame? Grab(IStream stream, int maxSize)
    {
        var instance = Instance;
        using var session = new Session(stream, maxSize);
        var handle = GCHandle.Alloc(session);
        var opaque = GCHandle.ToIntPtr(handle);
        IntPtr media = IntPtr.Zero, player = IntPtr.Zero;
        try
        {
            // Reading the file directly is faster than going through the stream, when Windows tells us where it is.
            var path = session.FilePath();
            media = path != null
                ? Vlc.libvlc_media_new_path(instance, path)
                : Vlc.libvlc_media_new_callbacks(instance, &Open, &Read, &Seek, &Close, opaque);
            if (media == IntPtr.Zero) return null;
            foreach (var option in MediaOptions) Vlc.libvlc_media_add_option(media, option);

            player = Vlc.libvlc_media_player_new_from_media(media);
            if (player == IntPtr.Zero) return null;
            Vlc.libvlc_video_set_callbacks(player, &Lock, &Unlock, &Display, opaque);
            Vlc.libvlc_video_set_format_callbacks(player, &Setup, &Cleanup);
            if (Vlc.libvlc_media_player_play(player) != 0) return null;

            var frame = session.Run(player);
            if (frame == null) Log.Write($"No frame from {path ?? "(stream)"}");
            return frame;
        }
        finally
        {
            if (player != IntPtr.Zero)
            {
                Vlc.libvlc_media_player_stop(player);
                Vlc.libvlc_media_player_release(player);
            }
            if (media != IntPtr.Zero) Vlc.libvlc_media_release(media);
            handle.Free();
        }
    }

    private sealed class Session : IDisposable
    {
        private const int StateEnded = 6, StateError = 7;

        private readonly IStream _stream;
        private readonly int _maxSize;
        private readonly object _gate = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private byte[] _readBuffer = Array.Empty<byte>();
        private IntPtr _buffer;
        private int _width, _height;
        private byte[]? _latest;
        private int _frames;

        public Session(IStream stream, int maxSize)
        {
            _stream = stream;
            _maxSize = maxSize;
        }

        public string? FilePath()
        {
            try
            {
                _stream.Stat(out var stat, 0);
                var name = stat.pwcsName;
                return !string.IsNullOrEmpty(name) && Path.IsPathFullyQualified(name) && File.Exists(name) ? name : null;
            }
            catch
            {
                return null;
            }
        }

        public long StreamSize()
        {
            _stream.Stat(out var stat, 1 /* STATFLAG_NONAME */);
            return stat.cbSize;
        }

        public Frame? Run(IntPtr player)
        {
            var deadline = TimeSpan.FromSeconds(20);

            // Wait for the first frame; no frame within a few seconds means there's no video track.
            if (!WaitForFrames(player, 1, TimeSpan.FromSeconds(10))) return null;

            var length = Vlc.libvlc_media_player_get_length(player);
            if (length <= 0 || Vlc.libvlc_media_player_is_seekable(player) == 0)
            {
                WaitForFrames(player, 12, TimeSpan.FromSeconds(1));
                return Snapshot();
            }

            Frame? best = null;
            var bestBrightness = -1.0;
            foreach (var spot in Spots)
            {
                if (_clock.Elapsed > deadline) break;
                var target = (long)(length * spot);
                Vlc.libvlc_media_player_set_time(player, target);

                // Fast seek lands on the keyframe before the target; wait for the clock to get there, then
                // let a few frames through so nothing from before the seek is still in the buffer.
                var tolerance = Math.Max(1500, Math.Min(30000, length / 20));
                var seekStart = _clock.Elapsed;
                while (Math.Abs(Vlc.libvlc_media_player_get_time(player) - target) > tolerance)
                {
                    if (_clock.Elapsed - seekStart > TimeSpan.FromSeconds(5) || Failed(player)) break;
                    Thread.Sleep(20);
                }
                WaitForFrames(player, Volatile.Read(ref _frames) + 3, TimeSpan.FromSeconds(3));

                var frame = Snapshot();
                if (frame == null) continue;
                var brightness = frame.Brightness();
                if (brightness >= DarkThreshold) return frame;
                if (brightness > bestBrightness)
                {
                    best = frame;
                    bestBrightness = brightness;
                }
            }
            return best ?? Snapshot();
        }

        private bool Failed(IntPtr player) => Vlc.libvlc_media_player_get_state(player) is StateEnded or StateError;

        private bool WaitForFrames(IntPtr player, int count, TimeSpan timeout)
        {
            var until = _clock.Elapsed + timeout;
            lock (_gate)
            {
                while (_frames < count)
                {
                    if (_clock.Elapsed > until || Failed(player)) return _frames >= count;
                    Monitor.Wait(_gate, 50);
                }
            }
            return true;
        }

        private Frame? Snapshot()
        {
            lock (_gate)
            {
                return _latest == null ? null : new Frame { Width = _width, Height = _height, Pixels = (byte[])_latest.Clone() };
            }
        }

        // ---- libvlc video callbacks ----

        public uint Setup(sbyte* chroma, uint* width, uint* height, uint* pitches, uint* lines)
        {
            var scale = Math.Min(1.0, (double)_maxSize / Math.Max(*width, *height));
            var w = Math.Max(2, (int)Math.Round(*width * scale));
            var h = Math.Max(2, (int)Math.Round(*height * scale));
            chroma[0] = (sbyte)'R';
            chroma[1] = (sbyte)'V';
            chroma[2] = (sbyte)'3';
            chroma[3] = (sbyte)'2';
            *width = (uint)w;
            *height = (uint)h;
            pitches[0] = (uint)(w * 4);
            lines[0] = (uint)h;
            lock (_gate)
            {
                FreeBuffer();
                _width = w;
                _height = h;
                _buffer = (IntPtr)NativeMemory.AlignedAlloc((nuint)(w * h * 4), 64);
                _latest = null;
            }
            return 1;
        }

        public IntPtr LockBuffer() => _buffer;

        public void OnDisplay()
        {
            lock (_gate)
            {
                if (_buffer == IntPtr.Zero) return;
                var size = _width * _height * 4;
                if (_latest?.Length != size) _latest = new byte[size];
                Marshal.Copy(_buffer, _latest, 0, size);
                _frames++;
                Monitor.PulseAll(_gate);
            }
        }

        // ---- libvlc input callbacks (used when the file path isn't available) ----

        public int ReadInto(byte* buffer, int length)
        {
            if (_readBuffer.Length < length) _readBuffer = new byte[length];
            var countPtr = stackalloc int[1];
            _stream.Read(_readBuffer, length, (IntPtr)countPtr);
            Marshal.Copy(_readBuffer, 0, (IntPtr)buffer, *countPtr);
            return *countPtr;
        }

        public void SeekTo(ulong offset) => _stream.Seek((long)offset, 0 /* STREAM_SEEK_SET */, IntPtr.Zero);

        private void FreeBuffer()
        {
            if (_buffer == IntPtr.Zero) return;
            NativeMemory.AlignedFree((void*)_buffer);
            _buffer = IntPtr.Zero;
        }

        public void Dispose()
        {
            lock (_gate) FreeBuffer();
        }
    }

    private static Session Of(IntPtr opaque) => (Session)GCHandle.FromIntPtr(opaque).Target!;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static uint Setup(IntPtr* opaque, sbyte* chroma, uint* width, uint* height, uint* pitches, uint* lines)
    {
        try { return Of(*opaque).Setup(chroma, width, height, pitches, lines); }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void Cleanup(IntPtr opaque) { }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static IntPtr Lock(IntPtr opaque, IntPtr* planes)
    {
        planes[0] = Of(opaque).LockBuffer();
        return IntPtr.Zero;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void Unlock(IntPtr opaque, IntPtr picture, IntPtr* planes) { }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void Display(IntPtr opaque, IntPtr picture)
    {
        try { Of(opaque).OnDisplay(); }
        catch { /* never throw into native code */ }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int Open(IntPtr opaque, IntPtr* data, ulong* size)
    {
        try
        {
            *data = opaque;
            *size = (ulong)Of(opaque).StreamSize();
            Of(opaque).SeekTo(0);
            return 0;
        }
        catch { return -1; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static nint Read(IntPtr opaque, byte* buffer, nuint length)
    {
        try { return Of(opaque).ReadInto(buffer, (int)Math.Min(length, 4u << 20)); }
        catch { return -1; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int Seek(IntPtr opaque, ulong offset)
    {
        try
        {
            Of(opaque).SeekTo(offset);
            return 0;
        }
        catch { return -1; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void Close(IntPtr opaque) { }
}

internal static unsafe class Vlc
{
    private const string Lib = "libvlc";
    private static IntPtr _handle;

    /// <summary>Loads libvlc from Lumen Player's install folder (..\libvlc\win-x64 relative to this assembly).</summary>
    public static void Load()
    {
        if (_handle != IntPtr.Zero) return;
        var here = Path.GetDirectoryName(typeof(Vlc).Assembly.Location)!;
        var dir = Path.GetFullPath(Path.Combine(here, "..", "libvlc", "win-x64"));
        NativeLibrary.Load(Path.Combine(dir, "libvlccore.dll"));
        _handle = NativeLibrary.Load(Path.Combine(dir, "libvlc.dll"));
        NativeLibrary.SetDllImportResolver(typeof(Vlc).Assembly, (name, _, _) => name == Lib ? _handle : IntPtr.Zero);
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr libvlc_new(int argc, [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPStr)] string[] argv); // ASCII-only args

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr libvlc_media_new_path(IntPtr instance, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr libvlc_media_new_callbacks(IntPtr instance,
        delegate* unmanaged[Cdecl]<IntPtr, IntPtr*, ulong*, int> open,
        delegate* unmanaged[Cdecl]<IntPtr, byte*, nuint, nint> read,
        delegate* unmanaged[Cdecl]<IntPtr, ulong, int> seek,
        delegate* unmanaged[Cdecl]<IntPtr, void> close,
        IntPtr opaque);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void libvlc_media_add_option(IntPtr media, [MarshalAs(UnmanagedType.LPUTF8Str)] string option);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void libvlc_media_release(IntPtr media);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr libvlc_media_player_new_from_media(IntPtr media);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void libvlc_media_player_release(IntPtr player);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void libvlc_video_set_callbacks(IntPtr player,
        delegate* unmanaged[Cdecl]<IntPtr, IntPtr*, IntPtr> lockCb,
        delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr*, void> unlockCb,
        delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> displayCb,
        IntPtr opaque);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void libvlc_video_set_format_callbacks(IntPtr player,
        delegate* unmanaged[Cdecl]<IntPtr*, sbyte*, uint*, uint*, uint*, uint*, uint> setup,
        delegate* unmanaged[Cdecl]<IntPtr, void> cleanup);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int libvlc_media_player_play(IntPtr player);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void libvlc_media_player_stop(IntPtr player);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern long libvlc_media_player_get_length(IntPtr player);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern long libvlc_media_player_get_time(IntPtr player);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void libvlc_media_player_set_time(IntPtr player, long time);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int libvlc_media_player_is_seekable(IntPtr player);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int libvlc_media_player_get_state(IntPtr player);
}

internal static class Gdi
{
    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER header, uint usage, out IntPtr bits, IntPtr section, uint offset);
}
