using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using static DynamicIsland.Motion;

namespace DynamicIsland;

public partial class MainWindow
{
    readonly ClipBook _copies = new();
    Button? _clipPressed;
    Point _clipFrom;

    void Copied()
    {
        ClipBook.Entry? entry = _copies.Capture();
        if (entry == null) return;
        ClipToastText.Text = entry.Type == ClipBook.Kind.Image ? $"Картинка {entry.Hint}" : entry.Snippet;
        ShowTransient(View.ClipToast, 1.5);
    }

    void SyncClip()
    {
        ClipRows.Children.Clear();
        foreach (ClipBook.Entry entry in _copies.Items) ClipRows.Children.Add(ClipRow(entry));
        int count = _copies.Items.Count;
        ClipClear.SetVisible(count > 0);
        ClipHint.SetVisible(count == 0);
    }

    Button ClipRow(ClipBook.Entry entry)
    {
        var line = new Grid();
        line.Children.Add(entry.Picture is { } picture
            ? new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(5), ClipToBounds = true,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
                Child = new Image { Source = picture, Stretch = Stretch.Uniform },
            }
            : (UIElement)new Icon
            {
                Kind = entry.Type == ClipBook.Kind.Files ? Glyph.Tray : Glyph.Lines, Width = 17, Height = 17,
                Fill = (Brush)FindResource("Dim"), HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
            });
        line.Children.Add(new TextBlock
        {
            Text = entry.Snippet, Margin = new Thickness(29, 0, 62, 0), TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var hint = new TextBlock
        {
            Text = entry.Hint, FontSize = 11.5, Foreground = (Brush)FindResource("Dim"),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
        };
        line.Children.Add(hint);
        var pin = new Button
        {
            Style = (Style)FindResource("IconButton"),
            Width = 22,
            Height = 22,
            Content = new Icon { Kind = Glyph.Tray, Width = 12, Height = 12 },
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0,
            IsHitTestVisible = false,
        };
        line.Children.Add(pin);

        void Reveal(bool on)
        {
            bool shelf = OnShelf(entry);
            ((Icon)pin.Content).Kind = shelf ? Glyph.Check : Glyph.Tray;
            AutomationProperties.SetName(pin, shelf ? "На полке" : "На полку");
            pin.IsHitTestVisible = on;
            pin.BeginAnimation(OpacityProperty, new DoubleAnimation(on ? 1.0 : 0.0, Ms(on ? 130 : 220)));
            hint.BeginAnimation(OpacityProperty, new DoubleAnimation(on ? 0.0 : 1.0, Ms(on ? 90 : 260)));
        }

        pin.Click += (_, args) =>
        {
            args.Handled = true;
            if (Pin(entry)) Reveal(true);
        };

        var row = new Button { Style = (Style)FindResource("RowButton"), Content = line };
        row.MouseEnter += (_, _) => Reveal(true);
        row.MouseLeave += (_, _) => Reveal(false);
        row.PreviewMouseLeftButtonDown += (_, args) =>
        {
            if (pin.IsMouseOver) return;
            args.Handled = true;
            _clipPressed = row;
            _clipFrom = args.GetPosition(this);
            row.CaptureMouse();
        };
        row.PreviewMouseMove += (_, args) =>
        {
            if (_clipPressed != row || args.LeftButton != MouseButtonState.Pressed) return;
            Vector moved = args.GetPosition(this) - _clipFrom;
            if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            _clipPressed = null;
            row.ReleaseMouseCapture();
            CarryClip(entry, row);
        };
        row.PreviewMouseLeftButtonUp += (_, args) =>
        {
            if (_clipPressed != row) return;
            args.Handled = true;
            _clipPressed = null;
            row.ReleaseMouseCapture();
            _copies.Restore(entry);
        };
        row.MouseRightButtonUp += (_, args) =>
        {
            _copies.Remove(entry);
            args.Handled = true;
        };
        return row;
    }

    void ClipClear_Click(object sender, RoutedEventArgs e) => _copies.Clear();

    bool Pin(ClipBook.Entry entry)
    {
        string[] paths;
        try
        {
            paths = entry.Type == ClipBook.Kind.Files ? entry.Paths : [ShelfPin.Put(entry)];
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return false;
        }
        entry.OnShelf = paths;
        return _shelf.Add(paths) || OnShelf(entry);
    }

    bool OnShelf(ClipBook.Entry entry) =>
        entry.OnShelf is { } paths &&
        paths.Any(p => _shelf.Items.Any(i => string.Equals(i.Path, p, StringComparison.OrdinalIgnoreCase)));

    void CarryClip(ClipBook.Entry entry, UIElement from)
    {
        DataObject data = ClipBook.Carry(entry);
        DragDropEffects effects = entry.Type == ClipBook.Kind.Files
            ? DragDropEffects.Copy | DragDropEffects.Link
            : DragDropEffects.Copy;
        from.BeginAnimation(OpacityProperty, new DoubleAnimation(CarriedOpacity, Ms(120)));
        _draggingOut = true;
        try { DragDrop.DoDragDrop(from, data, effects); }
        catch (Exception ex) { App.Log(ex); }
        finally { _draggingOut = false; }
        from.BeginAnimation(OpacityProperty, new DoubleAnimation(1, Ms(200)));
        ResyncPointer(CollapseDelay);
    }
}
