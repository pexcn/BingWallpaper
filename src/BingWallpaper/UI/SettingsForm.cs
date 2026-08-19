using System.Drawing;
using System.Windows.Forms;
using BingWallpaper.Theme;

namespace BingWallpaper.UI;

internal enum SettingKind
{
    Market,
    Resolution,
    Fit,
    Theme,
    Interval,
    KeepDays,
    RunAtStartup,
}

internal sealed class SettingsChangedEventArgs : EventArgs
{
    public SettingsChangedEventArgs(SettingKind kind) => Kind = kind;

    public SettingKind Kind { get; }
}

/// <summary>
/// Settings window. Every change is applied and persisted immediately, so there is
/// no "Apply" button - only "Close". Closing hides the window instead of disposing it.
/// </summary>
internal sealed class SettingsForm : Form
{
    private static readonly string[] Markets =
    {
        "zh-CN", "en-US", "en-GB", "en-AU", "en-CA", "en-IN", "en-NZ",
        "de-DE", "fr-FR", "fr-CA", "it-IT", "ja-JP", "es-ES", "pt-BR",
    };

    private readonly AppConfig _config;

    private readonly ComboBox _marketBox = new();
    private readonly RadioButton _resolution1080 = new();
    private readonly RadioButton _resolution4K = new();
    private readonly ComboBox _fitBox = new();
    private readonly RadioButton _themeSystem = new();
    private readonly RadioButton _themeLight = new();
    private readonly RadioButton _themeDark = new();
    private readonly NumericUpDown _intervalBox = new();
    private readonly NumericUpDown _keepDaysBox = new();
    private readonly CheckBox _startupBox = new();
    private readonly Button _cleanTracesButton = new();
    private readonly Button _closeButton = new();
    private readonly Label _pathLabel = new();

    private bool _loading;

    public SettingsForm(AppConfig config)
    {
        _config = config;

        Text = "BingWallpaper 设置";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(480, 430);

        BuildLayout();
        LoadFromConfig();
        WireEvents();

        ThemeManager.ApplyToForm(this);
    }

    public event EventHandler<SettingsChangedEventArgs>? SettingsChanged;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ThemeManager.ApplyToForm(this);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Hide, never dispose: the tray context keeps the instance alive.
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnFormClosing(e);
    }

    private void BuildLayout()
    {
        const int labelX = 16;
        const int fieldX = 150;
        const int fieldWidth = 300;
        int y = 18;

        Controls.Add(CreateLabel("壁纸地区", labelX, y + 3));
        _marketBox.SetBounds(fieldX, y, 200, 24);
        _marketBox.DropDownStyle = ComboBoxStyle.DropDown;
        _marketBox.Items.AddRange(Markets);
        Controls.Add(_marketBox);
        y += 38;

        Controls.Add(CreateLabel("分辨率", labelX, y + 3));
        _resolution4K.SetBounds(fieldX, y, 80, 24);
        _resolution4K.Text = "4K";
        _resolution1080.SetBounds(fieldX + 90, y, 100, 24);
        _resolution1080.Text = "1080p";
        Controls.Add(_resolution4K);
        Controls.Add(_resolution1080);
        y += 38;

        Controls.Add(CreateLabel("填充方式", labelX, y + 3));
        _fitBox.SetBounds(fieldX, y, 140, 24);
        _fitBox.DropDownStyle = ComboBoxStyle.DropDownList;
        foreach (WallpaperFit fit in Enum.GetValues<WallpaperFit>())
        {
            _fitBox.Items.Add(WallpaperService.GetFitDisplayName(fit));
        }

        Controls.Add(_fitBox);
        y += 38;

        Controls.Add(CreateLabel("界面主题", labelX, y + 3));
        _themeSystem.SetBounds(fieldX, y, 100, 24);
        _themeSystem.Text = "跟随系统";
        _themeLight.SetBounds(fieldX + 105, y, 70, 24);
        _themeLight.Text = "亮色";
        _themeDark.SetBounds(fieldX + 180, y, 70, 24);
        _themeDark.Text = "暗色";
        Controls.Add(_themeSystem);
        Controls.Add(_themeLight);
        Controls.Add(_themeDark);
        y += 38;

        Controls.Add(CreateLabel("检查间隔（小时）", labelX, y + 3));
        _intervalBox.SetBounds(fieldX, y, 80, 24);
        _intervalBox.Minimum = AppConfig.MinRefreshIntervalHours;
        _intervalBox.Maximum = AppConfig.MaxRefreshIntervalHours;
        Controls.Add(_intervalBox);
        y += 38;

        Controls.Add(CreateLabel("壁纸保留天数", labelX, y + 3));
        _keepDaysBox.SetBounds(fieldX, y, 80, 24);
        _keepDaysBox.Minimum = 0;
        _keepDaysBox.Maximum = AppConfig.MaxKeepDays;
        Controls.Add(_keepDaysBox);
        Controls.Add(CreateLabel("0 = 永久保留", fieldX + 90, y + 3));
        y += 38;

        _startupBox.SetBounds(fieldX, y, 200, 24);
        _startupBox.Text = "开机自动启动";
        Controls.Add(_startupBox);
        y += 42;

        _cleanTracesButton.SetBounds(labelX, y, 180, 30);
        _cleanTracesButton.Text = "清除所有系统痕迹";
        Controls.Add(_cleanTracesButton);

        _closeButton.SetBounds(ClientSize.Width - 116, y, 100, 30);
        _closeButton.Text = "关闭";
        Controls.Add(_closeButton);
        y += 44;

        _pathLabel.SetBounds(labelX, y, fieldWidth + 140, 76);
        _pathLabel.AutoSize = false;
        _pathLabel.Text =
            "便携模式：配置、日志与壁纸全部保存在程序所在目录，删除整个文件夹即完成卸载。\r\n" +
            "程序目录：" + Paths.BaseDirectory + "\r\n" +
            "壁纸目录：" + Paths.WallpaperDirectory;
        Controls.Add(_pathLabel);

        CancelButton = _closeButton;
    }

    private static Label CreateLabel(string text, int x, int y)
    {
        Label label = new()
        {
            Text = text,
            AutoSize = true,
        };
        label.Location = new Point(x, y);
        return label;
    }

    private void LoadFromConfig()
    {
        _loading = true;
        try
        {
            _marketBox.Text = _config.Market;
            _resolution4K.Checked = _config.Resolution == ResolutionKind.Uhd;
            _resolution1080.Checked = _config.Resolution == ResolutionKind.FullHd;
            _fitBox.SelectedIndex = (int)_config.Fit;
            _themeSystem.Checked = _config.Theme == ThemeMode.System;
            _themeLight.Checked = _config.Theme == ThemeMode.Light;
            _themeDark.Checked = _config.Theme == ThemeMode.Dark;
            _intervalBox.Value = Math.Clamp(
                _config.RefreshIntervalHours,
                AppConfig.MinRefreshIntervalHours,
                AppConfig.MaxRefreshIntervalHours);
            _keepDaysBox.Value = Math.Clamp(_config.KeepDays, 0, AppConfig.MaxKeepDays);
            _startupBox.Checked = _config.RunAtStartup;
        }
        finally
        {
            _loading = false;
        }
    }

    private void WireEvents()
    {
        _marketBox.SelectedIndexChanged += (_, _) => CommitMarket();
        _marketBox.Leave += (_, _) => CommitMarket();

        _resolution4K.CheckedChanged += (_, _) =>
        {
            if (_loading || !_resolution4K.Checked)
            {
                return;
            }

            _config.Resolution = ResolutionKind.Uhd;
            Persist(SettingKind.Resolution);
        };

        _resolution1080.CheckedChanged += (_, _) =>
        {
            if (_loading || !_resolution1080.Checked)
            {
                return;
            }

            _config.Resolution = ResolutionKind.FullHd;
            Persist(SettingKind.Resolution);
        };

        _fitBox.SelectedIndexChanged += (_, _) =>
        {
            if (_loading || _fitBox.SelectedIndex < 0)
            {
                return;
            }

            _config.Fit = (WallpaperFit)_fitBox.SelectedIndex;
            Persist(SettingKind.Fit);
        };

        _themeSystem.CheckedChanged += (_, _) => CommitTheme(_themeSystem, ThemeMode.System);
        _themeLight.CheckedChanged += (_, _) => CommitTheme(_themeLight, ThemeMode.Light);
        _themeDark.CheckedChanged += (_, _) => CommitTheme(_themeDark, ThemeMode.Dark);

        _intervalBox.ValueChanged += (_, _) =>
        {
            if (_loading)
            {
                return;
            }

            _config.RefreshIntervalHours = (int)_intervalBox.Value;
            Persist(SettingKind.Interval);
        };

        _keepDaysBox.ValueChanged += (_, _) =>
        {
            if (_loading)
            {
                return;
            }

            _config.KeepDays = (int)_keepDaysBox.Value;
            Persist(SettingKind.KeepDays);
        };

        _startupBox.CheckedChanged += (_, _) =>
        {
            if (_loading)
            {
                return;
            }

            _config.RunAtStartup = _startupBox.Checked;
            Persist(SettingKind.RunAtStartup);
        };

        _cleanTracesButton.Click += (_, _) => CleanSystemTraces();
        _closeButton.Click += (_, _) => Hide();
    }

    private void CommitMarket()
    {
        if (_loading)
        {
            return;
        }

        string market = AppConfig.NormalizeMarket(_marketBox.Text);
        if (string.IsNullOrWhiteSpace(market) || market.Length < 2)
        {
            MessageBox.Show(
                this,
                "市场代码格式类似 zh-CN 或 en-US，请重新输入。",
                "BingWallpaper",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            _loading = true;
            _marketBox.Text = _config.Market;
            _loading = false;
            return;
        }

        if (string.Equals(market, _config.Market, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _config.Market = market;
        _loading = true;
        _marketBox.Text = market;
        _loading = false;
        Persist(SettingKind.Market);
    }

    private void CommitTheme(RadioButton button, ThemeMode mode)
    {
        if (_loading || !button.Checked || _config.Theme == mode)
        {
            return;
        }

        _config.Theme = mode;
        Persist(SettingKind.Theme);
    }

    private void Persist(SettingKind kind)
    {
        try
        {
            _config.Save(Paths.ConfigFile);
        }
        catch (Exception ex)
        {
            Logger.Error("Could not save the configuration file.", ex);
            ErrorDialog.Show("BingWallpaper - 保存设置失败", Logger.Describe(ex));
            return;
        }

        SettingsChanged?.Invoke(this, new SettingsChangedEventArgs(kind));
    }

    /// <summary>
    /// Removes the only registry value this program writes on its own behalf and
    /// explains what Windows itself wrote when the wallpaper was applied.
    /// </summary>
    private void CleanSystemTraces()
    {
        AutoStartManager.Disable();

        _loading = true;
        _startupBox.Checked = false;
        _loading = false;

        _config.RunAtStartup = false;
        try
        {
            _config.Save(Paths.ConfigFile);
        }
        catch (Exception ex)
        {
            Logger.Error("Could not save the configuration after clearing traces.", ex);
        }

        Logger.Info("System traces cleared (Run value removed, RunAtStartup=false).");

        MessageBox.Show(
            this,
            "已完成：\r\n" +
            "  • 删除注册表 HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run 下的 BingWallpaper 值\r\n" +
            "  • 配置项 RunAtStartup 已置为 false\r\n\r\n" +
            "以下位置由 Windows 自身在设置壁纸时写入，本程序无法安全移除，如需清理请手动处理：\r\n" +
            "  • HKCU\\Control Panel\\Desktop 的 Wallpaper / WallpaperStyle / TileWallpaper\r\n" +
            "    （通过系统「个性化 → 背景」重新选择壁纸即可覆盖）\r\n" +
            "  • HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Wallpapers 的 BackgroundHistoryPath*\r\n" +
            "  • %APPDATA%\\Microsoft\\Windows\\Themes\\TranscodedWallpaper 及同目录下的 CachedFiles\r\n\r\n" +
            "本程序自身的文件（BingWallpaper.ini、BingWallpaper.log、wallpapers 文件夹）全部位于程序目录，" +
            "删除整个文件夹即可彻底卸载。",
            "BingWallpaper - 清除系统痕迹",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }
}
