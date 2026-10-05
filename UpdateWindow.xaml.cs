using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using static DynamicIsland.Motion;

namespace DynamicIsland;

/// <summary>The update screen: the whole desktop dims and the release stands in the middle. It is a window of its
/// own rather than another state of the island because the island is clipped to its pill and never scrolls, and a
/// release note long enough to read is neither.</summary>
public partial class UpdateWindow : Window
{
    readonly Updater _updater;
    readonly Action _installed;
    readonly ScreenInfo _screen;

    internal UpdateWindow(Updater updater, ScreenInfo screen, Action installed)
    {
        _updater = updater;
        _installed = installed;
        _screen = screen;

        InitializeComponent();
        if (Application.Current.MainWindow is { } owner)
        {
            Resources["Face"] = owner.Resources["Face"];
            Resources["FaceDisplay"] = owner.Resources["FaceDisplay"];
        }
        _updater.Changed += Refresh;
        Closed += (_, _) => _updater.Changed -= Refresh;
        Refresh();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        Native.HideFromTaskSwitcher(hwnd);
        // the desktop to cover is told in pixels; WPF would read the screen's own scale into Left and Top again
        Placement.Move(hwnd, _screen.Work, new Size(_screen.Work.Width / _screen.ScaleX, _screen.Work.Height / _screen.ScaleY),
            _screen.ScaleX, _screen.ScaleY);
    }

    void Refresh() => Dispatcher.Invoke(() =>
    {
        Updater.Stage stage = _updater.State;
        bool found = stage is Updater.Stage.Available or Updater.Stage.Loading, loading = stage == Updater.Stage.Loading;
        Version? latest = found || stage == Updater.Stage.Latest ? _updater.LatestVersion : null;

        VersionText.Text = (latest ?? Updater.CurrentVersion).ToString();
        VersionNote.Text = found ? "вместо " + Updater.CurrentVersion : "";
        StatusText.Text = stage switch
        {
            Updater.Stage.Checking => "ПРОВЕРЯЮ",
            Updater.Stage.Latest => "ЭТА ВЕРСИЯ ПОСЛЕДНЯЯ",
            Updater.Stage.Failed => "НЕ ПРОВЕРИТЬСЯ",
            _ => found ? "ДОСТУПНО ОБНОВЛЕНИЕ" : "",
        };
        StatusText.Foreground = stage switch
        {
            Updater.Stage.Available or Updater.Stage.Loading => (Brush)Resources["Orange"],
            Updater.Stage.Latest => (Brush)Resources["Green"],
            Updater.Stage.Failed => (Brush)Resources["Red"],
            _ => (Brush)Resources["Faint"],
        };

        NotesList.ItemsSource = _updater.Notes;
        NotesEmpty.Visibility = _updater.Notes.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
        NotesScroll.Visibility = _updater.Notes.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        CurrentText.Text = "v" + Updater.CurrentVersion;
        SizeText.Text = _updater.TotalBytes > 0 ? Mb(_updater.TotalBytes) : "—";
        DateText.Text = _updater.PublishedAt is { } published ? Day(published) : "—";
        HashText.Text = ShortHash(_updater.Digest);

        Progress.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        CheckButton.Visibility = InstallButton.Visibility = loading ? Visibility.Collapsed : Visibility.Visible;
        if (loading) ShowProgress();

        InstallButton.Content = stage switch
        {
            Updater.Stage.Available => "Обновить и перезапустить",
            Updater.Stage.Checking => "Проверяю…",
            Updater.Stage.Failed => "Проверить ещё раз",
            _ => "Уже последняя",
        };
        InstallButton.IsEnabled = stage != Updater.Stage.Checking;
        // only a release that is actually worth taking lights up
        InstallButton.Background = stage == Updater.Stage.Available ? (Brush)Resources["Orange"] : (Brush)Resources["Plate"];
        InstallButton.Foreground = stage == Updater.Stage.Available ? Brushes.Black : Brushes.White;
        CheckButton.Content = stage == Updater.Stage.Available ? "Позже" : "Проверить ещё раз";
    });

    void ShowProgress()
    {
        ProgressPercent.Text = _updater.Percent + "%";
        ProgressBytes.Text = Mb(_updater.DownloadedBytes) + " из " + Mb(_updater.TotalBytes);
        ProgressRing.BeginAnimation(Ring.ProgressProperty, new DoubleAnimation(_updater.Percent / 100.0, Ms(200)));
    }

    static string Mb(long bytes) => bytes <= 0 ? "—" : (bytes / 1024.0 / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " МБ";

    static string Day(DateTime at) => at.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.GetCultureInfo("ru-RU"));

    static string ShortHash(string digest) => digest.StartsWith(HashPrefix) && digest.Length > HashPrefix.Length + 12
        ? digest[HashPrefix.Length..(HashPrefix.Length + 12)] + "…" : "—";

    const string HashPrefix = "sha256:";

    async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_updater.State != Updater.Stage.Available) { await _updater.CheckAsync(true); return; }
        // the new exe takes this one's place, so the island has to be out of the way before it starts
        if (await _updater.InstallAsync()) _installed();
    }

    void Check_Click(object sender, RoutedEventArgs e)
    {
        // once there is something to install the same place reads "Позже" and just steps aside
        if (_updater.State == Updater.Stage.Available) Close();
        else _ = _updater.CheckAsync(true);
    }

    void OpenRelease_Click(object sender, RoutedEventArgs e)
    {
        if (_updater.ReleaseUrl.Length == 0) return;
        try
        {
            Process.Start(new ProcessStartInfo(_updater.ReleaseUrl) { UseShellExecute = true });
        }
        catch (Exception ex) { App.Log(ex); }
    }

    void Scrim_Click(object sender, RoutedEventArgs e) => Close();

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
