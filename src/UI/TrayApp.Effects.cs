using HomePodCast.Audio;

namespace HomePodCast.UI;

// Microphone, reverb and equalizers (MicEffects) and their place in the send path:
// mic mix-in → output EQ (all sound) → night-mode compressor.
internal sealed partial class TrayApp
{
    private readonly ToolStripMenuItem _effectsItem = new(L.T("麦克风与音效…"));
    private MicEffects? _fx;

    /// <summary>The microphone and effects engine shared by the send path and the 麦克风与音效 page.</summary>
    public MicEffects Fx => _fx!;

    /// <summary>Called once from the constructor, after InitSound.</summary>
    private void InitEffects(ContextMenuStrip menu)
    {
        _fx = new MicEffects(Config.Effects);
        Controller.Effects.Insert(0, _fx.OutputEq);
        Controller.Effects.Insert(0, new MixIn(_fx.HomePodSource));
        ApplyNightEq();

        _effectsItem.Click += (_, _) => ShowEffects();
        menu.Items.Insert(menu.Items.IndexOf(_muteItem) + 1, _effectsItem);
    }

    /// <summary>Night mode also turns on 「减弱低音」 on the output EQ, like the HomePod's own "Reduce Bass".</summary>
    private void ApplyNightEq()
    {
        if (_fx != null) _fx.OutputPresetOverride = Config.NightMode ? EqPresets.Id(EqPreset.ReduceBass) : null;
    }

    public void ShowEffects() => ShowMain(AppPage.Effects);

    private void DisposeEffects() => _fx?.Dispose();
}
