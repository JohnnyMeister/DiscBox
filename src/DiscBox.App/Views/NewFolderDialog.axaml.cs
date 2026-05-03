using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace DiscBox.Views;

public partial class NewFolderDialog : Window
{
    public NewFolderDialog()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnCreate(object? sender, RoutedEventArgs e)
    {
        var box = this.FindControl<TextBox>("FolderNameBox");
        var name = box?.Text?.Trim();
        if (!string.IsNullOrEmpty(name))
            Close(name);
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }
}