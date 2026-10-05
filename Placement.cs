using System.Runtime.InteropServices;
using System.Windows;

namespace DynamicIsland;

enum ScreenEdge { Top, Bottom, Left, Right }

/// <summary>Where along the edge the island stands. Free is what a drag leaves behind: the middle of the island
/// stays exactly where it was let go, so it reads as Center until something moves it again.</summary>
enum ScreenAnchor { Center, Start, End, Free }

/// <summary>A screen as Windows sees it: its whole surface and the part a taskbar leaves free, both in px, and the
/// scale it draws at.</summary>
readonly record struct ScreenInfo(IntPtr Handle, string Name, string Label, Rect Px, Rect Work, double ScaleX, double ScaleY);

/// <summary>
/// Where the island's window stands, and which way round it is. The island itself is drawn for the top of the screen
/// and never re-laid out: turning it to a side of the screen is one rotation of the whole thing about the point its
/// top edge stands on, so every spring, the goo between the pill and its bubble and the clip that reveals them carry
/// on untouched. The bottom of the screen cannot be reached that way — no turn of 180° leaves the writing right way
/// up — so there the island is asked to grow upwards from the edge it hangs on instead, which is the same drawing
/// read from the other end.
/// </summary>
static class Placement
{
    /// <summary>Room the window gives the island along its edge and across it, in the island's own units: enough for
    /// the widest state with its bubble beside it, and the tallest with the furthest gap that can be picked.</summary>
    public const double Along = 760, Cross = 700;

    /// <summary>How far from the near end of the window an island anchored to that end stands.</summary>
    public const double NearEnd = 10;

    public static bool Horizontal(ScreenEdge edge) => edge is ScreenEdge.Top or ScreenEdge.Bottom;

    /// <summary>Which way the island turns to stand on this edge. Only the sides of the screen need turning; the
    /// bottom is reached by making the island grow upwards from the edge it hangs on instead, which would take the
    /// writing upside down in any turn of 180°.</summary>
    public static double Angle(ScreenEdge edge) => edge switch
    {
        ScreenEdge.Left => -90,
        ScreenEdge.Right => 90,
        _ => 0,
    };

    /// <summary>True when the island hangs under an edge and grows away from it downwards; false when it stands on
    /// one and grows up from it — which is how it is drawn, and how it is turned for the sides of the screen.</summary>
    public static bool GrowsUp(ScreenEdge edge) => edge == ScreenEdge.Bottom;

    /// <summary>False when the island is turned so that the way it reads runs against the way the edge does, so a
    /// step along the edge taken in the settings has to be taken the other way inside the island.</summary>
    public static bool Follows(ScreenEdge edge) => edge != ScreenEdge.Left;

    /// <summary>Which way the shadow falls: away from the edge the island stands on.</summary>
    public static double ShadowAway(ScreenEdge edge) => edge switch
    {
        ScreenEdge.Bottom => 90,
        ScreenEdge.Left => 0,
        ScreenEdge.Right => 180,
        _ => 270,
    };

    /// <summary>The window's size for an island on this edge, in the island's units. A side of the screen is drawn
    /// turned: what the island needs along its edge is the window's own height, and the turn of the drawing swings
    /// its width into that height, so a side window is square — anything else would clip the ends off the island.</summary>
    public static Size BoxDip(ScreenEdge edge) =>
        Horizontal(edge) ? new(Along, Cross) : new(Along, Along);

    // ───────────────────────── the screens ─────────────────────────

    delegate bool MonitorEnum(IntPtr handle, IntPtr window, ref RECT box, IntPtr data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public uint flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string name;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnum callback, IntPtr data);

    // the wide export: the ansi one wants a name of 32 bytes rather than 32 characters and refuses the struct
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);

    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("shcore.dll")]
    static extern int GetDpiForMonitor(IntPtr monitor, int kind, out uint x, out uint y);

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);

    const uint MONITOR_DEFAULTTONEAREST = 2, MONITORINFOF_PRIMARY = 1;
    const uint SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;
    static readonly IntPtr Topmost = new(-1);

    /// <summary>Every screen attached, in the order Windows lists them, each with the scale it is drawn at. A screen
    /// that will not say, or a machine with nothing attached, leaves the primary alone.</summary>
    public static ScreenInfo[] Screens()
    {
        var found = new List<ScreenInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr handle, IntPtr _, ref RECT box, IntPtr _) =>
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (!GetMonitorInfo(handle, ref info)) return true;
            uint x = 96, y = 96;
            // an older driver that will not give a scale: the screen draws as told at 100%
            try { GetDpiForMonitor(handle, 0, out x, out y); }
            catch (Exception ex) { App.Log(ex); }
            found.Add(new ScreenInfo(handle, info.name, Label(info),
                new Rect(box.Left, box.Top, box.Right - box.Left, box.Bottom - box.Top),
                new Rect(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right - info.rcWork.Left, info.rcWork.Bottom - info.rcWork.Top),
                x / 96.0, y / 96.0));
            return true;
        }, IntPtr.Zero);
        return found.Count > 0 ? found.ToArray() : [];
    }

    static string Label(MONITORINFOEX info)
    {
        // "\\.\DISPLAY1" is what the system calls it: the number is what a person picks it by
        int at = info.name.LastIndexOf('\\');
        return at >= 0 ? info.name[(at + 1)..] : info.name;
    }

    /// <summary>The screen picked in the settings, or the one the island already stands on, or the main one. A screen
    /// that has since been unplugged must never leave the island nowhere to be.</summary>
    public static ScreenInfo Chosen(IntPtr hwnd)
    {
        ScreenInfo[] all = Screens();
        if (all.Length == 0) return Default(hwnd);
        if (Settings.Monitor is { Length: > 0 } name)
            foreach (ScreenInfo s in all)
                if (string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) return s;
        foreach (ScreenInfo s in all)
            if (s.Handle == MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST)) return s;
        foreach (ScreenInfo s in all)
            if (string.Equals(s.Name, @"\\.\DISPLAY1", StringComparison.OrdinalIgnoreCase)) return s;
        return all[0];
    }

    static ScreenInfo Default(IntPtr hwnd)
    {
        var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (GetMonitorInfo(monitor, ref info))
            return new ScreenInfo(monitor, info.name, Label(info),
                new(info.rcMonitor.Left, info.rcMonitor.Top,
                    info.rcMonitor.Right - info.rcMonitor.Left, info.rcMonitor.Bottom - info.rcMonitor.Top),
                new(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right - info.rcWork.Left,
                    info.rcWork.Bottom - info.rcWork.Top), 1, 1);
        // nothing to ask: leave the island where the old one stood, on the screen WPF says is the main
        return new(monitor, "", "1", new(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight),
            new(0, 0, SystemParameters.WorkArea.Width, SystemParameters.WorkArea.Height), 1, 1);
    }

    // ───────────────────────── where the window stands ─────────────────────────

    /// <summary>How far the island may be pushed along its edge before it runs past the screen it was put on: from
    /// the middle for one standing in the middle, and the whole of the room left over for one held by an end.</summary>
    public static int Travel(ScreenInfo screen, ScreenEdge edge, ScreenAnchor anchor)
    {
        Rect box = Settings.WorkArea ? screen.Work : screen.Px;
        double its = Horizontal(edge) ? box.Width / screen.ScaleX : box.Height / screen.ScaleY;
        double room = Math.Max(0, its - Along);
        return (int)(anchor == ScreenAnchor.Center ? room / 2 : room);
    }

    public static int Travel(ScreenInfo screen, ScreenEdge edge) => Travel(screen, edge, Settings.Anchor);

    /// <summary>The place and size of the window, in px: the edge it stands against, the distance from it and the
    /// distance along it picked in the settings, told to the screen that carries it. The size is the box the island
    /// needs at its largest, so nothing in it moves when a state opens.</summary>
    public static Rect WindowPx(ScreenInfo screen, ScreenEdge edge, ScreenAnchor anchor, double along)
    {
        Rect bound = Settings.WorkArea ? screen.Work : screen.Px;
        Size box = BoxDip(edge);
        bool side = !Horizontal(edge);
        double scale = side ? screen.ScaleY : screen.ScaleX;
        double length = Along * scale;

        // against the edge the island stands on the window is flush with it: the distance from the edge is taken
        // inside, where the island moves off it whatever way round it has been turned
        double cross = edge switch
        {
            ScreenEdge.Bottom => bound.Bottom - box.Height * screen.ScaleY,
            ScreenEdge.Right => bound.Right - box.Width * screen.ScaleX,
            ScreenEdge.Left => bound.Left,
            _ => bound.Top,
        };

        // along the edge the step is read the way the edge is: from its near end, its middle, or its far end, and
        // the distance picked runs inwards from wherever the anchor holds it
        double from = side ? bound.Top : bound.Left, span = side ? bound.Height : bound.Width;
        double start = anchor switch
        {
            ScreenAnchor.Start => from + along * scale,
            ScreenAnchor.End => from + span - length - along * scale,
            _ => from + (span - length) / 2 + along * scale,
        };
        // however far the setting is turned, the whole of the window stays on the screen it was put on
        start = Math.Clamp(start, from, Math.Max(from, from + span - length));

        double wide = box.Width * screen.ScaleX, tall = box.Height * screen.ScaleY;
        return side ? new(cross, start, wide, tall) : new(start, cross, wide, tall);
    }

    /// <summary>Sends the window to that place. The island's size is told in the units WPF lays it out in, so px only
    /// ever arrive here — setting Left and Top from WPF would have the screen's own scale read into them again.</summary>
    public static void Move(IntPtr hwnd, Rect px, Size dip, double sizeX, double sizeY) =>
        SetWindowPos(hwnd, Topmost, (int)Math.Round(px.X), (int)Math.Round(px.Y),
            (int)Math.Round(dip.Width * sizeX), (int)Math.Round(dip.Height * sizeY), SWP_NOACTIVATE | SWP_SHOWWINDOW);

    /// <summary>Where the point the island turns about — the middle of the edge it was drawn against — has to come to
    /// rest inside the window. Everything the island shows hangs from it.</summary>
    public static Point Anchor(ScreenEdge edge, Size box) => edge switch
    {
        ScreenEdge.Left => new(0, box.Height / 2),
        ScreenEdge.Right => new(box.Width, box.Height / 2),
        _ => new(box.Width / 2, 0),
    };

    /// <summary>How far from the near end of its window the middle of the island comes to rest along the edge, in the
    /// island's own units: the middle of the window for one standing in the middle of the screen, or one end over for
    /// an island held by an end of its edge. Read along the screen, whichever way the island is turned.</summary>
    public static double Rest(ScreenAnchor anchor, double width, double size) => anchor switch
    {
        ScreenAnchor.Start => NearEnd + width * size / 2,
        ScreenAnchor.End => Along - NearEnd - width * size / 2,
        _ => Along / 2,
    };

    /// <summary>How far along its own axis the middle of the island has to be slid so that it stands anchored at this
    /// end of the edge instead of in the middle of it: the pill's near end comes to rest by the window's own end, and
    /// the island grows away from the edge from there. Told in the island's units, the way round it is turned.</summary>
    public static double AlongShift(ScreenEdge edge, ScreenAnchor anchor, double width, double size)
    {
        double off = Rest(anchor, width, size) - Along / 2;
        return Follows(edge) ? off : -off;
    }
}
