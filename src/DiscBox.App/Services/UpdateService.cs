using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace DiscBox.Services;

public static class UpdateService
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/JohnnyMeister/DiscBox/releases/latest";
    private static readonly HttpClient Http = CreateHttpClient();

    public static string CurrentVersion => NormalizeVersion(
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
        ?? "0.0.0");

    public static async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await Http.GetAsync(LatestReleaseUrl, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;
            if (!response.IsSuccessStatusCode)
                return null;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, cancellationToken: cancellationToken);
            if (release is null || release.Draft)
                return null;

            var latestVersion = NormalizeVersion(string.IsNullOrWhiteSpace(release.TagName)
                ? release.Name ?? string.Empty
                : release.TagName);
            if (string.IsNullOrWhiteSpace(latestVersion))
                return null;

            if (CompareVersions(latestVersion, CurrentVersion) <= 0)
                return null;

            var installer = release.Assets?
                .Where(asset => asset is not null && !string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
                .OrderByDescending(asset => asset.Name?.Contains("setup", StringComparison.OrdinalIgnoreCase) == true)
                .ThenByDescending(asset => asset.Name?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true)
                .FirstOrDefault(asset => asset.Name?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true);

            var installerSha256 = NormalizeSha256(installer?.Digest);
            if (installer is not null && string.IsNullOrWhiteSpace(installerSha256))
                installerSha256 = await TryReadChecksumAssetAsync(release.Assets, installer.Name, cancellationToken);

            return new UpdateInfo(
                CurrentVersion,
                latestVersion,
                string.IsNullOrWhiteSpace(release.HtmlUrl) ? "https://github.com/JohnnyMeister/DiscBox/releases" : release.HtmlUrl!,
                release.Body,
                installer?.BrowserDownloadUrl,
                installer?.Name,
                installer?.Size,
                installerSha256);
        }
        catch
        {
            return null;
        }
    }

    public static async Task<string> DownloadInstallerAsync(
        UpdateInfo update,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(update.InstallerUrl))
            throw new InvalidOperationException("This release does not include a DiscBox installer.");

        var fileName = SanitizeFileName(string.IsNullOrWhiteSpace(update.InstallerFileName)
            ? $"DiscBoxSetup-{update.LatestVersion}.exe"
            : update.InstallerFileName!);
        var updateDir = Path.Combine(Path.GetTempPath(), "DiscBox", "updates", update.LatestVersion);
        Directory.CreateDirectory(updateDir);

        var installerPath = Path.Combine(updateDir, fileName);
        if (File.Exists(installerPath) && await VerifySha256Async(installerPath, update.InstallerSha256, cancellationToken))
        {
            progress?.Report(1);
            return installerPath;
        }

        using var response = await Http.GetAsync(update.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? update.InstallerSizeBytes;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = File.Create(installerPath);

        var buffer = new byte[128 * 1024];
        long copied = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            copied += read;
            if (totalBytes is > 0)
                progress?.Report(Math.Clamp((double)copied / totalBytes.Value, 0, 1));
        }

        progress?.Report(1);

        if (!await VerifySha256Async(installerPath, update.InstallerSha256, cancellationToken))
        {
            TryDeleteFile(installerPath);
            throw new InvalidOperationException("The downloaded installer did not match the release checksum.");
        }

        return installerPath;
    }

    public static void LaunchInstallerAndExit(string installerPath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = installerPath,
            UseShellExecute = true
        });

        if (Avalonia.Application.Current?.ApplicationLifetime is
            Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    public static void OpenReleasePage(string releaseUrl)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = releaseUrl,
            UseShellExecute = true
        });
    }

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"DiscBox/{CurrentVersion}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    private static async Task<bool> VerifySha256Async(string path, string? expectedSha256, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256))
            return true;

        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        var actual = Convert.ToHexString(hash);
        return actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeSha256(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest))
            return null;

        var value = digest.Trim();
        const string prefix = "sha256:";
        if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            value = value[prefix.Length..];

        return value.Length == 64 && value.All(Uri.IsHexDigit) ? value : null;
    }

    private static async Task<string?> TryReadChecksumAssetAsync(
        GitHubReleaseAsset[]? assets,
        string? installerName,
        CancellationToken cancellationToken)
    {
        if (assets is null || string.IsNullOrWhiteSpace(installerName))
            return null;

        var checksumAsset = assets.FirstOrDefault(asset =>
            asset.Name?.Equals(installerName + ".sha256", StringComparison.OrdinalIgnoreCase) == true);
        if (checksumAsset?.BrowserDownloadUrl is null)
            return null;

        try
        {
            var text = await Http.GetStringAsync(checksumAsset.BrowserDownloadUrl, cancellationToken);
            return NormalizeSha256(text.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault());
        }
        catch
        {
            return null;
        }
    }

    private static int CompareVersions(string left, string right)
    {
        var a = ParseVersionParts(left);
        var b = ParseVersionParts(right);
        var max = Math.Max(a.Length, b.Length);
        for (var i = 0; i < max; i++)
        {
            var av = i < a.Length ? a[i] : 0;
            var bv = i < b.Length ? b[i] : 0;
            var cmp = av.CompareTo(bv);
            if (cmp != 0)
                return cmp;
        }

        return 0;
    }

    private static int[] ParseVersionParts(string value) =>
        NormalizeVersion(value)
            .Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => int.TryParse(part, out var number) ? number : 0)
            .ToArray();

    private static string NormalizeVersion(string value)
    {
        var normalized = value.Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
            normalized = normalized[1..];

        var metadataStart = normalized.IndexOfAny(['+', '-']);
        if (metadataStart >= 0)
            normalized = normalized[..metadataStart];

        return normalized.Trim();
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var safe = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "DiscBoxSetup.exe" : safe;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }

        [JsonPropertyName("assets")]
        public GitHubReleaseAsset[]? Assets { get; set; }
    }

    private sealed class GitHubReleaseAsset
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }

        [JsonPropertyName("size")]
        public long? Size { get; set; }

        [JsonPropertyName("digest")]
        public string? Digest { get; set; }
    }
}
