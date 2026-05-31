using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;

namespace DiscBox.ViewModels;

public partial class TransferProgressViewModel : ObservableObject
{
    [ObservableProperty] private string _titleText = "Preparing...";
    [ObservableProperty] private string _itemName = string.Empty;
    [ObservableProperty] private int _percent = 0;
    [ObservableProperty] private string _statusText = "Preparing...";
    [ObservableProperty] private string _detailText = string.Empty;
    [ObservableProperty] private string _speedText = string.Empty;
    [ObservableProperty] private string _etaText = string.Empty;
    [ObservableProperty] private bool _isDone = false;
    [ObservableProperty] private bool _isCut = false;
    [ObservableProperty] private bool _isIndeterminate = true;

    private DateTime _startedAt = DateTime.Now;
    private DateTime _currentItemStartedAt = DateTime.Now;
    private int _doneItems;
    private int _totalItems;

    public bool Cancelled { get; private set; }

    [RelayCommand]
    private void Cancel()
    {
        Cancelled = true;
        StatusText = "Cancelling...";
        DetailText = "The operation will stop after the current item finishes.";
    }

    public void Start(string title, int totalItems, bool isCut)
    {
        TitleText = title;
        IsCut = isCut;
        _totalItems = Math.Max(totalItems, 0);
        _doneItems = 0;
        _startedAt = DateTime.Now;
        _currentItemStartedAt = _startedAt;
        Percent = 0;
        IsDone = false;
        IsIndeterminate = _totalItems <= 0;
        StatusText = _totalItems > 0 ? $"0/{_totalItems} item(s)" : "Preparing list...";
        DetailText = string.Empty;
        SpeedText = string.Empty;
        EtaText = string.Empty;
    }

    public void StartItem(string name)
    {
        ItemName = name;
        _currentItemStartedAt = DateTime.Now;
        IsIndeterminate = _totalItems <= 0;
        UpdateItemProgress(0);
    }

    public void UpdateBytes(long done, long total, string phase)
    {
        IsIndeterminate = total <= 0 || _totalItems <= 0;
        var itemFraction = total > 0 ? Math.Clamp(done / (double)total, 0, 1) : 0;
        UpdateItemProgress(itemFraction);

        var elapsed = Math.Max((DateTime.Now - _currentItemStartedAt).TotalSeconds, 0.1);
        var bytesPerSec = done / elapsed;
        SpeedText = bytesPerSec > 0 ? $"{FormatBytes((long)bytesPerSec)}/s" : string.Empty;

        if (bytesPerSec > 0 && total > done)
        {
            var remaining = (total - done) / bytesPerSec;
            EtaText = $"~{FormatTime(remaining)} remaining";
        }
        else
        {
            EtaText = RemainingItemsText();
        }

        StatusText = $"{phase}: {FormatBytes(done)} / {FormatBytes(total)}";
        DetailText = $"{_doneItems}/{_totalItems} item(s) completed";
    }

    public void CompleteItem()
    {
        _doneItems++;
        UpdateItemProgress(0);
        StatusText = $"{_doneItems}/{_totalItems} item(s)";
        DetailText = RemainingItemsText();

        var elapsed = Math.Max((DateTime.Now - _startedAt).TotalSeconds, 0.1);
        var itemsPerSec = _doneItems / elapsed;
        SpeedText = itemsPerSec > 0 ? $"{itemsPerSec:F1} items/s" : string.Empty;

        if (itemsPerSec > 0 && _totalItems > _doneItems)
        {
            var remaining = (_totalItems - _doneItems) / itemsPerSec;
            EtaText = $"~{FormatTime(remaining)} remaining";
        }
        else
        {
            EtaText = string.Empty;
        }
    }

    public void Complete(string message)
    {
        Percent = 100;
        IsIndeterminate = false;
        StatusText = message;
        DetailText = "Operation finished.";
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

    private void UpdateItemProgress(double itemFraction)
    {
        if (_totalItems <= 0)
        {
            Percent = 0;
            return;
        }

        var progress = (_doneItems + itemFraction) * 100 / _totalItems;
        Percent = Math.Clamp((int)Math.Round(progress), 0, 100);
    }

    private string RemainingItemsText()
    {
        var remaining = Math.Max(_totalItems - _doneItems, 0);
        return remaining == 1 ? "1 item remaining" : $"{remaining} items remaining";
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:F1} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:F2} GB"
    };

    private static string FormatTime(double seconds) => seconds switch
    {
        < 60 => $"{(int)seconds}s",
        < 3600 => $"{(int)(seconds / 60)}m {(int)(seconds % 60)}s",
        _ => $"{(int)(seconds / 3600)}h {(int)((seconds % 3600) / 60)}m"
    };
}
