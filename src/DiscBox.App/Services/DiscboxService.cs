using DiscBox.Models;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace DiscBox.Services;

public sealed record RemoteSyncResult(int CheckedFiles, int RemovedFiles, int RemovedFolders);

/// <summary>
/// Friendly C# wrapper around DiscboxNative.
/// Converts native pointers into C# objects and manages the context lifetime.
/// </summary>
public class DiscboxService : IDisposable
{
    private IntPtr _ctx;
    private bool _disposed;
    private string? _initError;

    public bool IsAvailable => _ctx != IntPtr.Zero;

    public DiscboxService(string webhookUrl, string dbPath, bool encrypt = false)
    {
        try
        {
            System.Diagnostics.Debug.WriteLine($"[DiscBox] Init: webhook={webhookUrl} db={dbPath} encrypt={encrypt}");
            _ctx = DiscboxNative.discbox_init_with_options(webhookUrl, dbPath, encrypt ? 1 : 0);
            if (_ctx == IntPtr.Zero)
                _initError = "DiscBox native engine did not start. Check the bundled native DLLs and the webhook URL.";
            System.Diagnostics.Debug.WriteLine($"[DiscBox] ctx={_ctx} IsAvailable={IsAvailable}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DiscBox] DLL error: {ex.GetType().Name}: {ex.Message}");
            _initError = $"DiscBox native engine failed to load: {ex.Message}";
            _ctx = IntPtr.Zero;
        }
    }

    // Virtual FS.

    public List<FileEntry> List(string virtualPath)
    {
        if (!IsAvailable) return [];

        int rc = DiscboxNative.discbox_list(
            _ctx, virtualPath, out IntPtr entriesPtr, out UIntPtr countPtr);

        if (rc != 0) return [];

        int count = (int)countPtr;
        var result = new List<FileEntry>(count);
        int stride = Marshal.SizeOf<DiscboxNative.NativeEntry>();

        for (int i = 0; i < count; i++)
        {
            IntPtr ptr = entriesPtr + i * stride;
            var native = Marshal.PtrToStructure<DiscboxNative.NativeEntry>(ptr);
            result.Add(FromNative(native));
        }

        DiscboxNative.discbox_free_entries(entriesPtr, countPtr);
        return result;
    }

    public bool Mkdir(string virtualPath)
    {
        if (!IsAvailable) return false;
        return DiscboxNative.discbox_mkdir(_ctx, virtualPath) == 0;
    }

    public bool Rename(string oldPath, string newPath)
    {
        if (!IsAvailable) return false;
        return DiscboxNative.discbox_rename(_ctx, oldPath, newPath) == 0;
    }

    public bool Delete(string virtualPath)
    {
        if (!IsAvailable) return false;
        return DiscboxNative.discbox_delete(_ctx, virtualPath) == 0;
    }

    public bool Delete(string virtualPath, Action<string, long, long, int, int>? onProgress)
    {
        if (!IsAvailable) return false;

        DiscboxNative.ProgressCallback? cb = null;
        if (onProgress != null)
        {
            cb = (_, vp, done, total, ci, cc) =>
            {
                onProgress(Marshal.PtrToStringAnsi(vp) ?? virtualPath, done, total, ci, cc);
                return 0;
            };
        }

        return DiscboxNative.discbox_delete_with_progress(_ctx, virtualPath, cb, IntPtr.Zero) == 0;
    }

    /// <summary>
    /// Imports a Disbox file directly into the local SQLite database.
    /// It registers metadata without uploading the file again.
    /// </summary>
    public bool ImportFile(string virtualPath, string name, long sizeBytes, string chunkMessageIdsJson)
    {
        if (!IsAvailable) return false;
        return DiscboxNative.discbox_import_file(
            _ctx, virtualPath, name, sizeBytes, chunkMessageIdsJson) == 0;
    }

    // Transfer.

    public bool Upload(string localPath, string virtualPath,
                       Action<long, long, int, int>? onProgress = null)
    {
        return UploadCore(
            localPath,
            virtualPath,
            onProgress is null
                ? null
                : (done, total, chunkIndex, chunkCount) =>
                {
                    onProgress(done, total, chunkIndex, chunkCount);
                    return false;
                });
    }

    public bool UploadCancellable(string localPath, string virtualPath,
                       Func<long, long, int, int, bool>? onProgress = null)
    {
        return UploadCore(localPath, virtualPath, onProgress);
    }

    private bool UploadCore(string localPath, string virtualPath,
                       Func<long, long, int, int, bool>? onProgress)
    {
        if (!IsAvailable) return false;

        DiscboxNative.ProgressCallback? cb = null;
        if (onProgress != null)
        {
            cb = (_, vp, done, total, ci, cc) =>
            {
                return onProgress(done, total, ci, cc) ? 1 : 0;
            };
        }

        return DiscboxNative.discbox_upload(
            _ctx, localPath, virtualPath, cb, IntPtr.Zero) == 0;
    }

    public bool Download(string virtualPath, string localPath,
                         Action<long, long, int, int>? onProgress = null)
    {
        return DownloadCore(
            virtualPath,
            localPath,
            onProgress is null
                ? null
                : (done, total, chunkIndex, chunkCount) =>
                {
                    onProgress(done, total, chunkIndex, chunkCount);
                    return false;
                });
    }

    public bool DownloadCancellable(string virtualPath, string localPath,
                         Func<long, long, int, int, bool>? onProgress = null)
    {
        return DownloadCore(virtualPath, localPath, onProgress);
    }

    private bool DownloadCore(string virtualPath, string localPath,
                         Func<long, long, int, int, bool>? onProgress)
    {
        if (!IsAvailable) return false;

        DiscboxNative.ProgressCallback? cb = null;
        if (onProgress != null)
        {
            cb = (_, vp, done, total, ci, cc) =>
            {
                return onProgress(done, total, ci, cc) ? 1 : 0;
            };
        }

        return DiscboxNative.discbox_download(
            _ctx, virtualPath, localPath, cb, IntPtr.Zero) == 0;
    }

    public bool BackupDatabase(string localPath)
    {
        if (!IsAvailable) return false;
        return DiscboxNative.discbox_backup_database(_ctx, localPath) == 0;
    }

    public RemoteSyncResult? SyncRemoteState(bool removeEmptyFolders)
    {
        if (!IsAvailable) return null;

        var rc = DiscboxNative.discbox_sync_remote_state(
            _ctx,
            removeEmptyFolders ? 1 : 0,
            out var checkedFiles,
            out var removedFiles,
            out var removedFolders);
        return rc == 0
            ? new RemoteSyncResult(checkedFiles, removedFiles, removedFolders)
            : null;
    }

    // Utilities.

    public long TotalSize() => IsAvailable ? DiscboxNative.discbox_total_size(_ctx) : 0;

    public static bool ValidateWebhook(string url)
    {
        try { return DiscboxNative.discbox_validate_webhook(url) == 0; }
        catch (DllNotFoundException) { return false; }
    }

    public string? LastError()
    {
        if (!IsAvailable) return _initError;
        return Marshal.PtrToStringAnsi(DiscboxNative.discbox_last_error(_ctx));
    }

    // Helpers.

    private static FileEntry FromNative(DiscboxNative.NativeEntry n) => new()
    {
        Id = n.id,
        Name = Marshal.PtrToStringAnsi(n.name) ?? string.Empty,
        VirtualPath = Marshal.PtrToStringAnsi(n.virtual_path) ?? string.Empty,
        Type = n.type == 1 ? EntryType.Folder : EntryType.File,
        SizeBytes = n.size_bytes,
        MimeType = Marshal.PtrToStringAnsi(n.mime_type),
        ThumbnailUrl = Marshal.PtrToStringAnsi(n.thumbnail_url),
        CreatedAt = DateTimeOffset.FromUnixTimeSeconds(n.created_at).LocalDateTime,
        ModifiedAt = DateTimeOffset.FromUnixTimeSeconds(n.modified_at).LocalDateTime,
        Encrypted = n.encrypted != 0,
    };

    // IDisposable.

    public void Dispose()
    {
        if (!_disposed && _ctx != IntPtr.Zero)
        {
            DiscboxNative.discbox_free(_ctx);
            _ctx = IntPtr.Zero;
        }
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
