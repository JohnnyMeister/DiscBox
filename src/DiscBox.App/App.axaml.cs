using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DiscBox.Services;
using DiscBox.ViewModels;
using DiscBox.Views;

namespace DiscBox;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var configService = new ConfigService();

            // Route to Setup if no webhook is configured, otherwise go straight to explorer
            if (!configService.IsConfigured)
            {
                var setupVm = new SetupViewModel(configService);
                var setupWindow = new SetupWindow { DataContext = setupVm };

                // When setup completes, open the main window
                setupVm.SetupCompleted += () =>
                {
                    var mainVm = new MainViewModel(configService);
                    var mainWindow = new MainWindow { DataContext = mainVm };
                    mainWindow.Show();
                    setupWindow.Close();
                };

                desktop.MainWindow = setupWindow;
            }
            else
            {
                var mainVm = new MainViewModel(configService);
                desktop.MainWindow = new MainWindow { DataContext = mainVm };
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
