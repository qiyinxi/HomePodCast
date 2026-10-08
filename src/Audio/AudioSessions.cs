using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HomePodCast.Audio;

// Session interfaces; GUIDs and vtable order checked against NAudio's CoreAudioApi definitions.

[ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionManager2
{
    [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, uint flags, out IntPtr control);
    [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, uint flags, out IntPtr volume);
    [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator enumerator);
    [PreserveSig] int RegisterSessionNotification(IAudioSessionNotification notification);
    [PreserveSig] int UnregisterSessionNotification(IAudioSessionNotification notification);
}

// audiopolicy.h (SDK 10.0.26100.0): IAudioSessionNotification 641DD20B-4D41-49CC-ABA3-174B9477BB08.
[ComImport, Guid("641DD20B-4D41-49CC-ABA3-174B9477BB08"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionNotification
{
    [PreserveSig] int OnSessionCreated(IntPtr newSession);
}

[ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionEnumerator
{
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
}

[ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl2
{
    // IAudioSessionControl
    [PreserveSig] int GetState(out int state);
    [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
    [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid context);
    [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
    [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid context);
    [PreserveSig] int GetGroupingParam(out Guid grouping);
    [PreserveSig] int SetGroupingParam(ref Guid grouping, ref Guid context);
    [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
    [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
    // IAudioSessionControl2
    [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetProcessId(out uint pid);
    [PreserveSig] int IsSystemSoundsSession();
    [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
}

// audiopolicy.h (SDK 10.0.19041.0): IAudioSessionEvents 24918ACC-64B3-37C1-8CA9-74A66E9957A8, methods in vtable
// order. Implemented by us and handed to IAudioSessionControl::RegisterAudioSessionNotification as a COM pointer.
[ComImport, Guid("24918ACC-64B3-37C1-8CA9-74A66E9957A8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionEvents
{
    [PreserveSig] int OnDisplayNameChanged(IntPtr newDisplayName, IntPtr eventContext);
    [PreserveSig] int OnIconPathChanged(IntPtr newIconPath, IntPtr eventContext);
    [PreserveSig] int OnSimpleVolumeChanged(float newVolume, int newMute, IntPtr eventContext);
    [PreserveSig] int OnChannelVolumeChanged(uint channelCount, IntPtr newChannelVolumes, uint changedChannel, IntPtr eventContext);
    [PreserveSig] int OnGroupingParamChanged(IntPtr newGroupingParam, IntPtr eventContext);
    [PreserveSig] int OnStateChanged(int newState);
    [PreserveSig] int OnSessionDisconnected(int disconnectReason);
}

[ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISimpleAudioVolume
{
    [PreserveSig] int SetMasterVolume(float level, ref Guid context);
    [PreserveSig] int GetMasterVolume(out float level);
    [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
    [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}

[ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioMeterInformation
{
    [PreserveSig] int GetPeakValue(out float peak);
}

/// <summary>All audio sessions of one process on the captured output device (what goes to the speaker).</summary>
public sealed class AppAudio : IDisposable
{
    private readonly List<(ISimpleAudioVolume Volume, IAudioMeterInformation Meter, IAudioSessionControl2 Control)> _sessions = [];
    private static Guid _context = Guid.NewGuid();

    public uint ProcessId { get; }
    public bool IsSystemSounds { get; }
    public string Name { get; }
    public Icon? Icon { get; }
    public string Key => IsSystemSounds ? "system" : ProcessId.ToString();

    internal AppAudio(uint pid, bool system, string name, Icon? icon)
    {
        ProcessId = pid;
        IsSystemSounds = system;
        Name = name;
        Icon = icon;
    }

    internal void Add(ISimpleAudioVolume v, IAudioMeterInformation m, IAudioSessionControl2 c) => _sessions.Add((v, m, c));

    /// <summary>Rule key for per-app routing (lower-case executable name without ".exe"); empty for system sounds.</summary>
    public string ExeKey { get; init; } = "";

    /// <summary>
    /// The app's volume as the user sees it. While per-app routing keeps the app silent here (session
    /// volume scaled by <see cref="SessionAttenuation.Epsilon"/>), this is the level sent to the speaker.
    /// </summary>
    public float Volume
    {
        get => _sessions.Count > 0 && _sessions[0].Volume.GetMasterVolume(out var v) >= 0 ? SessionAttenuation.ToLogical(v) : 1f;
        set
        {
            float logical = Math.Clamp(value, 0f, 1f);
            bool routed = SessionAttenuation.SilencedPids.Contains(ProcessId);
            foreach (var s in _sessions)
            {
                bool silenced = routed || s.Volume.GetMasterVolume(out var raw) >= 0 && SessionAttenuation.IsAttenuated(raw);
                s.Volume.SetMasterVolume(silenced && logical > 0 ? logical * SessionAttenuation.Epsilon : logical, ref _context);
            }
        }
    }

    /// <summary>The session volume exactly as Windows has it (tests).</summary>
    internal float RawVolume
    {
        get => _sessions.Count > 0 && _sessions[0].Volume.GetMasterVolume(out var v) >= 0 ? v : 1f;
        set { foreach (var s in _sessions) s.Volume.SetMasterVolume(value, ref _context); }
    }

    /// <summary>Make sessions silenced by per-app routing audible again (left over from an unclean exit).</summary>
    internal void RestoreIfSilenced()
    {
        foreach (var s in _sessions)
            if (s.Volume.GetMasterVolume(out var raw) >= 0 && SessionAttenuation.IsAttenuated(raw))
                s.Volume.SetMasterVolume(SessionAttenuation.Restored(raw), ref _context);
    }

    public bool Muted
    {
        get => _sessions.Count > 0 && _sessions[0].Volume.GetMute(out var m) >= 0 && m;
        set { foreach (var s in _sessions) s.Volume.SetMute(value, ref _context); }
    }

    /// <summary>A made-up meter for rows that have no session (the Debug mixer demo); null for real apps.</summary>
    internal Func<float>? FakePeak { get; init; }

    public float Peak
    {
        get
        {
            if (FakePeak != null) return FakePeak();
            float peak = 0;
            foreach (var s in _sessions)
                if (s.Meter.GetPeakValue(out var p) >= 0) peak = Math.Max(peak, p);
            return peak;
        }
    }

    public void Dispose()
    {
        foreach (var s in _sessions)
        {
            Marshal.ReleaseComObject(s.Volume);
            if (!ReferenceEquals(s.Meter, s.Volume)) Marshal.ReleaseComObject(s.Meter);
        }
        _sessions.Clear();
        Icon?.Dispose();
    }

    /// <summary>
    /// Snapshot of the non-expired sessions on a render device, merged per process: <paramref name="deviceId"/>
    /// (the chosen capture device; empty when it is missing) or, when null, the default output.
    /// </summary>
    public static List<AppAudio> Enumerate(string? deviceId = null)
    {
        var result = new Dictionary<string, AppAudio>();
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        IMMDevice? device = null;
        try
        {
            device = AudioEndpoints.Open(enumerator, EDataFlow.Render, deviceId);
            if (device == null) return [];
            var iid = typeof(IAudioSessionManager2).GUID;
            if (device.Activate(ref iid, CoreAudio.ClsCtxAll, IntPtr.Zero, out var obj) < 0) return [];
            var manager = (IAudioSessionManager2)obj;
            if (manager.GetSessionEnumerator(out var sessions) < 0) return [];
            sessions.GetCount(out int count);
            for (int i = 0; i < count; i++)
            {
                if (sessions.GetSession(i, out var raw) < 0) continue;
                var control = (IAudioSessionControl2)raw;
                control.GetState(out int state);
                if (state == 2) continue; // expired
                control.GetProcessId(out uint pid);
                bool system = control.IsSystemSoundsSession() == 0;
                var key = system ? "system" : pid.ToString();
                if (!result.TryGetValue(key, out var app))
                {
                    control.GetDisplayName(out var display);
                    var (name, icon) = Describe(pid, system, display);
                    app = result[key] = new AppAudio(pid, system, name, icon) { ExeKey = system ? "" : ExeKeyOf(pid) };
                }
                app.Add((ISimpleAudioVolume)raw, (IAudioMeterInformation)raw, control);
            }
            Marshal.ReleaseComObject(sessions);
            Marshal.ReleaseComObject(manager);
        }
        catch (Exception ex)
        {
            Log.Warn($"sessions: {ex.Message}");
        }
        finally
        {
            if (device != null) Marshal.ReleaseComObject(device);
            Marshal.ReleaseComObject(enumerator);
        }
        return result.Values.OrderBy(a => a.IsSystemSounds ? 1 : 0).ThenBy(a => a.Name).ToList();
    }

    private static string ExeKeyOf(uint pid)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return RouteRules.KeyFor(p.ProcessName);
        }
        catch
        {
            return "";
        }
    }

    private static (string, Icon?) Describe(uint pid, bool system, string? display)
    {
        if (system) return (L.T("系统声音"), null);
        try
        {
            using var p = Process.GetProcessById((int)pid);
            string? path = null;
            try { path = p.MainModule?.FileName; } catch { }
            string name = !string.IsNullOrWhiteSpace(display) && !display.StartsWith('@') ? display
                : path != null && FileVersionInfo.GetVersionInfo(path).FileDescription is { Length: > 0 } desc ? desc
                : p.ProcessName;
            Icon? icon = null;
            if (path != null)
                try { icon = Icon.ExtractAssociatedIcon(path); } catch { }
            return (name, icon);
        }
        catch
        {
            return (string.IsNullOrWhiteSpace(display) ? L.F("进程 {0}", pid) : display, null);
        }
    }
}
