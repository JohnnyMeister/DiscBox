using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace DiscBox.Views;

public partial class RenameDialog : Window
{
    public RenameDialog()
    {
        InitializeComponent();
    }

    public RenameDialog(string currentName)
    {
        InitializeComponent();
        var box = this.FindControl<TextBox>("NameBox")!;
        box.Text = currentName;
        box.SelectAll();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnRename(object? sender, RoutedEventArgs e)
    {
        var name = this.FindControl<TextBox>("NameBox")?.Text?.Trim();
        if (!string.IsNullOrEmpty(name)) Close(name);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
