using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using static DynamicIsland.Motion;

namespace DynamicIsland;

public partial class MainWindow
{
    static readonly TimeSpan PeekRest = TimeSpan.FromMilliseconds(230), PeekGone = TimeSpan.FromMilliseconds(180);
    static readonly int[] AlongSteps = [-50, -20, -8, 0, 8, 20, 50];

    readonly Dictionary<Button, (Func<Peek.Choice[]> Get, bool Chips)> _peeks = [];
    readonly DispatcherTimer _peekTimer = new();
    Button? _peekFor;
    Button? _peekOwner;
    Peek? _peek;

    void Peekable(Button row, Func<Peek.Choice[]> choices, bool chips = false)
    {
        _peeks[row] = (choices, chips);
        row.MouseEnter += (_, _) => PeekDue(row, PeekRest);
        row.MouseLeave += (_, _) => PeekDue(row, PeekGone);
    }

    void PeekDue(Button row, TimeSpan after)
    {
        _peekFor = row;
        _peekTimer.Stop();
        _peekTimer.Tick -= OnPeekDue;
        _peekTimer.Tick += OnPeekDue;
        _peekTimer.Interval = after;
        _peekTimer.Start();
    }

    void OnPeekDue(object? sender, EventArgs e)
    {
        _peekTimer.Stop();
        Button? row = _peekFor;
        if (row == null) return;
        if (_peek is { IsMouseOver: true }) return;
        if (row.IsMouseOver) Put(row);
        else Tuck();
    }

    void Put(Button row)
    {
        if (_peekOwner == row) return;
        Tuck();
        (Func<Peek.Choice[]> get, bool chips) = _peeks[row];

        var peek = new Peek(chips) { Width = row.ActualWidth };
        peek.MouseLeave += (_, _) => PeekDue(row, PeekGone);
        PeekLayer.Children.Add(peek);
        peek.Fill(get());
        peek.Picked += RefreshPeek;
        Locate(row, peek);
        peek.IsHitTestVisible = true;
        peek.BeginAnimation(OpacityProperty, new DoubleAnimation(1, Ms(130)));
        var grow = new DoubleAnimation(1, Ms(190)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        peek.Grow.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        peek.Grow.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        _peek = peek;
        _peekOwner = row;
    }

    void Locate(Button row, Peek peek)
    {
        peek.UpdateLayout();
        FrameworkElement page = _views.Values.First(v => row.IsDescendantOf(v));
        Rect at = row.TransformToAncestor(Host).TransformBounds(new Rect(0, 0, row.ActualWidth, row.ActualHeight));
        Rect room = page.TransformToAncestor(Host).TransformBounds(new Rect(0, 0, page.ActualWidth, page.ActualHeight));
        double top = at.Bottom + 4;
        if (top + peek.ActualHeight > room.Bottom - 4) top = Math.Max(room.Top + 4, at.Top - peek.ActualHeight - 4);
        Canvas.SetLeft(peek, at.Left);
        Canvas.SetTop(peek, top);
        peek.Grow.CenterX = peek.ActualWidth / 2;
        peek.Grow.CenterY = top > at.Bottom ? 0 : peek.ActualHeight;
    }

    void RefreshPeek()
    {
        if (_peek is not { } peek || _peekOwner is not { } row) return;
        peek.Fill(_peeks[row].Get());
        Locate(row, peek);
    }

    void Tuck()
    {
        Peek? gone = _peek;
        _peek = null;
        _peekOwner = null;
        if (gone == null) return;
        gone.IsHitTestVisible = false;
        Dispatcher.BeginInvoke(new Action(PeekUnder), DispatcherPriority.Input);
        var fade = new DoubleAnimation(0, Ms(110));
        fade.Completed += (_, _) => PeekLayer.Children.Remove(gone);
        gone.BeginAnimation(OpacityProperty, fade);
    }

    void PeekUnder()
    {
        if (_peek != null || _peekTimer.IsEnabled) return;
        if (VisualTreeHelper.HitTest(Host, Mouse.GetPosition(Host)) is not HitTestResult hit) return;
        for (DependencyObject? d = hit.VisualHit; d != null; d = VisualTreeHelper.GetParent(d))
            if (d is Button row && _peeks.ContainsKey(row))
            {
                PeekDue(row, PeekRest);
                return;
            }
    }

    Peek.Choice[] EdgeChoices() =>
    [
        new("Сверху", () => SetEdge(ScreenEdge.Top), Settings.Edge == ScreenEdge.Top),
        new("Слева", () => SetEdge(ScreenEdge.Left), Settings.Edge == ScreenEdge.Left),
        new("Справа", () => SetEdge(ScreenEdge.Right), Settings.Edge == ScreenEdge.Right),
        new("Снизу", () => SetEdge(ScreenEdge.Bottom), Settings.Edge == ScreenEdge.Bottom),
    ];

    Peek.Choice[] MonitorChoices()
    {
        ScreenInfo[] all = CachedScreens();
        return all.Select(s => new Peek.Choice(
            $"{s.Label} · {Math.Round(s.Px.Width / s.ScaleX)}×{Math.Round(s.Px.Height / s.ScaleY)} · {Math.Round(s.ScaleX * 100)}%",
            () => SetMonitor(s.Name),
            string.Equals(s.Name, _screen.Name, StringComparison.OrdinalIgnoreCase))).ToArray();
    }

    Peek.Choice[] AnchorChoices()
    {
        bool across = Placement.Horizontal(Settings.Edge);
        return
        [
            new(across ? "У левого края" : "У верхнего края", () => SetAnchor(ScreenAnchor.Start), Settings.Anchor == ScreenAnchor.Start),
            new("По центру", () => SetAnchor(ScreenAnchor.Center), Settings.Anchor == ScreenAnchor.Center),
            new(across ? "У правого края" : "У нижнего края", () => SetAnchor(ScreenAnchor.End), Settings.Anchor == ScreenAnchor.End),
        ];
    }

    Peek.Choice[] AlongChoices() =>
        [.. AlongSteps.Select(step => new Peek.Choice((step > 0 ? "+" : "") + step, () => AddAlong(step),
            step == 0 && Settings.Along == 0))];

    Peek.Choice[] GapChoices() =>
        [.. GapOptions.Select(px => new Peek.Choice(px + " px", () => SetGap(px), px == Settings.Gap))];

    Peek.Choice[] FontChoices()
    {
        var list = new List<Peek.Choice> { new("SF Pro", () => SetFace(""), string.IsNullOrWhiteSpace(Settings.Font)) };
        list.AddRange(FontPack.Families()
            .Where(family => !FontPack.Bundled.Contains(family, StringComparer.OrdinalIgnoreCase))
            .Select(family => new Peek.Choice(family, () => SetFace(family),
                string.Equals(family, Settings.Font, StringComparison.OrdinalIgnoreCase))));
        return list.ToArray();
    }

    Peek.Choice[] FontScaleChoices() =>
        [.. FontScales.Select(pc => new Peek.Choice(pc + "%", () => SetFontScale(pc), pc == Settings.FontScale))];

    Peek.Choice[] SizeChoices() =>
        [.. ScaleOptions.Select(pc => new Peek.Choice(pc + "%", () => SetScale(pc), pc == Settings.Scale))];
}
