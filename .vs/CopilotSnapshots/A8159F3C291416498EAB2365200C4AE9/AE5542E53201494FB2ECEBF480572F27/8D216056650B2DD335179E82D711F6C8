using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Runtime.CompilerServices;

namespace DiscBox.ViewModels;

public partial class UploadProgressViewModel : ObservableObject
{
    [ObservableProperty] private string _fileName = string.Empty;
    [ObservableProperty] private int _percent = 0;
    [ObservableProperty] private string _statusText = "A preparar...";
    [ObservableProperty] private bool _isDone = false;

    public bool Cancelled { get; private set; } = false;

    [RelayCommand]
    private void Cancel()
    {
        Cancelled = true;
        StatusText = "A cancelar...";
    }

    public void Update(long done, long total, int chunkIndex, int chunkCount)
    {
        Percent = total > 0 ? (int)(done * 100 / total) : 0;
        StatusText = $"Chunk {chunkIndex + 1} de {chunkCount} — {FormatBytes(done)} / {FormatBytes(total)}";
    }

    public void Complete(string fileName)
    {
        Percent = 100;
        StatusText = $"✓ {fileName} enviado com sucesso!";
        IsDone = true;
    }

    public void Error(string message)
    {
        StatusText = $"✗ Erro: {message}";
        IsDone = true;
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / 1024.0 / 1024:F1} MB"
    };
}