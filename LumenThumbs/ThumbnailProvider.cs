using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace LumenThumbs;

[ComImport, Guid("e357fccd-a995-4576-b01f-234630154e96"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IThumbnailProvider
{
    [PreserveSig] int GetThumbnail(uint cx, out IntPtr phbmp, out int pdwAlpha);
}

[ComImport, Guid("b824b49d-22ac-4161-ac8a-9916e8fa3f7f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IInitializeWithStream
{
    [PreserveSig] int Initialize(IStream pstream, uint grfMode);
}

/// <summary>
/// Explorer creates one of these per file: Initialize hands over the file's stream, GetThumbnail asks for a
/// bitmap no larger than cx × cx. Implementing IInitializeWithStream (not IInitializeWithFile) keeps us in
/// Windows' isolated thumbnail process, so a misbehaving file can never take Explorer down.
/// </summary>
[ComVisible(true)]
[Guid("FED58414-101E-4886-9BD5-992E50FF490E")]
[ClassInterface(ClassInterfaceType.None)]
public sealed class VideoThumbnailProvider : IThumbnailProvider, IInitializeWithStream
{
    private const int S_OK = 0;
    private const int E_FAIL = unchecked((int)0x80004005);
    private const int E_UNEXPECTED = unchecked((int)0x8000FFFF);
    private const int AlreadyInitialized = unchecked((int)0x800704DF); // HRESULT_FROM_WIN32(ERROR_ALREADY_INITIALIZED)
    private const int WTSAT_RGB = 1;
    private static readonly TimeSpan GrabTimeout = TimeSpan.FromSeconds(30);

    private IStream? _stream;

    public int Initialize(IStream pstream, uint grfMode)
    {
        if (_stream != null) return AlreadyInitialized;
        _stream = pstream;
        return S_OK;
    }

    public int GetThumbnail(uint cx, out IntPtr phbmp, out int pdwAlpha)
    {
        phbmp = IntPtr.Zero;
        pdwAlpha = WTSAT_RGB;
        var stream = _stream;
        if (stream == null) return E_UNEXPECTED;
        try
        {
            // Decode on a worker thread. libvlc reads the stream from its own threads, and those calls may need
            // to be marshalled back to this (possibly STA) thread, so this thread must keep pumping COM while it
            // waits — Thread.Join does — instead of blocking inside libvlc. The timeout keeps one bad file from
            // stalling Explorer's whole thumbnail queue.
            Frame? frame = null;
            Exception? error = null;
            var size = (int)Math.Clamp(cx, 16u, 1600u);
            var worker = new Thread(() =>
            {
                try { frame = FrameGrabber.Grab(stream, size); }
                catch (Exception ex) { error = ex; }
            }) { IsBackground = true, Name = "LumenThumbs grab" };
            worker.SetApartmentState(ApartmentState.MTA);
            worker.Start();
            if (!worker.Join(GrabTimeout))
            {
                Log.Write($"Timed out after {GrabTimeout.TotalSeconds:0}s");
                return E_FAIL;
            }
            if (error != null) throw error;
            if (frame == null) return E_FAIL;
            phbmp = frame.ToHBitmap();
            return phbmp == IntPtr.Zero ? E_FAIL : S_OK;
        }
        catch (Exception ex)
        {
            Log.Write($"Thumbnail failed: {ex}");
            return E_FAIL;
        }
        finally
        {
            // Let go of the file right away so it can be renamed or deleted.
            Marshal.ReleaseComObject(stream);
            _stream = null;
        }
    }
}

internal static class Log
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LumenPlayer", "thumbnailer.log");
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 512 * 1024) File.Delete(FilePath);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{Environment.ProcessId}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never break thumbnailing.
        }
    }
}
