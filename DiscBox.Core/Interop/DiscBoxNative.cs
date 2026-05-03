using System;
using System.Runtime.InteropServices;

namespace DiscBox.Core.Interop;

/// <summary>
/// P/Invoke bindings to libdiscbox (C library).
/// These mirror exactly the functions declared in include/discbox.h
/// </summary>
public static class DiscBoxNative
{
    // The DLL name without extension — .NET resolves platform automatically
    // (libdiscbox.so on Linux, libdiscbox.dylib on macOS, discbox.dll on Windows)
    private const string Lib = "discbox";

    // ── Error codes (must match discbox_err_t in discbox.h) ────────────
    public enum DiscboxErr : int
    {
        Ok           =  0,
        Args         = -1,
        Memory       = -2,
        Io           = -3,
        Network      = -4,
        Discord      = -5,
        RateLimit    = -6,
        Db           = -7,
        NotFound     = -8,
        Exists       = -9,
        Cancelled    = -10,
        Crypto       = -11,
    }

    // ── Entry type ──────────────────────────────────────────────────────
    public enum EntryType : int
    {
        File   = 0,
        Folder = 1,
    }

    // ── discbox_config_t ────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    public struct Config
    {
        public nuint ChunkSize;        // size_t
        public int   Encrypt;          // int (0 or 1)
        public int   MaxRetries;       // int
        public long  HttpTimeoutSec;   // long
        public IntPtr LogPath;         // const char* (NULL = no log)
    }

    // ── discbox_entry_t ─────────────────────────────────────────────────
    // We use IntPtr for char* fields and marshal them manually
    [StructLayout(LayoutKind.Sequential)]
    public struct NativeEntry
    {
        public long    Id;
        public IntPtr  Name;            // char*
        public IntPtr  VirtualPath;     // char*
        public EntryType Type;
        public long    SizeBytes;
        public IntPtr  MimeType;        // char*
        public IntPtr  ThumbnailUrl;    // char*
        public long    CreatedAt;       // time_t
        public long    ModifiedAt;      // time_t
        public int     Encrypted;
    }

    // ── Progress callback ───────────────────────────────────────────────
    // int (*cb)(void* userdata, const char* path, int64_t done, int64_t total, int chunk, int chunks)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int ProgressCallback(
        IntPtr userdata,
        IntPtr virtualPath,
        long   bytesDone,
        long   bytesTotal,
        int    chunkIndex,
        int    chunkCount);

    // ── Init / Teardown ─────────────────────────────────────────────────
    [DllImport(Lib, EntryPoint = "discbox_init", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr Init(
        [MarshalAs(UnmanagedType.LPStr)] string webhookUrl,
        [MarshalAs(UnmanagedType.LPStr)] string dbPath,
        IntPtr config);  // const discbox_config_t* — pass IntPtr.Zero for defaults

    [DllImport(Lib, EntryPoint = "discbox_free", CallingConvention = CallingConvention.Cdecl)]
    public static extern void Free(IntPtr ctx);

    [DllImport(Lib, EntryPoint = "discbox_last_error", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr LastErrorRaw(IntPtr ctx);

    public static string LastError(IntPtr ctx) =>
        Marshal.PtrToStringAnsi(LastErrorRaw(ctx)) ?? "no error";

    [DllImport(Lib, EntryPoint = "discbox_strerror", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr StrErrorRaw(DiscboxErr err);

    public static string StrError(DiscboxErr err) =>
        Marshal.PtrToStringAnsi(StrErrorRaw(err)) ?? "unknown error";

    // ── Virtual FS ──────────────────────────────────────────────────────
    [DllImport(Lib, EntryPoint = "discbox_mkdir", CallingConvention = CallingConvention.Cdecl)]
    public static extern DiscboxErr Mkdir(
        IntPtr ctx,
        [MarshalAs(UnmanagedType.LPStr)] string virtualPath);

    [DllImport(Lib, EntryPoint = "discbox_list", CallingConvention = CallingConvention.Cdecl)]
    public static extern DiscboxErr List(
        IntPtr ctx,
        [MarshalAs(UnmanagedType.LPStr)] string virtualPath,
        out IntPtr entries,   // discbox_entry_t**
        out nuint  count);

    [DllImport(Lib, EntryPoint = "discbox_free_entries", CallingConvention = CallingConvention.Cdecl)]
    public static extern void FreeEntries(IntPtr entries, nuint count);

    [DllImport(Lib, EntryPoint = "discbox_rename", CallingConvention = CallingConvention.Cdecl)]
    public static extern DiscboxErr Rename(
        IntPtr ctx,
        [MarshalAs(UnmanagedType.LPStr)] string oldPath,
        [MarshalAs(UnmanagedType.LPStr)] string newPath);

    [DllImport(Lib, EntryPoint = "discbox_delete", CallingConvention = CallingConvention.Cdecl)]
    public static extern DiscboxErr Delete(
        IntPtr ctx,
        [MarshalAs(UnmanagedType.LPStr)] string virtualPath);

    // ── Transfer ────────────────────────────────────────────────────────
    [DllImport(Lib, EntryPoint = "discbox_upload", CallingConvention = CallingConvention.Cdecl)]
    public static extern DiscboxErr Upload(
        IntPtr ctx,
        [MarshalAs(UnmanagedType.LPStr)] string localPath,
        [MarshalAs(UnmanagedType.LPStr)] string virtualPath,
        ProgressCallback? progressCb,
        IntPtr userdata);

    [DllImport(Lib, EntryPoint = "discbox_download", CallingConvention = CallingConvention.Cdecl)]
    public static extern DiscboxErr Download(
        IntPtr ctx,
        [MarshalAs(UnmanagedType.LPStr)] string virtualPath,
        [MarshalAs(UnmanagedType.LPStr)] string localPath,
        ProgressCallback? progressCb,
        IntPtr userdata);

    // ── Utilities ────────────────────────────────────────────────────────
    [DllImport(Lib, EntryPoint = "discbox_validate_webhook", CallingConvention = CallingConvention.Cdecl)]
    public static extern DiscboxErr ValidateWebhook(
        [MarshalAs(UnmanagedType.LPStr)] string webhookUrl);

    [DllImport(Lib, EntryPoint = "discbox_total_size", CallingConvention = CallingConvention.Cdecl)]
    public static extern long TotalSize(IntPtr ctx);
}
