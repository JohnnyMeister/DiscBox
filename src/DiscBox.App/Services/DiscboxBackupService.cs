using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DiscBox.Services;

public sealed record RemoteBackupDownload(string LocalPath, string MessageId);

public static class DiscboxBackupService
{
    private const string WebhookNamePrefix = "DiscBox manifest ";
    private const string BackupFileName = "discbox-backup.dbx";
    private static readonly byte[] BackupMagic = Encoding.ASCII.GetBytes("DBXDBK01");
    private static readonly byte[] BackupAad = Encoding.ASCII.GetBytes("DiscBox database backup v1");
    private static readonly Regex MessageIdRegex = new(@"DiscBox manifest (?<id>\d{10,32})", RegexOptions.Compiled);

    public static async Task<string> UploadAsync(DiscboxService discbox, ConfigService.DriveConfig drive)
    {
        if (!discbox.IsAvailable)
            throw new InvalidOperationException("DiscBox nao esta disponivel.");

        var tempDbPath = Path.Combine(Path.GetTempPath(), $"discbox-backup-{Guid.NewGuid():N}.sqlite");
        try
        {
            if (!discbox.BackupDatabase(tempDbPath))
                throw new InvalidOperationException(discbox.LastError() ?? "falha ao criar backup local");

            var sqliteBytes = await File.ReadAllBytesAsync(tempDbPath);
            var encrypted = EncryptBackup(sqliteBytes, drive.WebhookUrl);

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            var previousMessageId = !string.IsNullOrWhiteSpace(drive.BackupMessageId)
                ? drive.BackupMessageId
                : await TryReadBackupMessageIdAsync(http, drive.WebhookUrl);

            var newMessageId = await UploadBackupMessageAsync(http, drive.WebhookUrl, encrypted);
            await TryPatchWebhookNameAsync(http, drive.WebhookUrl, newMessageId);

            if (!string.IsNullOrWhiteSpace(previousMessageId) &&
                !string.Equals(previousMessageId, newMessageId, StringComparison.Ordinal))
            {
                await TryDeleteMessageAsync(http, drive.WebhookUrl, previousMessageId);
            }

            return newMessageId;
        }
        finally
        {
            TryDeleteFile(tempDbPath);
        }
    }

    public static async Task<RemoteBackupDownload?> TryDownloadAsync(ConfigService.DriveConfig drive)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var messageId = !string.IsNullOrWhiteSpace(drive.BackupMessageId)
            ? drive.BackupMessageId
            : await TryReadBackupMessageIdAsync(http, drive.WebhookUrl);

        if (string.IsNullOrWhiteSpace(messageId))
            return null;

        var messageUrl = BuildMessageUrl(drive.WebhookUrl, messageId);
        using var messageResponse = await http.GetAsync(messageUrl);
        if (!messageResponse.IsSuccessStatusCode)
            return null;

        await using var messageStream = await messageResponse.Content.ReadAsStreamAsync();
        using var messageJson = await JsonDocument.ParseAsync(messageStream);
        if (!TryGetFirstAttachmentUrl(messageJson.RootElement, out var attachmentUrl))
            return null;

        var encrypted = await http.GetByteArrayAsync(attachmentUrl);
        var sqliteBytes = DecryptBackup(encrypted, drive.WebhookUrl);
        if (!LooksLikeSqlite(sqliteBytes))
            throw new InvalidOperationException("backup remoto invalido");

        var localPath = Path.Combine(Path.GetTempPath(), $"discbox-restore-{Guid.NewGuid():N}.sqlite");
        await File.WriteAllBytesAsync(localPath, sqliteBytes);
        return new RemoteBackupDownload(localPath, messageId);
    }

    private static async Task<string> UploadBackupMessageAsync(HttpClient http, string webhookUrl, byte[] encrypted)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("DiscBox database backup"), "content");
        form.Add(new ByteArrayContent(encrypted), "file", BackupFileName);

        using var response = await http.PostAsync(BuildWaitUrl(webhookUrl), form);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"backup remoto falhou: HTTP {(int)response.StatusCode} - {body}");

        using var json = JsonDocument.Parse(body);
        if (json.RootElement.TryGetProperty("id", out var id) &&
            id.ValueKind == JsonValueKind.String)
        {
            return id.GetString() ?? string.Empty;
        }

        throw new InvalidOperationException("resposta de backup sem message id");
    }

    private static async Task<string> TryReadBackupMessageIdAsync(HttpClient http, string webhookUrl)
    {
        try
        {
            using var response = await http.GetAsync(StripQuery(webhookUrl));
            if (!response.IsSuccessStatusCode)
                return string.Empty;

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var json = await JsonDocument.ParseAsync(stream);
            if (!json.RootElement.TryGetProperty("name", out var nameElement) ||
                nameElement.ValueKind != JsonValueKind.String)
                return string.Empty;

            var name = nameElement.GetString() ?? string.Empty;
            var match = MessageIdRegex.Match(name);
            return match.Success ? match.Groups["id"].Value : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static async Task TryPatchWebhookNameAsync(HttpClient http, string webhookUrl, string messageId)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new { name = $"{WebhookNamePrefix}{messageId}" });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await http.PatchAsync(StripQuery(webhookUrl), content);
            _ = response.IsSuccessStatusCode;
        }
        catch
        {
            // A local config still keeps the backup id; clean restore just needs PATCH to work eventually.
        }
    }

    private static async Task TryDeleteMessageAsync(HttpClient http, string webhookUrl, string messageId)
    {
        try
        {
            using var response = await http.DeleteAsync(BuildMessageUrl(webhookUrl, messageId));
            _ = response.IsSuccessStatusCode;
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    private static byte[] EncryptBackup(byte[] plain, string webhookUrl)
    {
        var key = SHA256.HashData(Encoding.UTF8.GetBytes(webhookUrl));
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];

        using var aes = new AesGcm(key, tag.Length);
        aes.Encrypt(nonce, plain, cipher, tag, BackupAad);

        var result = new byte[BackupMagic.Length + nonce.Length + tag.Length + cipher.Length];
        Buffer.BlockCopy(BackupMagic, 0, result, 0, BackupMagic.Length);
        Buffer.BlockCopy(nonce, 0, result, BackupMagic.Length, nonce.Length);
        Buffer.BlockCopy(tag, 0, result, BackupMagic.Length + nonce.Length, tag.Length);
        Buffer.BlockCopy(cipher, 0, result, BackupMagic.Length + nonce.Length + tag.Length, cipher.Length);
        return result;
    }

    private static byte[] DecryptBackup(byte[] encrypted, string webhookUrl)
    {
        if (encrypted.Length < BackupMagic.Length + 12 + 16)
            throw new InvalidOperationException("backup remoto demasiado pequeno");

        for (var i = 0; i < BackupMagic.Length; i++)
        {
            if (encrypted[i] != BackupMagic[i])
                throw new InvalidOperationException("backup remoto nao e um backup DiscBox");
        }

        var key = SHA256.HashData(Encoding.UTF8.GetBytes(webhookUrl));
        var nonce = encrypted.AsSpan(BackupMagic.Length, 12);
        var tag = encrypted.AsSpan(BackupMagic.Length + 12, 16);
        var cipher = encrypted.AsSpan(BackupMagic.Length + 12 + 16);
        var plain = new byte[cipher.Length];

        using var aes = new AesGcm(key, tag.Length);
        aes.Decrypt(nonce, cipher, tag, plain, BackupAad);
        return plain;
    }

    private static bool TryGetFirstAttachmentUrl(JsonElement message, out string url)
    {
        url = string.Empty;
        if (!message.TryGetProperty("attachments", out var attachments) ||
            attachments.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var attachment in attachments.EnumerateArray())
        {
            if (attachment.TryGetProperty("url", out var urlElement) &&
                urlElement.ValueKind == JsonValueKind.String)
            {
                url = urlElement.GetString() ?? string.Empty;
                return !string.IsNullOrWhiteSpace(url);
            }
        }

        return false;
    }

    private static bool LooksLikeSqlite(byte[] bytes)
    {
        var header = Encoding.ASCII.GetBytes("SQLite format 3");
        if (bytes.Length < header.Length)
            return false;

        for (var i = 0; i < header.Length; i++)
        {
            if (bytes[i] != header[i])
                return false;
        }

        return true;
    }

    private static string BuildWaitUrl(string webhookUrl)
    {
        if (webhookUrl.Contains("wait=true", StringComparison.OrdinalIgnoreCase))
            return webhookUrl;

        return webhookUrl + (webhookUrl.Contains('?') ? "&" : "?") + "wait=true";
    }

    private static string BuildMessageUrl(string webhookUrl, string messageId)
    {
        return $"{StripQuery(webhookUrl).TrimEnd('/')}/messages/{messageId}";
    }

    private static string StripQuery(string webhookUrl)
    {
        var queryStart = webhookUrl.IndexOf('?');
        return queryStart >= 0 ? webhookUrl[..queryStart] : webhookUrl;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Temp cleanup only.
        }
    }
}
