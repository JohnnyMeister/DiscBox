using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DiscBox.Views;

public partial class TransferProgressWindow : Window
{
    public TransferProgressWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
