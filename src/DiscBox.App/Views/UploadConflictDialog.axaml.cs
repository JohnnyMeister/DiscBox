using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using System.Collections.Generic;
using System.Linq;

namespace DiscBox.Views;

public enum UploadConflictChoice
{
    Cancel,
    Skip,
    Replace
}

public partial class UploadConflictDialog : Window
{
    public UploadConflictDialog()
    {
        InitializeComponent();
    }

    public UploadConflictDialog(IReadOnlyCollection<string> conflicts)
    {
        InitializeComponent();

        var count = conflicts.Count;
        this.FindControl<TextBlock>("MessageText")!.Text =
            count == 1
                ? "One item has the same name as an item in this folder. Choose what DiscBox should do."
                : $"{count} items have the same names as items in this folder. Choose what DiscBox should do.";

        var preview = conflicts
            .Take(4)
            .ToArray();
        var suffix = count > preview.Length
            ? $" and {count - preview.Length} more"
            : string.Empty;
        this.FindControl<TextBlock>("PreviewText")!.Text = string.Join(", ", preview) + suffix;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnReplace(object? sender, RoutedEventArgs e) => Close(UploadConflictChoice.Replace);
    private void OnSkip(object? sender, RoutedEventArgs e) => Close(UploadConflictChoice.Skip);
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(UploadConflictChoice.Cancel);
}
