using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DynamicIsland;

/// <summary>
/// What the island keeps of the clipboard: the last few things copied, text, a picture, a handful of files, so that
/// an hour of Ctrl+C does not wash away the one line worth pasting. Only what is on the clipboard right now is read,
/// and only a copy put on the shelf on purpose is ever written to disk. Nothing leaves the computer, and a copy made
/// as protected — a password — is left out altogether.
/// </summary>
sealed class ClipBook
{
    public const int Keep = 8; // entries held: a page of them, no more
    const int MaxText = 200000; // signs of text kept: pasting back must give what was copied, so the cap sits far above what a clipboard holds

    public enum Kind { Text, Image, Files }

    /// <summary>One thing copied: enough to show it in a row and to put it back as it was.</summary>
    public sealed class Entry
    {
        public Kind Type { get; init; }
        /// <summary>All of it, exactly as it was copied, for putting back.</summary>
        public string Body { get; init; } = "";
        /// <summary>The first line of it, for a row and the bubble to read.</summary>
        public string Snippet { get; init; } = "";
        public string Hint { get; init; } = "";
        /// <summary>A picture, small enough to draw in a row; the whole one waits encoded behind it.</summary>
        public ImageSource? Picture { get; init; }
        public byte[]? Encoded { get; init; }
        public string[] Paths { get; init; } = [];
        /// <summary>Where the copy lies once it has been put on the shelf; nothing while it is only a copy.</summary>
        public string[]? OnShelf { get; set; }
        /// <summary>Two copies of the same words are still one thing to paste.</summary>
        public string Key { get; init; } = "";
    }

    readonly List<Entry> _items = new();

    /// <summary>Whether the island itself is putting something back, so that its own copy is not kept again.</summary>
    bool _restoring;

    public IReadOnlyList<Entry> Items => _items;

    /// <summary>Something was copied or taken away.</summary>
    public event Action? Changed;

    /// <summary>
    /// Reads whatever is on the clipboard now and puts it in front of the rest.
    /// </summary>
    /// <returns>The entry taken in, or null when there was nothing worth keeping.</returns>
    public Entry? Capture()
    {
        if (_restoring) return null;
        Entry? entry = null;
        try { Hold(() => entry = Read()); }
        catch (Exception ex) { App.Log(ex); return null; } // a clipboard that will not open at all is not an error of ours
        if (entry == null) return null;

        int known = _items.FindIndex(e => e.Key == entry.Key);
        if (known >= 0) _items.RemoveAt(known); // the same thing again: it moves to the front, it does not double
        _items.Insert(0, entry);
        if (_items.Count > Keep) _items.RemoveRange(Keep, _items.Count - Keep);
        Changed?.Invoke();
        return entry;
    }

    /// <summary>Whatever the clipboard holds right now, as one entry — or nothing worth keeping.</summary>
    static Entry? Read()
    {
        // a password manager says so on the clipboard itself; such copies are not for the island to remember
        if (Clipboard.ContainsData("IsProtected")) return null;
        if (Clipboard.ContainsFileDropList()) return Of(Clipboard.GetFileDropList());
        if (Clipboard.ContainsImage()) return Shot(Clipboard.GetImage());
        if (Clipboard.ContainsText()) return Words(Clipboard.GetText());
        return null;
    }

    /// <summary>
    /// Does something to the clipboard, waiting on the app that had it last: the system hands it to one window at a
    /// time, and the one letting go of it is often still holding it shut when the change is announced.
    /// </summary>
    static void Hold(Action what)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { what(); return; }
            catch (COMException) when (attempt < 5) { Thread.Sleep(40 * (attempt + 1)); }
        }
    }

    public void Remove(Entry entry)
    {
        if (!_items.Remove(entry)) return;
        Changed?.Invoke();
    }

    public void Clear()
    {
        if (_items.Count == 0) return;
        _items.Clear();
        Changed?.Invoke();
    }

    /// <summary>Puts an entry back where it was copied from, so that the next paste finds it.</summary>
    public void Restore(Entry entry)
    {
        _restoring = true;
        try
        {
            Hold(() =>
            {
                switch (entry.Type)
                {
                    case Kind.Files:
                        var list = new StringCollection();
                        list.AddRange(entry.Paths);
                        Clipboard.SetFileDropList(list);
                        break;
                    case Kind.Image when entry.Encoded != null:
                        Clipboard.SetImage(Load(entry.Encoded));
                        break;
                    default:
                        if (entry.Body.Length > 0) Clipboard.SetText(entry.Body);
                        break;
                }
            });
            if (_items.Remove(entry)) // the copy just brought back leads the rest: it is the one to paste now
            {
                _items.Insert(0, entry);
                Changed?.Invoke();
            }
        }
        catch (Exception ex) { App.Log(ex); }
        finally
        {
            // the update the system sends about this copy arrives after the call, not during it
            Application.Current.Dispatcher.BeginInvoke(new Action(() => _restoring = false),
                System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    /// <summary>
    /// What a copy is handed over as when it is carried out of the island: files as files, a picture as a picture,
    /// words as words — the same shapes Restore puts on the clipboard, only offered to whoever takes the drop.
    /// </summary>
    public static DataObject Carry(Entry entry)
    {
        var data = new DataObject();
        switch (entry.Type)
        {
            case Kind.Files:
                var list = new StringCollection();
                list.AddRange(entry.Paths);
                data.SetFileDropList(list);
                break;
            case Kind.Image when entry.Encoded != null:
                data.SetImage(Load(entry.Encoded));
                break;
            default:
                data.SetText(entry.Body);
                break;
        }
        return data;
    }

    static Entry? Words(string text)
    {
        string body = text.Length > MaxText ? text[..MaxText] : text;
        if (string.IsNullOrWhiteSpace(body)) return null;
        string[] lines = body.Split('\n');
        string first = Line(lines[0]);
        if (first.Length == 0) return null;
        return new Entry
        {
            Type = Kind.Text,
            Body = body,
            Snippet = first,
            Key = "t" + body,
            Hint = lines.Length > 1 ? $"{lines.Length} строки · {body.Length}" : $"{body.Length} знаков",
        };
    }

    /// <summary>A picture: kept encoded, because a screen of them in raw bytes would be worth more than the island.</summary>
    static Entry? Shot(BitmapSource image)
    {
        byte[] bytes = Encode(image);
        return new Entry
        {
            Type = Kind.Image,
            Body = "Картинка",
            Snippet = "Картинка",
            Key = $"i{image.PixelWidth}x{image.PixelHeight}:{Convert.ToHexString(bytes.AsSpan(0, Math.Min(64, bytes.Length)))}",
            Picture = Thumb(image),
            Encoded = bytes,
            Hint = $"{image.PixelWidth}×{image.PixelHeight}",
        };
    }

    static Entry? Of(StringCollection paths)
    {
        var kept = paths.Cast<string>().Where(p => p.Length > 0).ToArray();
        if (kept.Length == 0) return null;
        string first = System.IO.Path.GetFileName(kept[0].TrimEnd('\\'));
        return new Entry
        {
            Type = Kind.Files,
            Body = first.Length > 0 ? first : kept[0],
            Snippet = first.Length > 0 ? first : kept[0],
            Paths = kept,
            Key = "f" + string.Join(';', kept),
            Hint = kept.Length > 1 ? $"и ещё {kept.Length - 1}" : "",
        };
    }

    /// <summary>The row reads as one line, however the copy is made of many.</summary>
    static string Line(string raw)
    {
        string one = raw.Replace('\r', ' ').Replace('\t', ' ');
        while (one.Contains("  ")) one = one.Replace("  ", " ");
        one = one.Trim();
        return one.Length > 72 ? one[..72].TrimEnd() + "…" : one;
    }

    static byte[] Encode(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    static BitmapSource Load(byte[] bytes)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.StreamSource = new MemoryStream(bytes);
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return image;
    }

    /// <summary>The picture of a row: no longer than 44 px on its long side, drawn off the whole thing once.</summary>
    static ImageSource Thumb(BitmapSource image)
    {
        double side = 44.0 / Math.Max(image.PixelWidth, image.PixelHeight);
        var small = new TransformedBitmap(image, new ScaleTransform(side, side));
        small.Freeze();
        return small;
    }
}
