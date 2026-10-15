namespace HomePodCast.UI.Pages;

/// <summary>The single multi-speaker page shares the file transport and speaker editor.</summary>
internal sealed class GroupPage : CinemaPage
{
    internal GroupPage(ICinemaHost host, IReadOnlyList<Audio.AudioEndpoint>? inputs = null, IDebounce? volumeWait = null)
        : base(host, inputs, volumeWait) { }
}
