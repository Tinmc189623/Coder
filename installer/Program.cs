// Coder CLI — 图形安装器入口 (Windows-only)。
//   coder-setup.exe            -> 图形安装界面
//   coder-setup.exe /uninstall -> 图形卸载界面

using System.Runtime.Versioning;

[assembly: SupportedOSPlatform("windows")]

namespace Coder.Installer;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        bool uninstall = args.Length > 0 && args[0] is "/uninstall" or "-u" or "--uninstall";
        Application.Run(new MainForm(uninstall));
    }
}

/// <summary>P/Invoke:WM_SETTINGCHANGE 广播,让已打开终端立即感知 PATH 变化。</summary>
internal static class NativeMethods
{
    public const uint SMTO_ABORTIFHUNG = 0x0002;

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true,
        CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    public static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);
}
