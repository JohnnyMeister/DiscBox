using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DiscBox.Core.Services;
using DiscBox.UI.ViewModels;
using DiscBox.UI.Views;

namespace DiscBox.UI;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var configService = new ConfigService();

            // Se não há webhook configurado → Setup; caso contrário → Main
            if (!configService.IsConfigured)
            {
                desktop.MainWindow = new SetupWindow
                {
                    DataContext = new SetupViewModel(configService)
                };
            }
            else
            {
                desktop.MainWindow = new MainWindow
                {
                    DataContext = new MainViewModel(configService)
                };
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
