using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using DiscBox.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace DiscBox.Views;

public partial class UpdateDialog : Window
{
    private readonly UpdateInfo _update;
    private readonly CancellationTokenSource _cts = new();

    public UpdateDialog()
        : this(new UpdateInfo("0.0.0", "0.0.0", "https://github.com/JohnnyMeister/DiscBox/releases", null, null, null, null, null, null, null, null, null))
    {
    }

    public UpdateDialog(UpdateInfo update)
    {
        _update = update;
        InitializeComponent();
        Populate();
        Closed += (_, _) => _cts.Cancel();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void Populate()
    {
        this.FindControl<TextBlock>("VersionText")!.Text =
            $"Installed { _update.CurrentVersion }  ->  Available { _update.LatestVersion }";
        this.FindControl<TextBlock>("NotesText")!.Text =
            string.IsNullOrWhiteSpace(_update.ReleaseNotes)
                ? "No release notes were provided for this version."
                : _update.ReleaseNotes.Trim();

        if (string.IsNullOrWhiteSpace(_update.PortableUrl))
        {
            this.FindControl<TextBlock>("StatusText")!.Text =
                "This release does not include a portable package for in-app updates. Open the release page to download it manually.";
            this.FindControl<Button>("UpdateButton")!.IsEnabled = false;
        }
    }

    private void OnLater(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Close();
    }

    private void OnOpenRelease(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        UpdateService.OpenReleasePage(_update.ReleaseUrl);
    }

    private async void OnUpdate(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await DownloadAndLaunchAsync();
    }

    private async Task DownloadAndLaunchAsync()
    {
        var statusText = this.FindControl<TextBlock>("StatusText")!;
        var progressBar = this.FindControl<ProgressBar>("DownloadProgress")!;
        var updateButton = this.FindControl<Button>("UpdateButton")!;
        var releaseButton = this.FindControl<Button>("ReleaseButton")!;
        var laterButton = this.FindControl<Button>("LaterButton")!;

        updateButton.IsEnabled = false;
        releaseButton.IsEnabled = false;
        laterButton.IsEnabled = false;
        progressBar.IsVisible = true;
        progressBar.Value = 0;
        statusText.Text = "Downloading update...";

        try
        {
            var progress = new Progress<UpdateInstallProgress>(value =>
            {
                progressBar.Value = Math.Clamp(value.Percent, 0, 100);
                statusText.Text = value.Status;
            });

            await UpdateService.DownloadInstallAndRestartAsync(_update, progress, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            statusText.Text = "Update cancelled.";
        }
        catch (Exception ex)
        {
            statusText.Text = $"Update failed: {ex.Message}";
            updateButton.IsEnabled = true;
            releaseButton.IsEnabled = true;
            laterButton.IsEnabled = true;
        }
    }
}
