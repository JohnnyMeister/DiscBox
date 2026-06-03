namespace DiscBox.Services;

public sealed record UpdateInfo(
    string CurrentVersion,
    string LatestVersion,
    string ReleaseUrl,
    string? ReleaseNotes,
    string? InstallerUrl,
    string? InstallerFileName,
    long? InstallerSizeBytes,
    string? InstallerSha256,
    string? PortableUrl,
    string? PortableFileName,
    long? PortableSizeBytes,
    string? PortableSha256);

public sealed record UpdateInstallProgress(double Percent, string Status);
