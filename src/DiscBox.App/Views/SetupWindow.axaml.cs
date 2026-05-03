using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DiscBox.Views;

public partial class SetupWindow : Window
{
    public SetupWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
