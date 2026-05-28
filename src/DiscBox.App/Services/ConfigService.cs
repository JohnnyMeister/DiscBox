using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiscBox.Services;

/// <summary>
/// Persists the user's webhook URL and preferences to a JSON file
/// in the application data folder. This is the only thing we store
/// outside of the libdiscbox SQLite database.
/// </summary>
public class ConfigService
{
    // ── Paths ──────────────────────────────────────────────────
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

    // ── Config model ───────────────────────────────────────────
    public class Config
    {
        [JsonPropertyName("webhook_url")]
        public string WebhookUrl { get; set; } = string.Empty;

        [JsonPropertyName("db_path")]
        public string DbPath { get; set; } = string.Empty;

        [JsonPropertyName("encrypt")]
        public bool Encrypt { get; set; } = false;

        [JsonPropertyName("drive_name")]
        public string DriveName { get; set; } = "My DiscBox";

        [JsonPropertyName("quick_access_folders")]
        public System.Collections.Generic.List<QuickAccessFolder> QuickAccessFolders { get; set; } = new();
    }

    // ── State ──────────────────────────────────────────────────
    public Config Current { get; private set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Current.WebhookUrl) &&
        Current.WebhookUrl.StartsWith("https://discord.com/api/webhooks/");

    // ── Lifecycle ──────────────────────────────────────────────
    public ConfigService()
    {
        Directory.CreateDirectory(AppDataDir);
        Current = Load();
    }

    private Config Load()
    {
        if (!File.Exists(ConfigPath))
            return new Config { DbPath = DefaultDbPath };

        try
        {
            var json = File.ReadAllText(ConfigPath);
            return JsonSerializer.Deserialize<Config>(json) ?? new Config { DbPath = DefaultDbPath };
        }
        catch
        {
            return new Config { DbPath = DefaultDbPath };
        }
    }

    public void Save(Config config)
    {
        Current = config;
        if (string.IsNullOrWhiteSpace(Current.DbPath))
            Current.DbPath = DefaultDbPath;

        var json = JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ConfigPath, json);
    }
}
