using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscBox.Models;
using DiscBox.Services;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
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
    private DiscboxService? _discbox;
    private ConfigService.DriveConfig? _activeDrive;
    private static readonly TimeSpan DeleteTimeout = TimeSpan.FromHours(6);
    private readonly HashSet<FileEntry> _uiSelectedEntries = [];
    private bool _suppressSearchRefresh;
    private int _searchRevision;

    private static IStorageProvider? StorageProvider =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
        ?.MainWindow?.StorageProvider;

    [ObservableProperty] private string _currentPath = "/";
    [ObservableProperty] private ObservableCollection<FileEntry> _entries = [];
    [ObservableProperty] private FileEntry? _selectedEntry;
    [ObservableProperty] private bool _isLoading = false;
    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private string _driveName = "My DiscBox";
    [ObservableProperty] private string _searchText = string.Empty;

    // Clipboard interno
    [ObservableProperty] private bool _clipboardIsCut = false;
    public ObservableCollection<FileEntry> ClipboardEntries { get; } = [];
    public ObservableCollection<FileEntry> SelectedEntries { get; } = [];
    public bool HasClipboard => ClipboardEntries.Count > 0;
    public bool HasSelection => SelectedEntries.Count > 0 || SelectedEntry is not null;

    // BreadcrumbItem instead of tuple — tuples don't work with compiled Avalonia bindings
    public ObservableCollection<BreadcrumbItem> Breadcrumbs { get; } = [];

    public ObservableCollection<ConfigService.DriveConfig> Drives { get; } = [];

    public ObservableCollection<ConfigService.QuickAccessFolder> QuickAccessFolders { get; } = [];

    public MainViewModel(ConfigService config)
    {
        _config = config;
        RefreshDriveList();
        RefreshQuickAccess();
        ActivateDrive(config.Current.ActiveDrive, save: false);
        _ = NavigateToAsync("/");

    }

    private void RefreshDriveList()
    {
        Drives.Clear();
        foreach (var drive in _config.Current.Drives)
            Drives.Add(drive);
    }

    private void RefreshQuickAccess()
    {
        QuickAccessFolders.Clear();
        foreach (var folder in _config.Current.QuickAccessFolders)
            QuickAccessFolders.Add(folder);
    }

    public void SetSelectedEntries(IEnumerable<FileEntry> entries)
    {
        foreach (var previous in _uiSelectedEntries)
            previous.IsUiSelected = false;
        _uiSelectedEntries.Clear();

        var selected = entries
            .Where(e => e is not null)
            .GroupBy(e => e.VirtualPath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToArray();

        SelectedEntries.Clear();
        foreach (var entry in selected)
        {
            entry.IsUiSelected = true;
            _uiSelectedEntries.Add(entry);
            SelectedEntries.Add(entry);
        }

        SelectedEntry = SelectedEntries.LastOrDefault();
        OnPropertyChanged(nameof(HasSelection));
    }

    private void ClearSelectedEntries()
    {
        foreach (var previous in _uiSelectedEntries)
            previous.IsUiSelected = false;
        _uiSelectedEntries.Clear();

        SelectedEntries.Clear();
        SelectedEntry = null;
        OnPropertyChanged(nameof(HasSelection));
    }

    private IReadOnlyList<FileEntry> GetTargetEntries(FileEntry? entry)
    {
        if (SelectedEntries.Count > 0 &&
            (entry is null || SelectedEntries.Any(e =>
                string.Equals(e.VirtualPath, entry.VirtualPath, StringComparison.OrdinalIgnoreCase))))
        {
            return SelectedEntries.ToArray();
        }

        if (entry is not null)
            return [entry];

        return SelectedEntry is not null ? [SelectedEntry] : [];
    }

    private void SetClipboard(IEnumerable<FileEntry> entries, bool isCut)
    {
        ClipboardEntries.Clear();
        foreach (var entry in entries)
            ClipboardEntries.Add(entry);

        ClipboardIsCut = isCut;
        OnPropertyChanged(nameof(HasClipboard));
    }

    private void ClearClipboard()
    {
        ClipboardEntries.Clear();
        ClipboardIsCut = false;
        OnPropertyChanged(nameof(HasClipboard));
    }

    private void ClearSearchForNavigation()
    {
        if (string.IsNullOrEmpty(SearchText))
            return;

        _suppressSearchRefresh = true;
        SearchText = string.Empty;
        _suppressSearchRefresh = false;
        Interlocked.Increment(ref _searchRevision);
    }

    partial void OnSearchTextChanged(string value)
    {
        if (_suppressSearchRefresh)
            return;

        var revision = Interlocked.Increment(ref _searchRevision);
        _ = ApplySearchAsync(value, revision);
    }

    private void ActivateDrive(ConfigService.DriveConfig? drive, bool save, bool forceReconnect = false)
    {
        if (drive is null)
        {
            _activeDrive = null;
            DriveName = "DiscBox";
            _discbox?.Dispose();
            _discbox = null;
            return;
        }

        if (!forceReconnect && _activeDrive?.Id == drive.Id && _discbox?.IsAvailable == true)
            return;

        var currentPath = _activeDrive?.Id == drive.Id ? CurrentPath : "/";
        _discbox?.Dispose();
        _activeDrive = drive;
        DriveName = drive.Name;
        CurrentPath = currentPath;
        ClearSearchForNavigation();
        ClearSelectedEntries();
        ClearClipboard();

        _config.Current.ActiveDriveId = drive.Id;
        if (save)
            _config.Save();

        _discbox = new DiscboxService(drive.WebhookUrl, drive.DbPath, drive.Encrypt);
    }

    [RelayCommand]
    public async Task NavigateToAsync(string path)
    {
        ClearSearchForNavigation();
        IsLoading   = true;
        CurrentPath = path;
        StatusText  = $"Loading {path}…";
        try
        {
            var items = await Task.Run(() =>
                _discbox?.IsAvailable == true
                ? ListAndRepairPathConsistency(path)
                : Array.Empty<FileEntry>());
            Entries = new ObservableCollection<FileEntry>(items);
            ClearSelectedEntries();
            UpdateBreadcrumbs(path);
            StatusText = $"{Entries.Count} item(s)";
        }
        catch (Exception ex) { StatusText = $"Error: {ex.Message}"; }
        finally { IsLoading = false; }
    }

    private FileEntry[] ListAndRepairPathConsistency(string path)
    {
        if (_discbox is null)
            return [];

        var items = _discbox.List(path).ToArray();
        var repaired = false;

        foreach (var item in items)
        {
            var expectedPath = CombineVirtualPath(path, item.Name);
            if (string.Equals(item.VirtualPath, expectedPath, StringComparison.OrdinalIgnoreCase))
                continue;

            if (_discbox.Rename(item.VirtualPath, expectedPath))
                repaired = true;
        }

        return repaired ? _discbox.List(path).ToArray() : items;
    }

    private async Task ApplySearchAsync(string value, int revision)
    {
        var query = value.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            await NavigateToAsync(CurrentPath);
            return;
        }

        IsLoading = true;
        StatusText = $"A pesquisar \"{query}\"...";

        try
        {
            var items = await Task.Run(() =>
                _discbox?.IsAvailable == true
                    ? CollectEntriesRecursive("/")
                        .Where(e =>
                            e.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                            e.VirtualPath.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                        .ToArray()
                    : Array.Empty<FileEntry>());

            if (revision != _searchRevision)
                return;

            Entries = new ObservableCollection<FileEntry>(items);
            ClearSelectedEntries();
            UpdateBreadcrumbs(CurrentPath);
            StatusText = $"{Entries.Count} resultado(s) para \"{query}\"";
        }
        catch (Exception ex)
        {
            if (revision == _searchRevision)
                StatusText = $"Erro na pesquisa: {ex.Message}";
        }
        finally
        {
            if (revision == _searchRevision)
                IsLoading = false;
        }
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

    private List<FileEntry> CollectEntriesRecursive(string virtualPath)
    {
        var result = new List<FileEntry>();
        if (_discbox is null) return result;

        var entries = _discbox.List(virtualPath);
        foreach (var entry in entries)
        {
            result.Add(entry);
            if (entry.IsFolder)
                result.AddRange(CollectEntriesRecursive(entry.VirtualPath));
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

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var revision = Interlocked.Increment(ref _searchRevision);
            await ApplySearchAsync(SearchText, revision);
            return;
        }

        await NavigateToAsync(CurrentPath);
    }

    [RelayCommand]
    private async Task SwitchDriveAsync(ConfigService.DriveConfig? drive)
    {
        if (drive is null) return;
        ActivateDrive(drive, save: true);
        await NavigateToAsync("/");
    }

    [RelayCommand]
    private async Task AddDriveAsync()
    {
        var mainWindow = (Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)
            ?.MainWindow;

        var dialog = new Views.DriveDialog();
        var result = await dialog.ShowDialog<Views.DriveDialogResult?>(mainWindow!);
        if (result is null) return;

        if (_config.Current.Drives.Any(d =>
                string.Equals(d.WebhookUrl, result.WebhookUrl, StringComparison.OrdinalIgnoreCase)))
        {
            StatusText = "Esse webhook ja existe noutra drive.";
            return;
        }

        StatusText = "A validar webhook...";
        var valid = await ValidateWebhookAsync(result.WebhookUrl);
        if (!valid)
        {
            StatusText = "Webhook invalido. Confirma o URL e tenta outra vez.";
            return;
        }

        var drive = ConfigService.CreateDrive(result.Name, result.WebhookUrl, result.Encrypt);
        _config.Current.Drives.Add(drive);
        _config.Current.ActiveDriveId = drive.Id;
        _config.Save();

        RefreshDriveList();
        ActivateDrive(drive, save: false);
        await NavigateToAsync("/");
        StatusText = $"Drive '{drive.Name}' adicionada.";
    }

    [RelayCommand]
    private async Task RenameDriveAsync(ConfigService.DriveConfig? drive)
    {
        if (drive is null) return;

        var mainWindow = (Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)
            ?.MainWindow;

        var dialog = new Views.RenameDialog(drive.Name);
        var newName = await dialog.ShowDialog<string?>(mainWindow!);
        if (string.IsNullOrWhiteSpace(newName) || newName.Trim() == drive.Name) return;

        var configDrive = _config.Current.Drives.FirstOrDefault(d => d.Id == drive.Id);
        if (configDrive is null) return;

        configDrive.Name = newName.Trim();
        _config.Save();
        RefreshDriveList();

        if (_activeDrive?.Id == configDrive.Id)
        {
            _activeDrive = configDrive;
            DriveName = configDrive.Name;
        }

        StatusText = $"Drive renomeada para '{configDrive.Name}'.";
    }

    [RelayCommand]
    private void ToggleDriveEncryption(ConfigService.DriveConfig? drive)
    {
        if (drive is null) return;

        var configDrive = _config.Current.Drives.FirstOrDefault(d => d.Id == drive.Id);
        if (configDrive is null) return;

        configDrive.Encrypt = !configDrive.Encrypt;
        _config.Save();
        RefreshDriveList();

        if (_activeDrive?.Id == configDrive.Id)
        {
            ActivateDrive(configDrive, save: false, forceReconnect: true);
        }

        StatusText = configDrive.Encrypt
            ? $"Encriptação ativa para novos uploads em '{configDrive.Name}'."
            : $"Encriptação desligada para novos uploads em '{configDrive.Name}'.";
    }

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
        if (ok)
            await BackupActiveDriveAsync($"Pasta '{result}' criada.");
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

        var uploadedAny = false;
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
                uploadedAny = true;
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
        if (uploadedAny)
            await BackupActiveDriveAsync("Upload concluido.");
    }

    [RelayCommand]
    private async Task DeleteEntryAsync(FileEntry? entry)
    {
        var entries = GetTargetEntries(entry);
        if (entries.Count == 0) return;
        if (entries.Count > 1)
        {
            await DeleteEntriesAsync(entries);
            return;
        }

        entry = entries[0];

        try
        {
            var mainWindow = (Avalonia.Application.Current?.ApplicationLifetime
                as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)
                ?.MainWindow;

            var dialog = new Views.ConfirmDeleteDialog(
                entries.Count == 1 ? entries[0].Name : $"{entries.Count} itens selecionados",
                entries.Count != 1 || entries[0].IsFolder,
                entries.Count);
            var confirmed = await dialog.ShowDialog<bool>(mainWindow!);

            if (!confirmed) return;

            var progressVm = new DeleteProgressViewModel();
            var cancelRequested = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            progressVm.CancelRequested += () => cancelRequested.TrySetResult(true);
            progressVm.Start(entries.Count == 1 ? entries[0].Name : $"{entries.Count} itens");
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
                            await BackupActiveDriveAsync($"'{entry.Name}' apagado.");
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
                            await BackupActiveDriveAsync($"'{entry.Name}' apagado.");
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
            if (ok)
                await BackupActiveDriveAsync($"'{entry.Name}' apagado.");
        }
        catch (Exception ex)
        {
            StatusText = $"✗ Erro (Delete): {ex.Message}";
        }
    }

    private async Task DeleteEntriesAsync(IReadOnlyList<FileEntry> entries)
    {
        try
        {
            var mainWindow = (Avalonia.Application.Current?.ApplicationLifetime
                as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)
                ?.MainWindow;

            var dialog = new Views.ConfirmDeleteDialog(
                $"{entries.Count} itens selecionados",
                isFolder: true,
                itemCount: entries.Count);
            var confirmed = await dialog.ShowDialog<bool>(mainWindow!);

            if (!confirmed) return;

            var progressVm = new DeleteProgressViewModel();
            var cancelRequested = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            progressVm.CancelRequested += () => cancelRequested.TrySetResult(true);
            progressVm.Start($"{entries.Count} itens");
            var progressWindow = new Views.DeleteProgressWindow { DataContext = progressVm };
            progressWindow.Show(mainWindow!);

            var deleted = 0;
            var failed = 0;
            string? erro = null;

            for (var i = 0; i < entries.Count; i++)
            {
                var current = entries[i];
                progressVm.FileName = $"[{i + 1}/{entries.Count}] {current.Name}";
                StatusText = $"A apagar {current.Name}...";

                var deleteTask = DeleteOnDedicatedThreadAsync(
                    current.VirtualPath,
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
                                await BackupActiveDriveAsync($"'{current.Name}' apagado.");
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
                    StatusText = $"Erro ao apagar: {erro}";
                    _ = deleteTask.ContinueWith(async t =>
                    {
                        if (!t.IsFaulted && t.Result.Ok)
                        {
                            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
                            {
                                await RefreshAsync();
                                await BackupActiveDriveAsync($"'{current.Name}' apagado.");
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
                    if (result.Ok)
                    {
                        deleted++;
                    }
                    else
                    {
                        failed++;
                        erro = result.Error;
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    erro = ex.Message;
                }
            }

            if (failed == 0)
            {
                progressVm.Complete($"{deleted} itens");
                StatusText = $"{deleted} item(ns) apagado(s)!";
            }
            else
            {
                progressVm.Error(erro ?? "desconhecido");
                StatusText = $"{deleted} OK, {failed} erro(s) ao apagar: {erro}";
            }

            await Task.Delay(2000);
            progressWindow.Close();
            await RefreshAsync();
            if (deleted > 0)
                await BackupActiveDriveAsync($"{deleted} itens apagados.");
        }
        catch (Exception ex)
        {
            StatusText = $"Erro (Delete): {ex.Message}";
        }
    }

    private Task<(bool Ok, string? Error)> DeleteOnDedicatedThreadAsync(
        string virtualPath,
        Action<DeleteHelperProgress>? onProgress)
    {
        var tcs = new TaskCompletionSource<(bool Ok, string? Error)>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var drive = _activeDrive ?? _config.Current.ActiveDrive;
        if (drive is null)
        {
            tcs.TrySetResult((false, "nenhuma drive ativa"));
            return tcs.Task;
        }

        var webhookUrl = drive.WebhookUrl;
        var dbPath = string.IsNullOrWhiteSpace(drive.DbPath)
            ? ConfigService.DefaultDbPath
            : drive.DbPath;
        var encrypt = drive.Encrypt;

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
                    Encrypt = encrypt,
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

    private static async Task<bool> ValidateWebhookAsync(string url)
    {
        if (!ConfigService.IsValidWebhookUrl(url))
            return false;

        try
        {
            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(10);
            var resp = await http.GetAsync(url);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private async Task BackupActiveDriveAsync(string successText)
    {
        var drive = _activeDrive ?? _config.Current.ActiveDrive;
        if (drive is null || _discbox is null || !_discbox.IsAvailable)
            return;

        try
        {
            StatusText = "A atualizar backup remoto da drive...";
            var messageId = await DiscboxBackupService.UploadAsync(_discbox, drive);

            var configDrive = _config.Current.Drives.FirstOrDefault(d => d.Id == drive.Id);
            if (configDrive is not null)
                configDrive.BackupMessageId = messageId;
            drive.BackupMessageId = messageId;

            _config.Save();
            RefreshDriveList();
            StatusText = $"{successText} Backup remoto atualizado.";
        }
        catch (Exception ex)
        {
            StatusText = $"{successText} Backup remoto falhou: {ex.Message}";
        }
    }

    private async Task<bool> RestoreActiveDriveBackupAsync()
    {
        var drive = _activeDrive ?? _config.Current.ActiveDrive;
        if (drive is null)
            return false;

        RemoteBackupDownload? backup = null;
        try
        {
            backup = await DiscboxBackupService.TryDownloadAsync(drive);
            if (backup is null)
                return false;

            _discbox?.Dispose();
            _discbox = null;

            var dbPath = string.IsNullOrWhiteSpace(drive.DbPath)
                ? ConfigService.DbPathForDrive(drive.Id)
                : drive.DbPath;
            var dbDir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrWhiteSpace(dbDir))
                Directory.CreateDirectory(dbDir);

            TryDeleteFile(dbPath + "-wal");
            TryDeleteFile(dbPath + "-shm");
            File.Copy(backup.LocalPath, dbPath, overwrite: true);

            drive.DbPath = dbPath;
            drive.BackupMessageId = backup.MessageId;
            var configDrive = _config.Current.Drives.FirstOrDefault(d => d.Id == drive.Id);
            if (configDrive is not null)
            {
                configDrive.DbPath = dbPath;
                configDrive.BackupMessageId = backup.MessageId;
            }

            _config.Save();
            RefreshDriveList();
            ActivateDrive(configDrive ?? drive, save: false, forceReconnect: true);
            return true;
        }
        finally
        {
            if (backup is not null)
                TryDeleteFile(backup.LocalPath);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    [RelayCommand]
    private async Task MigrateFromDisboxAsync()
    {
        var restoredBackup = false;
        try
        {
            StatusText = "A sincronizar backup DiscBox remoto...";
            restoredBackup = await RestoreActiveDriveBackupAsync();
            if (restoredBackup)
            {
                await RefreshAsync();
                StatusText = "Backup DiscBox restaurado. A importar dados do Disbox...";
            }
            else
            {
                StatusText = "Sem backup DiscBox remoto. A importar dados do Disbox...";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Backup DiscBox falhou: {ex.Message}. A importar dados do Disbox...";
        }

        var migration = new DisboxMigrationService(_config, _discbox!);
        var progress = new Progress<string>(msg => StatusText = msg);

        try
        {
            var (folders, files) = await migration.MigrateAsync(progress);
            StatusText = restoredBackup
                ? $"✓ Sync completa: backup restaurado, {folders} pastas, {files} ficheiros Disbox importados!"
                : $"✓ Migração completa: {folders} pastas, {files} ficheiros importados!";
        }
        catch (Exception ex)
        {
            StatusText = restoredBackup
                ? $"✓ Backup DiscBox restaurado. Disbox falhou: {ex.Message}"
                : $"✗ Erro na migração: {ex.Message}";
        }

        await RefreshAsync();
        await BackupActiveDriveAsync("Sincronizacao concluida.");
    }

    [RelayCommand]
    private void CutEntry(FileEntry? entry)
    {
        var entries = GetTargetEntries(entry);
        if (entries.Count == 0) return;
        SetClipboard(entries, isCut: true);
        ClipboardIsCut = true;
        StatusText = entries.Count == 1
            ? $"'{entries[0].Name}' cortado - navega para o destino e cola"
            : $"{entries.Count} itens cortados - navega para o destino e cola";
    }

    [RelayCommand]
    private void CopyEntry(FileEntry? entry)
    {
        var entries = GetTargetEntries(entry);
        if (entries.Count == 0) return;
        SetClipboard(entries, isCut: false);
        ClipboardIsCut = false;
        StatusText = entries.Count == 1
            ? $"'{entries[0].Name}' copiado - navega para o destino e cola"
            : $"{entries.Count} itens copiados - navega para o destino e cola";
    }

    [RelayCommand]
    private async Task PasteAsync()
    {
        if (ClipboardEntries.Count == 0 || _discbox is null) return;

        var items = ClipboardEntries.ToArray();
        var wasCut = ClipboardIsCut;
        var movedOrCopied = 0;
        var failed = 0;
        var skippedSameDestination = 0;
        string? lastError = null;

        var mainWindow = (Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)
            ?.MainWindow;
        var progressVm = new TransferProgressViewModel();
        var progressWindow = new Views.TransferProgressWindow { DataContext = progressVm };
        progressVm.Start(
            wasCut ? "A mover item(ns)..." : "A copiar item(ns)...",
            CountTransferItems(items, wasCut),
            wasCut);
        progressWindow.Show(mainWindow!);

        foreach (var item in items)
        {
            if (progressVm.Cancelled)
                break;

            var directDestPath = CombineVirtualPath(CurrentPath, item.Name);
            if (wasCut && string.Equals(item.VirtualPath, directDestPath, StringComparison.OrdinalIgnoreCase))
            {
                skippedSameDestination++;
                lastError = "destino igual a origem";
                continue;
            }

            var destPath = GetAvailableDestinationPath(CurrentPath, item.Name);

            if (item.IsFolder && IsSameOrChildPath(destPath, item.VirtualPath))
            {
                failed++;
                lastError = "nao podes colar uma pasta dentro dela propria";
                continue;
            }

            StatusText = wasCut
                ? $"A mover {item.Name}..."
                : $"A copiar {item.Name}...";

            var ok = wasCut
                ? await MoveEntryToAsync(item, destPath, progressVm)
                : await CopyEntryToAsync(item, destPath, progressVm);

            if (ok)
            {
                movedOrCopied++;
            }
            else
            {
                failed++;
                lastError = _discbox.LastError();
            }
        }

        if (wasCut && movedOrCopied > 0)
            ClearClipboard();

        await RefreshAsync();
        var operationText = BuildPasteResultText(movedOrCopied, failed, skippedSameDestination, lastError);

        if (movedOrCopied > 0)
        {
            progressVm.Complete(operationText);
            await BackupActiveDriveAsync(operationText);
        }
        else
        {
            if (failed > 0)
                progressVm.Error(operationText);
            else
                progressVm.Complete(operationText);
            StatusText = operationText;
        }

        await Task.Delay(progressVm.Cancelled ? 800 : 1500);
        progressWindow.Close();
    }

    private async Task<bool> MoveEntryToAsync(
        FileEntry entry,
        string destPath,
        TransferProgressViewModel progressVm)
    {
        if (_discbox is null || progressVm.Cancelled)
            return false;

        progressVm.StartItem(entry.Name);

        var movePath = destPath;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var moved = await Task.Run(() => _discbox.Rename(entry.VirtualPath, movePath));
            if (moved)
            {
                progressVm.CompleteItem();
                return true;
            }

            var error = _discbox.LastError();
            if (!IsPathAlreadyExistsError(error))
                return false;

            movePath = MakeCopyPath(destPath, attempt + 2);
        }

        return false;
    }

    private async Task<bool> CopyEntryToAsync(
        FileEntry entry,
        string destPath,
        TransferProgressViewModel progressVm)
    {
        if (_discbox is null || progressVm.Cancelled)
            return false;

        progressVm.StartItem(entry.Name);

        if (entry.IsFolder)
        {
            var children = await Task.Run(() => _discbox.List(entry.VirtualPath).ToArray());
            var finalDestPath = await CreateFolderWithRetriesAsync(destPath);
            if (finalDestPath is null)
                return false;

            progressVm.CompleteItem();

            foreach (var child in children)
            {
                if (progressVm.Cancelled)
                    return true;

                var childDest = GetAvailableDestinationPath(finalDestPath, child.Name);
                var childCopied = await CopyEntryToAsync(child, childDest, progressVm);
                if (!childCopied)
                    return false;
            }

            return true;
        }

        var tempPath = Path.Combine(
            Path.GetTempPath(),
            $"discbox-copy-{Guid.NewGuid():N}-{SanitizeTempFileName(entry.Name)}");

        try
        {
            var downloaded = await Task.Run(() =>
                _discbox.Download(entry.VirtualPath, tempPath,
                    (done, total, ci, cc) =>
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                            progressVm.UpdateBytes(done, total, "A descarregar"));
                    }));
            if (!downloaded)
                return false;

            var uploadPath = destPath;
            for (var attempt = 0; attempt < 100; attempt++)
            {
                var uploaded = await Task.Run(() =>
                    _discbox.Upload(tempPath, uploadPath,
                        (done, total, ci, cc) =>
                        {
                            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                                progressVm.UpdateBytes(done, total, "A enviar"));
                        }));
                if (uploaded)
                {
                    progressVm.CompleteItem();
                    return true;
                }

                var error = _discbox.LastError();
                if (!IsPathAlreadyExistsError(error))
                    return false;

                uploadPath = MakeCopyPath(destPath, attempt + 2);
            }

            return false;
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    private async Task<string?> CreateFolderWithRetriesAsync(string destPath)
    {
        if (_discbox is null)
            return null;

        var folderPath = destPath;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var created = await Task.Run(() => _discbox.Mkdir(folderPath));
            if (created)
                return folderPath;

            var error = _discbox.LastError();
            if (!IsPathAlreadyExistsError(error))
                return null;

            folderPath = MakeCopyPath(destPath, attempt + 2);
        }

        return null;
    }

    private string BuildPasteResultText(
        int changed,
        int failed,
        int skippedSameDestination,
        string? lastError)
    {
        if (changed == 0 && failed == 0 && skippedSameDestination > 0)
            return "Nada movido: escolhe uma pasta diferente da origem.";

        if (changed == 0 && failed == 0)
            return "Operacao cancelada.";

        if (failed == 0 && skippedSameDestination == 0)
            return $"{changed} item(ns) colado(s).";

        var details = lastError;
        if (skippedSameDestination > 0)
            details = string.IsNullOrWhiteSpace(details)
                ? $"{skippedSameDestination} item(ns) ja estavam nesse destino"
                : $"{details}; {skippedSameDestination} item(ns) ja estavam nesse destino";

        return $"{changed} OK, {failed} erro(s): {details}";
    }

    private int CountTransferItems(IReadOnlyList<FileEntry> items, bool isCut)
    {
        if (isCut)
            return items.Count;

        return items.Sum(CountCopyItems);
    }

    private int CountCopyItems(FileEntry entry)
    {
        if (_discbox is null || !entry.IsFolder)
            return 1;

        try
        {
            return 1 + _discbox.List(entry.VirtualPath).Sum(CountCopyItems);
        }
        catch
        {
            return 1;
        }
    }

    private string GetAvailableDestinationPath(string folderPath, string name)
    {
        if (_discbox is null)
            return CombineVirtualPath(folderPath, name);

        var existingNames = _discbox.List(folderPath)
            .Select(e => e.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!existingNames.Contains(name))
            return CombineVirtualPath(folderPath, name);

        var extension = Path.GetExtension(name);
        var stem = string.IsNullOrEmpty(extension)
            ? name
            : name[..^extension.Length];

        for (var i = 2; i < 10_000; i++)
        {
            var candidate = string.IsNullOrEmpty(extension)
                ? $"{stem} - Copy {i}"
                : $"{stem} - Copy {i}{extension}";
            if (!existingNames.Contains(candidate))
                return CombineVirtualPath(folderPath, candidate);
        }

        return CombineVirtualPath(folderPath, $"{name} - Copy {Guid.NewGuid():N}");
    }

    private static bool IsPathAlreadyExistsError(string? error) =>
        !string.IsNullOrWhiteSpace(error) &&
        error.Contains("path already exists", StringComparison.OrdinalIgnoreCase);

    private static string MakeCopyPath(string virtualPath, int copyNumber)
    {
        var normalized = NormalizeVirtualPath(virtualPath);
        var slash = normalized.LastIndexOf('/');
        var folder = slash <= 0 ? "/" : normalized[..slash];
        var name = slash >= 0 && slash < normalized.Length - 1
            ? normalized[(slash + 1)..]
            : normalized.TrimStart('/');

        var extension = Path.GetExtension(name);
        var stem = string.IsNullOrEmpty(extension)
            ? name
            : name[..^extension.Length];
        var copyName = string.IsNullOrEmpty(extension)
            ? $"{stem} - Copy {copyNumber}"
            : $"{stem} - Copy {copyNumber}{extension}";

        return CombineVirtualPath(folder, copyName);
    }

    private static bool IsSameOrChildPath(string path, string possibleParent)
    {
        var normalizedPath = NormalizeVirtualPath(path);
        var normalizedParent = NormalizeVirtualPath(possibleParent);
        return normalizedPath.Equals(normalizedParent, StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.StartsWith(normalizedParent.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string CombineVirtualPath(string folderPath, string name)
    {
        var folder = string.IsNullOrWhiteSpace(folderPath) ? "/" : folderPath.TrimEnd('/');
        return folder == "/" ? "/" + name : folder + "/" + name;
    }

    private static string NormalizeVirtualPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "/";

        var normalized = path.Replace('\\', '/').Trim();
        if (!normalized.StartsWith('/'))
            normalized = "/" + normalized;
        return normalized.Length > 1 ? normalized.TrimEnd('/') : normalized;
    }

    private static string SanitizeTempFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var safe = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "file" : safe;
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
        if (ok)
            await BackupActiveDriveAsync($"Renomeado para '{newName}'.");
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
