namespace HomePodCast;

// experimental since 1.4
/// <summary>
/// A single speaker's connection should try 5.1 (StreamController.Surround.cs). The capture reads the device choice and
/// the blocking reason live, so a running 5.1 capture notices 按声卡分流 and per-app routing changes by itself.
/// </summary>
/// <param name="CaptureDeviceId">The device chosen with 按声卡分流; null: follow the Windows default output.</param>
/// <param name="Blocked">Non-null (translated) while 5.1 can't be captured (per-app routing on): the session then ends.</param>
public sealed record SurroundRequest(Func<string?> CaptureDeviceId, Func<string?> Blocked)
{
    /// <summary>Known before any network traffic: stereo at once, with this reason in the status (translated).</summary>
    public string? Refusal { get; init; }
}

// experimental since 1.4
/// <summary>When a connection tries 5.1 surround, and its fixed latency.</summary>
public static class SurroundPolicy
{
    /// <summary>
    /// The anchor lead in ms. 2000 was verified on a HomePod 2 (OS 27.0) on 2026-10-28; 1800 keeps the picture delay
    /// (this + the encoder delay + the A/V extras, about 1.9 s) under the browser extension's 2000 ms clamp.
    /// </summary>
    public const int LatencyMs = 1800;

    /// <summary>The AAC-LC encoder's priming (2048 frames at 48 kHz, about 43 ms): an estimate, not measured.</summary>
    public const int EncoderDelayMs = 43;

    /// <summary>The switch is on and the destination is one speaker, not a group, stereo pair or multi-room.</summary>
    public static bool Applies(AppConfig c) => c.Surround51 && c.DeviceId != null && GroupPlan.FromConfig(c) == null;

    /// <summary>
    /// Whether this speaker's connection asks for 5.1 at all: only <c>StreamController.RunAsync</c> does, and Start runs
    /// it for a HomePod, an Apple TV or a speaker not found yet (null). Third-party AirPlay 2 and AirPlay 1 receivers go
    /// to RunThirdPartyAsync or RunRaopAsync whatever their protocol setting, so they stream stereo either way.
    /// </summary>
    public static bool Reaches(Net.AirPlayDevice? device) => device == null || device.Kind is Net.ReceiverKind.HomePod or Net.ReceiverKind.AppleTv;

    /// <summary>
    /// Null: stereo as always (switch off, a group, no speaker). Otherwise a request; its <see cref="SurroundRequest.Refusal"/>
    /// is set when the mixer hub (several destinations, or this PC plus a speaker) or per-app routing rules 5.1 out, since
    /// both capture apps one by one, in stereo.
    /// </summary>
    public static SurroundRequest? Request(AppConfig destination, bool hub, bool routed, Func<string?> captureDeviceId, Func<string?> blocked) =>
        !Applies(destination) ? null : new(captureDeviceId, blocked)
        {
            Refusal = hub ? L.T("多个去处同时播放时不能用") : routed ? L.T("按程序分流开启时不能用") : null,
        };
}
