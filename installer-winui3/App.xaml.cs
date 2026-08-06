// Coder CLI — WinUI 3 installer entry point.
using Microsoft.UI.Xaml;

namespace Coder.Installer;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
