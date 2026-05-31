using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace DiscBox.Models;

public enum EntryType { File, Folder }

/// <summary>
/// Represents one item (file or folder) in the DiscBox virtual filesystem.
/// Mirrors the discbox_entry_t struct from libdiscbox.
/// </summary>
public class FileEntry : ObservableObject
{
    private bool _isUiSelected;
    private bool _isQuickAccess;

    public long   Id            { get; set; }
    public string Name         { get; set; } = string.Empty;
    public string VirtualPath  { get; set; } = string.Empty;
    public EntryType Type      { get; set; }
    public long   SizeBytes    { get; set; }
    public string? MimeType    { get; set; }
    public string? ThumbnailUrl { get; set; }
    public string? ChunkMessageIds { get; set; }
    public DateTime CreatedAt  { get; set; }
    public DateTime ModifiedAt { get; set; }
    public bool   Encrypted    { get; set; }
    public bool IsUiSelected
    {
        get => _isUiSelected;
        set => SetProperty(ref _isUiSelected, value);
    }

    public bool IsQuickAccess
    {
        get => _isQuickAccess;
        set => SetProperty(ref _isQuickAccess, value);
    }

    // Computed helpers.

    public bool IsFolder => Type == EntryType.Folder;
    public bool IsFile   => Type == EntryType.File;

    public bool IsImage => MimeType?.StartsWith("image/") == true;
    public bool IsVideo => MimeType?.StartsWith("video/") == true;
    public bool HasThumbnail => !string.IsNullOrEmpty(ThumbnailUrl);

    /// <summary>Human-readable file size, e.g. "4.2 MB"</summary>
    public string SizeDisplay => IsFolder ? string.Empty : FormatBytes(SizeBytes);

    /// <summary>Icon character for the entry type / mime type.</summary>
    public string Icon => Type switch
    {
        EntryType.Folder => "📁",
        EntryType.File when IsImage => "🖼️",
        EntryType.File when IsVideo => "🎬",
        EntryType.File when MimeType == "application/pdf" => "📋",
        EntryType.File when MimeType?.StartsWith("audio/") == true => "🎵",
        EntryType.File when MimeType?.StartsWith("text/") == true => "📝",
        EntryType.File when MimeType?.Contains("zip") == true => "🗜️",
        _ => "📄",
    };

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:F1} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:F2} GB"
    };
}
