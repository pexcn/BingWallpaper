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
///
/// The layout is built from auto sizing panels rather than fixed coordinates so it
/// stays correct at any DPI, and each radio group lives in its own container -
/// radio buttons are grouped by their parent, so sharing one parent would make the
/// resolution and theme options exclude each other.
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
    private readonly ThemedRadioButton _resolution4K = new("4K");
    private readonly ThemedRadioButton _resolution1080 = new("1080p");
    private readonly ComboBox _fitBox = new();
    private readonly ThemedRadioButton _themeSystem = new("跟随系统");
    private readonly ThemedRadioButton _themeLight = new("亮色");
    private readonly ThemedRadioButton _themeDark = new("暗色");
    private readonly NumericUpDown _intervalBox = new();
    private readonly NumericUpDown _keepDaysBox = new();
    private readonly ThemedCheckBox _startupBox = new("开机自动启动");
    private readonly Button _cleanTracesButton = new();
    private readonly Button _closeButton = new();

    private TableLayoutPanel _root = null!;
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
        Padding = new Padding(18, 16, 18, 14);

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

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        FitToContent();
    }

    /// <summary>
    /// Sizes the dialog from the measured content instead of hard coded pixels, so
    /// nothing is clipped at 125%, 150% or any other scaling factor.
    /// </summary>
    private void FitToContent()
    {
        Size preferred = _root.PreferredSize;
        int width = Math.Max(preferred.Width, LogicalToDeviceUnits(430)) + Padding.Horizontal;
        int height = preferred.Height + Padding.Vertical;
        ClientSize = new Size(width, height);
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
        TableLayoutPanel fields = new()
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            GrowStyle = TableLayoutPanelGrowStyle.AddRows,
            Margin = Padding.Empty,
        };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _marketBox.DropDownStyle = ComboBoxStyle.DropDown;
        _marketBox.Width = 190;
        _marketBox.Items.AddRange(Markets);
        AddRow(fields, "壁纸地区", _marketBox);

        AddRow(fields, "分辨率", CreateGroup(_resolution4K, _resolution1080));

        _fitBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _fitBox.Width = 150;
        foreach (WallpaperFit fit in Enum.GetValues<WallpaperFit>())
        {
            _fitBox.Items.Add(WallpaperService.GetFitDisplayName(fit));
        }

        AddRow(fields, "填充方式", _fitBox);

        AddRow(fields, "界面主题", CreateGroup(_themeSystem, _themeLight, _themeDark));

        _intervalBox.Width = 80;
        _intervalBox.Minimum = AppConfig.MinRefreshIntervalHours;
        _intervalBox.Maximum = AppConfig.MaxRefreshIntervalHours;
        AddRow(fields, "检查间隔", CreateGroup(_intervalBox, CreateHint("小时")));

        _keepDaysBox.Width = 80;
        _keepDaysBox.Minimum = 0;
        _keepDaysBox.Maximum = AppConfig.MaxKeepDays;
        AddRow(fields, "壁纸保留天数", CreateGroup(_keepDaysBox, CreateHint("天，0 = 永久保留")));

        AddRow(fields, string.Empty, CreateGroup(_startupBox));

        _cleanTracesButton.Text = "清除所有系统痕迹";
        _cleanTracesButton.AutoSize = true;
        _cleanTracesButton.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _cleanTracesButton.Padding = new Padding(10, 5, 10, 5);
        _cleanTracesButton.Margin = new Padding(0, 0, 8, 0);

        _closeButton.Text = "关闭";
        _closeButton.AutoSize = true;
        _closeButton.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _closeButton.Padding = new Padding(24, 5, 24, 5);
        _closeButton.Margin = new Padding(8, 0, 0, 0);

        FlowLayoutPanel buttons = new()
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(0, 14, 0, 0),
            Padding = Padding.Empty,
        };
        buttons.Controls.Add(_closeButton);
        buttons.Controls.Add(_cleanTracesButton);

        _root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
        };
        _root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _root.Controls.Add(fields, 0, 0);
        _root.Controls.Add(buttons, 0, 1);

        Controls.Add(_root);
        CancelButton = _closeButton;
    }

    /// <summary>Adds a "label + control" row to the field grid.</summary>
    private static void AddRow(TableLayoutPanel table, string caption, Control field)
    {
        Label label = new()
        {
            Text = caption,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 8, 16, 8),
        };

        field.Anchor = AnchorStyles.Left;
        field.Margin = new Padding(0, 4, 0, 4);

        int row = table.RowCount;
        table.Controls.Add(label, 0, row);
        table.Controls.Add(field, 1, row);
        table.RowCount = row + 1;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    }

    /// <summary>
    /// Wraps controls in their own container. For radio buttons this is what makes
    /// them a mutually exclusive group that is independent from the other groups.
    /// </summary>
    private static FlowLayoutPanel CreateGroup(params Control[] children)
    {
        FlowLayoutPanel panel = new()
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0, 4, 0, 4),
            Padding = Padding.Empty,
        };

        foreach (Control child in children)
        {
            child.Margin = new Padding(0, 2, 14, 2);
            child.Anchor = AnchorStyles.Left;
            panel.Controls.Add(child);
        }

        return panel;
    }

    private static Label CreateHint(string text) => new()
    {
        Text = text,
        AutoSize = true,
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(0, 4, 0, 0),
    };

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

        _resolution4K.CheckedChanged += (_, _) => CommitResolution(_resolution4K, ResolutionKind.Uhd);
        _resolution1080.CheckedChanged += (_, _) => CommitResolution(_resolution1080, ResolutionKind.FullHd);

        _fitBox.SelectedIndexChanged += (_, _) =>
        {
            if (_loading || _fitBox.SelectedIndex < 0 || _config.Fit == (WallpaperFit)_fitBox.SelectedIndex)
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
            if (_loading || _config.RefreshIntervalHours == (int)_intervalBox.Value)
            {
                return;
            }

            _config.RefreshIntervalHours = (int)_intervalBox.Value;
            Persist(SettingKind.Interval);
        };

        _keepDaysBox.ValueChanged += (_, _) =>
        {
            if (_loading || _config.KeepDays == (int)_keepDaysBox.Value)
            {
                return;
            }

            _config.KeepDays = (int)_keepDaysBox.Value;
            Persist(SettingKind.KeepDays);
        };

        _startupBox.CheckedChanged += (_, _) =>
        {
            if (_loading || _config.RunAtStartup == _startupBox.Checked)
            {
                return;
            }

            _config.RunAtStartup = _startupBox.Checked;
            Persist(SettingKind.RunAtStartup);
        };

        _cleanTracesButton.Click += (_, _) => CleanSystemTraces();
        _closeButton.Click += (_, _) => Hide();
    }

    private void CommitResolution(RadioButton button, ResolutionKind resolution)
    {
        if (_loading || !button.Checked || _config.Resolution == resolution)
        {
            return;
        }

        _config.Resolution = resolution;
        Persist(SettingKind.Resolution);
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
