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

    private static async Task Until(Func<bool> complete)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!complete()) await Task.Delay(10, timeout.Token);
    }
}
