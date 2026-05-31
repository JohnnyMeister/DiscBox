using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DiscBox.Views;

public partial class CloudSyncProgressWindow : Window
{
    private bool _canClose;

    public CloudSyncProgressWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
    }

    public void AllowClose()
    {
        _canClose = true;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!_canClose)
            e.Cancel = true;
    }
}
