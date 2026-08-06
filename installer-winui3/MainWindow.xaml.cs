// Coder CLI — WinUI 3 installer main window.
// Four-page flow: welcome → options (path / start-menu toggle) → progress → done.
// Install logic (extract / PATH / shortcut) lives in InstallEngine.

using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Coder.Installer;

public sealed partial class MainWindow : Window
{
    private enum Page { Welcome, Options, Progress, Done, UninstallConfirm, UninstallProgress }

    private Page _page = Page.Welcome;
    private readonly InstallEngine _engine;
    private string _installPath = "";
    private bool _startMenuShortcut = true;
    private bool _uninstallMode;

    public MainWindow()
    {
        InitializeComponent();
        _engine = new InstallEngine(ReportProgress);
        _installPath = _engine.InstallDir;
        var cliArgs = Environment.GetCommandLineArgs();
        _uninstallMode = cliArgs.Length > 1 && cliArgs[1] is "/uninstall" or "-u" or "--uninstall";
        ShowPage(_uninstallMode ? Page.UninstallConfirm : Page.Welcome);
    }

    // ── 页面渲染 ──────────────────────────────────────────────────────────

    private void ShowPage(Page p)
    {
        _page = p;
        switch (p)
        {
            case Page.Welcome:
                SecondaryButton.Content = "取消";
                PrimaryButton.Content = "安装";
                PageHost.Content = BuildWelcome();
                break;
            case Page.Options:
                SecondaryButton.Content = "上一步";
                PrimaryButton.Content = "安装";
                PageHost.Content = BuildOptions();
                break;
            case Page.Progress:
                SecondaryButton.IsEnabled = false;
                PrimaryButton.IsEnabled = false;
                PageHost.Content = BuildProgress();
                break;
            case Page.Done:
                SecondaryButton.Visibility = Visibility.Collapsed;
                PrimaryButton.Content = "关闭";
                PrimaryButton.IsEnabled = true;
                PageHost.Content = BuildDone();
                break;
            case Page.UninstallConfirm:
                SecondaryButton.Content = "取消";
                PrimaryButton.Content = "卸载";
                PageHost.Content = BuildUninstallConfirm();
                break;
            case Page.UninstallProgress:
                SecondaryButton.IsEnabled = false;
                PrimaryButton.IsEnabled = false;
                PageHost.Content = BuildProgress();
                break;
        }
    }

    private UIElement BuildWelcome()
    {
        var stack = new StackPanel { Spacing = 12, Padding = new Thickness(24, 20, 24, 20) };
        stack.Children.Add(new TextBlock
        {
            Text = _uninstallMode ? "卸载 Coder CLI" : "安装 Coder CLI",
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
        });
        stack.Children.Add(new TextBlock
        {
            Text = _uninstallMode
                ? "将移除 Coder CLI 及其快捷方式与 PATH 配置。"
                : $"Coder CLI v{VersionText} 将安装到你的用户目录,无需管理员权限。",
            TextWrapping = TextWrapping.Wrap,
        });
        stack.Children.Add(new TextBlock
        {
            Text = "安装位置: " + _installPath,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
        });
        return stack;
    }

    private UIElement BuildOptions()
    {
        var stack = new StackPanel { Spacing = 16, Padding = new Thickness(24, 20, 24, 20) };
        stack.Children.Add(new TextBlock { Text = "安装选项", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.Bold });

        var pathBox = new TextBox { Text = _installPath, Header = "安装目录", Width = 420, HorizontalAlignment = HorizontalAlignment.Left };
        pathBox.TextChanged += (_, _) => _installPath = pathBox.Text;
        stack.Children.Add(pathBox);

        var shortcutToggle = new ToggleSwitch
        {
            Header = "创建开始菜单快捷方式",
            IsOn = _startMenuShortcut,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        shortcutToggle.Toggled += (_, _) => _startMenuShortcut = shortcutToggle.IsOn;
        stack.Children.Add(shortcutToggle);

        stack.Children.Add(new TextBlock
        {
            Text = "Copyright © 2026 Nexsteaduser. All Rights Reserved.",
            Opacity = 0.5,
            FontSize = 12,
            Margin = new Thickness(0, 12, 0, 0),
        });
        return stack;
    }

    private UIElement BuildProgress()
    {
        var stack = new StackPanel { Spacing = 12, Padding = new Thickness(24, 20, 24, 20) };
        stack.Children.Add(new TextBlock { Text = _uninstallMode ? "正在卸载…" : "正在安装…", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.Bold });
        var bar = new ProgressBar { Minimum = 0, Maximum = 100, Value = 0, Width = 420, HorizontalAlignment = HorizontalAlignment.Left };
        bar.SetValue(FrameworkElement.TagProperty, _progressBar = bar);
        stack.Children.Add(bar);
        var status = new TextBlock { Text = "", TextWrapping = TextWrapping.Wrap };
        status.SetValue(FrameworkElement.TagProperty, _progressText = status);
        stack.Children.Add(status);
        return stack;
    }

    private UIElement BuildDone()
    {
        var stack = new StackPanel { Spacing = 12, Padding = new Thickness(24, 20, 24, 20) };
        stack.Children.Add(new TextBlock
        {
            Text = _uninstallMode ? "卸载完成。" : "安装完成,在任意终端运行 `coder` 即可使用。",
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
        });
        stack.Children.Add(new TextBlock { Text = "卸载:运行 coder-setup.exe /uninstall", Opacity = 0.7 });
        return stack;
    }

    private UIElement BuildUninstallConfirm()
    {
        var stack = new StackPanel { Spacing = 12, Padding = new Thickness(24, 20, 24, 20) };
        stack.Children.Add(new TextBlock { Text = "卸载 Coder CLI", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.Bold });
        stack.Children.Add(new TextBlock
        {
            Text = "将移除 Coder CLI 及其快捷方式与 PATH 配置。此操作不可撤销。",
            TextWrapping = TextWrapping.Wrap,
        });
        return stack;
    }

    private ProgressBar? _progressBar;
    private TextBlock? _progressText;

    private void ReportProgress(string text, int percent)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_progressBar != null) _progressBar.Value = percent;
            if (_progressText != null) _progressText.Text = text;
        });
    }

    private static string VersionText => "0.3.1";

    // ── 按钮 ──────────────────────────────────────────────────────────────

    private async void OnPrimaryClick(object sender, RoutedEventArgs e)
    {
        switch (_page)
        {
            case Page.Welcome:
                ShowPage(Page.Options);
                break;
            case Page.Options:
                _engine.SetOptions(_installPath, _startMenuShortcut);
                ShowPage(Page.Progress);
                await RunInstallAsync();
                break;
            case Page.UninstallConfirm:
                ShowPage(Page.UninstallProgress);
                await RunUninstallAsync();
                break;
            case Page.Done:
                Close();
                break;
        }
    }

    private void OnSecondaryClick(object sender, RoutedEventArgs e)
    {
        switch (_page)
        {
            case Page.Options:
                ShowPage(Page.Welcome);
                break;
            default:
                Close();
                break;
        }
    }

    private async Task RunInstallAsync()
    {
        try
        {
            await Task.Run(_engine.Install);
            ShowPage(Page.Done);
        }
        catch (Exception ex)
        {
            var dlg = new ContentDialog
            {
                Title = "Coder CLI Installer",
                Content = ex.Message,
                CloseButtonText = "确定",
                XamlRoot = Content.XamlRoot,
            };
            await dlg.ShowAsync();
            ShowPage(Page.Options);
        }
    }

    private async Task RunUninstallAsync()
    {
        try
        {
            await Task.Run(_engine.Uninstall);
            ShowPage(Page.Done);
        }
        catch (Exception ex)
        {
            var dlg = new ContentDialog
            {
                Title = "Coder CLI Installer",
                Content = ex.Message,
                CloseButtonText = "确定",
                XamlRoot = Content.XamlRoot,
            };
            await dlg.ShowAsync();
            ShowPage(Page.UninstallConfirm);
        }
    }
}
