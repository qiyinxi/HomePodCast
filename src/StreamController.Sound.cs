using HomePodCast.Audio;
using HomePodCast.Net;

namespace HomePodCast;

// Volume cap, speaker mute, night mode and the per-packet effects chain.
public sealed partial class StreamController
{
    private EffectChain? _effects;
    private int _volumeWorker;  // 1 while a sender task runs
    private int _volumeDirty;   // 1 when the speaker should get the current volume

    /// <summary>Night mode (dynamic-range compressor); toggled with Enabled, lives in <see cref="Effects"/>.</summary>
    public Compressor NightMode { get; } = new(RtpSender.SampleRate);

    /// <summary>
    /// Applied in place to every packet on the sender thread (RtpSender's effects hook). An output EQ
    /// goes in front of the night-mode compressor: Effects.Insert(0, eq).
    /// </summary>
    public EffectChain Effects => LazyInitializer.EnsureInitialized(ref _effects, () => new EffectChain(NightMode));

    /// <summary>Ceiling for every volume sent to the speaker, percent (100 = no limit).</summary>
    public double VolumeCapPercent { get; private set; } = 100;

    /// <summary>Speaker muted by us (hotkey, tray, mute key); <see cref="Volume"/> is kept for unmuting.</summary>
    public bool Muted { get; private set; }

    public void SetVolumeCap(double capPercent)
    {
        VolumeCapPercent = Math.Clamp(capPercent, 0, 100);
        if (_client is { } client) client.VolumeCapPercent = VolumeCapPercent;
        if (Volume > VolumeCapPercent)
        {
            Volume = VolumeCapPercent;
            PushVolume();
            Changed?.Invoke();
        }
    }

    public void SetMuted(bool muted)
    {
        if (Muted == muted) return;
        Muted = muted;
        PushVolume();
        Changed?.Invoke();
    }

    /// <summary>
    /// Send the current volume (or mute) to the speaker. Coalesced on one task, latest value wins, so
    /// held hotkeys and volume keys never queue up RTSP requests or arrive out of order.
    /// </summary>
    private void PushVolume()
    {
        Volatile.Write(ref _volumeDirty, 1);
        if (Interlocked.CompareExchange(ref _volumeWorker, 1, 0) != 0) return;
        Task.Run(() =>
        {
            while (true)
            {
                while (Interlocked.Exchange(ref _volumeDirty, 0) == 1)
                {
                    double? percent = Muted ? 0 : Volume;
                    if (percent is null) continue;
                    double capped = VolumeLimit.Clamp(percent.Value, VolumeCapPercent);
                    try
                    {
                        if (_groupRunner?.Current is { } group) group.SetVolumePercent(capped); // linked, plus per-speaker offsets
                        else _client?.SetVolumePercent(capped);
                    }
                    catch (Exception ex) { Log.Warn($"set volume: {ex.Message}"); }
                }
                Volatile.Write(ref _volumeWorker, 0);
                if (Volatile.Read(ref _volumeDirty) == 0 || Interlocked.CompareExchange(ref _volumeWorker, 1, 0) != 0) return;
            }
        });
    }
}
