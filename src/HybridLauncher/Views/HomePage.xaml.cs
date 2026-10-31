using Microsoft.UI.Xaml.Controls;

namespace HybridLauncher.Views;

public sealed partial class HomePage : Page
{
    public HomePage()
    {
        InitializeComponent();
        Helpers.Loc.Apply(this);
    }
}
