using DiscBox.Models;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace DiscBox.Services;

public class DisboxMigrationService
{
    private readonly ConfigService _config;
    private readonly DiscboxService _discbox;

    public DisboxMigrationService(ConfigService config, DiscboxService discbox)
    {
        _config = config;
        _discbox = discbox;
    }

    public static string HashWebhook(string webhookUrl)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(webhookUrl));
        return Convert.ToHexString(bytes).ToLower();
    }

    public async Task<(int folders, int files)> MigrateAsync(
        IProgress<string>? progress = null)
    {
        var webhookUrl = _config.Current.ActiveDrive?.WebhookUrl ?? _config.Current.WebhookUrl;
        var hash = HashWebhook(webhookUrl);
        var url = $"https://disbox-server.fly.dev/files/get/{hash}";

        progress?.Report("Connecting to Disbox server...");

        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromSeconds(30);
        var json = await http.GetStringAsync(url);

        var root = JsonNode.Parse(json)?["children"];
        if (root is null) return (0, 0);

        var result = await ImportNodeAsync(root, "/", progress);
        return result;
    }

    private async Task<(int folders, int files)> ImportNodeAsync(
        JsonNode node, string parentPath,
        IProgress<string>? progress)
    {
        int folders = 0, files = 0;

        foreach (var kvp in node.AsObject())
        {
            var item = kvp.Value;
            if (item is null) continue;

            var name = item["name"]?.GetValue<string>() ?? kvp.Key;
            var type = item["type"]?.GetValue<string>();
            var virtualPath = parentPath.TrimEnd('/') + "/" + name;

            if (type == "directory")
            {
                progress?.Report($"Folder: {virtualPath}");
                _discbox.Mkdir(virtualPath); // Ignore if it already exists.
                folders++;

                var children = item["children"];
                if (children is not null)
                {
                    var (subFolders, subFiles) = await ImportNodeAsync(children, virtualPath, progress);
                    folders += subFolders;
                    files += subFiles;
                }
            }
            else if (type == "file")
            {
                var size = item["size"]?.GetValue<long>() ?? 0;
                var content = item["content"]?.GetValue<string>() ?? "[]";

                progress?.Report($"File: {name}");
                _discbox.ImportFile(virtualPath, name, size, content);
                files++;
            }

            await Task.Delay(1); // Yield so the UI stays responsive.
        }

        return (folders, files);
    }
}
