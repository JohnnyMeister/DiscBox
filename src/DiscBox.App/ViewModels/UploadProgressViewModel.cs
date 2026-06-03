using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Threading;

namespace DiscBox.ViewModels;

public partial class UploadProgressViewModel : ObservableObject
{
    [ObservableProperty] private string _headerText = "Uploading file...";
    [ObservableProperty] private string _fileName = string.Empty;
    [ObservableProperty] private string _fileCounterText = string.Empty;
    [ObservableProperty] private int _percent = 0;
    [ObservableProperty] private string _statusText = "Preparing...";
    [ObservableProperty] private string _detailText = string.Empty;
    [ObservableProperty] private string _speedText = string.Empty;
    [ObservableProperty] private string _etaText = string.Empty;
    [ObservableProperty] private bool _isDone = false;
    [ObservableProperty] private bool _isPaused = false;
    [ObservableProperty] private string _pauseResumeText = "Pause";

    public bool Cancelled { get; private set; } = false;

    private readonly ManualResetEventSlim _pauseGate = new(true);
    private DateTime _startTime = DateTime.Now;
    private int _totalFiles = 1;
    private int _currentFileIndex = 1;
    private int _uploadedFiles;
    private long _totalBatchBytes;
    private long _completedBatchBytes;
    private long _currentFileSize;

    [RelayCommand]
    private void Cancel()
    {
        Cancelled = true;
        IsPaused = false;
        PauseResumeText = "Pause";
        _pauseGate.Set();
        StatusText = "Cancelling...";
    }

    [RelayCommand]
    private void PauseResume()
    {
        if (IsDone || Cancelled)
            return;

        if (IsPaused)
        {
            IsPaused = false;
            PauseResumeText = "Pause";
            StatusText = "Resuming...";
            _pauseGate.Set();
            return;
        }

        IsPaused = true;
        PauseResumeText = "Resume";
        StatusText = "Paused";
        DetailText = "The operation will continue when you resume.";
        _pauseGate.Reset();
    }

    public void WaitIfPaused()
    {
        while (IsPaused && !Cancelled && !IsDone)
            _pauseGate.Wait(100);
    }

    public void StartBatch(int totalFiles, long totalBytes)
    {
        _startTime = DateTime.Now;
        _totalFiles = Math.Max(1, totalFiles);
        _currentFileIndex = 1;
        _uploadedFiles = 0;
        _totalBatchBytes = Math.Max(0, totalBytes);
        _completedBatchBytes = 0;
        _currentFileSize = 0;
        HeaderText = _totalFiles == 1 ? "Uploading file..." : "Uploading files...";
        FileCounterText = _totalFiles == 1 ? string.Empty : $"0/{_totalFiles} files uploaded";
        Percent = 0;
        StatusText = "Preparing upload...";
        DetailText = _totalBatchBytes > 0
            ? $"Total size: {FormatBytes(_totalBatchBytes)}"
            : string.Empty;
        SpeedText = string.Empty;
        EtaText = string.Empty;
        IsDone = false;
        IsPaused = false;
        PauseResumeText = "Pause";
        Cancelled = false;
        _pauseGate.Set();
    }

    public void BeginFile(string fileName, int fileIndex, int totalFiles, long completedBytes, long fileSize)
    {
        FileName = fileName;
        _currentFileIndex = Math.Clamp(fileIndex, 1, Math.Max(1, totalFiles));
        _totalFiles = Math.Max(1, totalFiles);
        _completedBatchBytes = Math.Max(0, completedBytes);
        _currentFileSize = Math.Max(0, fileSize);
        FileCounterText = _totalFiles == 1
            ? string.Empty
            : $"{_uploadedFiles}/{_totalFiles} files uploaded";
        StatusText = $"Uploading {_currentFileIndex}/{_totalFiles}";
        DetailText = _currentFileSize > 0
            ? $"{fileName} - {FormatBytes(_currentFileSize)}"
            : fileName;
    }

    public void Update(long done, long total, int chunkIndex, int chunkCount)
    {
        if (_startTime == default) _startTime = DateTime.Now;

        if (total < 0)
        {
            var retrieved = Math.Max(0, done);
            var totalUrls = Math.Max(0, chunkCount);
            Percent = totalUrls > 0 ? (int)(retrieved * 100 / totalUrls) : 0;
            StatusText = $"Fetching URLs {retrieved}/{totalUrls}";
            DetailText = "Requesting temporary Discord links before downloading chunks.";
            SpeedText = totalUrls > 0 ? $"{retrieved}/{totalUrls} URLs" : string.Empty;
            EtaText = string.Empty;
            return;
        }

        Percent = total > 0 ? (int)(done * 100 / total) : 0;

        var elapsed = (DateTime.Now - _startTime).TotalSeconds;
        double bytesPerSec = elapsed > 0.5 ? done / elapsed : 0;

        SpeedText = bytesPerSec > 0
            ? $"{FormatBytes((long)bytesPerSec)}/s"
            : string.Empty;

        if (bytesPerSec > 0 && total > done)
        {
            double remainingSec = (total - done) / bytesPerSec;
            EtaText = $"~{FormatTime(remainingSec)} remaining";
        }
        else
        {
            EtaText = string.Empty;
        }

        var currentChunk = chunkCount > 0 ? Math.Min(chunkIndex + 1, chunkCount) : 0;
        StatusText = $"Chunk {currentChunk}/{chunkCount} - " +
                     $"{FormatBytes(done)} / {FormatBytes(total)}";
        DetailText = chunkCount > 0
            ? $"{chunkCount} chunk(s) total; {Math.Min(done, total):N0} bytes processed"
            : string.Empty;
    }

    public void UpdateBatch(long done, long total, int chunkIndex, int chunkCount)
    {
        if (_startTime == default) _startTime = DateTime.Now;

        var currentTotal = total > 0 ? total : _currentFileSize;
        var safeDone = Math.Clamp(done, 0, Math.Max(done, currentTotal));
        var processedTotal = Math.Min(
            _totalBatchBytes > 0 ? _totalBatchBytes : _completedBatchBytes + currentTotal,
            _completedBatchBytes + safeDone);

        Percent = _totalBatchBytes > 0
            ? (int)Math.Clamp(processedTotal * 100 / _totalBatchBytes, 0, 100)
            : 0;

        var elapsed = (DateTime.Now - _startTime).TotalSeconds;
        double bytesPerSec = elapsed > 0.5 ? processedTotal / elapsed : 0;

        SpeedText = bytesPerSec > 0
            ? $"{FormatBytes((long)bytesPerSec)}/s"
            : string.Empty;

        if (bytesPerSec > 0 && _totalBatchBytes > processedTotal)
        {
            double remainingSec = (_totalBatchBytes - processedTotal) / bytesPerSec;
            EtaText = $"~{FormatTime(remainingSec)} remaining";
        }
        else
        {
            EtaText = string.Empty;
        }

        var currentChunk = chunkCount > 0 ? Math.Min(chunkIndex + 1, chunkCount) : 0;
        StatusText = _totalFiles == 1
            ? $"Chunk {currentChunk}/{chunkCount} - {FormatBytes(safeDone)} / {FormatBytes(currentTotal)}"
            : $"File {_currentFileIndex}/{_totalFiles} - chunk {currentChunk}/{chunkCount}";
        DetailText = _totalBatchBytes > 0
            ? $"{FormatBytes(processedTotal)} / {FormatBytes(_totalBatchBytes)} total"
            : $"{FormatBytes(safeDone)} processed";
    }

    public void FinishCurrentFile(bool uploaded, long completedBytes)
    {
        _completedBatchBytes = Math.Max(_completedBatchBytes, completedBytes);
        if (uploaded)
            _uploadedFiles++;

        FileCounterText = _totalFiles == 1
            ? string.Empty
            : $"{_uploadedFiles}/{_totalFiles} files uploaded";
    }

    public void CompleteBatch(int uploaded, int failed, bool cancelled)
    {
        Percent = cancelled ? Percent : 100;
        var elapsed = (DateTime.Now - _startTime).TotalSeconds;
        StatusText = cancelled
            ? $"Cancelled after {FormatTime(elapsed)}"
            : failed == 0
                ? $"Completed in {FormatTime(elapsed)}"
                : $"Completed with {failed} error(s)";
        DetailText = failed == 0
            ? $"{uploaded}/{_totalFiles} file(s) uploaded"
            : $"{uploaded}/{_totalFiles} file(s) uploaded, {failed} failed";
        SpeedText = string.Empty;
        EtaText = string.Empty;
        IsDone = true;
        IsPaused = false;
        PauseResumeText = "Pause";
        _pauseGate.Set();
    }

    public void Complete(string fileName)
    {
        Percent = 100;
        var elapsed = (DateTime.Now - _startTime).TotalSeconds;
        StatusText = $"Completed in {FormatTime(elapsed)}";
        DetailText = string.Empty;
        SpeedText = string.Empty;
        EtaText = string.Empty;
        IsDone = true;
        IsPaused = false;
        PauseResumeText = "Pause";
        _pauseGate.Set();
    }

    public void Error(string message)
    {
        StatusText = $"Error: {message}";
        DetailText = string.Empty;
        SpeedText = string.Empty;
        EtaText = string.Empty;
        IsDone = true;
        IsPaused = false;
        PauseResumeText = "Pause";
        _pauseGate.Set();
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
