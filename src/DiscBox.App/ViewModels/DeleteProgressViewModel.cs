using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;

namespace DiscBox.ViewModels;

public partial class DeleteProgressViewModel : ObservableObject
{
    [ObservableProperty] private string _fileName = string.Empty;
    [ObservableProperty] private int _percent = 0;
    [ObservableProperty] private string _statusText = "Preparing...";
    [ObservableProperty] private string _detailText = string.Empty;
    [ObservableProperty] private string _speedText = string.Empty;
    [ObservableProperty] private string _etaText = string.Empty;
    [ObservableProperty] private bool _isDone = false;
    [ObservableProperty] private bool _isIndeterminate = true;

    public bool Cancelled { get; private set; } = false;
    public event Action? CancelRequested;

    private DateTime _startTime = DateTime.Now;

    [RelayCommand]
    private void Cancel()
    {
        Cancelled = true;
        StatusText = "Cancelling...";
        CancelRequested?.Invoke();
    }

    public void Start(string fileName)
    {
        FileName = fileName;
        Percent = 0;
        StatusText = "Starting delete helper...";
        DetailText = "Preparing the Discord chunk list.";
        SpeedText = string.Empty;
        EtaText = string.Empty;
        IsDone = false;
        IsIndeterminate = true;
        _startTime = DateTime.Now;
    }

    public void Update(long done, long total, string currentPath)
    {
        IsIndeterminate = total <= 0;
        Percent = total > 0 ? (int)(done * 100 / total) : 0;

        StatusText = total > 0
            ? $"Deleting chunks {done}/{total}"
            : "Deleting on Discord...";

        var name = currentPath;
        var slash = currentPath.LastIndexOf('/');
        if (slash >= 0 && slash < currentPath.Length - 1)
            name = currentPath[(slash + 1)..];

        DetailText = string.IsNullOrWhiteSpace(name)
            ? "Removing messages and metadata."
            : $"Current: {name}";

        var elapsed = Math.Max((DateTime.Now - _startTime).TotalSeconds, 0.1);
        var itemsPerSec = done / elapsed;
        SpeedText = itemsPerSec > 0 && total > 0
            ? $"{itemsPerSec:F1} chunks/s"
            : string.Empty;

        if (itemsPerSec > 0 && total > done)
        {
            var remaining = (total - done) / itemsPerSec;
            EtaText = $"~{FormatTime(remaining)} remaining";
        }
        else
        {
            EtaText = string.Empty;
        }
    }

    public void Complete(string fileName)
    {
        Percent = 100;
        IsIndeterminate = false;
        var elapsed = (DateTime.Now - _startTime).TotalSeconds;
        StatusText = $"Deleted in {FormatTime(elapsed)}";
        DetailText = "Discord and database updated.";
        SpeedText = string.Empty;
        EtaText = string.Empty;
        IsDone = true;
    }

    public void Error(string message)
    {
        IsIndeterminate = false;
        StatusText = $"Error: {message}";
        DetailText = string.Empty;
        SpeedText = string.Empty;
        EtaText = string.Empty;
        IsDone = true;
    }

    private static string FormatTime(double seconds) => seconds switch
    {
        < 60 => $"{(int)seconds}s",
        < 3600 => $"{(int)(seconds / 60)}m {(int)(seconds % 60)}s",
        _ => $"{(int)(seconds / 3600)}h {(int)((seconds % 3600) / 60)}m"
    };
}
