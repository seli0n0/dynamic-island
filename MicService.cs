using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace DynamicIsland;

/// <summary>
/// Watches the microphone: which apps hold it now. The same list the system's own mic light goes by, read off the
/// capture device a second at a time. A stream open but taking in nothing still holds the microphone and counts: the
/// engine marks such a session inactive, and an app quiet between words has not let the microphone go.
/// </summary>
sealed class MicService
{
    const int ECapture = 1, EConsole = 1, ClsCtxAll = 23;
    const int SessionExpired = 2; // AudioSessionState: the app that recorded is gone

    readonly Dispatcher _ui;
    readonly DispatcherTimer _poll;
    string[] _apps = [];
    bool _reading;

    public MicService(Dispatcher ui)
    {
        _ui = ui;
        _poll = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromSeconds(1) };
        _poll.Tick += (_, _) => _ = RefreshAsync();
    }

    /// <summary>Raised on the UI thread when the set of apps recording changes: the ones that were, the ones that are.</summary>
    public event Action<string[], string[]>? Changed;

    /// <summary>Whether an app has the microphone now, as it was at the last reading.</summary>
    public bool InUse => _apps.Length > 0;

    public async Task StartAsync()
    {
        // what is already recording when the island starts is no news
        _apps = await Task.Run(Read);
        _poll.Start();
    }

    async Task RefreshAsync()
    {
        if (_reading) return; // a read that outlasts its second is still going
        _reading = true;
        string[] now;
        try { now = await Task.Run(Read); }
        finally { _reading = false; }

        if (now.SequenceEqual(_apps)) return;
        string[] was = _apps;
        _apps = now;
        Changed?.Invoke(was, now);
    }

    /// <summary>The apps recording through the default microphone, each named once, in order.</summary>
    static string[] Read()
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            IMMDevice? device = null;
            try
            {
                if (enumerator.GetDefaultAudioEndpoint(ECapture, EConsole, out device) != 0 || device == null) return [];
                Guid iid = typeof(IAudioSessionManager2).GUID;
                object? mixer = null;
                try
                {
                    if (device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out mixer) != 0
                        || mixer is not IAudioSessionManager2 manager) return [];
                    return Sessions(manager);
                }
                finally
                {
                    if (mixer != null) Marshal.ReleaseComObject(mixer);
                }
            }
            finally
            {
                if (device != null) Marshal.ReleaseComObject(device);
                Marshal.ReleaseComObject(enumerator);
            }
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return [];
        }
    }

    static string[] Sessions(IAudioSessionManager2 manager)
    {
        if (manager.GetSessionEnumerator(out IAudioSessionEnumerator? sessions) != 0 || sessions == null) return [];
        try
        {
            if (sessions.GetCount(out int count) != 0) return [];
            var names = new List<string>(count);
            for (int i = 0; i < count; i++)
            {
                if (sessions.GetSession(i, out object? session) != 0 || session == null) continue;
                try
                {
                    if (session is not IAudioSessionControl2 control) continue;
                    if (control.GetState(out int state) != 0 || state == SessionExpired) continue;
                    control.GetProcessId(out uint process);
                    // the device's own idle stream sits on the device all the time and belongs to no app
                    if (process == 0) continue;
                    // one app records through several streams: it is named once
                    string name = Who(process);
                    if (name.Length > 0 && !names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
                }
                finally
                {
                    Marshal.ReleaseComObject(session);
                }
            }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return [.. names];
        }
        finally
        {
            Marshal.ReleaseComObject(sessions);
        }
    }

    /// <summary>What the process was started as, with its first letter up: the way the app is known to the user.</summary>
    static string Who(uint process)
    {
        try
        {
            using Process app = Process.GetProcessById((int)process);
            string name = app.ProcessName;
            return name.Length == 0 ? "" : char.ToUpperInvariant(name[0]) + name[1..];
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            // the app has already gone
            return "";
        }
    }
}
