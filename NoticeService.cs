using System.Windows.Threading;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace DynamicIsland;

/// <summary>
/// Reads the notifications Windows itself shows, so that the island can say them too: a message, a calendar reminder,
/// anything an app puts in the corner. The system is asked for leave first, and without it nothing is read at all.
/// Windows raises no event for an app that is not packaged, so the centre is read a second at a time and what was not
/// in it a second ago has just arrived. Only the app's name and the notification's lines of text are ever taken.
/// </summary>
sealed class NoticeService
{
    readonly Dispatcher _ui;
    readonly DispatcherTimer _poll;
    readonly HashSet<uint> _seen = [];
    UserNotificationListener? _listener;
    bool _reading;

    public NoticeService(Dispatcher ui)
    {
        _ui = ui;
        _poll = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromSeconds(1) };
        _poll.Tick += (_, _) => _ = RefreshAsync();
    }

    /// <summary>Raised on the UI thread for a notification that has just arrived: who sent it, what it says.</summary>
    public event Action<string, string>? Raised;

    /// <summary>Whether the system let the island listen. Without leave nothing is read and this stays false.</summary>
    public bool Live => _listener != null;

    public async Task StartAsync()
    {
        try
        {
            UserNotificationListener listener = UserNotificationListener.Current;
            if (await listener.RequestAccessAsync() != UserNotificationListenerAccessStatus.Allowed) return;
            _listener = listener;
            // what is already in the corner when the island starts is no news: remember it, say nothing
            foreach (UserNotification note in await listener.GetNotificationsAsync(NotificationKinds.Toast))
                _seen.Add(note.Id);
            _poll.Start();
        }
        catch (Exception ex)
        {
            _listener = null;
            App.Log(ex);
        }
    }

    public void Stop()
    {
        _poll.Stop();
        _listener = null;
        _seen.Clear();
    }

    async Task RefreshAsync()
    {
        if (_listener is not { } listener || _reading) return; // a read that outlasts its second is still going
        _reading = true;
        try
        {
            var present = new HashSet<uint>();
            foreach (UserNotification note in await listener.GetNotificationsAsync(NotificationKinds.Toast))
            {
                present.Add(note.Id);
                if (_seen.Add(note.Id)) // not in the corner a second ago: it has just arrived
                {
                    (string who, string what) = Read(note);
                    if (what.Length > 0) Raised?.Invoke(who, what);
                }
            }
            _seen.RemoveWhere(id => !present.Contains(id)); // dismissed: forget it, so it can be new again
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
        finally
        {
            _reading = false;
        }
    }

    /// <summary>The app's name and the notification's lines: the first of them its title, the rest its body.</summary>
    static (string Who, string What) Read(UserNotification note)
    {
        string who = "";
        try { who = note.AppInfo?.DisplayInfo?.DisplayName ?? ""; }
        catch (Exception ex) { App.Log(ex); }

        var lines = new List<string>();
        try
        {
            foreach (NotificationBinding binding in note.Notification.Visual.Bindings)
                foreach (AdaptiveNotificationText element in binding.GetTextElements())
                    if (!string.IsNullOrWhiteSpace(element.Text)) lines.Add(element.Text.Trim());
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
        if (lines.Count == 0) return ("", "");

        // an app that puts its own name in the title has it once, not twice
        string title = lines[0];
        string body = string.Join(" ", lines.Skip(1));
        bool same = title.Equals(who, StringComparison.OrdinalIgnoreCase);
        string what = body.Length > 0 ? (same || title.Length == 0 ? body : title + ": " + body) : same ? "" : title;
        if (what.Length == 0) return ("", "");
        if (who.Length == 0) who = same || title.Length == 0 || title == what ? "Уведомление" : title;
        return (who, what);
    }
}
