using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using DiscBox.Models;
using DiscBox.ViewModels;

namespace DiscBox.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnEntryDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (vm.SelectedEntry is not FileEntry entry) return;

        if (entry.IsFolder)
            vm.NavigateToCommand.Execute(entry.VirtualPath);
        else
            vm.DownloadEntryCommand.Execute(entry);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (DataContext is not MainViewModel vm) return;

        if (e.Key == Key.Back) { vm.GoUpCommand.Execute(null); e.Handled = true; }
        if (e.Key == Key.F5) { vm.RefreshCommand.Execute(null); e.Handled = true; }
        if (e.Key == Key.Delete && vm.SelectedEntry is not null)
        {
            vm.DeleteEntryCommand.Execute(vm.SelectedEntry);
            e.Handled = true;
        }
    }
}