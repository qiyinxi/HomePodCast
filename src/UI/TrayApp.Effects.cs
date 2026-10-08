using HomePodCast.Audio;

namespace HomePodCast.UI;

// Microphone, reverb and equalizers (MicEffects) and their place in the send path:
// mic mix-in → output EQ (all sound) → night-mode compressor.
internal sealed partial class TrayApp
{
    private MicEffects? _fx;

    /// <summary>The microphone and effects engine shared by the send path and the 麦克风与音效 page.</summary>
    public MicEffects Fx => _fx!;

    /// <summary>Called once from the constructor, after InitSound.</summary>
    private void InitEffects()
    {
        _fx = new MicEffects(Config.Effects);
        Controller.Effects.Insert(0, _fx.OutputEq);
        Controller.Effects.Insert(0, new MixIn(_fx.HomePodSource));
        ApplyNightEq();
    }

    /// <summary>Night mode also turns on 「减弱低音」 on the output EQ, like the HomePod's own "Reduce Bass".</summary>
    private void ApplyNightEq()
    {
        if (_fx != null) _fx.OutputPresetOverride = Config.NightMode ? EqPresets.Id(EqPreset.ReduceBass) : null;
    }

    /// <summary>
    /// Turn the microphone on or off (tray flyout) with the rules of the switch on the 麦克风与音效 page, which
    /// asks first when the voice would also go to loudspeakers. Never saved. Returns whether it is on now.
    /// </summary>
    public bool SetMicOn(bool on)
    {
        bool result = _form.SetMicOn(on);
        RaiseStateChanged();
        return result;
    }

    private void DisposeEffects() => _fx?.Dispose();
}
