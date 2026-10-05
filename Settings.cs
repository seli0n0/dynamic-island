using System.Windows.Media;
using Microsoft.Win32;

namespace DynamicIsland;

static class Settings
{
    const string Key = @"Software\DynamicIsland";
    public const int MaxGap = 200;
    const int MinScale = 85, MaxScale = 130, MaxAlong = 4096, MinFontScale = 80, MaxFontScale = 160, MaxPulse = 3;
    const int DefaultScale = 100, DefaultGap = 8, DefaultPulse = 2;

    static bool _lyrics = ReadSwitch(nameof(Lyrics)), _lyricEffects = ReadSwitch(nameof(LyricEffects));
    static bool _network = ReadSwitch(nameof(Network)), _hideFullscreen = ReadSwitch(nameof(HideFullscreen));
    static bool _rim = ReadSwitch(nameof(Rim)), _appVolume = ReadSwitch(nameof(AppVolume));
    static bool _timerPauses = ReadSwitch(nameof(TimerPauses)), _workArea = ReadSwitch(nameof(WorkArea));
    static bool _mic = ReadSwitch(nameof(Mic));
    static bool _notices = ReadSwitch(nameof(Notices), false);
    static bool _dots = ReadSwitch(nameof(Dots), false);
    static bool _lineBar = ReadSwitch(nameof(LineBar));
    static int _scale = Math.Clamp(Read(nameof(Scale), DefaultScale), MinScale, MaxScale);
    static int _gap = Math.Clamp(Read(nameof(Gap), DefaultGap), 0, MaxGap);
    static int _accent = Read(nameof(Accent), 0);
    static int _edge = Math.Clamp(Read(nameof(Edge), 0), 0, 3);
    static int _anchor = Math.Clamp(Read(nameof(Anchor), 0), 0, 3);
    static int _along = Math.Clamp(Read(nameof(Along), 0), -MaxAlong, MaxAlong);
    static int _fontScale = Math.Clamp(Read(nameof(FontScale), 100), MinFontScale, MaxFontScale);
    static int _pulse = Math.Clamp(Read(nameof(Pulse), DefaultPulse), 0, MaxPulse);
    static string _monitor = Read(nameof(Monitor), "");
    static string _font = Read(nameof(Font), "");

    public static bool Dots
    {
        get => _dots;
        set => Write(nameof(Dots), _dots = value);
    }

    public static bool LineBar
    {
        get => _lineBar;
        set => Write(nameof(LineBar), _lineBar = value);
    }

    public static bool Lyrics
    {
        get => _lyrics;
        set => Write(nameof(Lyrics), _lyrics = value);
    }

    public static bool LyricEffects
    {
        get => _lyricEffects;
        set => Write(nameof(LyricEffects), _lyricEffects = value);
    }

    public static bool Rim
    {
        get => _rim;
        set => Write(nameof(Rim), _rim = value);
    }

    public static bool AppVolume
    {
        get => _appVolume;
        set => Write(nameof(AppVolume), _appVolume = value);
    }

    public static bool TimerPauses
    {
        get => _timerPauses;
        set => Write(nameof(TimerPauses), _timerPauses = value);
    }

    public static bool Network
    {
        get => _network;
        set => Write(nameof(Network), _network = value);
    }

    public static bool Mic
    {
        get => _mic;
        set => Write(nameof(Mic), _mic = value);
    }

    public static bool Notices
    {
        get => _notices;
        set => Write(nameof(Notices), _notices = value);
    }

    public static bool HideFullscreen
    {
        get => _hideFullscreen;
        set => Write(nameof(HideFullscreen), _hideFullscreen = value);
    }

    public static int Scale
    {
        get => _scale;
        set => Write(nameof(Scale), _scale = Math.Clamp(value, MinScale, MaxScale));
    }

    public static int Gap
    {
        get => _gap;
        set => Write(nameof(Gap), _gap = Math.Clamp(value, 0, MaxGap));
    }

    public static ScreenEdge Edge
    {
        get => (ScreenEdge)_edge;
        set => Write(nameof(Edge), _edge = Math.Clamp((int)value, 0, 3));
    }

    public static ScreenAnchor Anchor
    {
        get => (ScreenAnchor)_anchor;
        set => Write(nameof(Anchor), _anchor = Math.Clamp((int)value, 0, 3));
    }

    public static int Along
    {
        get => _along;
        set => Write(nameof(Along), _along = Math.Clamp(value, -MaxAlong, MaxAlong));
    }

    public static string Monitor
    {
        get => _monitor;
        set => Write(nameof(Monitor), _monitor = value ?? "", RegistryValueKind.String);
    }

    public static bool WorkArea
    {
        get => _workArea;
        set => Write(nameof(WorkArea), _workArea = value);
    }

    public static string Font
    {
        get => _font;
        set => Write(nameof(Font), _font = value ?? "", RegistryValueKind.String);
    }

    public static int FontScale
    {
        get => _fontScale;
        set => Write(nameof(FontScale), _fontScale = Math.Clamp(value, MinFontScale, MaxFontScale));
    }

    public static int Pulse
    {
        get => _pulse;
        set => Write(nameof(Pulse), _pulse = Math.Clamp(value, 0, MaxPulse));
    }

    public static Color? Accent
    {
        get => _accent == 0 ? null : Color.FromRgb((byte)(_accent >> 16), (byte)(_accent >> 8), (byte)_accent);
        set => Write(nameof(Accent), _accent = value is { } c ? c.R << 16 | c.G << 8 | c.B : 0);
    }

    public static string[] Shelf
    {
        get => Read<string[]>(nameof(Shelf), []);
        set => Write(nameof(Shelf), value, RegistryValueKind.MultiString);
    }

    static bool ReadSwitch(string name, bool fallback = true) => Read(name, fallback ? 1 : 0) != 0;

    static T Read<T>(string name, T fallback)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(Key);
            return key?.GetValue(name) is T value ? value : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    static void Write(string name, bool value) => Write(name, value ? 1 : 0);

    static void Write(string name, int value) => Write(name, value, RegistryValueKind.DWord);

    static void Write(string name, object value, RegistryValueKind kind)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(Key);
            key.SetValue(name, value, kind);
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }
}
