using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace DiscBox.Views;

public partial class ConfirmDeleteDialog : Window
{
    public ConfirmDeleteDialog(string name, bool isFolder)
    {
        InitializeComponent();
        var tipo = isFolder ? "a pasta" : "o ficheiro";
        this.FindControl<TextBlock>("MessageText")!.Text =
            $"Tens a certeza que queres apagar {tipo} \"{name}\"?";
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(true);
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}