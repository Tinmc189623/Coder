// Coder CLI — 图形安装/卸载窗体。
// 品牌配色:电光紫 #6A4A96 / #AF87FF,青蓝 #00D7FF,琥珀 #FFAF00。
// 单窗口两态:安装态(欢迎 → 进度 → 完成)与卸载态(确认 → 进度 → 完成)。

using System.Drawing.Drawing2D;

namespace Coder.Installer;

public sealed class MainForm : Form
{
    private readonly bool _uninstallMode;
    private readonly InstallEngine _engine;
    private readonly Label _titleLabel;
    private readonly Label _statusLabel;
    private readonly ProgressBar _progressBar;
    private readonly Button _primaryButton;
    private readonly Button _secondaryButton;
    private readonly PictureBox _logoBox;

    public MainForm(bool uninstallMode)
    {
        _uninstallMode = uninstallMode;
        _engine = new InstallEngine(ReportProgress);

        // ── 窗体基础 ──────────────────────────────────────────────
        Text = "Coder CLI Installer";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(480, 340);
        BackColor = Color.FromArgb(245, 242, 255);
        Font = new Font("Microsoft YaHei UI", 9.5f);
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);

        // ── 顶部品牌区(深紫渐变) ──────────────────────────────────
        var header = new Panel { Dock = DockStyle.Top, Height = 108 };
        header.Paint += (s, e) =>
        {
            var r = header.ClientRectangle;
            using var brush = new LinearGradientBrush(r,
                Color.FromArgb(106, 74, 150), Color.FromArgb(80, 56, 120), 45f);
            e.Graphics.FillRectangle(brush, r);
        };
        Controls.Add(header);

        _logoBox = new PictureBox
        {
            Location = new Point(18, 14),
            Size = new Size(80, 80),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Transparent,
        };
        using (var icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath))
        {
            if (icon != null)
            {
                var bmp = new Bitmap(icon.Width, icon.Height);
                using var g = Graphics.FromImage(bmp);
                g.DrawIcon(icon, 0, 0);
                _logoBox.Image = bmp;
            }
        }
        header.Controls.Add(_logoBox);

        _titleLabel = new Label
        {
            Text = _uninstallMode ? "卸载 Coder CLI" : "安装 Coder CLI",
            Location = new Point(114, 22),
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 16f, FontStyle.Bold),
            ForeColor = Color.White,
            BackColor = Color.Transparent,
        };
        header.Controls.Add(_titleLabel);

        var tagline = new Label
        {
            Text = "production-grade AI coding agent for the terminal",
            Location = new Point(116, 60),
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 9f),
            ForeColor = Color.FromArgb(215, 200, 240),
            BackColor = Color.Transparent,
        };
        header.Controls.Add(tagline);

        // ── 中部状态区 ────────────────────────────────────────────
        _statusLabel = new Label
        {
            Text = _uninstallMode
                ? "将移除 Coder CLI 及其快捷方式与 PATH 配置。"
                : "Coder CLI v0.2.2 将安装到你的用户目录,无需管理员权限。",
            Location = new Point(24, 128),
            Size = new Size(432, 44),
            Font = new Font("Microsoft YaHei UI", 10f),
            ForeColor = Color.FromArgb(60, 55, 80),
        };
        Controls.Add(_statusLabel);

        _progressBar = new ProgressBar
        {
            Location = new Point(24, 186),
            Size = new Size(432, 16),
            Style = ProgressBarStyle.Continuous,
            Visible = false,
        };
        Controls.Add(_progressBar);

        // ── 底部按钮区 ────────────────────────────────────────────
        _primaryButton = new Button
        {
            Text = _uninstallMode ? "卸载" : "安装",
            Location = new Point(330, 252),
            Size = new Size(126, 38),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(0, 215, 255),
            ForeColor = Color.White,
            Font = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold),
            Cursor = Cursors.Hand,
        };
        _primaryButton.FlatAppearance.BorderSize = 0;
        _primaryButton.Click += OnPrimaryClick;
        Controls.Add(_primaryButton);

        _secondaryButton = new Button
        {
            Text = "取消",
            Location = new Point(196, 252),
            Size = new Size(110, 38),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.Transparent,
            ForeColor = Color.FromArgb(106, 74, 150),
            Font = new Font("Microsoft YaHei UI", 10f),
            Cursor = Cursors.Hand,
        };
        _secondaryButton.FlatAppearance.BorderSize = 1;
        _secondaryButton.FlatAppearance.BorderColor = Color.FromArgb(175, 135, 255);
        _secondaryButton.Click += (_, _) => Close();
        Controls.Add(_secondaryButton);

        // ── 底部版权行 ────────────────────────────────────────────
        var footer = new Label
        {
            Text = "Copyright © 2026 Nexsteaduser. All Rights Reserved.",
            Location = new Point(0, 306),
            Size = new Size(480, 22),
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Microsoft YaHei UI", 8f),
            ForeColor = Color.FromArgb(150, 140, 170),
        };
        Controls.Add(footer);
    }

    /// <summary>主按钮点击:完成态关闭窗口,否则执行安装/卸载。</summary>
    private async void OnPrimaryClick(object? sender, EventArgs e)
    {
        if (_completed) { Close(); return; }
        await RunCoreAsync();
    }

    /// <summary>执行安装/卸载,期间禁用按钮、显示进度条。</summary>
    private async Task RunCoreAsync()
    {
        SetBusy(true);
        try
        {
            await Task.Run(_uninstallMode ? _engine.Uninstall : _engine.Install);
            _completed = true;
            _statusLabel.Text = _uninstallMode ? "卸载完成。" : "安装完成,在任意终端运行 `coder` 即可使用。";
            _primaryButton.Text = "关闭";
            _primaryButton.Enabled = true;       // 恢复可用,点击走 OnPrimaryClick -> Close
            _secondaryButton.Visible = false;    // 完成态只保留"关闭"
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"操作失败:{ex.Message}";
            _progressBar.Visible = false;
            SetBusy(false);
            MessageBox.Show(this, ex.Message, "Coder CLI Installer",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private bool _completed;

    private void SetBusy(bool busy)
    {
        _progressBar.Visible = busy;
        _primaryButton.Enabled = !busy;
        _secondaryButton.Enabled = !busy;
    }

    /// <summary>进度回报(可能在后台线程,需切回 UI 线程)。</summary>
    private void ReportProgress(string text, int percent)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => ReportProgress(text, percent));
            return;
        }
        _statusLabel.Text = text;
        _progressBar.Value = Math.Clamp(percent, 0, 100);
    }
}
