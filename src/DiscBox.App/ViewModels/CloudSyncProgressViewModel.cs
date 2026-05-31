using CommunityToolkit.Mvvm.ComponentModel;

namespace DiscBox.ViewModels;

public partial class CloudSyncProgressViewModel : ObservableObject
{
    [ObservableProperty] private int _percent;
    [ObservableProperty] private string _statusText = "Preparing cloud sync...";
    [ObservableProperty] private string _detailText = string.Empty;
    [ObservableProperty] private bool _isDone;
    [ObservableProperty] private bool _hasError;

    public void Update(int percent, string status, string? detail = null)
    {
        Percent = Math.Clamp(percent, 0, 100);
        StatusText = status;
        if (detail is not null)
            DetailText = detail;
    }

    public void Complete(string detail)
    {
        Percent = 100;
        StatusText = "Sync Cloud complete";
        DetailText = detail;
        IsDone = true;
        HasError = false;
    }

    public void Error(string message)
    {
        StatusText = "Sync Cloud failed";
        DetailText = message;
        IsDone = true;
        HasError = true;
    }
}
