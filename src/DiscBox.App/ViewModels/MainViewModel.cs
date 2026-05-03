using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscBox.Models;
using DiscBox.Services;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Platform.Storage;
using Avalonia.Controls.ApplicationLifetimes;

namespace DiscBox.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly ConfigService _config;
    private readonly DiscboxService? _discbox;

    private static IStorageProvider? StorageProvider =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
        ?.MainWindow?.StorageProvider;

    [ObservableProperty] private string _currentPath = "/";
    [ObservableProperty] private ObservableCollection<FileEntry> _entries = [];
    [ObservableProperty] private FileEntry? _selectedEntry;
    [ObservableProperty] private bool _isLoading = false;
    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private string _driveName = "My DiscBox";

    // BreadcrumbItem instead of tuple — tuples don't work with compiled Avalonia bindings
    public ObservableCollection<BreadcrumbItem> Breadcrumbs { get; } = [];

    public MainViewModel(ConfigService config)
    {
        _config   = config;
        DriveName = config.Current.DriveName;
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
        var result = await dialog.ShowDialog<string?>(mainWindow);

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

        var mainWindow = (Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)
            ?.MainWindow;

        var dialog = new Views.ConfirmDeleteDialog(entry.Name, entry.IsFolder);
        var confirmed = await dialog.ShowDialog<bool>(mainWindow);

        if (!confirmed) return;

        StatusText = $"A apagar {entry.Name}...";
        bool ok = await Task.Run(() => _discbox?.Delete(entry.VirtualPath) ?? false);

        if (ok)
            StatusText = $"✓ '{entry.Name}' apagado!";
        else
            StatusText = $"✗ Erro ao apagar: {_discbox?.LastError()}";

        await RefreshAsync();
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
}
