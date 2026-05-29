using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiscBox.Services;

/// <summary>
/// Persists DiscBox drives, webhook URLs and preferences to a JSON file
/// in the application data folder.
/// </summary>
public class ConfigService
{
    public static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DiscBox");

    public static readonly string ConfigPath = Path.Combine(AppDataDir, "config.json");

    public static readonly string DefaultDbPath = Path.Combine(AppDataDir, "drive.sqlite");

    public class QuickAccessFolder
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;
    }

    public class DriveConfig
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = "My DiscBox";

        [JsonPropertyName("webhook_url")]
        public string WebhookUrl { get; set; } = string.Empty;

        [JsonPropertyName("db_path")]
        public string DbPath { get; set; } = string.Empty;

        [JsonPropertyName("encrypt")]
        public bool Encrypt { get; set; }

        [JsonPropertyName("backup_message_id")]
        public string BackupMessageId { get; set; } = string.Empty;
    }

    public class Config
    {
        // Legacy single-drive fields. Kept so old config files migrate cleanly.
        [JsonPropertyName("webhook_url")]
        public string WebhookUrl { get; set; } = string.Empty;

        [JsonPropertyName("db_path")]
        public string DbPath { get; set; } = string.Empty;

        [JsonPropertyName("encrypt")]
        public bool Encrypt { get; set; }

        [JsonPropertyName("drive_name")]
        public string DriveName { get; set; } = "My DiscBox";

        [JsonPropertyName("active_drive_id")]
        public string ActiveDriveId { get; set; } = string.Empty;

        [JsonPropertyName("drives")]
        public List<DriveConfig> Drives { get; set; } = new();

        [JsonPropertyName("quick_access_folders")]
        public List<QuickAccessFolder> QuickAccessFolders { get; set; } = new();

        [JsonIgnore]
        public DriveConfig? ActiveDrive =>
            Drives.FirstOrDefault(d => d.Id == ActiveDriveId) ?? Drives.FirstOrDefault();
    }

    public Config Current { get; private set; }

    public bool IsConfigured => Current.ActiveDrive is { } drive && IsValidWebhookUrl(drive.WebhookUrl);

    public ConfigService()
    {
        Directory.CreateDirectory(AppDataDir);
        Current = Normalize(Load());
    }

    public static bool IsValidWebhookUrl(string url) =>
        !string.IsNullOrWhiteSpace(url) &&
        url.Trim().StartsWith("https://discord.com/api/webhooks/", StringComparison.OrdinalIgnoreCase);

    public static DriveConfig CreateDrive(string name, string webhookUrl, bool encrypt, string? dbPath = null)
    {
        var id = Guid.NewGuid().ToString("N");
        return new DriveConfig
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(name) ? "My DiscBox" : name.Trim(),
            WebhookUrl = webhookUrl.Trim(),
            Encrypt = encrypt,
            DbPath = string.IsNullOrWhiteSpace(dbPath) ? DbPathForDrive(id) : dbPath
        };
    }

    public static string DbPathForDrive(string driveId)
    {
        var safeId = new string(driveId.Where(char.IsLetterOrDigit).ToArray());
        if (string.IsNullOrWhiteSpace(safeId))
            safeId = Guid.NewGuid().ToString("N");
        return Path.Combine(AppDataDir, $"drive-{safeId}.sqlite");
    }

    private Config Load()
    {
        if (!File.Exists(ConfigPath))
            return new Config();

        try
        {
            var json = File.ReadAllText(ConfigPath);
            return JsonSerializer.Deserialize<Config>(json) ?? new Config();
        }
        catch
        {
            return new Config();
        }
    }

    public void Save(Config config)
    {
        Current = Normalize(config);

        var json = JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ConfigPath, json);
    }

    public void Save()
    {
        Save(Current);
    }

    private static Config Normalize(Config config)
    {
        config.Drives ??= new List<DriveConfig>();
        config.QuickAccessFolders ??= new List<QuickAccessFolder>();

        if (config.Drives.Count == 0 && IsValidWebhookUrl(config.WebhookUrl))
        {
            config.Drives.Add(new DriveConfig
            {
                Id = string.IsNullOrWhiteSpace(config.ActiveDriveId)
                    ? "primary"
                    : config.ActiveDriveId.Trim(),
                Name = string.IsNullOrWhiteSpace(config.DriveName)
                    ? "My DiscBox"
                    : config.DriveName.Trim(),
                WebhookUrl = config.WebhookUrl.Trim(),
                DbPath = string.IsNullOrWhiteSpace(config.DbPath)
                    ? DefaultDbPath
                    : config.DbPath,
                Encrypt = config.Encrypt
            });
        }

        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < config.Drives.Count; i++)
        {
            var drive = config.Drives[i];
            if (string.IsNullOrWhiteSpace(drive.Id) || !usedIds.Add(drive.Id))
            {
                drive.Id = Guid.NewGuid().ToString("N");
                usedIds.Add(drive.Id);
            }

            drive.Name = string.IsNullOrWhiteSpace(drive.Name) ? "My DiscBox" : drive.Name.Trim();
            drive.WebhookUrl = drive.WebhookUrl.Trim();
            if (string.IsNullOrWhiteSpace(drive.DbPath))
                drive.DbPath = i == 0 && !string.IsNullOrWhiteSpace(config.DbPath)
                    ? config.DbPath
                    : DbPathForDrive(drive.Id);
        }

        var active = config.ActiveDrive;
        if (active is null && config.Drives.Count > 0)
        {
            active = config.Drives[0];
            config.ActiveDriveId = active.Id;
        }
        else if (active is not null)
        {
            config.ActiveDriveId = active.Id;
        }

        if (active is not null)
        {
            config.WebhookUrl = active.WebhookUrl;
            config.DbPath = active.DbPath;
            config.Encrypt = active.Encrypt;
            config.DriveName = active.Name;
        }

        return config;
    }
}
