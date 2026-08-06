// Coder CLI — 安装引擎:解压嵌入 payload、写用户 PATH、建开始菜单快捷方式、卸载。
// 与 UI 解耦:进度通过 Action<string, int> 回报(文本 + 百分比),异常抛给调用方展示。

using System.Diagnostics;
using System.Reflection;
using Microsoft.Win32;

namespace Coder.Installer;

/// <summary>安装/卸载引擎,供图形界面调用。</summary>
public sealed class InstallEngine
{
    public const string AppDirName = "Coder CLI";
    public const string ExeName = "coder.exe";

    private readonly Action<string, int> _progress;

    public InstallEngine(Action<string, int> progress) => _progress = progress;

    public string InstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppDirName);

    /// <summary>执行安装:解压 → PATH → 快捷方式。覆盖安装会先清理旧版本。</summary>
    public void Install()
    {
        var installDir = InstallDir;
        var coderExe = Path.Combine(installDir, ExeName);

        _progress("正在检查已有安装…", 5);
        if (File.Exists(coderExe))
        {
            TryKillRunning(coderExe);
            TryDeleteDirectory(installDir);
            _progress("已移除旧版本", 10);
        }

        Directory.CreateDirectory(installDir);
        var asm = Assembly.GetExecutingAssembly();
        var payload = new[] { ExeName, "coder.dll", "coder.deps.json", "coder.runtimeconfig.json", "Tomlyn.dll" };
        for (int i = 0; i < payload.Length; i++)
        {
            _progress($"正在解压 {payload[i]} …", 10 + (i * 15));
            using var res = asm.GetManifestResourceStream($"payload.{payload[i]}")
                ?? throw new InvalidOperationException($"缺少嵌入资源 payload.{payload[i]}");
            using var fs = File.Create(Path.Combine(installDir, payload[i]));
            res.CopyTo(fs);
        }

        _progress("正在配置 PATH …", 72);
        AddUserPath(installDir);

        _progress("正在创建开始菜单快捷方式 …", 85);
        CreateShortcut(coderExe);

        _progress("完成", 100);
    }

    /// <summary>执行卸载:杀进程 → 删 PATH → 删快捷方式 → 删文件。</summary>
    public void Uninstall()
    {
        var installDir = InstallDir;
        var coderExe = Path.Combine(installDir, ExeName);

        _progress("正在停止运行中的进程…", 10);
        TryKillRunning(coderExe);

        _progress("正在清理 PATH …", 35);
        RemoveUserPath(installDir);

        _progress("正在移除快捷方式 …", 55);
        RemoveShortcut();

        _progress("正在删除文件 …", 75);
        TryDeleteDirectory(installDir);

        _progress("完成", 100);
    }

    /// <summary>目标已安装?</summary>
    public bool IsInstalled => File.Exists(Path.Combine(InstallDir, ExeName));

    private static void TryKillRunning(string coderExe)
    {
        try
        {
            var running = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName));
            foreach (var p in running)
            {
                try { if (p.MainModule?.FileName.StartsWith(coderExe, StringComparison.OrdinalIgnoreCase) == true) p.Kill(); }
                catch { /* 进程已退出 */ }
                p.Dispose();
            }
        }
        catch { /* 无权限查看进程列表时跳过 */ }
    }

    private static void TryDeleteDirectory(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* 文件占用时保留,提示用户重启后重试 */ }
    }

    /// <summary>追加安装目录到用户 PATH(HKCU\Environment),并广播 WM_SETTINGCHANGE。</summary>
    private static void AddUserPath(string installDir)
    {
        using var key = Registry.CurrentUser.OpenSubKey("Environment", writable: true)
                        ?? throw new InvalidOperationException("无法打开 HKCU\\Environment");
        var current = (string?)key.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) ?? "";
        if (current.Split(';', StringSplitOptions.RemoveEmptyEntries)
                   .Any(p => p.Equals(installDir, StringComparison.OrdinalIgnoreCase))) return;
        var updated = string.IsNullOrEmpty(current) ? installDir : current.TrimEnd(';') + ";" + installDir;
        key.SetValue("Path", updated, RegistryValueKind.ExpandString);
        BroadcastPathChange();
    }

    private static void RemoveUserPath(string installDir)
    {
        using var key = Registry.CurrentUser.OpenSubKey("Environment", writable: true);
        if (key == null) return;
        var current = (string?)key.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) ?? "";
        var parts = current.Split(';', StringSplitOptions.RemoveEmptyEntries)
                           .Where(p => !p.Equals(installDir, StringComparison.OrdinalIgnoreCase)).ToArray();
        key.SetValue("Path", string.Join(';', parts), RegistryValueKind.ExpandString);
        BroadcastPathChange();
    }

    private static void BroadcastPathChange()
        => NativeMethods.SendMessageTimeout(
            new IntPtr(0xFFFF), 0x001A, IntPtr.Zero, "Environment",
            NativeMethods.SMTO_ABORTIFHUNG, 5000, out _);

    /// <summary>创建开始菜单快捷方式(COM WScript.Shell)。</summary>
    private static void CreateShortcut(string coderExe)
    {
        var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppDirName);
        Directory.CreateDirectory(startMenu);
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
                        ?? throw new InvalidOperationException("WScript.Shell 不可用");
        var shell = Activator.CreateInstance(shellType)!;
        var lnk = InvokeCom(shellType, shell, "CreateShortcut",
                            Path.Combine(startMenu, "Coder CLI.lnk"))!;
        var lnkType = lnk.GetType();
        SetComProp(lnkType, lnk, "TargetPath", coderExe);
        SetComProp(lnkType, lnk, "WorkingDirectory", Path.GetDirectoryName(coderExe));
        SetComProp(lnkType, lnk, "Description", "Coder CLI — production-grade AI coding agent for the terminal");
        InvokeCom(lnkType, lnk, "Save");
    }

    private static void RemoveShortcut()
    {
        var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppDirName);
        var lnk = Path.Combine(startMenu, "Coder CLI.lnk");
        if (File.Exists(lnk)) File.Delete(lnk);
        if (Directory.Exists(startMenu) && !Directory.EnumerateFileSystemEntries(startMenu).Any())
            Directory.Delete(startMenu);
    }

    private static object? InvokeCom(Type type, object instance, string member, params object?[] args)
    {
        const System.Reflection.BindingFlags flags =
            System.Reflection.BindingFlags.InvokeMethod |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.Instance;
        return type.InvokeMember(member, flags, null, instance, args);
    }

    private static void SetComProp(Type type, object instance, string prop, object? value)
    {
        const System.Reflection.BindingFlags flags =
            System.Reflection.BindingFlags.SetProperty |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.Instance;
        type.InvokeMember(prop, flags, null, instance, new[] { value });
    }
}
