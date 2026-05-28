using Avalonia;
using DiscBox.Services;
using System;
using System.IO;
using System.Text.Json;

namespace DiscBox;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized yet.
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--delete-helper")
        {
            Environment.ExitCode = RunDeleteHelper(args[1]);
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static int RunDeleteHelper(string requestPath)
    {
        DeleteHelperRequest? request = null;
        try
        {
            var json = File.ReadAllText(requestPath);
            request = JsonSerializer.Deserialize<DeleteHelperRequest>(json);
            if (request is null)
                return 2;

            using var discbox = new DiscboxService(request.WebhookUrl, request.DbPath);
            var ok = discbox.IsAvailable && discbox.Delete(
                request.VirtualPath,
                (currentPath, done, total, chunkIndex, chunkCount) =>
                {
                    if (string.IsNullOrWhiteSpace(request.ProgressPath))
                        return;

                    var progress = new DeleteHelperProgress
                    {
                        CurrentPath = currentPath,
                        Done = done,
                        Total = total,
                        ChunkIndex = chunkIndex,
                        ChunkCount = chunkCount
                    };
                    File.WriteAllText(request.ProgressPath, JsonSerializer.Serialize(progress));
                });
            var result = new DeleteHelperResult
            {
                Ok = ok,
                Error = ok ? null : discbox.LastError()
            };

            File.WriteAllText(request.ResultPath, JsonSerializer.Serialize(result));
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            if (!string.IsNullOrWhiteSpace(request?.ResultPath))
            {
                var result = new DeleteHelperResult { Ok = false, Error = ex.Message };
                File.WriteAllText(request.ResultPath, JsonSerializer.Serialize(result));
            }

            return 3;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
