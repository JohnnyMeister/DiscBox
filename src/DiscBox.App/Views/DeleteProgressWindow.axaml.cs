using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DiscBox.Views;

public partial class DeleteProgressWindow : Window
{
    public DeleteProgressWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
