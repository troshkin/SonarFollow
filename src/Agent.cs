using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;

// Background process: when the Windows default playback device is switched to a
// physical device, route Sonar's output channels to it. In "sonar" mode the Sonar
// virtual devices are then put back as the Windows default so audio goes through
// Sonar; in "direct" mode Windows stays on the device.
static class Agent
{
    const string MutexName = @"Local\SonarFollow.Agent";
    const string StopEventName = @"Local\SonarFollow.Stop";

    const int eRender = 0;
    const int eConsole = 0, eMultimedia = 1, eCommunications = 2;
    static readonly string[] OutputChannels = { "game", "chat", "media", "aux" };
    static readonly string[] VolumeFollowers = { "chatRender", "media", "aux" };

    static AppSettings settings;
    static IMMDeviceEnumerator enumerator;
    static NotificationClient client; // must stay referenced while registered
    static FileSystemWatcher watcher;
    static Timer switchDebounce, volumeDebounce, reloadDebounce;
    static readonly object gate = new object();

    static IAudioEndpointVolume gameVolume;
    static VolumeCallback volumeCallback;
    static float pendingVolume;
    static bool pendingMuted;
    static double lastGame = -1;
    static readonly Dictionary<string, double> ratios = new Dictionary<string, double>();
    static readonly Dictionary<string, double> lastSet = new Dictionary<string, double>();

    public static bool IsRunning()
    {
        Mutex m;
        if (!Mutex.TryOpenExisting(MutexName, out m)) return false;
        m.Dispose();
        return true;
    }

    public static void Start()
    {
        Process.Start(Assembly.GetEntryAssembly().Location, "--agent");
    }

    public static void Stop()
    {
        EventWaitHandle e;
        if (!EventWaitHandle.TryOpenExisting(StopEventName, out e)) return;
        e.Set();
        e.Dispose();
    }

    public static void Run()
    {
        bool created;
        using (var mutex = new Mutex(true, MutexName, out created))
        using (var stop = new EventWaitHandle(false, EventResetMode.ManualReset, StopEventName))
        {
            if (!created) return;
            stop.Reset();
            settings = AppSettings.TryLoad() ?? new AppSettings();
            Log.Write("Agent started, SyncVolume=" + settings.SyncVolume);

            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            client = new NotificationClient();
            // Windows fires one event per role for a single switch; collapse them.
            switchDebounce = new Timer(_ => Locked(SyncDevice), null, Timeout.Infinite, Timeout.Infinite);
            volumeDebounce = new Timer(_ => Locked(SyncVolume), null, Timeout.Infinite, Timeout.Infinite);
            reloadDebounce = new Timer(_ => Locked(Reload), null, Timeout.Infinite, Timeout.Infinite);

            watcher = new FileSystemWatcher(AppSettings.Dir, AppSettings.FileName);
            watcher.Changed += (s, e) => reloadDebounce.Change(300, Timeout.Infinite);
            watcher.Created += (s, e) => reloadDebounce.Change(300, Timeout.Infinite);
            watcher.Renamed += (s, e) => reloadDebounce.Change(300, Timeout.Infinite);
            watcher.EnableRaisingEvents = true;

            Marshal.ThrowExceptionForHR(enumerator.RegisterEndpointNotificationCallback(client));
            Locked(SyncDevice);

            stop.WaitOne();

            watcher.Dispose();
            enumerator.UnregisterEndpointNotificationCallback(client);
            if (gameVolume != null) gameVolume.UnregisterControlChangeNotify(volumeCallback);
            Log.Write("Agent stopped");
        }
    }

    static void Locked(Action action)
    {
        lock (gate)
        {
            try { action(); }
            catch (Exception e) { Log.Write("Error: " + e.Message); }
        }
    }

    static void Reload()
    {
        var s = AppSettings.TryLoad();
        if (s == null) return;
        settings = s;
        Log.Write("Settings reloaded, SyncVolume=" + settings.SyncVolume);
        // A mode change for the device that is the default right now applies at once.
        SyncDevice();
    }

    // COM forbids calling back into the audio API from inside a notification,
    // so callbacks only arm timers and the work happens on a pool thread.
    internal static void OnDefaultChanged(int flow, int role)
    {
        if (flow == eRender && (role == eConsole || role == eMultimedia))
            switchDebounce.Change(400, Timeout.Infinite);
    }

    internal static void OnGameVolume(float level, bool muted)
    {
        pendingVolume = level;
        pendingMuted = muted;
        volumeDebounce.Change(150, Timeout.Infinite);
    }

    static void SyncDevice()
    {
        string current = GetDefault(eMultimedia);
        if (current == null) return;

        var devices = Sonar.GetDevices();
        if (devices == null) return; // Sonar isn't running: leave Windows as is
        if (gameVolume == null) WatchGameVolume(devices);

        var dev = devices.FirstOrDefault(d => string.Equals(d.Id, current, StringComparison.OrdinalIgnoreCase));
        if (dev == null)
        {
            Log.Write("Default device unknown to Sonar: " + current);
            return;
        }
        if (dev.IsVad) return;

        var mode = settings.ModeFor(dev.Id, dev.Name);
        if (mode == DeviceMode.Ignore) return;

        string sonarMode = Sonar.GetMode();
        if (sonarMode != "classic")
        {
            Log.Write("Sonar is in '" + sonarMode + "' mode, only classic is supported");
            return;
        }

        // Route Sonar in direct mode too, so apps bound to Sonar devices (e.g. Discord on Chat) follow.
        foreach (var ch in OutputChannels)
            Sonar.Put("/classicRedirections/" + ch + "/deviceId/" + Uri.EscapeDataString(dev.Id));

        if (mode == DeviceMode.Direct)
        {
            Log.Write("Sonar output -> " + dev.Name + ", Windows stays on it");
            return;
        }

        string game = Sonar.FindVad(devices, "game");
        string chat = Sonar.FindVad(devices, "chatRender") ?? game;
        if (game == null) { Log.Write("Sonar Gaming device not found"); return; }

        var policy = (IPolicyConfig)new PolicyConfigClient();
        policy.SetDefaultEndpoint(game, eConsole);
        policy.SetDefaultEndpoint(game, eMultimedia);
        policy.SetDefaultEndpoint(chat, eCommunications);
        Log.Write("Sonar output -> " + dev.Name + ", Windows default back to Sonar");
    }

    static string GetDefault(int role)
    {
        IMMDevice device;
        if (enumerator.GetDefaultAudioEndpoint(eRender, role, out device) != 0) return null;
        string id;
        device.GetId(out id);
        Marshal.ReleaseComObject(device);
        return id;
    }

    // The volume keys act on the Sonar Gaming device (the Windows default in
    // "sonar" mode); watch its endpoint volume and carry changes to the other channels.
    static void WatchGameVolume(List<SonarDevice> devices)
    {
        string game = Sonar.FindVad(devices, "game");
        if (game == null) return;

        IMMDevice device;
        Marshal.ThrowExceptionForHR(enumerator.GetDevice(game, out device));
        var iid = typeof(IAudioEndpointVolume).GUID;
        object obj;
        Marshal.ThrowExceptionForHR(device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out obj));
        var volume = (IAudioEndpointVolume)obj;

        float level;
        volume.GetMasterVolumeLevelScalar(out level);
        lastGame = level;
        volumeCallback = new VolumeCallback();
        Marshal.ThrowExceptionForHR(volume.RegisterControlChangeNotify(volumeCallback));
        gameVolume = volume;
    }

    static void SyncVolume()
    {
        double game = pendingVolume;
        bool muted = pendingMuted;
        if (!settings.SyncVolume)
        {
            lastGame = game;
            lastSet.Clear();
            return;
        }

        string json = Sonar.Get("/volumeSettings/classic");
        if (json == null) return;
        var all = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
        var channels = (Dictionary<string, object>)all["devices"];

        foreach (var ch in VolumeFollowers)
        {
            object entry;
            if (!channels.TryGetValue(ch, out entry)) continue;
            var classic = (Dictionary<string, object>)((Dictionary<string, object>)entry)["classic"];
            double current = Convert.ToDouble(classic["volume"], CultureInfo.InvariantCulture);
            bool chMuted = (bool)classic["muted"];

            // Each channel keeps its ratio to Gaming. The ratio is remembered while the
            // channel still holds the value we set, so passing through 0% doesn't lose
            // the balance; if the user moved it in Sonar since, it's taken anew.
            double ratio, set;
            bool untouched = lastSet.TryGetValue(ch, out set) && Math.Abs(set - current) < 0.005;
            if (!(untouched && ratios.TryGetValue(ch, out ratio)))
                ratio = lastGame > 0.001 ? current / lastGame : 1;

            double target = Math.Round(Math.Max(0, Math.Min(1, ratio * game)), 3);
            // Capped at 100%: what the user hears now is the new balance, otherwise
            // a channel set louder than Gaming wouldn't move until Gaming drops far.
            ratios[ch] = target >= 1 && game > 0.001 ? 1 / game : ratio;

            if (Math.Abs(target - current) > 0.002)
                Sonar.Put("/volumeSettings/classic/" + ch + "/Volume/" + target.ToString(CultureInfo.InvariantCulture));
            lastSet[ch] = target;

            if (chMuted != muted)
                Sonar.Put("/volumeSettings/classic/" + ch + "/Mute/" + (muted ? "true" : "false"));
        }
        lastGame = game;
    }
}
