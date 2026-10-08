using System.Text.Json.Serialization;
using HomePodCast.Audio;

namespace HomePodCast;

/// <summary>What the keyboard's volume keys (and the Windows volume) do to the HomePod (设置 → 键盘音量键).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<VolumeKeyMode>))]
public enum VolumeKeyMode
{
    /// <summary>
    /// The keys change Windows as usual and the HomePod follows the Windows master volume (scaled to the volume cap);
    /// muting Windows mutes the HomePod. The default (older configs with ForwardVolumeKeys = true move here).
    /// Without a Windows output device it behaves like <see cref="WhileStreaming"/> (<see cref="VolumeKeyRules.RouteFor"/>).
    /// </summary>
    FollowWindows,

    /// <summary>While streaming, volume up/down/mute go to the HomePod only (a low-level keyboard hook swallows them).</summary>
    WhileStreaming,

    /// <summary>The old forwarding: only while Windows is muted or at 0 %, key steps go to the HomePod and Windows stays silent.</summary>
    WhenWindowsMuted,

    Off,
}

/// <summary>A volume key as the HomePod sees it.</summary>
public enum VolumeKeyCommand { None, Up, Down, Mute }

/// <summary>What actually happens with the volume keys right now (<see cref="VolumeKeyRules.RouteFor"/>).</summary>
public enum VolumeKeyRoute
{
    /// <summary>The keys only change Windows; the HomePod is left alone.</summary>
    PassThrough,

    /// <summary>The keys change Windows and the HomePod follows the Windows volume.</summary>
    Follow,

    /// <summary>Key steps on a muted (or 0 %) Windows go to the HomePod; Windows is put back.</summary>
    Forward,

    /// <summary>The keyboard hook takes the keys: they change the HomePod only.</summary>
    Intercept,
}

/// <summary>What the keyboard hook does with one key event: let it through or swallow it, and what to send.</summary>
internal readonly record struct HookDecision(bool Swallow, VolumeKeyCommand Command);

/// <summary>What one change of the Windows output means for the HomePod in <see cref="VolumeKeyMode.FollowWindows"/>.</summary>
internal readonly record struct FollowAction(double? Percent, bool? Mute)
{
    public bool IsNone => Percent is null && Mute is null;
}

/// <summary>
/// The rules behind <see cref="VolumeKeyMode"/>, kept free of Windows calls so they can be tested:
/// config migration, which keys the hook may swallow, the step per key, and the Windows → HomePod mapping.
/// </summary>
internal static class VolumeKeyRules
{
    public const int VkVolumeMute = 0xAD, VkVolumeDown = 0xAE, VkVolumeUp = 0xAF;

    /// <summary>Percent points per key press or auto-repeat, the same as Windows' own volume keys.</summary>
    public const double StepPercent = 2;

    /// <summary>
    /// The mode for a config written by a version that only had the ForwardVolumeKeys switch: on (its default) →
    /// <see cref="VolumeKeyMode.FollowWindows"/>, off → <see cref="VolumeKeyMode.Off"/>; no switch → the mode as read.
    /// </summary>
    public static VolumeKeyMode Migrate(bool? forwardVolumeKeys, VolumeKeyMode current) => forwardVolumeKeys switch
    {
        true => VolumeKeyMode.FollowWindows,
        false => VolumeKeyMode.Off,
        null => Enum.IsDefined(current) ? current : VolumeKeyMode.FollowWindows,
    };

    public static VolumeKeyCommand CommandFor(int vk) => vk switch
    {
        VkVolumeUp => VolumeKeyCommand.Up,
        VkVolumeDown => VolumeKeyCommand.Down,
        VkVolumeMute => VolumeKeyCommand.Mute,
        _ => VolumeKeyCommand.None,
    };

    /// <summary>Signed step for a key (0 for mute and anything else).</summary>
    public static double StepFor(VolumeKeyCommand command) => command switch
    {
        VolumeKeyCommand.Up => +StepPercent,
        VolumeKeyCommand.Down => -StepPercent,
        _ => 0,
    };

    /// <summary>The HomePod volume after a volume key: one step, never below 0 or above the cap.</summary>
    public static double Next(double current, VolumeKeyCommand command, double cap) =>
        VolumeLimit.Clamp(current + StepFor(command), cap);

    /// <summary>
    /// The effective behaviour. Nothing happens to the HomePod unless we stream. Following or forwarding needs a Windows
    /// output device to watch: without one (no sound driver, the device unplugged or disabled, the endpoint volume not
    /// available) the keys do nothing in Windows anyway, so they are taken over like in
    /// <see cref="VolumeKeyMode.WhileStreaming"/> until a device is back.
    /// </summary>
    public static VolumeKeyRoute RouteFor(VolumeKeyMode mode, bool streaming, bool outputDevice)
    {
        if (!streaming) return VolumeKeyRoute.PassThrough;
        return mode switch
        {
            VolumeKeyMode.WhileStreaming => VolumeKeyRoute.Intercept,
            VolumeKeyMode.FollowWindows => outputDevice ? VolumeKeyRoute.Follow : VolumeKeyRoute.Intercept,
            VolumeKeyMode.WhenWindowsMuted => outputDevice ? VolumeKeyRoute.Forward : VolumeKeyRoute.Intercept,
            _ => VolumeKeyRoute.PassThrough,
        };
    }

    /// <summary>The keyboard hook is installed only while the keys are intercepted.</summary>
    public static bool HookWanted(VolumeKeyMode mode, bool streaming, bool outputDevice) =>
        RouteFor(mode, streaming, outputDevice) == VolumeKeyRoute.Intercept;

    /// <summary>
    /// The keyboard hook's rule for one key event. Only VK_VOLUME_UP/DOWN/MUTE are ever swallowed, and a key-down only
    /// while the keys are intercepted (streaming in <see cref="VolumeKeyMode.WhileStreaming"/>, or with no output device).
    /// A key-up is swallowed exactly when its key-down was, so Windows never sees half a key press. Auto-repeat steps
    /// the volume again; the mute key toggles once per press.
    /// </summary>
    /// <param name="downSwallowed">This key's last key-down was swallowed (the key is held, or this is its key-up).</param>
    public static HookDecision Decide(VolumeKeyMode mode, bool streaming, bool outputDevice, int vk, bool keyDown, bool downSwallowed)
    {
        var command = CommandFor(vk);
        if (command == VolumeKeyCommand.None) return default;
        if (!keyDown) return new HookDecision(downSwallowed, VolumeKeyCommand.None);
        if (!HookWanted(mode, streaming, outputDevice)) return default;
        bool repeat = downSwallowed;
        return new HookDecision(true, command == VolumeKeyCommand.Mute && repeat ? VolumeKeyCommand.None : command);
    }

    /// <summary>What the Windows endpoint watcher does for a route.</summary>
    public static EndpointMode EndpointModeFor(VolumeKeyRoute route) => route switch
    {
        VolumeKeyRoute.Follow => EndpointMode.Follow,
        VolumeKeyRoute.Forward => EndpointMode.Forward,
        _ => EndpointMode.Off,
    };

    /// <summary>HomePod percent for a Windows master level (0..1): the whole Windows range spans 0…cap.</summary>
    public static double FollowPercent(float level, double cap) =>
        Math.Round(Math.Clamp(level, 0f, 1f) * Math.Clamp(cap, 0, 100), 1);

    /// <summary>
    /// Windows went from <paramref name="from"/> to <paramref name="to"/>: mute follows mute, and a new level (while
    /// Windows is not muted) becomes HomePod percent = level × cap. Nothing for an unchanged state.
    /// </summary>
    public static FollowAction Follow(EndpointState from, EndpointState to, double cap)
    {
        bool muteChanged = to.Muted != from.Muted;
        bool levelChanged = MathF.Abs(to.Level - from.Level) > 0.0005f;
        return new FollowAction(
            levelChanged && !to.Muted ? FollowPercent(to.Level, cap) : null,
            muteChanged ? to.Muted : null);
    }
}
