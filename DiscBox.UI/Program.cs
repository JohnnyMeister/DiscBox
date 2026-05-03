using Avalonia;
using System;

namespace DiscBox.UI;

class Program
{
    // Avalonia exige que o entry point seja STA (Single Thread Apartment) no Windows
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
