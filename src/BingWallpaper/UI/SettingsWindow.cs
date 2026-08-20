using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
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
/// no "Apply" button - only "Close". Closing hides the window instead of
/// destroying it.
///
/// The window sizes itself to its content, which is what keeps the layout correct
/// at any scaling factor without a single hard coded pixel count. The drop downs
/// share one minimum width so they line up on both edges.
/// </summary>
internal sealed class SettingsWindow : AppWindow
{
    private static readonly string[] Markets =
    {
        "zh-CN", "en-US", "en-GB", "en-AU", "en-CA", "en-IN", "en-NZ",
        "de-DE", "fr-FR", "fr-CA", "it-IT", "ja-JP", "es-ES", "pt-BR",
    };

    private const double FieldWidth = 132;

    private readonly AppConfig _config;

    private readonly ComboBox _marketBox = NewComboBox();
    private readonly RadioButton _resolution4K = NewRadio("4K", "resolution");
    private readonly RadioButton _resolution1080 = NewRadio("1080p", "resolution");
    private readonly ComboBox _fitBox = NewComboBox();
    private readonly RadioButton _themeSystem = NewRadio("跟随系统", "theme");
    private readonly RadioButton _themeLight = NewRadio("亮色", "theme");
    private readonly RadioButton _themeDark = NewRadio("暗色", "theme");
    private readonly ComboBox _intervalBox = NewComboBox();
    private readonly ComboBox _keepDaysBox = NewComboBox();

    // No caption on the check box: the row label carries it, like every other setting.
    private readonly CheckBox _startupBox = new();
    private readonly Button _closeButton = new() { Content = "关闭", MinWidth = 88 };

    private readonly Grid _fields = new();
    private readonly Border _separator = new() { Height = 1, Margin = new Thickness(0, 14, 0, 0) };

    private bool _loading;

    public SettingsWindow(AppConfig config)
        : base("设置")
    {
        _config = config;

        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;

        BuildLayout();
        LoadFromConfig();
        WireEvents();
        ApplyPalette();
    }

    public event EventHandler<SettingsChangedEventArgs>? SettingsChanged;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // The focus would otherwise land on the market drop down, and a focused drop
        // down answers the mouse wheel - so a stray scroll anywhere over the window
        // would silently change a setting.
        _closeButton.Focus();
    }

    protected override void OnThemeChanged() => ApplyPalette();

    private void BuildLayout()
    {
        _fields.ColumnDefinitions = new ColumnDefinitions
        {
            new ColumnDefinition(GridLength.Auto),
            new ColumnDefinition(GridLength.Auto),
        };

        foreach (string market in Markets)
        {
            _marketBox.Items.Add(market);
        }

        AddRow("壁纸地区", _marketBox);
        AddRow("分辨率", Group(_resolution4K, _resolution1080));

        foreach (WallpaperFit fit in Enum.GetValues<WallpaperFit>())
        {
            _fitBox.Items.Add(WallpaperService.GetFitDisplayName(fit));
        }

        AddRow("填充方式", _fitBox);
        AddRow("界面主题", Group(_themeSystem, _themeLight, _themeDark));

        // Drop downs instead of a number box: every value that makes sense is in the
        // list, and carrying the unit in the item text removes a separate hint label.
        foreach (int hours in new[] { 1, 2, 3, 4, 6, 8, 12, 24 })
        {
            _intervalBox.Items.Add(new Choice(hours, FormatHours(hours)));
        }

        AddRow("检查间隔", _intervalBox);

        foreach (int days in new[] { 0, 7, 14, 30, 60, 90, 180, 365 })
        {
            _keepDaysBox.Items.Add(new Choice(days, FormatDays(days)));
        }

        AddRow("保留天数", _keepDaysBox);

        _startupBox.VerticalAlignment = VerticalAlignment.Center;
        AddRow("开机自启", _startupBox);

        StackPanel buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        buttons.Children.Add(_closeButton);

        StackPanel root = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(18, 16, 18, 14),
        };
        root.Children.Add(_fields);
        root.Children.Add(_separator);
        root.Children.Add(buttons);

        Content = root;
    }

    /// <summary>Adds a "label + control" row to the field grid.</summary>
    private void AddRow(string caption, Control field)
    {
        int row = _fields.RowDefinitions.Count;
        _fields.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        TextBlock label = new TextBlock
        {
            Text = caption,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 8, 16, 8),
        };

        field.HorizontalAlignment = HorizontalAlignment.Left;
        field.Margin = new Thickness(0, 4, 0, 4);

        Grid.SetRow(label, row);
        Grid.SetColumn(label, 0);
        Grid.SetRow(field, row);
        Grid.SetColumn(field, 1);
        _fields.Children.Add(label);
        _fields.Children.Add(field);
    }

    private static StackPanel Group(params Control[] children)
    {
        StackPanel panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        foreach (Control child in children)
        {
            child.Margin = new Thickness(0, 0, 14, 0);
            panel.Children.Add(child);
        }

        return panel;
    }

    private static ComboBox NewComboBox() => new() { MinWidth = FieldWidth };

    private static RadioButton NewRadio(string caption, string group)
        => new() { Content = caption, GroupName = group };

    private void ApplyPalette() => _separator.Background = ThemeManager.Palette.Border;

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
            _resolution4K.IsChecked = _config.Resolution == ResolutionKind.Uhd;
            _resolution1080.IsChecked = _config.Resolution == ResolutionKind.FullHd;
            _fitBox.SelectedIndex = (int)_config.Fit;
            _themeSystem.IsChecked = _config.Theme == ThemeMode.System;
            _themeLight.IsChecked = _config.Theme == ThemeMode.Light;
            _themeDark.IsChecked = _config.Theme == ThemeMode.Dark;
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
            _startupBox.IsChecked = _config.RunAtStartup;
        }
        finally
        {
            _loading = false;
        }
    }

    private void WireEvents()
    {
        _marketBox.SelectionChanged += (_, _) => CommitMarket();

        _resolution4K.IsCheckedChanged += (_, _) => CommitResolution(_resolution4K, ResolutionKind.Uhd);
        _resolution1080.IsCheckedChanged += (_, _) => CommitResolution(_resolution1080, ResolutionKind.FullHd);

        _fitBox.SelectionChanged += (_, _) =>
        {
            if (_loading || _fitBox.SelectedIndex < 0 || _config.Fit == (WallpaperFit)_fitBox.SelectedIndex)
            {
                return;
            }

            _config.Fit = (WallpaperFit)_fitBox.SelectedIndex;
            Persist(SettingKind.Fit);
        };

        _themeSystem.IsCheckedChanged += (_, _) => CommitTheme(_themeSystem, ThemeMode.System);
        _themeLight.IsCheckedChanged += (_, _) => CommitTheme(_themeLight, ThemeMode.Light);
        _themeDark.IsCheckedChanged += (_, _) => CommitTheme(_themeDark, ThemeMode.Dark);

        _intervalBox.SelectionChanged += (_, _) =>
        {
            int hours = GetValue(_intervalBox, _config.RefreshIntervalHours);
            if (_loading || _config.RefreshIntervalHours == hours)
            {
                return;
            }

            _config.RefreshIntervalHours = hours;
            Persist(SettingKind.Interval);
        };

        _keepDaysBox.SelectionChanged += (_, _) =>
        {
            int days = GetValue(_keepDaysBox, _config.KeepDays);
            if (_loading || _config.KeepDays == days)
            {
                return;
            }

            _config.KeepDays = days;
            Persist(SettingKind.KeepDays);
        };

        _startupBox.IsCheckedChanged += (_, _) =>
        {
            bool enabled = _startupBox.IsChecked == true;
            if (_loading || _config.RunAtStartup == enabled)
            {
                return;
            }

            _config.RunAtStartup = enabled;
            Persist(SettingKind.RunAtStartup);
        };

        _closeButton.Click += (_, _) => Hide();
    }

    private void CommitResolution(RadioButton button, ResolutionKind resolution)
    {
        if (_loading || button.IsChecked != true || _config.Resolution == resolution)
        {
            return;
        }

        _config.Resolution = resolution;
        Persist(SettingKind.Resolution);
    }

    private void CommitTheme(RadioButton button, ThemeMode mode)
    {
        if (_loading || button.IsChecked != true || _config.Theme == mode)
        {
            return;
        }

        _config.Theme = mode;
        Persist(SettingKind.Theme);
    }

    private void CommitMarket()
    {
        if (_loading || _marketBox.SelectedItem is not string market)
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
            ErrorDialog.Show("保存设置失败", Logger.Describe(ex));
            return;
        }

        SettingsChanged?.Invoke(this, new SettingsChangedEventArgs(kind));
    }
}
