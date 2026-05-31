using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using DiscBox.Services;

namespace DiscBox.Views;

public sealed record DriveDialogResult(string Name, string WebhookUrl, bool Encrypt);

public partial class DriveDialog : Window
{
    public DriveDialog()
    {
        InitializeComponent();
        Opened += (_, _) => this.FindControl<TextBox>("NameBox")?.Focus();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnAdd(object? sender, RoutedEventArgs e)
    {
        var name = this.FindControl<TextBox>("NameBox")?.Text?.Trim();
        var webhook = this.FindControl<TextBox>("WebhookBox")?.Text?.Trim();
        var encrypt = this.FindControl<CheckBox>("EncryptBox")?.IsChecked == true;

        if (string.IsNullOrWhiteSpace(name))
            name = "My DiscBox";

        if (!ConfigService.IsValidWebhookUrl(webhook ?? string.Empty))
        {
            ShowError("Enter a valid Discord webhook.");
            return;
        }

        Close(new DriveDialogResult(name, webhook!, encrypt));
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void ShowError(string message)
    {
        var error = this.FindControl<TextBlock>("ErrorText");
        if (error is null) return;
        error.Text = message;
        error.IsVisible = true;
    }
}
