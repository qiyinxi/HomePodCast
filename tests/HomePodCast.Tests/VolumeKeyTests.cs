using System.Runtime.InteropServices;
using HomePodCast.Audio;

namespace HomePodCast.Tests;

public class VolumeKeyForwardingTests
{
    private const float Step = 0.02f;
    private static readonly EndpointState Muted40 = new(true, 0.40f);
    private static readonly EndpointState Zero = new(false, 0f);

    private static ForwardAction Decide(EndpointState from, EndpointState to, VolumeKeys keys = VolumeKeys.None) =>
        VolumeKeyForwarder.Decide(from, to, keys, Step);

    [Fact]
    public void Volume_up_on_muted_windows_goes_to_the_speaker_and_windows_is_re_muted()
    {
        var a = Decide(Muted40, new EndpointState(false, 0.42f), VolumeKeys.Up);
        Assert.True(a.Restore);
        Assert.Equal(2, a.Percent, 3);
        Assert.False(a.ToggleMute);
    }

    [Fact]
    public void Volume_down_on_muted_windows_steps_the_speaker_down()
    {
        var a = Decide(Muted40, new EndpointState(false, 0.38f), VolumeKeys.Down);
        Assert.True(a.Restore);
        Assert.Equal(-2, a.Percent, 3);
    }

    [Fact]
    public void Key_that_only_unmutes_takes_its_direction_from_the_key()
    {
        var full = new EndpointState(true, 1f);
        Assert.Equal(2, Decide(full, new EndpointState(false, 1f), VolumeKeys.Up).Percent, 3);
        var empty = new EndpointState(true, 0f);
        Assert.Equal(-2, Decide(empty, new EndpointState(false, 0f), VolumeKeys.Down).Percent, 3);
    }

    [Fact]
    public void One_step_without_a_held_key_counts_too_tray_wheel_or_knob()
    {
        var a = Decide(Zero, new EndpointState(false, 0.02f));
        Assert.True(a.Restore);
        Assert.Equal(2, a.Percent, 3);
        var device = VolumeKeyForwarder.Decide(Zero, new EndpointState(false, 0.01f), VolumeKeys.None, deviceStep: 0.01f);
        Assert.Equal(1, device.Percent, 3);
    }

    [Fact]
    public void Mute_key_toggles_the_speaker_mute()
    {
        var a = Decide(Muted40, new EndpointState(false, 0.40f), VolumeKeys.Mute);
        Assert.True(a.ToggleMute);
        Assert.True(a.Restore);
        var b = Decide(Zero, new EndpointState(true, 0f), VolumeKeys.Mute);
        Assert.True(b.ToggleMute);
    }

    [Fact]
    public void Deliberate_changes_in_windows_are_left_alone()
    {
        Assert.Equal(default, Decide(Muted40, new EndpointState(false, 0.40f)));   // unmute clicked
        Assert.Equal(default, Decide(Zero, new EndpointState(false, 0.35f)));      // slider dragged
        Assert.Equal(default, Decide(Muted40, new EndpointState(true, 0.55f)));
    }

    [Fact]
    public void Nothing_happens_while_windows_audio_is_in_normal_use()
    {
        var normal = new EndpointState(false, 0.5f);
        Assert.Equal(default, Decide(normal, new EndpointState(false, 0.52f), VolumeKeys.Up));
        Assert.Equal(default, Decide(normal, new EndpointState(true, 0.5f), VolumeKeys.Mute));
        Assert.Equal(default, Decide(Muted40, Muted40, VolumeKeys.Up));
    }

    // ---------------------------------------------------------------- COM callback

    [Fact]
    public void Callback_interface_ids_match_the_windows_sdk()
    {
        // endpointvolume.h: MIDL_INTERFACE("657804FA-D6AD-4496-8A60-352752AF4F89") IAudioEndpointVolumeCallback
        Assert.Equal(new Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), typeof(IAudioEndpointVolumeCallback).GUID);
        Assert.Equal(new Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), typeof(IAudioEndpointVolume).GUID);
        Assert.Equal(28, Marshal.SizeOf<AudioVolumeNotificationData>()); // GUID + BOOL + float + UINT
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int OnNotifyFn(IntPtr self, IntPtr data);

    [Fact]
    public void Windows_calling_vtable_slot_3_reaches_OnNotify_with_the_notification_data()
    {
        (Guid Context, bool Muted, float Level)? got = null;
        var callback = new EndpointVolumeCallback((ctx, muted, level) => got = (ctx, muted, level));
        IntPtr unknown = Marshal.GetIUnknownForObject(callback);
        IntPtr data = Marshal.AllocHGlobal(32);
        try
        {
            // What audiosrv does: QueryInterface for the callback IID, then call OnNotify through the vtable.
            var iid = new Guid("657804FA-D6AD-4496-8A60-352752AF4F89");
            Assert.Equal(0, Marshal.QueryInterface(unknown, ref iid, out IntPtr itf));
            try
            {
                var context = Guid.NewGuid();
                Marshal.StructureToPtr(new AudioVolumeNotificationData { EventContext = context, Muted = 1, MasterVolume = 0.25f, Channels = 2 }, data, false);
                IntPtr vtable = Marshal.ReadIntPtr(itf);
                var onNotify = Marshal.GetDelegateForFunctionPointer<OnNotifyFn>(Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size));
                Assert.Equal(0, onNotify(itf, data));
                Assert.NotNull(got);
                Assert.Equal((context, true, 0.25f), got.Value);
            }
            finally
            {
                Marshal.Release(itf);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(data);
            Marshal.Release(unknown);
        }
    }

    /// <summary>
    /// Against the real default output device: registers our callback, moves the level by 1 % and puts it
    /// straight back (Windows does not notify for a set to the current value), expecting the notification
    /// with our context GUID. Opt-in because it touches the Windows volume: set HOMEPODCAST_LIVE_AUDIO=1.
    /// </summary>
    [LiveAudioFact]
    public void Real_endpoint_delivers_notifications_to_our_callback()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        Assert.Equal(0, enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Console, out var device));
        var iid = typeof(IAudioEndpointVolume).GUID;
        Assert.Equal(0, device.Activate(ref iid, CoreAudio.ClsCtxAll, IntPtr.Zero, out var obj));
        var volume = (IAudioEndpointVolume)obj;
        Assert.Equal(0, volume.GetVolumeStepInfo(out uint step, out uint count));
        Assert.True(count > 1 && step < count, $"step {step}/{count}");

        var context = Guid.NewGuid();
        using var seen = new ManualResetEventSlim();
        (bool Muted, float Level)? got = null;
        var callback = new EndpointVolumeCallback((ctx, m, l) => { if (ctx == context) { got = (m, l); seen.Set(); } });
        IntPtr ptr = Marshal.GetComInterfaceForObject<EndpointVolumeCallback, IAudioEndpointVolumeCallback>(callback);
        Assert.Equal(0, volume.RegisterControlChangeNotify(ptr));
        volume.GetMute(out bool muted);
        volume.GetMasterVolumeLevelScalar(out float level);
        float probe = level >= 0.5f ? level - 0.01f : level + 0.01f;
        try
        {
            volume.SetMasterVolumeLevelScalar(probe, ref context);
            Assert.True(seen.Wait(2000), "no OnNotify within 2 s");
            Assert.Equal(muted, got!.Value.Muted);
            Assert.Equal(probe, got.Value.Level, 3);
        }
        finally
        {
            volume.SetMasterVolumeLevelScalar(level, ref context);  // restore at once
            volume.GetMasterVolumeLevelScalar(out float after);
            volume.GetMute(out bool mutedAfter);
            Assert.Equal(level, after, 3);
            Assert.Equal(muted, mutedAfter);
            volume.UnregisterControlChangeNotify(ptr);
            Marshal.Release(ptr);
            Marshal.ReleaseComObject(volume);
            Marshal.ReleaseComObject(device);
            Marshal.ReleaseComObject(enumerator);
        }
    }
}

public sealed class LiveAudioFactAttribute : FactAttribute
{
    public LiveAudioFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("HOMEPODCAST_LIVE_AUDIO") != "1")
            Skip = "touches the Windows endpoint volume API; set HOMEPODCAST_LIVE_AUDIO=1 to run";
    }
}
