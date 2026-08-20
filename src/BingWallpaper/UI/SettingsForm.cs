using System;
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
///
/// Every drop down is as wide as the longest entry of all of them, measured at
/// run time, so they share one right edge without any of them being wider than
/// its content needs.
/// </summary>
internal sealed class SettingsForm : Form
{
    private static readonly string[] Markets =
    {
        "zh-CN", "en-US", "en-GB", "en-AU", "en-CA", "en-IN", "en-NZ",
        "de-DE", "fr-FR", "fr-CA", "it-IT", "ja-JP", "es-ES", "pt-BR",
    };

    private readonly AppConfig _config;

    private readonly ThemedComboBox _marketBox = new();
    private readonly ThemedRadioButton _resolution4K = new("4K");
    private readonly ThemedRadioButton _resolution1080 = new("1080p");
    private readonly ThemedComboBox _fitBox = new();
    private readonly ThemedRadioButton _themeSystem = new("跟随系统");
    private readonly ThemedRadioButton _themeLight = new("亮色");
    private readonly ThemedRadioButton _themeDark = new("暗色");
    private readonly ThemedComboBox _intervalBox = new();
    private readonly ThemedComboBox _keepDaysBox = new();
    // No caption: the row label carries it, like every other setting.
    private readonly ThemedCheckBox _startupBox = new(string.Empty);
    private readonly Button _closeButton = new();

    private TableLayoutPanel _root = null!;
    private bool _loading;

    public SettingsForm(AppConfig config)
    {
        _config = config;

        ThemeManager.ApplySystemFont(this);

        Text = "BingWallpaper 设置";
        Icon = AppIcon.Window;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        // DPI based scaling, not font based: the .NET Framework default font and the
        // system UI font have different metrics, which makes font scaling unreliable.
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
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
        SizeDropDowns();
        FitToContent();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);

        // A focused DropDownList paints its value in the system highlight colours,
        // so the first drop down came up as a blue block - the window hands the
        // focus to the first control in the tab order, which is the market box. The
        // close button takes it instead; it is where the focus ends up anyway once
        // the window has been closed once, which is why reopening looked correct.
        if (Visible)
        {
            ActiveControl = _closeButton;
        }
    }

    /// <summary>
    /// Gives every drop down the width of the longest entry any of them holds, so
    /// they line up on both edges while staying as narrow as their content.
    ///
    /// This runs from OnLoad rather than from the constructor: the widths are
    /// measured with the font that is actually in use, which already reflects the
    /// system DPI, and AutoScaleMode.Dpi has finished scaling the layout by now -
    /// a width assigned earlier would be scaled a second time.
    /// </summary>
    private void SizeDropDowns()
    {
        ComboBox[] boxes = { _marketBox, _fitBox, _intervalBox, _keepDaysBox };
        int textWidth = 0;

        foreach (ComboBox box in boxes)
        {
            foreach (object item in box.Items)
            {
                Size size = TextRenderer.MeasureText(
                    item.ToString(),
                    box.Font,
                    new Size(int.MaxValue, int.MaxValue),
                    TextFormatFlags.NoPadding);
                textWidth = Math.Max(textWidth, size.Width);
            }
        }

        // Room for the drop down button, the frame and the inset in front of the
        // text. Kept tight on purpose: the longest entry is only "24 小时" wide.
        int width = textWidth + SystemInformation.VerticalScrollBarWidth + DpiScale.Round(8);

        foreach (ComboBox box in boxes)
        {
            box.Width = width;
        }
    }

    /// <summary>
    /// Sizes the dialog from the measured content instead of hard coded pixels, so
    /// nothing is clipped at 125%, 150% or any other scaling factor.
    /// </summary>
    private void FitToContent()
    {
        Size preferred = _root.PreferredSize;
        int width = Math.Max(preferred.Width, DpiScale.Round(340)) + Padding.Horizontal;
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

        // Read only: a free text market code is a typo waiting to happen. A code that
        // is not in this list can still be set by editing the INI file, and
        // LoadFromConfig adds it to the list so it stays visible and selectable.
        _marketBox.Items.AddRange(Markets);
        AddRow(fields, "壁纸地区", _marketBox);

        AddRow(fields, "分辨率", CreateGroup(_resolution4K, _resolution1080));

        foreach (WallpaperFit fit in (WallpaperFit[])Enum.GetValues(typeof(WallpaperFit)))
        {
            _fitBox.Items.Add(WallpaperService.GetFitDisplayName(fit));
        }

        AddRow(fields, "填充方式", _fitBox);

        AddRow(fields, "界面主题", CreateGroup(_themeSystem, _themeLight, _themeDark));

        // Drop downs instead of NumericUpDown: the spinner buttons of a NumericUpDown
        // are painted by the Windows visual styles and stay light in the dark theme,
        // and carrying the unit in the item text removes the separate hint label.
        foreach (int hours in new[] { 1, 2, 3, 4, 6, 8, 12, 24 })
        {
            _intervalBox.Items.Add(new Choice(hours, FormatHours(hours)));
        }

        AddRow(fields, "检查间隔", _intervalBox);

        foreach (int days in new[] { 0, 7, 14, 30, 60, 90, 180, 365 })
        {
            _keepDaysBox.Items.Add(new Choice(days, FormatDays(days)));
        }

        AddRow(fields, "保留天数", _keepDaysBox);

        AddRow(fields, "开机自启", _startupBox);

        _closeButton.Text = "关闭";
        // A fixed size, not AutoSize: ThemeManager swaps FlatStyle between Standard
        // and Flat with the palette, the two measure differently, and the dialog is
        // sized once in OnLoad - so an auto sized button ends up clipped after a
        // theme change. The value is in logical pixels; AutoScaleMode.Dpi scales it.
        _closeButton.AutoSize = false;
        _closeButton.Size = new Size(92, 30);
        _closeButton.Margin = new Padding(8, 0, 0, 0);

        FlowLayoutPanel buttons = new()
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(0, 12, 0, 0),
            Padding = Padding.Empty,
        };
        buttons.Controls.Add(_closeButton);

        ThemedSeparator separator = new()
        {
            Dock = DockStyle.Top,
            Margin = new Padding(0, 14, 0, 0),
        };

        _root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 3,
            Margin = Padding.Empty,
        };
        _root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _root.Controls.Add(fields, 0, 0);
        _root.Controls.Add(separator, 0, 1);
        _root.Controls.Add(buttons, 0, 2);

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
        table.RowCount = row + 1;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(label, 0, row);
        table.Controls.Add(field, 1, row);
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

    /// <summary>One entry of a numeric drop down: the stored value plus its label.</summary>
    private sealed class Choice
    {
        public Choice(int value, string text)
        {
            Value = value;
            Text = text;
        }

        public int Value { get; }

        public string Text { get; }

        public override string ToString() => Text;
    }

    private static string FormatHours(int hours) => hours + " 小时";

    private static string FormatDays(int days) => days == 0 ? "永久" : days + " 天";

    /// <summary>
    /// Selects the entry carrying <paramref name="value"/>. A value that no preset
    /// covers comes from a hand edited INI file, so it is inserted rather than
    /// silently replaced by the closest preset.
    /// </summary>
    private static void SelectValue(ComboBox box, int value, Func<int, string> format)
    {
        for (int i = 0; i < box.Items.Count; i++)
        {
            if (box.Items[i] is Choice choice && choice.Value == value)
            {
                box.SelectedIndex = i;
                return;
            }
        }

        int index = 0;
        while (index < box.Items.Count
               && box.Items[index] is Choice existing
               && existing.Value < value)
        {
            index++;
        }

        box.Items.Insert(index, new Choice(value, format(value)));
        box.SelectedIndex = index;
    }

    private static int GetValue(ComboBox box, int fallback)
        => box.SelectedItem is Choice choice ? choice.Value : fallback;

    private void LoadFromConfig()
    {
        _loading = true;
        try
        {
            if (!_marketBox.Items.Contains(_config.Market))
            {
                // Market code set by hand in the INI file - keep it selectable.
                _marketBox.Items.Add(_config.Market);
            }

            _marketBox.SelectedItem = _config.Market;
            _resolution4K.Checked = _config.Resolution == ResolutionKind.Uhd;
            _resolution1080.Checked = _config.Resolution == ResolutionKind.FullHd;
            _fitBox.SelectedIndex = (int)_config.Fit;
            _themeSystem.Checked = _config.Theme == ThemeMode.System;
            _themeLight.Checked = _config.Theme == ThemeMode.Light;
            _themeDark.Checked = _config.Theme == ThemeMode.Dark;
            SelectValue(
                _intervalBox,
                AppConfig.Clamp(
                    _config.RefreshIntervalHours,
                    AppConfig.MinRefreshIntervalHours,
                    AppConfig.MaxRefreshIntervalHours),
                FormatHours);
            SelectValue(
                _keepDaysBox,
                AppConfig.Clamp(_config.KeepDays, 0, AppConfig.MaxKeepDays),
                FormatDays);
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

        _intervalBox.SelectedIndexChanged += (_, _) =>
        {
            int hours = GetValue(_intervalBox, _config.RefreshIntervalHours);
            if (_loading || _config.RefreshIntervalHours == hours)
            {
                return;
            }

            _config.RefreshIntervalHours = hours;
            Persist(SettingKind.Interval);
        };

        _keepDaysBox.SelectedIndexChanged += (_, _) =>
        {
            int days = GetValue(_keepDaysBox, _config.KeepDays);
            if (_loading || _config.KeepDays == days)
            {
                return;
            }

            _config.KeepDays = days;
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
        if (_loading || !(_marketBox.SelectedItem is string market))
        {
            return;
        }

        if (string.Equals(market, _config.Market, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _config.Market = market;
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
}
