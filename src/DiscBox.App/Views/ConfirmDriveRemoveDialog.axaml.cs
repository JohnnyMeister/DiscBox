using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace DiscBox.Views;

public partial class ConfirmDriveRemoveDialog : Window
{
    public ConfirmDriveRemoveDialog()
    {
        InitializeComponent();
    }

    public ConfirmDriveRemoveDialog(string driveName)
    {
        InitializeComponent();
        var msgText = this.FindControl<TextBlock>("MessageText");
        if (msgText is not null)
            msgText.Text = $"Are you sure you want to remove \"{driveName}\"?";
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(true);
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
