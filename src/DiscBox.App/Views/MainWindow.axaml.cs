using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using DiscBox.Models;
using DiscBox.ViewModels;
using System.Collections;
using System.ComponentModel;
using System.Linq;

namespace DiscBox.Views;

public partial class MainWindow : Window
{
    private bool _isDragSelecting;
    private bool _dragSelectionActive;
    private bool _isClearingSelection;
    private Point _dragStart;
    private MainViewModel? _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        AttachDragSelectionHandlers();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        _viewModel = DataContext as MainViewModel;
        if (_viewModel is not null)
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Entries))
            Avalonia.Threading.Dispatcher.UIThread.Post(ClearListSelection);
    }

    private void AttachDragSelectionHandlers()
    {
        var host = this.FindControl<Control>("FileListHost");
        if (host is null)
            return;

        host.AddHandler(PointerPressedEvent, OnFileListPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        host.AddHandler(PointerMovedEvent, OnFileListPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        host.AddHandler(PointerReleasedEvent, OnFileListPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void OnEntryDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var entry = FindDataContext<FileEntry>(e.Source) ?? vm.SelectedEntry;
        if (entry is null) return;

        if (entry.IsFolder)
            vm.NavigateToCommand.Execute(entry.VirtualPath);
        else
            vm.DownloadEntryCommand.Execute(entry);
    }

    private void OnFileSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_dragSelectionActive || _isClearingSelection)
            return;

        if (DataContext is not MainViewModel vm || sender is not ListBox list)
            return;

        vm.SetSelectedEntries(list.SelectedItems?.OfType<FileEntry>() ?? Enumerable.Empty<FileEntry>());
    }

    private void OnFileListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control host)
            return;

        var point = e.GetCurrentPoint(host);
        if (!point.Properties.IsLeftButtonPressed)
            return;

        if (FindDataContext<FileEntry>(e.Source) is null)
            ClearListSelection();

        _isDragSelecting = true;
        _dragSelectionActive = false;
        _dragStart = point.Position;
        e.Pointer.Capture(host);
    }

    private void OnFileListPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDragSelecting || sender is not Control host)
            return;

        var position = e.GetPosition(host);
        var dx = position.X - _dragStart.X;
        var dy = position.Y - _dragStart.Y;
        if (!_dragSelectionActive && (dx * dx + dy * dy) < 16)
            return;

        _dragSelectionActive = true;
        var selectionRect = BuildSelectionRect(_dragStart, position);
        UpdateSelectionRectangle(selectionRect);
        SelectEntriesInside(selectionRect);
        e.Handled = true;
    }

    private void OnFileListPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDragSelecting)
            return;

        _isDragSelecting = false;
        e.Pointer.Capture(null);

        var rectangle = this.FindControl<Border>("SelectionRectangle");
        if (rectangle is not null)
            rectangle.IsVisible = false;

        if (_dragSelectionActive)
            e.Handled = true;

        _dragSelectionActive = false;
    }

    private void UpdateSelectionRectangle(Rect selectionRect)
    {
        var rectangle = this.FindControl<Border>("SelectionRectangle");
        if (rectangle is null)
            return;

        rectangle.IsVisible = true;
        Canvas.SetLeft(rectangle, selectionRect.X);
        Canvas.SetTop(rectangle, selectionRect.Y);
        rectangle.Width = selectionRect.Width;
        rectangle.Height = selectionRect.Height;
    }

    private void SelectEntriesInside(Rect selectionRect)
    {
        var list = this.FindControl<ListBox>("FileList");
        var host = this.FindControl<Control>("FileListHost");
        if (list?.SelectedItems is null || host is null)
            return;

        var selected = ((list.ItemsSource as IEnumerable)?.OfType<FileEntry>() ?? Enumerable.Empty<FileEntry>())
            .Where(entry =>
            {
                if (list.ContainerFromItem(entry) is not Control container)
                    return false;

                var topLeft = container.TranslatePoint(new Point(0, 0), host);
                if (topLeft is null)
                    return false;

                var itemRect = new Rect(topLeft.Value, container.Bounds.Size);
                return selectionRect.Intersects(itemRect);
            })
            .ToArray();

        list.SelectedItems.Clear();
        foreach (var entry in selected)
            list.SelectedItems.Add(entry);

        if (DataContext is MainViewModel vm)
            vm.SetSelectedEntries(selected);
    }

    private void ClearListSelection()
    {
        var list = this.FindControl<ListBox>("FileList");
        _isClearingSelection = true;
        try
        {
            list?.Selection.Clear();
            list?.SelectedItems?.Clear();
        }
        finally
        {
            _isClearingSelection = false;
        }

        if (DataContext is MainViewModel vm)
            vm.SetSelectedEntries(Enumerable.Empty<FileEntry>());
    }

    private static Rect BuildSelectionRect(Point start, Point end)
    {
        var x = Math.Min(start.X, end.X);
        var y = Math.Min(start.Y, end.Y);
        return new Rect(x, y, Math.Abs(end.X - start.X), Math.Abs(end.Y - start.Y));
    }

    private static T? FindDataContext<T>(object? source) where T : class
    {
        var visual = source as Visual;
        while (visual is not null)
        {
            if (visual is StyledElement styled && styled.DataContext is T item)
                return item;
            visual = visual.GetVisualParent();
        }

        return null;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (DataContext is not MainViewModel vm) return;

        if (e.Key == Key.Back) { vm.GoUpCommand.Execute(null); e.Handled = true; }
        if (e.Key == Key.F5) { vm.RefreshCommand.Execute(null); e.Handled = true; }
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.C && vm.HasSelection)
        {
            vm.CopyEntryCommand.Execute(vm.SelectedEntry);
            e.Handled = true;
        }
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.X && vm.HasSelection)
        {
            vm.CutEntryCommand.Execute(vm.SelectedEntry);
            e.Handled = true;
        }
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.V && vm.HasClipboard)
        {
            vm.PasteCommand.Execute(null);
            e.Handled = true;
        }
        if (e.Key == Key.Delete && vm.HasSelection)
        {
            vm.DeleteEntryCommand.Execute(vm.SelectedEntry);
            e.Handled = true;
        }
    }
}
