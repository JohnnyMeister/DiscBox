using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscBox.Models;
using DiscBox.Services;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using Avalonia;
using Avalonia.Platform.Storage;
using Avalonia.Controls.ApplicationLifetimes;

namespace DiscBox.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly ConfigService _config;
    private readonly DiscboxService? _discbox;
    private static readonly TimeSpan DeleteTimeout = TimeSpan.FromHours(6);

    private static IStorageProvider? StorageProvider =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
        ?.MainWindow?.StorageProvider;

    [ObservableProperty] private string _currentPath = "/";
    [ObservableProperty] private ObservableCollection<FileEntry> _entries = [];
    [ObservableProperty] private FileEntry? _selectedEntry;
    [ObservableProperty] private bool _isLoading = false;
    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private string _driveName = "My DiscBox";

    // Clipboard interno
    [ObservableProperty] private FileEntry? _clipboardEntry;
    [ObservableProperty] private bool _clipboardIsCut = false;
    public bool HasClipboard => ClipboardEntry is not null;

    // BreadcrumbItem instead of tuple — tuples don't work with compiled Avalonia bindings
    public ObservableCollection<BreadcrumbItem> Breadcrumbs { get; } = [];

    public ObservableCollection<ConfigService.QuickAccessFolder> QuickAccessFolders { get; } = [];

    public MainViewModel(ConfigService config)
    {
        _config   = config;
        DriveName = config.Current.DriveName;
        foreach (var folder in config.Current.QuickAccessFolders)
        {
            QuickAccessFolders.Add(folder);
        }
        _discbox = new DiscboxService(config.Current.WebhookUrl, config.Current.DbPath);
        _ = NavigateToAsync("/");

    }

    [RelayCommand]
    public async Task NavigateToAsync(string path)
    {
        IsLoading   = true;
        CurrentPath = path;
        StatusText  = $"Loading {path}…";
        try
        {
            var items = await Task.Run(() =>
                _discbox?.IsAvailable == true
                ? _discbox.List(path).ToArray()
                : Array.Empty<FileEntry>());
            Entries = new ObservableCollection<FileEntry>(items);
            UpdateBreadcrumbs(path);
            StatusText = $"{Entries.Count} item(s)";
        }
        catch (Exception ex) { StatusText = $"Error: {ex.Message}"; }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private async Task OpenEntryAsync(FileEntry? entry)
    {
        if (entry is null) return;
        if (entry.IsFolder) { await NavigateToAsync(entry.VirtualPath); return; }
        await DownloadEntryAsync(entry);
    }

    [RelayCommand]
    private async Task DownloadEntryAsync(FileEntry? entry)
    {
        if (entry is null || StorageProvider is null) return;

        if (entry.IsFolder)
        {
            await DownloadFolderAsync(entry);
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = $"Guardar {entry.Name}",
            SuggestedFileName = entry.Name,
        });

        if (file is null) return;

        var localPath = file.TryGetLocalPath();
        if (localPath is null) return;

        var mainWindow = (Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)
            ?.MainWindow;

        var progressVm = new UploadProgressViewModel { FileName = $"⬇ {entry.Name}" };
        var progressWindow = new Views.UploadProgressWindow { DataContext = progressVm };
        progressWindow.Show(mainWindow!);

        bool ok = false;
        string? erro = null;

        try
        {
            ok = await Task.Run(() =>
                _discbox?.Download(entry.VirtualPath, localPath,
                    (done, total, ci, cc) =>
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                            progressVm.Update(done, total, ci, cc));
                    }) ?? false);
            erro = _discbox?.LastError();
        }
        catch (Exception ex) { erro = ex.Message; }

        if (ok)
        {
            progressVm.Complete(entry.Name);
            StatusText = $"✓ {entry.Name} descarregado!";
        }
        else
        {
            progressVm.Error(erro ?? "desconhecido");
            StatusText = $"✗ Erro: {erro}";
        }

        await Task.Delay(2000);
        progressWindow.Close();
    }

    private async Task DownloadFolderAsync(FileEntry folderEntry)
    {
        if (StorageProvider is null || _discbox is null) return;

        // Ask the user for a destination folder
        var folders = await StorageProvider.OpenFolderPickerAsync(
            new Avalonia.Platform.Storage.FolderPickerOpenOptions
            {
                Title = $"Escolhe onde guardar a pasta '{folderEntry.Name}'",
                AllowMultiple = false,
            });

        if (folders is null || folders.Count == 0) return;
        var destRoot = folders[0].TryGetLocalPath();
        if (destRoot is null) return;

        var mainWindow = (Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)
            ?.MainWindow;

        // Collect all files recursively
        StatusText = $"A analisar pasta '{folderEntry.Name}'…";
        var allFiles = await Task.Run(() => CollectFilesRecursive(folderEntry.VirtualPath));

        if (allFiles.Count == 0)
        {
            StatusText = $"A pasta '{folderEntry.Name}' está vazia.";
            return;
        }

        var progressVm = new UploadProgressViewModel
        {
            FileName = $"⬇ {folderEntry.Name} ({allFiles.Count} ficheiros)"
        };
        var progressWindow = new Views.UploadProgressWindow { DataContext = progressVm };
        progressWindow.Show(mainWindow!);

        int downloaded = 0;
        int failed = 0;
        string? lastError = null;

        // The base path to strip from virtual paths so we recreate only the relative structure
        var basePath = folderEntry.VirtualPath.TrimEnd('/');

        foreach (var file in allFiles)
        {
            // Build local path preserving folder structure
            // e.g. virtualPath="/Photos/Vacation/img.jpg", basePath="/Photos"
            //   → relative = "/Vacation/img.jpg" → local = destRoot\Photos\Vacation\img.jpg
            var relativePath = file.VirtualPath[basePath.Length..].TrimStart('/');
            var localFilePath = System.IO.Path.Combine(destRoot, folderEntry.Name, relativePath.Replace('/', '\\'));

            // Create local directories as needed
            var localDir = System.IO.Path.GetDirectoryName(localFilePath);
            if (localDir is not null)
                System.IO.Directory.CreateDirectory(localDir);

            // Update progress UI with current file name
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                progressVm.FileName = $"⬇ [{downloaded + 1}/{allFiles.Count}] {file.Name}");

            bool ok = false;
            try
            {
                ok = await Task.Run(() =>
                    _discbox.Download(file.VirtualPath, localFilePath,
                        (done, total, ci, cc) =>
                        {
                            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                                progressVm.Update(done, total, ci, cc));
                        }));
                if (!ok) lastError = _discbox.LastError();
            }
            catch (Exception ex) { lastError = ex.Message; }

            if (ok)
                downloaded++;
            else
                failed++;
        }

        if (failed == 0)
        {
            progressVm.Complete(folderEntry.Name);
            StatusText = $"✓ Pasta '{folderEntry.Name}' descarregada — {downloaded} ficheiro(s)!";
        }
        else
        {
            progressVm.Error($"{failed} ficheiro(s) falharam");
            StatusText = $"⚠ {downloaded} OK, {failed} erro(s): {lastError}";
        }

        await Task.Delay(3000);
        progressWindow.Close();
    }

    /// <summary>
    /// Recursively collects all files (not folders) inside a virtual path.
    /// </summary>
    private List<FileEntry> CollectFilesRecursive(string virtualPath)
    {
        var result = new List<FileEntry>();
        if (_discbox is null) return result;

        var entries = _discbox.List(virtualPath);
        foreach (var entry in entries)
        {
            if (entry.IsFolder)
                result.AddRange(CollectFilesRecursive(entry.VirtualPath));
            else
                result.Add(entry);
        }
        return result;
    }

    [RelayCommand]
    private async Task GoUpAsync()
    {
        if (CurrentPath == "/") return;
        var parent = System.IO.Path.GetDirectoryName(CurrentPath)?.Replace('\\', '/') ?? "/";
        await NavigateToAsync(string.IsNullOrEmpty(parent) ? "/" : parent);
    }

    [RelayCommand] private async Task RefreshAsync() => await NavigateToAsync(CurrentPath);

    [RelayCommand]
    private async Task NewFolderAsync()
    {
        var mainWindow = (Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)
            ?.MainWindow;

        var dialog = new Views.NewFolderDialog();
        var result = await dialog.ShowDialog<string?>(mainWindow!);

        if (string.IsNullOrWhiteSpace(result)) return;

        var virtualPath = CurrentPath.TrimEnd('/') + "/" + result.Trim();

        bool ok = await Task.Run(() => _discbox?.Mkdir(virtualPath) ?? false);

        if (ok)
            StatusText = $"✓ Pasta '{result}' criada!";
        else
            StatusText = $"✗ Erro ao criar pasta: {_discbox?.LastError()}";

        await RefreshAsync();
    }

    [RelayCommand]
    private async Task UploadFileAsync()
    {
        if (StorageProvider is null) return;
        var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Seleciona ficheiros para upload",
            AllowMultiple = true
        });
        if (files is null || files.Count == 0) return;

        var mainWindow = (Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)
            ?.MainWindow;

        foreach (var file in files)
        {
            var localPath = file.TryGetLocalPath();
            if (localPath is null) continue;

            
            var virtualPath = CurrentPath.TrimEnd('/') + "/" + file.Name;
            System.Diagnostics.Debug.WriteLine($"[Upload2] localPath após replace: {localPath}");

            var progressVm = new UploadProgressViewModel { FileName = file.Name };
            var progressWindow = new Views.UploadProgressWindow { DataContext = progressVm };
            progressWindow.Show(mainWindow!);

            bool ok = false;
            string? erro = null;

            try
            {
                ok = await Task.Run(() =>
                    _discbox?.Upload(localPath, virtualPath,
                        (done, total, ci, cc) =>
                        {
                            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                                progressVm.Update(done, total, ci, cc));
                        }) ?? false);
                erro = _discbox?.LastError();
            }
            catch (Exception ex) { erro = ex.Message; }

            if (ok)
            {
                progressVm.Complete(file.Name);
                StatusText = $"✓ {file.Name} enviado!";
            }
            else
            {
                progressVm.Error(erro ?? "desconhecido");
                StatusText = $"✗ Erro: {erro}";
            }

            await Task.Delay(2000);
            progressWindow.Close();
        }

        await RefreshAsync();
    }

    [RelayCommand]
    private async Task DeleteEntryAsync(FileEntry? entry)
    {
        if (entry is null) return;

        try
        {
            var mainWindow = (Avalonia.Application.Current?.ApplicationLifetime
                as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)
                ?.MainWindow;

            var dialog = new Views.ConfirmDeleteDialog(entry.Name, entry.IsFolder);
            var confirmed = await dialog.ShowDialog<bool>(mainWindow!);

            if (!confirmed) return;

            var progressVm = new DeleteProgressViewModel();
            var cancelRequested = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            progressVm.CancelRequested += () => cancelRequested.TrySetResult(true);
            progressVm.Start(entry.Name);
            var progressWindow = new Views.DeleteProgressWindow { DataContext = progressVm };
            progressWindow.Show(mainWindow!);

            StatusText = $"A apagar {entry.Name}...";
            bool ok = false;
            string? erro = null;
            var deleteTask = DeleteOnDedicatedThreadAsync(
                entry.VirtualPath,
                progress => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    progressVm.Update(progress.Done, progress.Total, progress.CurrentPath)));
            var timeoutTask = Task.Delay(DeleteTimeout);
            var finishedTask = await Task.WhenAny(deleteTask, cancelRequested.Task, timeoutTask);

            if (finishedTask == cancelRequested.Task)
            {
                StatusText = "Cancelado";
                progressWindow.Close();
                _ = deleteTask.ContinueWith(async t =>
                {
                    if (!t.IsFaulted && t.Result.Ok)
                    {
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
                        {
                            await RefreshAsync();
                        });
                    }
                    else if (t.IsFaulted)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Delete] background error: {t.Exception}");
                    }
                }, TaskScheduler.Default);
                return;
            }

            if (finishedTask == timeoutTask)
            {
                erro = $"timeout apos {(int)DeleteTimeout.TotalHours}h";
                progressVm.Error(erro);
                StatusText = $"✗ Erro ao apagar: {erro}";
                _ = deleteTask.ContinueWith(async t =>
                {
                    if (!t.IsFaulted && t.Result.Ok)
                    {
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
                        {
                            await RefreshAsync();
                        });
                    }
                }, TaskScheduler.Default);
                await Task.Delay(2500);
                progressWindow.Close();
                return;
            }

            try
            {
                var result = await deleteTask;
                ok = result.Ok;
                erro = result.Error;
            }
            catch (Exception ex)
            {
                erro = ex.Message;
                ok = false;
            }

            if (ok)
            {
                progressVm.Complete(entry.Name);
                StatusText = $"✓ '{entry.Name}' apagado!";
            }
            else
            {
                progressVm.Error(erro ?? "desconhecido");
                StatusText = $"✗ Erro ao apagar: {erro}";
            }

            await Task.Delay(2000);
            progressWindow.Close();
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"✗ Erro (Delete): {ex.Message}";
        }
    }

    private Task<(bool Ok, string? Error)> DeleteOnDedicatedThreadAsync(
        string virtualPath,
        Action<DeleteHelperProgress>? onProgress)
    {
        var tcs = new TaskCompletionSource<(bool Ok, string? Error)>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var webhookUrl = _config.Current.WebhookUrl;
        var dbPath = string.IsNullOrWhiteSpace(_config.Current.DbPath)
            ? ConfigService.DefaultDbPath
            : _config.Current.DbPath;

        var worker = new Thread(() =>
        {
            string? requestPath = null;
            string? resultPath = null;
            string? progressPath = null;
            string? lastProgressJson = null;
            try
            {
                LogDelete($"START {virtualPath}");

                var exePath = Path.Combine(AppContext.BaseDirectory, "DiscBox.exe");
                if (!File.Exists(exePath) && !string.IsNullOrWhiteSpace(Environment.ProcessPath))
                    exePath = Environment.ProcessPath;

                var id = Guid.NewGuid().ToString("N");
                requestPath = Path.Combine(Path.GetTempPath(), $"discbox-delete-{id}.json");
                resultPath = Path.Combine(Path.GetTempPath(), $"discbox-delete-{id}.result.json");
                progressPath = Path.Combine(Path.GetTempPath(), $"discbox-delete-{id}.progress.json");

                var request = new DeleteHelperRequest
                {
                    WebhookUrl = webhookUrl,
                    DbPath = dbPath,
                    VirtualPath = virtualPath,
                    ResultPath = resultPath,
                    ProgressPath = progressPath
                };
                File.WriteAllText(requestPath, JsonSerializer.Serialize(request));

                var startInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                startInfo.ArgumentList.Add("--delete-helper");
                startInfo.ArgumentList.Add(requestPath);

                using var process = Process.Start(startInfo);
                if (process is null)
                {
                    LogDelete($"HELPER_START_FAIL {virtualPath}");
                    tcs.TrySetResult((false, "não foi possível iniciar o helper de delete"));
                    return;
                }

                LogDelete($"HELPER_PID {process.Id} {virtualPath}");
                var startedAt = DateTimeOffset.Now;
                while (!process.WaitForExit(500))
                {
                    ReportDeleteProgress(progressPath, onProgress, ref lastProgressJson);
                    if (DateTimeOffset.Now - startedAt > DeleteTimeout)
                    {
                        try { process.Kill(entireProcessTree: true); } catch { }
                        LogDelete($"HELPER_TIMEOUT {virtualPath}");
                        tcs.TrySetResult((false, $"timeout apos {(int)DeleteTimeout.TotalHours}h"));
                        return;
                    }
                }
                ReportDeleteProgress(progressPath, onProgress, ref lastProgressJson);

                DeleteHelperResult? result = null;
                if (File.Exists(resultPath))
                {
                    var json = File.ReadAllText(resultPath);
                    result = JsonSerializer.Deserialize<DeleteHelperResult>(json);
                }

                var ok = result?.Ok ?? process.ExitCode == 0;
                var error = result?.Error ?? (ok ? null : $"helper terminou com código {process.ExitCode}");
                LogDelete($"END {virtualPath} ok={ok} exit={process.ExitCode} error={error}");
                tcs.TrySetResult((ok, error));
            }
            catch (Exception ex)
            {
                LogDelete($"EXCEPTION {virtualPath} {ex}");
                tcs.TrySetResult((false, ex.Message));
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(requestPath))
                    try { File.Delete(requestPath); } catch { }
                if (!string.IsNullOrWhiteSpace(resultPath))
                    try { File.Delete(resultPath); } catch { }
                if (!string.IsNullOrWhiteSpace(progressPath))
                    try { File.Delete(progressPath); } catch { }
            }
        })
        {
            IsBackground = true,
            Name = "DiscBox delete worker"
        };

        worker.Start();
        return tcs.Task;
    }

    private static void ReportDeleteProgress(
        string? progressPath,
        Action<DeleteHelperProgress>? onProgress,
        ref string? lastProgressJson)
    {
        if (string.IsNullOrWhiteSpace(progressPath) || onProgress is null)
            return;

        try
        {
            if (!File.Exists(progressPath))
                return;

            var json = File.ReadAllText(progressPath);
            if (string.IsNullOrWhiteSpace(json) || json == lastProgressJson)
                return;

            var progress = JsonSerializer.Deserialize<DeleteHelperProgress>(json);
            if (progress is null)
                return;

            lastProgressJson = json;
            onProgress(progress);
        }
        catch
        {
            // The helper may be writing the file while we poll it.
        }
    }

    private static void LogDelete(string message)
    {
        try
        {
            Directory.CreateDirectory(ConfigService.AppDataDir);
            var line = $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}";
            File.AppendAllText(Path.Combine(ConfigService.AppDataDir, "delete.log"), line);
        }
        catch
        {
            // Best-effort diagnostics only.
        }
    }

    [RelayCommand]
    private async Task MigrateFromDisboxAsync()
    {
        StatusText = "A importar dados do Disbox...";
        var migration = new DisboxMigrationService(_config, _discbox!);
        var progress = new Progress<string>(msg => StatusText = msg);

        try
        {
            var (folders, files) = await migration.MigrateAsync(progress);
            StatusText = $"✓ Migração completa: {folders} pastas, {files} ficheiros importados!";
        }
        catch (Exception ex)
        {
            StatusText = $"✗ Erro na migração: {ex.Message}";
        }

        await RefreshAsync();
    }

    [RelayCommand]
    private void CutEntry(FileEntry? entry)
    {
        if (entry is null) return;
        ClipboardEntry = entry;
        ClipboardIsCut = true;
        StatusText = $"✂ '{entry.Name}' cortado — navega para o destino e cola";
    }

    [RelayCommand]
    private void CopyEntry(FileEntry? entry)
    {
        if (entry is null) return;
        ClipboardEntry = entry;
        ClipboardIsCut = false;
        StatusText = $"📋 '{entry.Name}' copiado — navega para o destino e cola";
    }

    [RelayCommand]
    private async Task PasteAsync()
    {
        if (ClipboardEntry is null) return;

        var destPath = CurrentPath.TrimEnd('/') + "/" + ClipboardEntry.Name;

        if (ClipboardIsCut)
        {
            bool ok = await Task.Run(() =>
                _discbox?.Rename(ClipboardEntry.VirtualPath, destPath) ?? false);
            StatusText = ok
                ? $"✓ '{ClipboardEntry.Name}' movido!"
                : $"✗ Erro ao mover: {_discbox?.LastError()}";
        }
        else
        {
            // Para copiar, usa importFile com os dados que temos
            bool ok = await Task.Run(() =>
                _discbox?.ImportFile(destPath, ClipboardEntry.Name,
                    ClipboardEntry.SizeBytes, "[]") ?? false);
            StatusText = ok
                ? $"✓ '{ClipboardEntry.Name}' colado!"
                : $"✗ Erro ao colar: {_discbox?.LastError()}";
        }

        if (ClipboardIsCut) ClipboardEntry = null;
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task RenameEntryAsync(FileEntry? entry)
    {
        if (entry is null) return;

        var mainWindow = (Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)
            ?.MainWindow;

        var dialog = new Views.RenameDialog(entry.Name);
        var newName = await dialog.ShowDialog<string?>(mainWindow!);

        if (string.IsNullOrWhiteSpace(newName) || newName == entry.Name) return;

        var parentPath = entry.VirtualPath.Contains('/')
            ? entry.VirtualPath[..entry.VirtualPath.LastIndexOf('/')]
            : "/";
        var newPath = (parentPath == "" ? "/" : parentPath) + "/" + newName;

        bool ok = await Task.Run(() =>
            _discbox?.Rename(entry.VirtualPath, newPath) ?? false);

        StatusText = ok
            ? $"✓ Renomeado para '{newName}'"
            : $"✗ Erro: {_discbox?.LastError()}";

        await RefreshAsync();
    }

    [RelayCommand]
    private async Task CopyPathAsync(FileEntry? entry)
    {
        if (entry is null) return;
        var clipboard = (Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)
            ?.MainWindow?.Clipboard;
        if (clipboard is not null)
            await clipboard.SetTextAsync(entry.VirtualPath);
        StatusText = $"📋 Caminho copiado: {entry.VirtualPath}";
    }

    [RelayCommand]
    private async Task ShowPropertiesAsync(FileEntry? entry)
    {
        if (entry is null) return;
        var mainWindow = (Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)
            ?.MainWindow;
        var dialog = new Views.PropertiesDialog(entry);
        await dialog.ShowDialog(mainWindow!);
    }

    private void UpdateBreadcrumbs(string path)
    {
        Breadcrumbs.Clear();
        Breadcrumbs.Add(new BreadcrumbItem { Label = "DiscBox", Path = "/" });
        if (path == "/") return;
        var built = string.Empty;
        foreach (var part in path.TrimStart('/').Split('/'))
        {
            built += "/" + part;
            Breadcrumbs.Add(new BreadcrumbItem { Label = part, Path = built });
        }
    }

    private static FileEntry[] GetPlaceholderEntries(string path) => path switch
    {
        "/" =>
        [
            new() { Id=1, Name="Photos",     VirtualPath="/Photos",     Type=EntryType.Folder, CreatedAt=DateTime.Now, ModifiedAt=DateTime.Now },
            new() { Id=2, Name="Videos",     VirtualPath="/Videos",     Type=EntryType.Folder, CreatedAt=DateTime.Now, ModifiedAt=DateTime.Now },
            new() { Id=3, Name="Documents",  VirtualPath="/Documents",  Type=EntryType.Folder, CreatedAt=DateTime.Now, ModifiedAt=DateTime.Now },
            new() { Id=4, Name="report.pdf", VirtualPath="/report.pdf", Type=EntryType.File,   SizeBytes=2_400_000, MimeType="application/pdf", CreatedAt=DateTime.Now, ModifiedAt=DateTime.Now },
        ],
        "/Photos" =>
        [
            new() { Id=10, Name="cat.jpg",     VirtualPath="/Photos/cat.jpg",     Type=EntryType.File, SizeBytes=512_000,   MimeType="image/jpeg", CreatedAt=DateTime.Now, ModifiedAt=DateTime.Now },
            new() { Id=11, Name="holiday.png", VirtualPath="/Photos/holiday.png", Type=EntryType.File, SizeBytes=3_100_000, MimeType="image/png",  CreatedAt=DateTime.Now, ModifiedAt=DateTime.Now },
        ],
        _ => []
    };

    [RelayCommand]
    private void AddToQuickAccess(FileEntry? entry)
    {
        if (entry is null || !entry.IsFolder) return;
        
        // Evita duplicados
        foreach (var qaf in QuickAccessFolders)
        {
            if (qaf.Path == entry.VirtualPath) return;
        }

        var folder = new ConfigService.QuickAccessFolder { Name = entry.Name, Path = entry.VirtualPath };
        QuickAccessFolders.Add(folder);
        _config.Current.QuickAccessFolders.Add(folder);
        _config.Save(_config.Current);
        StatusText = $"✓ '{entry.Name}' adicionado ao Quick Access!";
    }

    [RelayCommand]
    private void RemoveFromQuickAccess(ConfigService.QuickAccessFolder? folder)
    {
        if (folder is null) return;

        QuickAccessFolders.Remove(folder);
        _config.Current.QuickAccessFolders.Remove(folder);
        _config.Save(_config.Current);
        StatusText = $"✓ '{folder.Name}' removido do Quick Access!";
    }
}
