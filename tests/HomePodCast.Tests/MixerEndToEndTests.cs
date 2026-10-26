using HomePodCast.Audio;

namespace HomePodCast.Tests;

public sealed class MixerEndToEndTests
{
    [Fact]
    public async Task Real_controllers_and_all_fake_transports_keep_independent_destinations_and_release_together()
    {
        await using var scenario = new MixerScenario();
        Assert.Equal(MixerEditResult.Applied, (await scenario.Connect()).Result);
        await Until(() => scenario.Coordinator.Statuses.Count == 2 && scenario.Coordinator.Statuses.All(s => s.State == StreamState.Streaming));
        await Until(() => new[] { "rtp:" + MixerScenario.GameId, "rtp:" + MixerScenario.MusicId, "local", "dlna" }
            .All(sink => scenario.Nonzero.GetValueOrDefault(sink) > 1000));
        var game = scenario.Controller(scenario.GameKey)!;
        var sender = game.ActiveSender;
        Assert.Equal(105, game.EffectiveLatencyMs);
        Assert.Equal(RoutedCapture.RoutedExtraLatencyMs, game.Capture!.ExtraLatencyMs);
        Assert.True(scenario.Controller("group:" + MixerScenario.MusicGroup)!.EffectiveLatencyMs >= 2000);
        Assert.DoesNotContain(scenario.Coordinator.Statuses, s => s.Key == "group:" + MixerScenario.QuietGroup);
        Assert.All(scenario.Host!.Live.Values, live => Assert.Equal(1, live));
        Assert.Equal(2, scenario.Host.Live.Values.Sum());
        scenario.LoseMusic();
        await Until(() => scenario.Coordinator.Statuses.Single(s => s.Key == "group:" + MixerScenario.MusicGroup).State == StreamState.Idle);
        Assert.Equal(StreamState.Streaming, game.State);
        Assert.Same(sender, game.ActiveSender);
        Assert.Equal(105, game.EffectiveLatencyMs);
        Assert.Empty(scenario.Errors);
        await scenario.Coordinator.DisconnectAsync();
        Assert.Equal(0, scenario.Coordinator.Leases.Count);
        Assert.All(scenario.Host.Live.Values, live => Assert.Equal(0, live));
        Assert.NotEmpty(scenario.OpenedRenderIds);
        Assert.All(scenario.OpenedRenderIds, id => Assert.Equal(MixerScenario.LocalId, id));
        Assert.Equal(MixerScenario.CaptureId, scenario.Config.CaptureDeviceId);
    }

    [Fact]
    public async Task Full_route_sequence_including_offline_and_replace_all_keeps_fake_payloads_correct()
    {
        await using var scenario = new MixerScenario();
        var c = scenario.Config; var mixer = scenario.Coordinator;
        await scenario.Connect();
        await Until(() => mixer.Statuses.Count == 2 && mixer.Statuses.All(s => s.State == StreamState.Streaming));
        var settings = MixerRoutes.Read(c).Settings!;
        async Task Apply(MixerRoutingSettings next)
        {
            Assert.Equal(MixerEditResult.Applied, (await mixer.EditAsync(c, next, scenario.NotifyRoutingChanged)).Result);
        }
        var quiet = MixerScenario.Group(MixerScenario.QuietGroup);
        await Apply(settings with { Default = quiet });
        await Until(() => mixer.Statuses.Any(s => s.Key == "group:" + MixerScenario.QuietGroup && s.State == StreamState.Streaming));
        var offline = MixerRoutingV2Tests.Named("airplay:" + MixerScenario.OfflineId);
        Assert.Equal(MixerEditResult.Applied, mixer.PreviewRoute(c, null, offline).Result);
        await Apply(MixerRoutes.Read(c).Settings! with { Default = offline });
        await Until(() => mixer.Statuses.Any(s => s.Key == MixerRoutes.SpeakerKey("airplay:" + MixerScenario.OfflineId) && s.State == StreamState.Retrying));
        await Apply(MixerRoutes.Read(c).Settings! with { Default = quiet });
        await Until(() => mixer.Statuses.Any(s => s.Key == "group:" + MixerScenario.QuietGroup && s.State == StreamState.Streaming));
        var sender = scenario.Controller("group:" + MixerScenario.QuietGroup)!.ActiveSender;
        settings = MixerRoutes.Read(c).Settings!; settings.Apps["game"] = quiet;
        await Apply(settings);
        Assert.Same(sender, scenario.Controller("group:" + MixerScenario.QuietGroup)!.ActiveSender);
        Assert.Equal(MixerEditResult.Applied, (await mixer.SwitchAllAsync(c, MixerScenario.Group(MixerScenario.MusicGroup),
            mixer.PreviewSwitch(c), scenario.NotifyRoutingChanged)).Result);
        await Until(() => mixer.Statuses.Count == 1 && mixer.Statuses[0].State == StreamState.Streaming);
        settings = MixerRoutes.Read(c).Settings!; settings.Apps["voice"] = MixerRoute.Local;
        await Apply(settings);
        await Task.Delay(3000); // Past the documented receiver-buffer drain interval; payload ratios must now match.
        Assert.Empty(scenario.Errors);
        Assert.Equal(32, c.AppSendVolumes["game"]); Assert.Equal(MixerScenario.CaptureId, c.CaptureDeviceId);
        await mixer.DisconnectAsync(); Assert.Equal(0, mixer.Leases.Count);
    }

    private static async Task Until(Func<bool> complete)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!complete()) await Task.Delay(10, timeout.Token);
    }
}
