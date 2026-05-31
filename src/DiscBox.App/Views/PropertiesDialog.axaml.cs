using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using DiscBox.Models;

namespace DiscBox.Views;

public partial class PropertiesDialog : Window
{
    public PropertiesDialog()
    {
        InitializeComponent();
    }

    public PropertiesDialog(FileEntry entry)
    {
        InitializeComponent();
        this.FindControl<TextBlock>("NameText")!.Text    = entry.Name;
        this.FindControl<TextBlock>("TypeText")!.Text    = entry.IsFolder ? "Folder" : "File";
        this.FindControl<TextBlock>("SizeText")!.Text    = entry.SizeDisplay == "" ? "-" : entry.SizeDisplay;
        this.FindControl<TextBlock>("PathText")!.Text    = entry.VirtualPath;
        this.FindControl<TextBlock>("CreatedText")!.Text = entry.CreatedAt.ToString("dd/MM/yyyy HH:mm");
        this.FindControl<TextBlock>("EncryptedText")!.Text = entry.Encrypted ? "Yes" : "No";
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
