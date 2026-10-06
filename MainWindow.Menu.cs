using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using static DynamicIsland.Motion;

namespace DynamicIsland;

public partial class MainWindow
{
    static readonly int[] ScaleOptions = [85, 100, 115, 130];
    static readonly int[] GapOptions = [0, 4, 8, 12, 16, 24];
    static readonly string[] Pulses = ["Выкл.", "Слабый", "Средний", "Сильный"];

    void SettingsRow_Click(object sender, RoutedEventArgs e) => ShowPanelAndCheckUpdate(Panel.Settings);

    /// Dimming the whole desktop is only worth it when there is something to take; with nothing new the island
    /// answers in its own pill instead. A check still running opens the screen too, since it follows states live.
    async void UpdateRow_Click(object sender, RoutedEventArgs e)
    {
        await _updater.CheckAsync();
        if (_updater.State is Updater.Stage.Available or Updater.Stage.Loading or Updater.Stage.Checking) OpenUpdate();
        else if (_updater.State == Updater.Stage.Latest) Notify(Glyph.Check, _green, "Обновлений нет", "Уже стоит v" + Updater.CurrentVersion, force: true);
        else Notify(Glyph.Cross, _red, "Не проверить", "GitHub не отвечает", force: true);
    }

    void LookRow_Click(object sender, RoutedEventArgs e) => ShowPanel(Panel.Look);

    void SettingsBack_Click(object sender, RoutedEventArgs e) => ShowPanel(Panel.Menu);

    /// Closing the island is the one entry that cannot be undone from the island itself, so the red button asks
    /// for a second press and goes quiet again after ExitArmSeconds.
    const int ExitArmSeconds = 4;

    static readonly SolidColorBrush ExitRest = Tint(.13), ExitArmed = Tint(.34);

    bool _exitWaiting;

    readonly DelayedAction _exitDisarm;

    static SolidColorBrush Tint(double alpha) => new(Color.FromArgb((byte)(255 * alpha), 0xFF, 0x45, 0x3A));

    void Exit_Click(object sender, RoutedEventArgs e)
    {
        if (_exitWaiting) { Exit(); return; }
        _exitWaiting = true;
        ExitButton.Background = ExitArmed;
        ExitLabel.Text = "Нажмите ещё раз";
        _exitDisarm.Start(TimeSpan.FromSeconds(ExitArmSeconds));
    }

    void DisarmExit()
    {
        if (!_exitWaiting) return;
        _exitWaiting = false;
        _exitDisarm.Cancel();
        ExitButton.Background = ExitRest;
        ExitLabel.Text = "Закрыть остров";
    }

    void ShowPanelAndCheckUpdate(Panel panel)
    {
        ShowPanel(panel);
        _ = _updater.CheckAsync();
    }

    void Autostart_Click(object sender, RoutedEventArgs e)
    {
        try { Autostart.Set(!Autostart.Enabled); }
        catch (Exception ex) { App.Log(ex); }
        UpdateSwitches(true);
    }

    void Lyrics_Click(object sender, RoutedEventArgs e)
    {
        Settings.Lyrics = !Settings.Lyrics;
        UpdateSwitches(true);
        TrackLyrics();
    }

    void LyricEffects_Click(object sender, RoutedEventArgs e)
    {
        Settings.LyricEffects = !Settings.LyricEffects;
        UpdateSwitches(true);
        _playerLines = [];
    }

    void Rim_Click(object sender, RoutedEventArgs e)
    {
        Settings.Rim = !Settings.Rim;
        UpdateSwitches(true);
        SyncRim();
    }

    void AppVolume_Click(object sender, RoutedEventArgs e)
    {
        Settings.AppVolume = !Settings.AppVolume;
        UpdateSwitches(true);
    }

    void Network_Click(object sender, RoutedEventArgs e)
    {
        Settings.Network = !Settings.Network;
        UpdateSwitches(true);
    }

    void Mic_Click(object sender, RoutedEventArgs e)
    {
        Settings.Mic = !Settings.Mic;
        UpdateSwitches(true);
    }

    void Notices_Click(object sender, RoutedEventArgs e)
    {
        Settings.Notices = !Settings.Notices;
        UpdateSwitches(true);
        _ = ApplyNotices();
    }

    void Fullscreen_Click(object sender, RoutedEventArgs e)
    {
        Settings.HideFullscreen = !Settings.HideFullscreen;
        UpdateSwitches(true);
        CheckFullscreen();
    }

    void UpdateSwitches(bool animate)
    {
        LyricsSwitch.Set(Settings.Lyrics, animate);
        LyricEffectsSwitch.Set(Settings.LyricEffects, animate);
        RimSwitch.Set(Settings.Rim, animate);
        AppVolumeSwitch.Set(Settings.AppVolume, animate);
        NetworkSwitch.Set(Settings.Network, animate);
        MicSwitch.Set(Settings.Mic, animate);
        NoticesSwitch.Set(Settings.Notices, animate);
        FullscreenSwitch.Set(Settings.HideFullscreen, animate);
        AutostartSwitch.Set(Autostart.Enabled, animate);
        BridgeSwitch.Set(Settings.Bridge, animate);
        BridgeKdeSwitch.Set(Settings.BridgeKde, animate);
    }

    /// The update screen is a window that dims the whole desktop, so the island itself stands down while the release
    /// is on screen: a pill floating over the dim would sit above the modal and swallow clicks meant for it.
    void OpenUpdate()
    {
        _ = _updater.CheckAsync();
        if (_updateWindow != null) return;
        _updateWindow = new UpdateWindow(_updater, _screen, Exit);
        _updateWindow.Closed += (_, _) =>
        {
            _updateWindow = null;
            Show();
        };
        ShowPanel(Panel.None);
        Hide();
        _updateWindow.Show();
    }

    void RefreshUpdate()
    {
        Updater.Stage stage = _updater.State;
        bool loading = stage == Updater.Stage.Loading, found = loading || stage == Updater.Stage.Available;

        UpdateText.Foreground = found ? _orange : _dim;
        UpdateText.Text = loading ? _updater.Percent + "%" : "v" + (found ? _updater.LatestVersion! : Updater.CurrentVersion);

        if (loading)
        {
            LoadingText.Text = _updater.Percent + "%";
            LoadingRing.BeginAnimation(Ring.ProgressProperty, new DoubleAnimation(_updater.Percent / 100.0, Ms(200)));
        }
        UpdateView();
    }

    static int StepOption(int[] among, int value, int by, bool wrap)
    {
        int count = among.Length, at = Array.IndexOf(among, value) + by;
        return among[wrap ? (at % count + count) % count : Math.Clamp(at, 0, count - 1)];
    }

    void Size_Click(object sender, RoutedEventArgs e) => SetScale(StepOption(ScaleOptions, Settings.Scale, 1, true));

    void Gap_Click(object sender, RoutedEventArgs e) => SetGap(StepOption(GapOptions, Settings.Gap, 1, true));

    void SetScale(int percent)
    {
        if (percent == Settings.Scale) return;
        Settings.Scale = percent;
        ApplyLook();
    }

    void SetGap(int px)
    {
        if (px == Settings.Gap) return;
        Settings.Gap = px;
        ApplyLook();
    }

    void Dots_Click(object sender, RoutedEventArgs e)
    {
        Eq.Dots = Settings.Dots = !Settings.Dots;
        RefreshLookPage();
    }

    void Pulse_Click(object sender, RoutedEventArgs e) => SetPulse((Settings.Pulse + 1) % Pulses.Length);

    void SetPulse(int level)
    {
        if (level == Settings.Pulse) return;
        Settings.Pulse = level;
        RefreshLookPage();
    }

    void SeekStyle_Click(object sender, RoutedEventArgs e)
    {
        Settings.LineBar = !Settings.LineBar;
        RefreshLookPage();
        SyncSeekStyle(true);
    }

    void Accent_Click(object sender, RoutedEventArgs e)
    {
        Settings.Accent = ((RadioButton)sender).Background is SolidColorBrush picked ? picked.Color : null;
        RefreshLookPage();
        SyncAccent();
        SyncRim();
    }

    void RefreshLookPage()
    {
        SizeText.Text = Settings.Scale + "%";
        GapText.Text = Settings.Gap + " px";
        DotsText.Text = Settings.Dots ? "Матрица" : "Полоски";
        PulseText.Text = Pulses[Settings.Pulse];
        SeekStyleText.Text = Settings.LineBar ? "По строкам" : "Сплошная";
        foreach (RadioButton dot in AccentStrip.Children)
        {
            Color? color = dot.Background is SolidColorBrush own ? own.Color : null;
            if (color != Settings.Accent) continue;
            dot.IsChecked = true;
            AccentText.Text = (string)dot.Tag;
        }
    }

    void ApplyLook()
    {
        RefreshLookPage();
        _userScale.Target = Settings.Scale / 100.0;
        _topGap.Target = Settings.Gap;
        UpdateTargets();
    }
}
