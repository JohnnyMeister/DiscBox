using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscBox.Services;
using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace DiscBox.ViewModels;

public partial class SetupViewModel : ObservableObject
{
    private readonly ConfigService _config;

    public event Action? SetupCompleted;

    // Bindable properties.

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _webhookUrl = string.Empty;

    [ObservableProperty]
    private string _driveName = "My DiscBox";

    [ObservableProperty]
    private bool _encrypt = false;

    [ObservableProperty]
    private bool _isValidating = false;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isError = false;

    [ObservableProperty]
    private bool _isSuccess = false;

    // Constructor.

    public SetupViewModel(ConfigService config)
    {
        _config = config;
    }

    // Commands.

    private bool CanSave => ConfigService.IsValidWebhookUrl(WebhookUrl) && !IsValidating;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        IsValidating  = true;
        IsError       = false;
        IsSuccess     = false;
        StatusMessage = "Validating webhook...";

        bool valid = await ValidateWebhookAsync(WebhookUrl);

        IsValidating = false;

        if (!valid)
        {
            StatusMessage = "Invalid webhook URL. Check the URL and try again.";
            IsError       = true;
            return;
        }

        // Save config
        _config.Save(new ConfigService.Config
        {
            WebhookUrl = WebhookUrl.Trim(),
            DriveName  = string.IsNullOrWhiteSpace(DriveName) ? "My DiscBox" : DriveName.Trim(),
            Encrypt    = Encrypt,
            DbPath     = ConfigService.DefaultDbPath
        });

        StatusMessage = "Webhook validated. Opening DiscBox...";
        IsSuccess     = true;

        // Small delay so user sees the success message
        await Task.Delay(800);
        SetupCompleted?.Invoke();
    }

    // Internal helpers.

    private static async Task<bool> ValidateWebhookAsync(string url)
    {
        try
        {
            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(10);
            // A GET to a Discord webhook returns 200 with webhook info if valid
            var resp = await http.GetAsync(url);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
