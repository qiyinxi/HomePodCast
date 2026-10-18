using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using HomePodCast.Audio;
using HomePodCast.UI;
using HomePodCast.UI.Controls;
using HomePodCast.UI.Pages;

namespace HomePodCast.Tests;

/// <summary>
/// The Windows default output for <see cref="PcMute"/>, in memory: these tests never mute, unmute or read the real PC
/// (the maintainer's speakers). Counts every read and write.
/// </summary>
internal sealed class FakePcOutput : IPcOutput
{
    public sealed class Device(AudioEndpoint endpoint, bool muted, float level)
    {
        public AudioEndpoint Endpoint { get; } = endpoint;
        public bool Muted { get; set; } = muted;
        public float Level { get; set; } = level;
        public bool Gone { get; set; }
    }

    public const string Speakers = "{0.0.0.00000000}.{speakers}", Headphones = "{0.0.0.00000000}.{headphones}",
        Cable = "{0.0.0.00000000}.{cable}";

    public Dictionary<string, Device> Devices { get; } = [];
    public string? DefaultId { get; set; }
    public List<(string Id, bool Muted)> Writes { get; } = [];
    public int Reads { get; private set; }
    public bool FailWrites { get; set; }

    /// <summary>PC speakers as the default output (not muted, at 50 %), headphones and a virtual card next to them.</summary>
    public static FakePcOutput WithSpeakers(bool muted = false)
    {
        var fake = new FakePcOutput();
        fake.Add(Speakers, "扬声器 (Realtek(R) Audio)", EndpointFormFactor.Speakers, muted);
        fake.Add(Headphones, "耳机 (WH-1000XM4 Stereo)", EndpointFormFactor.Headphones);
        fake.Add(Cable, "CABLE Input (VB-Audio Virtual Cable)", EndpointFormFactor.Speakers);
        fake.DefaultId = Speakers;
        return fake;
    }

    public Device Add(string id, string name, EndpointFormFactor formFactor, bool muted = false, float level = 0.5f) =>
        Devices[id] = new Device(new AudioEndpoint(id, name, formFactor), muted, level);

    public Device this[string id] => Devices[id];

    public PcOutputState? Default()
    {
        Reads++;
        return DefaultId != null && Devices.TryGetValue(DefaultId, out var d) && !d.Gone ? new PcOutputState(d.Endpoint, d.Muted, d.Level) : null;
    }

    public bool? IsMuted(string id)
    {
        Reads++;
        return Devices.TryGetValue(id, out var d) && !d.Gone ? d.Muted : null;
    }

    public bool SetMute(string id, bool muted)
    {
        if (FailWrites || !Devices.TryGetValue(id, out var d) || d.Gone) return false;
        d.Muted = muted;
        Writes.Add((id, muted));
        return true;
    }

    public string? Name(string id) => Devices.TryGetValue(id, out var d) && !d.Gone ? d.Endpoint.Name : null;
}

/// <summary>
/// 推流时电脑静音 (Audio.PcMute): what is muted when, what is given back, and what is left alone; with a fake default
/// output, never the real one.
/// </summary>
public class PcMuteTests
{
    private static readonly PcCapture FollowDefault = new(SystemCapture: true, DeviceId: null);

    private static (PcMute Mute, FakePcOutput Pc) Make(bool muted = false)
    {
        var pc = FakePcOutput.WithSpeakers(muted);
        return (new PcMute(pc), pc);
    }

    [Theory]
    [InlineData(StreamState.Streaming, 0, "Streaming")]
    [InlineData(StreamState.Connecting, 0, "Between")]
    [InlineData(StreamState.Retrying, 0, "Between")]
    [InlineData(StreamState.Retrying, 60_000, "Between")]    // retrying for a minute: still the session
    [InlineData(StreamState.Idle, 0, "Between")]             // a reconnect's stop before its start
    [InlineData(StreamState.Idle, 999, "Between")]
    [InlineData(StreamState.Idle, PcMute.IdleGraceMs, "Ended")]
    [InlineData(StreamState.Idle, 60_000, "Ended")]
    public void Only_an_idle_that_lasts_ends_the_session(StreamState state, double idleMs, string expected) =>
        Assert.Equal(Enum.Parse<PcMutePhase>(expected), PcMute.PhaseFor(state, idleMs));

    [Fact]
    public void Streaming_mutes_the_default_output_and_the_end_of_the_session_unmutes_it()
    {
        var (mute, pc) = Make();
        mute.Update(PcMutePhase.Between, true, FollowDefault); // connecting: nothing yet, and nothing read
        Assert.Empty(pc.Writes);
        Assert.Equal(0, pc.Reads);

        mute.Update(PcMutePhase.Streaming, true, FollowDefault);
        Assert.True(pc[FakePcOutput.Speakers].Muted);
        Assert.Equal(new[] { (FakePcOutput.Speakers, true) }, pc.Writes);
        Assert.Equal(FakePcOutput.Speakers, mute.Holding);

        mute.Update(PcMutePhase.Streaming, true, FollowDefault); // again (another change of the controller): once
        Assert.Single(pc.Writes);

        mute.Update(PcMutePhase.Ended, true, FollowDefault);
        Assert.False(pc[FakePcOutput.Speakers].Muted);
        Assert.Equal(new[] { (FakePcOutput.Speakers, true), (FakePcOutput.Speakers, false) }, pc.Writes);
        Assert.Null(mute.Holding);
        Assert.Null(mute.Output);
    }

    [Fact]
    public void Retrying_and_a_reconnect_keep_the_pc_muted()
    {
        var (mute, pc) = Make();
        mute.Update(PcMutePhase.Streaming, true, FollowDefault);
        foreach (var phase in new[] { PcMutePhase.Between, PcMutePhase.Between, PcMutePhase.Streaming, PcMutePhase.Between, PcMutePhase.Streaming })
        {
            mute.Update(phase, true, FollowDefault);
            Assert.True(pc[FakePcOutput.Speakers].Muted, $"{phase}: the PC speakers play again");
        }
        Assert.Single(pc.Writes); // never unmuted and muted again in between
    }

    [Fact]
    public void An_output_muted_already_is_not_ours_and_stays_muted_at_the_end()
    {
        var (mute, pc) = Make(muted: true);
        mute.Update(PcMutePhase.Streaming, true, FollowDefault);
        Assert.Null(mute.Holding);
        mute.Update(PcMutePhase.Ended, true, FollowDefault);
        Assert.Empty(pc.Writes);
        Assert.True(pc[FakePcOutput.Speakers].Muted);
    }

    [Fact]
    public void An_unmute_by_hand_is_respected_for_the_rest_of_the_session()
    {
        var (mute, pc) = Make();
        mute.Update(PcMutePhase.Streaming, true, FollowDefault);
        pc[FakePcOutput.Speakers].Muted = false; // the user, in Windows
        foreach (var phase in new[] { PcMutePhase.Streaming, PcMutePhase.Between, PcMutePhase.Streaming })
            mute.Update(phase, true, FollowDefault);
        Assert.False(pc[FakePcOutput.Speakers].Muted); // not muted again
        Assert.Null(mute.Holding);

        pc[FakePcOutput.Speakers].Muted = true; // and muted by hand again: theirs now, kept at the end
        mute.Update(PcMutePhase.Ended, true, FollowDefault);
        Assert.True(pc[FakePcOutput.Speakers].Muted);
        Assert.Single(pc.Writes);

        mute.Update(PcMutePhase.Streaming, true, FollowDefault); // the next session decides afresh: muted already, not ours
        Assert.Null(mute.Holding);
        pc[FakePcOutput.Speakers].Muted = false;
        mute.Update(PcMutePhase.Ended, true, FollowDefault);
        mute.Update(PcMutePhase.Streaming, true, FollowDefault); // and one after that mutes it again
        Assert.Equal(FakePcOutput.Speakers, mute.Holding);
    }

    [Fact]
    public void The_end_unmutes_only_an_output_that_is_still_muted()
    {
        var (mute, pc) = Make();
        mute.Update(PcMutePhase.Streaming, true, FollowDefault);
        pc[FakePcOutput.Speakers].Muted = false; // unmuted by hand, and the session ends before anything looked
        mute.Update(PcMutePhase.Ended, true, FollowDefault);
        Assert.Single(pc.Writes);

        mute.Update(PcMutePhase.Streaming, true, FollowDefault);
        pc[FakePcOutput.Speakers].Gone = true; // unplugged meanwhile
        mute.Release("test");
        Assert.Equal(2, pc.Writes.Count);
        Assert.Null(mute.Holding);
    }

    [Fact]
    public void Quitting_gives_the_sound_back()
    {
        var (mute, pc) = Make();
        mute.Update(PcMutePhase.Streaming, true, FollowDefault);
        mute.Release("HomePodCast quits");
        Assert.False(pc[FakePcOutput.Speakers].Muted);
        mute.Release("HomePodCast quits"); // twice (Quit, then the window's FormClosing): once
        Assert.Equal(2, pc.Writes.Count);
    }

    [Fact]
    public void Turned_on_while_streaming_it_mutes_now_and_turned_off_it_unmutes()
    {
        var (mute, pc) = Make();
        mute.Update(PcMutePhase.Streaming, false, FollowDefault);
        Assert.Empty(pc.Writes);
        Assert.NotNull(mute.Output); // read for 首页's hint

        mute.Update(PcMutePhase.Streaming, true, FollowDefault);
        Assert.True(pc[FakePcOutput.Speakers].Muted);
        mute.Update(PcMutePhase.Streaming, false, FollowDefault);
        Assert.False(pc[FakePcOutput.Speakers].Muted);
        Assert.Null(mute.Holding);

        // Unmuted by hand, then the switch turned off and on again: that is a new decision.
        mute.Update(PcMutePhase.Streaming, true, FollowDefault);
        pc[FakePcOutput.Speakers].Muted = false;
        mute.Update(PcMutePhase.Streaming, true, FollowDefault);
        Assert.False(pc[FakePcOutput.Speakers].Muted);
        mute.Update(PcMutePhase.Streaming, false, FollowDefault);
        mute.Update(PcMutePhase.Streaming, true, FollowDefault);
        Assert.True(pc[FakePcOutput.Speakers].Muted);
    }

    [Fact]
    public void Off_never_touches_the_pc()
    {
        var (mute, pc) = Make();
        foreach (var phase in new[] { PcMutePhase.Between, PcMutePhase.Streaming, PcMutePhase.Between, PcMutePhase.Ended })
            mute.Update(phase, false, FollowDefault);
        Assert.Empty(pc.Writes);
    }

    [Fact]
    public void A_capture_routed_to_another_device_leaves_the_default_output_alone()
    {
        // Mixer → 采集设备 is the virtual card: voice chat on the speakers is meant to stay there.
        var (mute, pc) = Make();
        mute.Update(PcMutePhase.Streaming, true, new PcCapture(true, FakePcOutput.Cable));
        Assert.Empty(pc.Writes);

        // The default output chosen by its id (in another case) is still the default output.
        mute.Update(PcMutePhase.Streaming, true, new PcCapture(true, FakePcOutput.Speakers.ToUpperInvariant()));
        Assert.True(pc[FakePcOutput.Speakers].Muted);

        // Switched to the virtual card while streaming: the speakers get their sound back.
        mute.Update(PcMutePhase.Streaming, true, new PcCapture(true, FakePcOutput.Cable));
        Assert.False(pc[FakePcOutput.Speakers].Muted);
    }

    [Fact]
    public void Nothing_is_muted_for_the_cinema_or_a_group_that_plays_on_the_default_output()
    {
        var (mute, pc) = Make();
        mute.Update(PcMutePhase.Streaming, true, new PcCapture(SystemCapture: false, null)); // the cinema's file
        mute.Update(PcMutePhase.Streaming, true, new PcCapture(true, null, [FakePcOutput.Headphones, FakePcOutput.Speakers]));
        Assert.Empty(pc.Writes);
        mute.Update(PcMutePhase.Streaming, true, new PcCapture(true, null, [FakePcOutput.Headphones])); // another local output
        Assert.True(pc[FakePcOutput.Speakers].Muted);
    }

    [Fact]
    public void A_new_default_output_gets_the_mute_and_the_old_one_its_sound_back()
    {
        var (mute, pc) = Make();
        mute.Update(PcMutePhase.Streaming, true, FollowDefault);
        pc.DefaultId = FakePcOutput.Headphones; // headphones plugged in while streaming
        mute.Update(PcMutePhase.Streaming, true, FollowDefault);
        Assert.False(pc[FakePcOutput.Speakers].Muted);
        Assert.True(pc[FakePcOutput.Headphones].Muted);
        Assert.Equal(FakePcOutput.Headphones, mute.Holding);

        // While retrying the old one is given back, the new one waits for streaming.
        pc.DefaultId = FakePcOutput.Speakers;
        mute.Update(PcMutePhase.Between, true, FollowDefault);
        Assert.False(pc[FakePcOutput.Headphones].Muted);
        Assert.False(pc[FakePcOutput.Speakers].Muted);
        mute.Update(PcMutePhase.Streaming, true, FollowDefault);
        Assert.True(pc[FakePcOutput.Speakers].Muted);

        // No default output any more (the last device unplugged): the mute is let go.
        pc.DefaultId = null;
        mute.Update(PcMutePhase.Streaming, true, FollowDefault);
        Assert.False(pc[FakePcOutput.Speakers].Muted);
        Assert.Null(mute.Holding);
    }

    [Fact]
    public void A_mute_that_failed_is_not_undone_later()
    {
        var (mute, pc) = Make();
        pc.FailWrites = true;
        mute.Update(PcMutePhase.Streaming, true, FollowDefault);
        Assert.Null(mute.Holding);
        pc.FailWrites = false;
        mute.Update(PcMutePhase.Ended, true, FollowDefault);
        Assert.Empty(pc.Writes);
    }

    // ---------------------------------------------------------------- the record for a crash (AppConfig.PcMutedEndpointId)

    private static (PcMute Mute, FakePcOutput Pc, List<string?> Saved) Recorded(bool muted = false)
    {
        var pc = FakePcOutput.WithSpeakers(muted);
        var saved = new List<string?>();
        return (new PcMute(pc, saved.Add), pc, saved);
    }

    [Fact]
    public void Our_mute_is_written_down_when_it_begins_and_cleared_when_it_is_given_back()
    {
        var (mute, _, saved) = Recorded();
        mute.Update(PcMutePhase.Streaming, true, FollowDefault);
        Assert.Equal(new string?[] { FakePcOutput.Speakers }, saved);
        mute.Update(PcMutePhase.Between, true, FollowDefault);
        mute.Update(PcMutePhase.Streaming, true, FollowDefault);
        Assert.Single(saved); // kept through retrying, written once
        mute.Update(PcMutePhase.Ended, true, FollowDefault);
        Assert.Equal(new string?[] { FakePcOutput.Speakers, null }, saved);

        mute.Update(PcMutePhase.Streaming, true, FollowDefault);
        mute.Release("HomePodCast quits");
        Assert.Equal(new string?[] { FakePcOutput.Speakers, null, FakePcOutput.Speakers, null }, saved);
    }

    [Fact]
    public void An_unmute_by_hand_clears_the_record()
    {
        var (mute, pc, saved) = Recorded();
        mute.Update(PcMutePhase.Streaming, true, FollowDefault);
        pc[FakePcOutput.Speakers].Muted = false;
        mute.Update(PcMutePhase.Streaming, true, FollowDefault); // hands off
        Assert.Equal(new string?[] { FakePcOutput.Speakers, null }, saved);
        mute.Update(PcMutePhase.Ended, true, FollowDefault);
        Assert.Equal(2, saved.Count);
    }

    [Fact]
    public void A_mute_that_is_not_ours_is_never_written_down()
    {
        var (mute, pc, saved) = Recorded(muted: true);
        mute.Update(PcMutePhase.Streaming, true, FollowDefault); // muted already
        mute.Update(PcMutePhase.Ended, true, FollowDefault);
        pc[FakePcOutput.Speakers].Muted = false;
        pc.FailWrites = true;
        mute.Update(PcMutePhase.Streaming, true, FollowDefault); // the mute failed
        Assert.Empty(saved);
    }

    [Fact]
    public void A_new_default_output_moves_the_record_and_a_failed_unmute_keeps_it_for_the_next_start()
    {
        var (mute, pc, saved) = Recorded();
        mute.Update(PcMutePhase.Streaming, true, FollowDefault);
        pc.DefaultId = FakePcOutput.Headphones;
        mute.Update(PcMutePhase.Streaming, true, FollowDefault);
        Assert.Equal(new string?[] { FakePcOutput.Speakers, null, FakePcOutput.Headphones }, saved);

        pc.FailWrites = true; // Windows refuses the unmute at the end
        mute.Update(PcMutePhase.Ended, true, FollowDefault);
        Assert.Equal(FakePcOutput.Headphones, saved[^1]);
        Assert.True(pc[FakePcOutput.Headphones].Muted);
    }

    [Fact]
    public void The_next_start_gives_back_what_a_crashed_run_left_muted()
    {
        var pc = FakePcOutput.WithSpeakers(muted: true); // muted by us, then the process was killed
        PcMute.RestoreLeftover(pc, FakePcOutput.Speakers);
        Assert.False(pc[FakePcOutput.Speakers].Muted);
        Assert.Equal(new[] { (FakePcOutput.Speakers, false) }, pc.Writes);
    }

    [Fact]
    public void A_leftover_unmuted_meanwhile_or_gone_is_left_alone()
    {
        var pc = FakePcOutput.WithSpeakers(); // the user unmuted it before starting again
        PcMute.RestoreLeftover(pc, FakePcOutput.Speakers);
        Assert.Empty(pc.Writes);
        Assert.False(pc[FakePcOutput.Speakers].Muted);

        pc[FakePcOutput.Headphones].Muted = true;
        pc[FakePcOutput.Headphones].Gone = true; // unplugged
        PcMute.RestoreLeftover(pc, FakePcOutput.Headphones);
        PcMute.RestoreLeftover(pc, "{0.0.0.00000000}.{uninstalled}");
        Assert.Empty(pc.Writes);
    }

    [Fact]
    public void Idle_without_a_mute_reads_nothing()
    {
        var (mute, pc) = Make();
        for (int i = 0; i < 5; i++)
        {
            mute.Update(PcMutePhase.Between, true, FollowDefault);
            mute.Update(PcMutePhase.Ended, true, FollowDefault);
        }
        Assert.Equal(0, pc.Reads);
    }

    // ---------------------------------------------------------------- 首页's hint 「电脑也在出声」

    private static PcOutputState Output(EndpointFormFactor formFactor = EndpointFormFactor.Speakers, string name = "扬声器 (Realtek(R) Audio)",
        bool muted = false, float level = 0.4f) => new(new AudioEndpoint(FakePcOutput.Speakers, name, formFactor), muted, level);

    [Theory]
    [InlineData(EndpointFormFactor.Speakers, true)]
    [InlineData(EndpointFormFactor.Headphones, true)]
    [InlineData(EndpointFormFactor.Headset, true)]
    [InlineData(EndpointFormFactor.DigitalAudioDisplayDevice, false)] // HDMI / DisplayPort audio
    [InlineData(EndpointFormFactor.Spdif, false)]
    [InlineData(EndpointFormFactor.LineLevel, false)]
    [InlineData(EndpointFormFactor.RemoteNetworkDevice, false)]
    [InlineData(EndpointFormFactor.Handset, false)]
    [InlineData(EndpointFormFactor.UnknownDigitalPassthrough, false)]
    [InlineData(EndpointFormFactor.Unknown, false)]
    public void Only_speakers_and_headphones_get_the_hint(EndpointFormFactor formFactor, bool shown) =>
        Assert.Equal(shown, PcMute.HintShown(true, false, false, false, FollowDefault, Output(formFactor)));

    [Theory]
    [InlineData("扬声器 (Realtek(R) Audio)", false)]
    [InlineData("Speakers (Realtek High Definition Audio)", false)]
    [InlineData("耳机 (WH-1000XM4 Stereo)", false)]
    [InlineData("Headset Earphone (HyperX Cloud II Wireless)", false)]
    [InlineData("Lautsprecher (Muuto Speaker)", false)]                 // "uu" inside a word is no UU card
    [InlineData("CABLE Input (VB-Audio Virtual Cable)", true)]
    [InlineData("VoiceMeeter Input (VB-Audio VoiceMeeter VAIO)", true)]
    [InlineData("Speakers (Steam Streaming Speakers)", true)]
    [InlineData("扬声器 (UU 虚拟声卡)", true)]
    [InlineData("扬声器 (UU Audio Device)", true)]
    [InlineData("Speakers (Oculus Virtual Audio Device)", true)]
    [InlineData("喇叭 (虛擬音效卡)", true)]
    [InlineData("?", true)]                                             // Windows gave no name: unsure
    [InlineData("", true)]
    public void Virtual_cards_that_say_they_are_speakers_get_no_hint(string name, bool virtualCard)
    {
        Assert.Equal(virtualCard, PcMute.LooksVirtual(name));
        Assert.Equal(!virtualCard, PcMute.HintShown(true, false, false, false, FollowDefault, Output(name: name)));
    }

    [Fact]
    public void The_hint_needs_a_stream_heard_on_this_pc_and_no_decision_yet()
    {
        var o = Output();
        Assert.True(PcMute.HintShown(true, false, false, false, FollowDefault, o));
        Assert.False(PcMute.HintShown(false, false, false, false, FollowDefault, o));            // not streaming
        Assert.False(PcMute.HintShown(true, true, false, false, FollowDefault, o));             // 推流时电脑静音 on
        Assert.False(PcMute.HintShown(true, false, true, false, FollowDefault, o));             // 不再提示
        Assert.False(PcMute.HintShown(true, false, false, true, FollowDefault, o));             // per-app routing: chosen per app
        Assert.False(PcMute.HintShown(true, false, false, false, FollowDefault, null));         // not read
        Assert.False(PcMute.HintShown(true, false, false, false, FollowDefault, Output(muted: true)));
        Assert.False(PcMute.HintShown(true, false, false, false, FollowDefault, Output(level: EndpointState.SilentLevel)));
        Assert.False(PcMute.HintShown(true, false, false, false, FollowDefault, Output(level: 0)));
        Assert.True(PcMute.HintShown(true, false, false, false, FollowDefault, Output(level: 0.02f)));
        Assert.False(PcMute.HintShown(true, false, false, false, new PcCapture(true, FakePcOutput.Cable), o)); // routed elsewhere
        Assert.True(PcMute.HintShown(true, false, false, false, new PcCapture(true, FakePcOutput.Speakers), o));
        Assert.False(PcMute.HintShown(true, false, false, false, new PcCapture(false, null), o)); // the cinema
        Assert.False(PcMute.HintShown(true, false, false, false, new PcCapture(true, null, [FakePcOutput.Speakers]), o));
    }

    // ---------------------------------------------------------------- the volume keys

    [Fact]
    public void With_the_pc_muted_FollowWindows_is_handled_as_WhileStreaming_and_nothing_else_changes()
    {
        foreach (var mode in Enum.GetValues<VolumeKeyMode>())
        {
            Assert.Equal(mode, VolumeKeyRules.Effective(mode, mutePcWhileStreaming: false));
            Assert.Equal(mode == VolumeKeyMode.FollowWindows ? VolumeKeyMode.WhileStreaming : mode, VolumeKeyRules.Effective(mode, true));
        }
        // So a muted Windows is never followed: the keys go to the HomePod, Windows is not watched.
        var route = VolumeKeyRules.RouteFor(VolumeKeyRules.Effective(VolumeKeyMode.FollowWindows, true), streaming: true, outputDevice: true);
        Assert.Equal(VolumeKeyRoute.Intercept, route);
        Assert.Equal(EndpointMode.Off, VolumeKeyRules.EndpointModeFor(route));
    }

    [Fact]
    public void A_profile_that_says_FollowWindows_does_not_bring_the_problem_back()
    {
        var cfg = new AppConfig { MutePcWhileStreaming = true, VolumeKeys = VolumeKeyMode.WhileStreaming };
        var saved = UsageProfiles.Capture(new AppConfig { VolumeKeys = VolumeKeyMode.FollowWindows });
        UsageProfiles.Apply(cfg, saved);
        Assert.Equal(VolumeKeyMode.FollowWindows, cfg.VolumeKeys); // the profile's choice, as saved
        Assert.True(cfg.MutePcWhileStreaming);                       // this PC's setting is not the profile's
        Assert.Equal(VolumeKeyMode.WhileStreaming, VolumeKeyRules.Effective(cfg.VolumeKeys, cfg.MutePcWhileStreaming));
    }

    [Fact]
    public void Our_own_mute_carries_a_fixed_event_context_that_following_windows_ignores()
    {
        Assert.NotEqual(Guid.Empty, PcMute.EventContext);
        Assert.Equal(PcMute.EventContext, new Guid("3f0c5e2a-9b7d-4c61-8e45-1d2a7b9c6e10")); // the same in every run and version
        var forwarder = Guid.NewGuid();
        Assert.True(VolumeKeyForwarder.NotFollowed(PcMute.EventContext, forwarder)); // our unmute as the switch goes off
        Assert.True(VolumeKeyForwarder.NotFollowed(forwarder, forwarder));           // the follower's own level writes
        Assert.False(VolumeKeyForwarder.NotFollowed(Guid.Empty, forwarder));         // the keys, the Windows slider
        Assert.False(VolumeKeyForwarder.NotFollowed(Guid.NewGuid(), forwarder));     // another program
    }

    [Fact]
    public void A_new_config_has_the_pc_mute_off_and_the_hint_not_dismissed()
    {
        var cfg = new AppConfig();
        Assert.False(cfg.MutePcWhileStreaming);
        Assert.False(cfg.PcSoundHintDismissed);
    }
}

// TrayApp.Save writes the unit-tests profile's config.json, and the notice switches the process-wide language: one at a time.
[CollectionDefinition("PC mute app", DisableParallelization = true)]
public sealed class PcMuteAppCollection { }

/// <summary>
/// The app's side of 推流时电脑静音 (TrayApp.PcMute.cs) on a TrayApp that never ran its constructor (TestApp), with the
/// fake default output: the hint's 「只用 HomePod 播放」 and 「不再提示」, the switch while streaming, and the end of the session.
/// </summary>
[Collection("PC mute app")]
public class PcMuteAppTests
{
    private static (TrayApp App, FakePcOutput Pc) Make(VolumeKeyMode keys = VolumeKeyMode.FollowWindows, bool streaming = true,
        string? leftover = null, bool leftoverMuted = false)
    {
        var cfg = TestApp.Config();
        cfg.VolumeKeys = keys;
        cfg.PcMutedEndpointId = leftover;
        var app = TestApp.Create(cfg);
        var pc = FakePcOutput.WithSpeakers(leftoverMuted);
        app.UsePcOutput(pc); // what InitPcMute does with the real output
        SetState(app, streaming ? StreamState.Streaming : StreamState.Idle);
        return (app, pc);
    }

    [Fact]
    public void A_start_after_a_crash_gives_the_sound_back_and_forgets_the_record()
    {
        var (app, pc) = Make(streaming: false, leftover: FakePcOutput.Speakers, leftoverMuted: true);
        Assert.False(pc[FakePcOutput.Speakers].Muted);
        Assert.Null(app.Config.PcMutedEndpointId);

        var (again, untouched) = Make(streaming: false, leftover: FakePcOutput.Speakers); // unmuted meanwhile
        Assert.Empty(untouched.Writes);
        Assert.Null(again.Config.PcMutedEndpointId);
    }

    [Fact]
    public void The_app_writes_the_record_while_its_mute_stands()
    {
        var (app, _) = Make();
        app.SetMutePcWhileStreaming(true);
        Assert.Equal(FakePcOutput.Speakers, app.Config.PcMutedEndpointId);
        app.SetMutePcWhileStreaming(false);
        Assert.Null(app.Config.PcMutedEndpointId);
    }

    private static void SetState(TrayApp app, StreamState state) =>
        typeof(StreamController).GetProperty(nameof(StreamController.State))!.SetValue(app.Controller, state);

    [Theory]
    [InlineData(VolumeKeyMode.FollowWindows, VolumeKeyMode.WhileStreaming)]
    [InlineData(VolumeKeyMode.Off, VolumeKeyMode.WhileStreaming)]          // keys on Windows would unmute the PC
    [InlineData(VolumeKeyMode.WhileStreaming, VolumeKeyMode.WhileStreaming)]
    [InlineData(VolumeKeyMode.WhenWindowsMuted, VolumeKeyMode.WhenWindowsMuted)] // works on a muted Windows as it is
    public void Only_HomePod_turns_the_pc_mute_on_and_mutes_now(VolumeKeyMode before, VolumeKeyMode after)
    {
        var (app, pc) = Make(before);
        app.RefreshPcSound();
        Assert.True(app.PcSoundHintShown);
        Assert.Empty(pc.Writes);

        app.PlayOnlyOnHomePod();
        Assert.True(app.Config.MutePcWhileStreaming);
        Assert.Equal(after, app.Config.VolumeKeys);
        Assert.Equal(after, app.EffectiveVolumeKeys);
        Assert.True(pc[FakePcOutput.Speakers].Muted);
        Assert.False(app.PcSoundHintShown);
        Assert.False(app.Config.PcSoundHintDismissed);
    }

    [Fact]
    public void Dont_show_again_hides_the_hint_for_good_and_changes_nothing_else()
    {
        var (app, pc) = Make();
        app.RefreshPcSound();
        Assert.True(app.PcSoundHintShown);
        app.DismissPcSoundHint();
        Assert.True(app.Config.PcSoundHintDismissed);
        Assert.False(app.PcSoundHintShown);
        Assert.False(app.Config.MutePcWhileStreaming);
        Assert.Equal(VolumeKeyMode.FollowWindows, app.Config.VolumeKeys);
        Assert.Empty(pc.Writes);
    }

    [Fact]
    public void No_hint_and_no_mute_before_streaming()
    {
        var (app, pc) = Make(streaming: false);
        app.SetMutePcWhileStreaming(true);
        app.RefreshPcSound();
        Assert.False(app.PcSoundHintShown);
        Assert.Empty(pc.Writes);
        Assert.Equal(0, pc.Reads);
    }

    [Fact]
    public void The_switch_works_while_streaming_and_the_pc_comes_back_once_the_session_is_over()
    {
        var (app, pc) = Make();
        app.SetMutePcWhileStreaming(true);
        Assert.Equal(VolumeKeyMode.FollowWindows, app.Config.VolumeKeys);
        Assert.Equal(VolumeKeyMode.WhileStreaming, app.EffectiveVolumeKeys);
        Assert.True(pc[FakePcOutput.Speakers].Muted);

        SetState(app, StreamState.Retrying);
        app.RefreshPcSound();
        SetState(app, StreamState.Idle); // the stop of a reconnect
        app.RefreshPcSound();
        Assert.True(pc[FakePcOutput.Speakers].Muted);

        // Idle for longer than the grace: the session is over.
        typeof(TrayApp).GetField("_idleSince", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(app, Stopwatch.GetTimestamp() - Stopwatch.Frequency * 2);
        app.RefreshPcSound();
        Assert.False(pc[FakePcOutput.Speakers].Muted);

        SetState(app, StreamState.Streaming); // the next connection
        app.RefreshPcSound();
        Assert.True(pc[FakePcOutput.Speakers].Muted);
        app.SetMutePcWhileStreaming(false);
        Assert.False(pc[FakePcOutput.Speakers].Muted);
        Assert.Equal(VolumeKeyMode.FollowWindows, app.EffectiveVolumeKeys);
    }

    [Fact]
    public void A_capture_device_chosen_elsewhere_gets_no_mute_and_no_hint()
    {
        var (app, pc) = Make();
        app.Routing.SetCaptureDevice(FakePcOutput.Cable, "CABLE Input (VB-Audio Virtual Cable)");
        app.SetMutePcWhileStreaming(true);
        Assert.Empty(pc.Writes);
        app.SetMutePcWhileStreaming(false);
        Assert.False(app.PcSoundHintShown);
    }

    // ---------------------------------------------------------------- the notice on 首页

    [Fact]
    public void The_notice_appears_only_when_asked_and_its_buttons_do_their_one_thing() => OnSta("zh-CN", () =>
    {
        int only = 0, dismissed = 0;
        using var host = new Host(784, out var notice, () => only++, () => dismissed++);
        Show(host, notice, shown: false, followsWindows: true);
        Assert.True(notice.Card.Collapsed);

        Show(host, notice, shown: true, followsWindows: true);
        Assert.False(notice.Card.Collapsed);
        Assert.True(notice.Card.Visible);
        Assert.Equal("电脑也在出声", notice.Title.Text);
        Assert.Equal("HomePod 跟随 Windows 音量，所以电脑这边也在放。", notice.Text.Text);
        Assert.Equal("只用 HomePod 播放", notice.OnlyHomePod.Text);
        Assert.Equal(ButtonKind.Primary, notice.OnlyHomePod.Kind);
        Assert.Equal("不再提示", notice.Dismiss.Text);
        Assert.True(notice.Card.Bottom < host.Below.Top, "the notice is above the cards");
        Show(host, notice, shown: true, followsWindows: false);
        Assert.Equal("推送到 HomePod 的声音，电脑这边也在放。", notice.Text.Text);
        Assert.Equal(0, only + dismissed); // never without a click

        notice.OnlyHomePod.PerformClick();
        Assert.Equal((1, 0), (only, dismissed));
        notice.Dismiss.PerformClick();
        Assert.Equal((1, 1), (only, dismissed));
    });

    [Theory]
    [InlineData("zh-CN", 784)] // 首页 at the window's default width
    [InlineData("zh-CN", 456)] // and at its minimum
    [InlineData("de", 784)]
    [InlineData("de", 456)]
    [InlineData("en", 456)]
    public void The_buttons_stay_whole(string language, int pageWidth) => OnSta(language, () =>
    {
        using var host = new Host(pageWidth, out var notice, () => { }, () => { });
        foreach (bool follows in new[] { true, false })
        {
            Show(host, notice, shown: true, follows);
            var card = notice.Card;
            Rectangle InCard(Control c) => card.RectangleToClient(c.Parent!.RectangleToScreen(c.Bounds));
            string where = $"{language} {pageWidth}{(follows ? ", follows Windows" : "")}";
            foreach (var button in new[] { notice.OnlyHomePod, notice.Dismiss })
            {
                Assert.True(card.ClientRectangle.Contains(InCard(button)), $"{where}: 「{button.Text}」 sticks out");
                Assert.True(button.Width >= button.GetPreferredSize(Size.Empty).Width, $"{where}: 「{button.Text}」 is squeezed");
                Assert.False(InCard(button).IntersectsWith(InCard(notice.Text)), $"{where}: 「{button.Text}」 covers the text");
            }
            Assert.False(InCard(notice.OnlyHomePod).IntersectsWith(InCard(notice.Dismiss)), $"{where}: the buttons overlap");
            foreach (var text in new[] { notice.Title, notice.Text })
            {
                Assert.True(card.ClientRectangle.Contains(InCard(text)), $"{where}: 「{text.Text}」 sticks out");
                Assert.True(text.Height >= text.GetPreferredSize(new Size(text.Width, 0)).Height, $"{where}: 「{text.Text}」 is cut off");
            }
            Assert.True(card.Bottom < host.Below.Top, $"{where}: the notice overlaps the cards");
        }
    });

    private static void Show(Host host, PcSoundNotice notice, bool shown, bool followsWindows)
    {
        notice.Show(shown, followsWindows);
        host.Page.PerformLayout();
        Application.DoEvents();
    }

    /// <summary>An invisible window with a page like 首页: the notice, then a card standing for the speaker and scene cards.</summary>
    private sealed class Host : Form
    {
        public Host(int pageWidth, out PcSoundNotice notice, Action onlyHomePod, Action dismiss)
        {
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-20000, -20000);
            Opacity = 0;
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            Page = new ScrollPage();
            notice = new PcSoundNotice(onlyHomePod, dismiss);
            Below = Ui.Card(Ui.Header(L.T("音量")));
            Page.Content.Controls.AddRange([notice.Card, Below]);
            Controls.Add(Page);
            Show();
            int width = Theme.Dp(pageWidth, DeviceDpi), height = Theme.Dp(420, DeviceDpi);
            ClientSize = new Size(width, height);
            Page.SetBounds(0, 0, width, height);
            Page.PerformLayout();
            Application.DoEvents();
        }

        public ScrollPage Page { get; }
        public Card Below { get; }
    }

    private static void OnSta(string language, Action body)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            string previous = L.Language;
            try { L.Use(language); body(); }
            catch (Exception ex) { error = ex; }
            finally { L.Use(previous); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The notice test did not finish.");
        if (error != null) ExceptionDispatchInfo.Throw(error);
    }
}
