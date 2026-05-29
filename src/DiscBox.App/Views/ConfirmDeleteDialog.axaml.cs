using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace DiscBox.Views;

public partial class ConfirmDeleteDialog : Window
{
    public ConfirmDeleteDialog()
    {
        InitializeComponent();
    }

    public ConfirmDeleteDialog(string name, bool isFolder, int itemCount = 1)
    {
        InitializeComponent();
        var tipo = isFolder ? "a pasta" : "o ficheiro";
        var msgText = this.FindControl<TextBlock>("MessageText");
        if (msgText != null)
        {
            msgText.Text = itemCount > 1
                ? $"Tens a certeza que queres apagar {itemCount} itens selecionados?"
                : $"Tens a certeza que queres apagar {tipo} \"{name}\"?";
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(true);
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
