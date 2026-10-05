using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DynamicIsland;

/// <summary>
/// The values a row can take, laid out under it as soon as the pointer rests there: what the click would only
/// step through, one at a time, is all of it in view, and picking one leaves the rest there to be compared.
/// A row of words takes the list; a row of short numbers takes the chips, which pack tighter.
/// </summary>
public sealed class Peek : Border
{
    /// <param name="Current">The value the row stands on: drawn lit, with the tick beside it.</param>
    public sealed record Choice(string Text, Action Pick, bool Current = false);

    const int Plate = 0xFF;   // the panel is opaque: what lies under it is a menu page, not a backdrop
    const int Ticked = 0x4D;  // ...and the chip of the value picked is the brighter one
    static readonly Color Shade = Color.FromRgb(0x24, 0x24, 0x26),
        Dimmed = Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF);

    readonly Panel _body;
    readonly bool _chips;

    /// <summary>What the values come out with: its centre is the edge they come out from.</summary>
    public readonly ScaleTransform Grow = new(0.94, 0.94);

    /// <summary>Raised after a value has been picked, so the tick can be moved to it.</summary>
    public event Action? Picked;

    public Peek(bool chips)
    {
        _chips = chips;
        _body = chips
            ? new WrapPanel { Margin = new Thickness(6, 5, 6, 6) }
            : new StackPanel { Margin = new Thickness(4, 3, 4, 4) };
        Child = new ScrollViewer
        {
            Content = _body,
            // the wheel is the island's own past the end of what fits: only the bar is hidden, not the scrolling
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 200,
            Focusable = false,
        };
        Background = new SolidColorBrush(Color.FromArgb(Plate, Shade.R, Shade.G, Shade.B));
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(13);
        RenderTransform = Grow;
        Opacity = 0;
        IsHitTestVisible = false;
    }

    /// <summary>Lays the values out again: after a pick the tick has to move to another one.</summary>
    public void Fill(IReadOnlyList<Choice> choices)
    {
        _body.Children.Clear();
        foreach (Choice choice in choices) _body.Children.Add(_chips ? Chip(choice) : Row(choice));
    }

    UIElement Row(Choice choice)
    {
        var line = new Grid();
        line.Children.Add(new TextBlock
        {
            Text = choice.Text,
            FontSize = 12.5,
            Foreground = Lit(choice.Current),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (choice.Current) line.Children.Add(new Icon
        {
            Kind = Glyph.Check,
            Width = 12,
            Height = 12,
            Fill = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var row = new Button { Style = (Style)FindResource("PeekRow"), Content = line };
        row.Click += (_, _) => { choice.Pick(); Picked?.Invoke(); };
        return row;
    }

    UIElement Chip(Choice choice)
    {
        var chip = new Button
        {
            Style = (Style)FindResource("ChipButton"),
            Content = new TextBlock { Text = choice.Text, Margin = new Thickness(9, 0, 9, 0) },
            Height = 26,
            FontSize = 11.5,
            Foreground = choice.Current ? Brushes.Black : new SolidColorBrush(Dimmed),
            Background = new SolidColorBrush(Color.FromArgb((byte)(choice.Current ? Ticked : 0x1F), 0xFF, 0xFF, 0xFF)),
        };
        chip.Click += (_, _) => { choice.Pick(); Picked?.Invoke(); };
        return chip;
    }

    Brush Lit(bool on) => on ? Brushes.White : new SolidColorBrush(Dimmed);
}
