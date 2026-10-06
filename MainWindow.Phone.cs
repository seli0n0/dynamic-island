using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static DynamicIsland.Motion;

namespace DynamicIsland;

/// <summary>
/// The island's side of the bridge: the page that shows the code a phone reads, the switches that open the doors, and
/// the answers to what arrives through them. A saying goes to the clipboard, where the ring of copies already watches;
/// a file goes to the shelf, beside the ones dragged there from Explorer.
/// </summary>
public partial class MainWindow
{
    readonly Bridge _bridge = new();
    string _phoneGuest = "", _phoneBattery = "";
    Bridge.Asking? _pairAsk;

    /// <summary>
    /// An answer given from the command line instead of by hand, for checking both ways of the door on a running
    /// island. A person who can start the island with a switch can already reach the page, so nothing is let in here
    /// that the page does not offer.
    /// </summary>
    bool? _answeredAhead = Of(Argument("--pair"));

    static bool? Of(string? answer) => answer?.ToLowerInvariant() switch
    {
        "yes" or "on" or "1" => true,
        "no" or "off" or "0" => false,
        _ => null,
    };

    void WireBridge()
    {
        _bridge.Said += said => OnUi(() => PhoneSaid(said));
        _bridge.Laid += path => OnUi(() => PhoneLaid(path));
        _bridge.Visited += who => OnUi(() => { _phoneGuest = who; RefreshPhone(); });
        _bridge.Battery += (_, level) => OnUi(() => { _phoneBattery = level + "%"; RefreshPhone(); });
        _bridge.Notified += (who, text) => OnUi(() => Notify(Glyph.Note, _indigo, who, text));
        _bridge.Wanted += ask => OnUi(() =>
        {
            _pairAsk = ask;
            RefreshPhone();
            Notify(Glyph.Phone, _orange, "Телефон просит пару", $"{ask.Phone} · ответьте на странице «Телефон»", force: true);
            if (_answeredAhead is { } given)
            {
                _answeredAhead = null; // the switch answers the first request only, the rest are the owner's
                _bridge.AnswerPair(given);
            }
        });
        _bridge.Answered += (who, paired) => OnUi(() =>
        {
            _pairAsk = null;
            RefreshPhone();
            // a refusal is seen on the page the moment it is given; only a yes needs saying out loud
            if (paired) Notify(Glyph.Phone, _green, "Пара разрешена", who, force: true);
        });
    }

    /// <summary>Bridge words arrive on the wire's own threads; the island is only touched from its own.</summary>
    void OnUi(Action action)
    {
        try { Dispatcher.InvokeAsync(action); }
        catch (Exception ex) { App.Log(ex); }
    }

    void PhoneSaid(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            App.Log(ex);
            Notify(Glyph.Phone, _red, "Текст не лёг", "Буфер обмена занят другим окном");
            return;
        }
        Copied(); // the same ring that watches Ctrl+C keeps the phone's saying, and its toast
    }

    void PhoneLaid(string path)
    {
        _shelf.Add(new[] { path });
        Notify(Glyph.Phone, _green, "Файл на полке", Path.GetFileName(path));
    }

    void PhoneRow_Click(object sender, RoutedEventArgs e)
    {
        RefreshPhone();
        ShowPanel(Panel.Phone);
    }

    void PhoneBack_Click(object sender, RoutedEventArgs e) => ShowPanel(Panel.Menu);

    void PairYes_Click(object sender, RoutedEventArgs e) => _bridge.AnswerPair(true);

    void PairNo_Click(object sender, RoutedEventArgs e) => _bridge.AnswerPair(false);

    void Bridge_Click(object sender, RoutedEventArgs e)
    {
        Settings.Bridge = !Settings.Bridge;
        UpdateSwitches(true);
        if (Settings.Bridge) OpenBridge();
        else
        {
            _bridge.Stop();
            _phoneGuest = _phoneBattery = "";
            _pairAsk = null;
            RefreshPhone();
        }
    }

    /// <summary>The KDE face is only chosen when the doors are opened, so a live bridge has to be opened again.</summary>
    void BridgeKde_Click(object sender, RoutedEventArgs e)
    {
        Settings.BridgeKde = !Settings.BridgeKde;
        UpdateSwitches(true);
        if (!Settings.Bridge) return;
        _bridge.Stop();
        _pairAsk = null;
        OpenBridge();
    }

    void OpenBridge()
    {
        try { _bridge.Start(); }
        catch (Exception ex)
        {
            App.Log(ex);
            Notify(Glyph.Phone, _red, "Мост не открылся", ex.Message, force: true);
        }
        RefreshPhone();
    }

    /// <summary>The page, the menu line and the address under the code, all from what the bridge says now.</summary>
    void RefreshPhone()
    {
        bool live = Settings.Bridge;
        string url = _bridge.Url;
        PhoneState.Text = live ? $"Открыт · :{Bridge.Port}" : "Выключен";
        PhoneState.Foreground = live ? _green : _dim;
        PhoneUrl.Text = live && url.Length > 0 ? url : "—";
        PhoneNote.Text = live
            ? "Наведите камеру телефона на код · адрес можно открыть и вручную · брандмауэр должен разрешить острову вход"
            : "Включите мост, чтобы телефон перекидывал текст и файлы на остров";
        PhoneDevice.Text = live ? (_phoneGuest.Length > 0 ? _phoneGuest : "никто не заходил") : "—";
        PhoneBattery.Text = live && _phoneBattery.Length > 0 ? _phoneBattery : "—";
        MenuPhone.Text = live && _phoneBattery.Length > 0 ? _phoneBattery : live ? "он" : "";
        if (live && url.Length > 0 && Code(url, out BitmapSource? code, out int side))
        {
            int grown = Math.Max(2, PlateRoom / side) * side;
            PhoneCode.Width = PhoneCode.Height = grown;
            PhoneCode.Source = code;
        }
        else PhoneCode.Source = null;
        CodePlate.Opacity = PhoneCode.Source == null ? 0.25 : 1;
        PairCard.Visibility = _pairAsk == null ? Visibility.Collapsed : Visibility.Visible;
        if (_pairAsk is { } ask)
        {
            PairWho.Text = ask.Phone;
            PairPrint.Text = $"у телефона {Print(ask.Theirs)}\nу острова {Print(ask.Ours)}";
        }
        // the page is measured by its words, and these words were not spoken yet when the pages were first fitted
        FitPage(View.Phone, PhoneBody);
        UpdateTargets();
    }

    /// <summary>
    /// The beginning of a fingerprint, in signs a person can read aloud. Sixteen of the sixty-four are enough to be
    /// sure two sides speak of the same certificate, and finding another that starts the same way is out of reach.
    /// </summary>
    static string Print(string fingerprint) =>
        fingerprint.Length < 16 ? fingerprint.ToUpperInvariant()
            : string.Join(' ', Enumerable.Range(0, 4).Select(i => fingerprint.Substring(i * 4, 4).ToUpperInvariant()));

    const int Quiet = 4, PlateRoom = 156; // signs of white the scanner needs around the code, and the room on the plate

    /// <summary>
    /// The code drawn from its closed modules: no library, only the matrix the island made itself. One pixel per
    /// module, and the picture is then grown by whole signs, so a cell on screen is never half a cell.
    /// </summary>
    static bool Code(string url, out BitmapSource? bitmap, out int side)
    {
        bool[,]? cells = Qr.Make(url);
        side = cells == null ? 0 : cells.GetLength(0) + Quiet * 2;
        if (cells == null) { bitmap = null; return false; }

        var pixels = new byte[side * side * 4];
        Qr.Paint(cells, Quiet, pixels);
        var drawn = new WriteableBitmap(side, side, 96, 96, PixelFormats.Bgra32, null);
        drawn.WritePixels(new Int32Rect(0, 0, side, side), pixels, side * 4, 0);
        drawn.Freeze();
        bitmap = drawn;
        return true;
    }
}
