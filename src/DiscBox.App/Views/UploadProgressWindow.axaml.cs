using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DiscBox.Views;

public partial class UploadProgressWindow : Window
{
    public UploadProgressWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}