using System;
using System.Collections.Generic;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BingWallpaper.UI;

/// <summary>
/// Settings window. Every change is applied and persisted immediately, so there is
/// no "Apply" button - only "关闭". Closing hides the window instead of destroying
/// it, because the tray keeps the instance around.
/// </summary>
public sealed partial class SettingsWindow : Window
{
    /// <summary>
    /// Read only list: a free text market code is a typo waiting to happen. A code
    /// that is not in here can still be set by editing the INI file, and
    /// <see cref="LoadFromConfig"/> adds it to the list so it stays visible.
    /// </summary>
    private static readonly string[] Markets =
    {
        "zh-CN", "en-US", "en-GB", "en-AU", "en-CA", "en-IN", "en-NZ",
        "de-DE", "fr-FR", "fr-CA", "it-IT", "ja-JP", "es-ES", "pt-BR",
    };

    private static readonly int[] IntervalChoices = { 1, 2, 3, 4, 6, 8, 12, 24 };

    private static readonly int[] KeepDaysChoices = { 0, 7, 14, 30, 60, 90, 180, 365 };

    private readonly AppConfig _config;
    private readonly Action<SettingKind> _changed;

    private bool _loading;
    private bool _sized;
    private bool _closingForGood;

    internal SettingsWindow(AppConfig config, Action<SettingKind> changed)
    {
        _config = config;
        _changed = changed;

        InitializeComponent();
        WindowSupport.Prepare(this, "设置");
        AppWindow.Closing += OnClosing;
        Root.Loaded += OnRootLoaded;

        BuildItems();
        LoadFromConfig();
        WireEvents();
        WindowSupport.ApplyTheme(this);
    }

    /// <summary>Raised after the window took itself off the screen.</summary>
    internal event EventHandler? Hidden;

    internal void ShowAndActivate() => WindowSupport.ShowAndActivate(this);

    internal void ApplyTheme() => WindowSupport.ApplyTheme(this);

    /// <summary>Really closes the window - only the shutdown path asks for this.</summary>
    internal void CloseForGood()
    {
        _closingForGood = true;
        try
        {
            Close();
        }
        catch (Exception ex)
        {
            Logger.Debug("Closing the settings window failed: " + ex.Message);
        }
    }

    private void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        if (_sized)
        {
            return;
        }

        _sized = true;
        WindowSupport.ResizeToContent(this, Root, minWidth: 360, minHeight: 0);
        WindowSupport.Center(this);
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closingForGood)
        {
            return;
        }

        // Hide, never destroy: the controller keeps this instance, and with it the
        // list of markets that were added by hand.
        args.Cancel = true;
        sender.Hide();
        Hidden?.Invoke(this, EventArgs.Empty);
    }

    private void BuildItems()
    {
        foreach (string market in Markets)
        {
            MarketBox.Items.Add(market);
        }

        foreach (WallpaperFit fit in Enum.GetValues<WallpaperFit>())
        {
            FitBox.Items.Add(WallpaperService.GetFitDisplayName(fit));
        }

        // Drop downs rather than a number box: the presets are the values that make
        // sense, and carrying the unit in the item text removes a separate hint label.
        foreach (int hours in IntervalChoices)
        {
            IntervalBox.Items.Add(CreateChoice(hours, FormatHours(hours)));
        }

        foreach (int days in KeepDaysChoices)
        {
            KeepDaysBox.Items.Add(CreateChoice(days, FormatDays(days)));
        }
    }

    /// <summary>
    /// One entry of a numeric drop down. The value rides along in Tag, which keeps
    /// the list free of a model type and the window free of a binding.
    /// </summary>
    private static ComboBoxItem CreateChoice(int value, string text) => new ComboBoxItem
    {
        Content = text,
        Tag = value,
    };

    private static string FormatHours(int hours) => hours + " 小时";

    private static string FormatDays(int days) => days == 0 ? "永久" : days + " 天";

    private void LoadFromConfig()
    {
        _loading = true;
        try
        {
            if (!MarketBox.Items.Contains(_config.Market))
            {
                // Market code set by hand in the INI file - keep it selectable.
                MarketBox.Items.Add(_config.Market);
            }

            MarketBox.SelectedItem = _config.Market;
            ResolutionButtons.SelectedIndex = _config.Resolution == ResolutionKind.Uhd ? 0 : 1;
            FitBox.SelectedIndex = (int)_config.Fit;
            ThemeButtons.SelectedIndex = (int)_config.Theme;
            SelectValue(
                IntervalBox,
                Math.Clamp(
                    _config.RefreshIntervalHours,
                    AppConfig.MinRefreshIntervalHours,
                    AppConfig.MaxRefreshIntervalHours),
                FormatHours);
            SelectValue(KeepDaysBox, Math.Clamp(_config.KeepDays, 0, AppConfig.MaxKeepDays), FormatDays);
            StartupSwitch.IsOn = _config.RunAtStartup;
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>
    /// Selects the entry carrying <paramref name="value"/>. A value that no preset
    /// covers comes from a hand edited INI file, so it is inserted rather than
    /// silently replaced by the closest preset.
    /// </summary>
    private static void SelectValue(ComboBox box, int value, Func<int, string> format)
    {
        for (int i = 0; i < box.Items.Count; i++)
        {
            if (GetValue(box.Items[i]) == value)
            {
                box.SelectedIndex = i;
                return;
            }
        }

        int index = 0;
        while (index < box.Items.Count && GetValue(box.Items[index]) is int existing && existing < value)
        {
            index++;
        }

        box.Items.Insert(index, CreateChoice(value, format(value)));
        box.SelectedIndex = index;
    }

    private static int? GetValue(object? item) => item is ComboBoxItem entry && entry.Tag is int value ? value : null;

    private void WireEvents()
    {
        MarketBox.SelectionChanged += (_, _) => CommitMarket();

        ResolutionButtons.SelectionChanged += (_, _) =>
        {
            ResolutionKind resolution = ResolutionButtons.SelectedIndex == 1
                ? ResolutionKind.FullHd
                : ResolutionKind.Uhd;
            if (_loading || ResolutionButtons.SelectedIndex < 0 || _config.Resolution == resolution)
            {
                return;
            }

            _config.Resolution = resolution;
            Persist(SettingKind.Resolution);
        };

        FitBox.SelectionChanged += (_, _) =>
        {
            if (_loading || FitBox.SelectedIndex < 0 || _config.Fit == (WallpaperFit)FitBox.SelectedIndex)
            {
                return;
            }

            _config.Fit = (WallpaperFit)FitBox.SelectedIndex;
            Persist(SettingKind.Fit);
        };

        ThemeButtons.SelectionChanged += (_, _) =>
        {
            if (_loading || ThemeButtons.SelectedIndex < 0 || _config.Theme == (ThemeMode)ThemeButtons.SelectedIndex)
            {
                return;
            }

            _config.Theme = (ThemeMode)ThemeButtons.SelectedIndex;
            Persist(SettingKind.Theme);
        };

        IntervalBox.SelectionChanged += (_, _) =>
        {
            int hours = GetValue(IntervalBox.SelectedItem) ?? _config.RefreshIntervalHours;
            if (_loading || _config.RefreshIntervalHours == hours)
            {
                return;
            }

            _config.RefreshIntervalHours = hours;
            Persist(SettingKind.Interval);
        };

        KeepDaysBox.SelectionChanged += (_, _) =>
        {
            int days = GetValue(KeepDaysBox.SelectedItem) ?? _config.KeepDays;
            if (_loading || _config.KeepDays == days)
            {
                return;
            }

            _config.KeepDays = days;
            Persist(SettingKind.KeepDays);
        };

        StartupSwitch.Toggled += (_, _) =>
        {
            if (_loading || _config.RunAtStartup == StartupSwitch.IsOn)
            {
                return;
            }

            _config.RunAtStartup = StartupSwitch.IsOn;
            Persist(SettingKind.RunAtStartup);
        };

        CloseButton.Click += (_, _) =>
        {
            AppWindow.Hide();
            Hidden?.Invoke(this, EventArgs.Empty);
        };
    }

    private void CommitMarket()
    {
        if (_loading || MarketBox.SelectedItem is not string market)
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
            ErrorWindow.Show("保存设置失败", Logger.Describe(ex));
            return;
        }

        // The theme is the one setting this window has to react to itself: the
        // controller flips the palette, and every open window repaints.
        _changed(kind);
        if (kind == SettingKind.Theme)
        {
            WindowSupport.ApplyTheme(this);
        }
    }
}
