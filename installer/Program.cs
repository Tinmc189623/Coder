// Coder CLI — one-click installer (Windows-only).
// Extracts embedded payload to %LOCALAPPDATA%\Programs\Coder CLI, adds the
// directory to the user PATH, and creates a Start-menu shortcut.
//   coder-setup.exe            -> install
//   coder-setup.exe /uninstall -> remove everything

using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Versioning;
using Microsoft.Win32;

[assembly: SupportedOSPlatform("windows")]

const string AppDirName = "Coder CLI";
const string ExeName = "coder.exe";

var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
var installDir = Path.Combine(appData, "Programs", AppDirName);
var coderExe = Path.Combine(installDir, ExeName);

if (args.Length > 0 && args[0] is "/uninstall" or "-u" or "--uninstall")
{
    Uninstall(installDir, coderExe);
    return 0;
}

Install(installDir, coderExe);
return 0;

// ── 安装:解压嵌入资源 → 写 PATH → 建快捷方式 ─────────────────────────────
static void Install(string installDir, string coderExe)
{
    Console.WriteLine();
    Console.WriteLine("◆ Coder CLI  Installer  (v0.2.2)");
    Console.WriteLine("   production-grade AI coding agent for the terminal");
    Console.WriteLine();

    if (File.Exists(coderExe))
    {
        Console.WriteLine("已检测到已安装的版本，正在覆盖更新。");
        TryKillRunning(coderExe);
        TryDeleteDirectory(installDir);
    }

    Directory.CreateDirectory(installDir);
    var asm = Assembly.GetExecutingAssembly();
    foreach (var name in new[] { "coder.exe", "coder.dll", "coder.deps.json", "coder.runtimeconfig.json" })
    {
        using var res = asm.GetManifestResourceStream($"payload.{name}");
        if (res == null) { Console.Error.WriteLine($"缺少嵌入资源 payload.{name}，安装中止。"); return; }
        using var fs = File.Create(Path.Combine(installDir, name));
        res.CopyTo(fs);
        Console.WriteLine($"  ✓ {name}");
    }

    AddUserPath(installDir);
    CreateShortcut(coderExe);

    Console.WriteLine();
    Console.WriteLine("安装完成。在任意终端运行 `coder` 即可使用。");
    Console.WriteLine("卸载：coder-setup.exe /uninstall");
    Console.WriteLine();
    Console.WriteLine("Copyright © 2026 Nexsteaduser. All Rights Reserved.");
}

// ── 卸载:删除文件、PATH 条目、快捷方式 ───────────────────────────────────
static void Uninstall(string installDir, string coderExe)
{
    Console.WriteLine("正在卸载 Coder CLI …");
    TryKillRunning(coderExe);
    RemoveUserPath(installDir);
    RemoveShortcut();
    TryDeleteDirectory(installDir);
    Console.WriteLine("卸载完成。");
}

static void TryKillRunning(string coderExe)
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

static void TryDeleteDirectory(string dir)
{
    try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    catch { Console.WriteLine("  警告：部分文件占用未能删除，重启后重试。"); }
}

// ── 用户级 PATH:追加安装目录(注册表 HKCU\Environment) ────────────────────
static void AddUserPath(string installDir)
{
    using var key = Registry.CurrentUser.OpenSubKey("Environment", writable: true)
                    ?? throw new InvalidOperationException("无法打开 HKCU\\Environment");
    var current = (string?)key.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) ?? "";
    if (current.Split(';', StringSplitOptions.RemoveEmptyEntries)
               .Any(p => p.Equals(installDir, StringComparison.OrdinalIgnoreCase))) return;
    var updated = string.IsNullOrEmpty(current) ? installDir : current.TrimEnd(';') + ";" + installDir;
    key.SetValue("Path", updated, RegistryValueKind.ExpandString);
    // 广播 WM_SETTINGCHANGE，让已打开的终端立即生效
    NativeMethods.SendMessageTimeout(
        new IntPtr(0xFFFF), 0x001A, IntPtr.Zero, "Environment",
        NativeMethods.SMTO_ABORTIFHUNG, 5000, out _);
}

static void RemoveUserPath(string installDir)
{
    using var key = Registry.CurrentUser.OpenSubKey("Environment", writable: true);
    if (key == null) return;
    var current = (string?)key.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) ?? "";
    var parts = current.Split(';', StringSplitOptions.RemoveEmptyEntries)
                       .Where(p => !p.Equals(installDir, StringComparison.OrdinalIgnoreCase)).ToArray();
    key.SetValue("Path", string.Join(';', parts), RegistryValueKind.ExpandString);
    NativeMethods.SendMessageTimeout(
        new IntPtr(0xFFFF), 0x001A, IntPtr.Zero, "Environment",
        NativeMethods.SMTO_ABORTIFHUNG, 5000, out _);
}

// ── 开始菜单快捷方式(COM WScript.Shell) ───────────────────────────────────
static void CreateShortcut(string coderExe)
{
    try
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
    catch (Exception ex)
    {
        Console.WriteLine($"  警告：开始菜单快捷方式创建失败：{ex.Message}(可手动创建)。");
    }
}

/// <summary>通过 IDispatch 调用 COM 方法。</summary>
static object? InvokeCom(Type type, object instance, string member, params object?[] args)
{
    const System.Reflection.BindingFlags flags =
        System.Reflection.BindingFlags.InvokeMethod |
        System.Reflection.BindingFlags.Public |
        System.Reflection.BindingFlags.Instance;
    return type.InvokeMember(member, flags, null, instance, args);
}

/// <summary>通过 IDispatch 设置 COM 属性(DISPATCH_PROPERTYPUT)。</summary>
static void SetComProp(Type type, object instance, string prop, object? value)
{
    const System.Reflection.BindingFlags flags =
        System.Reflection.BindingFlags.SetProperty |
        System.Reflection.BindingFlags.Public |
        System.Reflection.BindingFlags.Instance;
    type.InvokeMember(prop, flags, null, instance, new[] { value });
}

static void RemoveShortcut()
{
    try
    {
        var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppDirName);
        var lnk = Path.Combine(startMenu, "Coder CLI.lnk");
        if (File.Exists(lnk)) File.Delete(lnk);
        if (Directory.Exists(startMenu) && !Directory.EnumerateFileSystemEntries(startMenu).Any())
            Directory.Delete(startMenu);
    }
    catch { /* 忽略 */ }
}

static class NativeMethods
{
    public const uint SMTO_ABORTIFHUNG = 0x0002;
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);
}
