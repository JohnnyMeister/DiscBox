using System;
using System.IO;
using System.Text.Json;

namespace DiscBox.Core.Services;

public class DiscBoxConfig
{
    public string WebhookUrl { get; set; } = string.Empty;
    public string DriveName  { get; set; } = "O Meu Drive";
    public bool   Encrypt    { get; set; } = false;
}

/// <summary>
/// Manages DiscBox configuration stored in ~/.discbox/config.json
/// </summary>
public class ConfigService
{
    private static readonly string ConfigDir  = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".discbox");

    private static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");
    public  static readonly string DbPath     = Path.Combine(ConfigDir, "db.sqlite");
    public  static readonly string NativeLibPath = GetNativeLibPath();

    private DiscBoxConfig _config;

    public DiscBoxConfig Config => _config;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_config.WebhookUrl) &&
        _config.WebhookUrl.StartsWith("https://discord.com/api/webhooks/");

    public ConfigService()
    {
        Directory.CreateDirectory(ConfigDir);
        _config = Load();
    }

    private DiscBoxConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                return JsonSerializer.Deserialize<DiscBoxConfig>(json)
                       ?? new DiscBoxConfig();
            }
        }
        catch { /* fallback to defaults */ }
        return new DiscBoxConfig();
    }

    public void Save(DiscBoxConfig config)
    {
        _config = config;
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        File.WriteAllText(ConfigPath, json);
    }

    private static string GetNativeLibPath()
    {
        // Look for libdiscbox next to the executable first, then fallback
        var exeDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(exeDir, "libdiscbox.dll"),           // Windows
            Path.Combine(exeDir, "libdiscbox.so"),            // Linux
            Path.Combine(exeDir, "libdiscbox.dylib"),         // macOS
            Path.Combine(exeDir, "native", "libdiscbox.dll"),
            Path.Combine(exeDir, "native", "libdiscbox.so"),
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return c;

        return "libdiscbox"; // let the OS find it via PATH / LD_LIBRARY_PATH
    }
}
