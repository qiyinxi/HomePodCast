using HomePodCast.Protocol;

namespace HomePodCast.Tests;

/// <summary>The pairing dialog's messages for what the receiver answered (no window is opened).</summary>
public class PairingDialogTests
{
    [Fact]
    public void The_dialog_explains_the_receiver_s_refusals()
    {
        const string detail = "pair-setup M2: TLV error";
        Assert.Contains("90", UI.PairingDialog.Describe(new HapPairingException(detail, HapError.BackOff, 90), starting: true));
        foreach (var error in new[] { HapError.BackOff, HapError.Busy, HapError.MaxTries, HapError.MaxPeers, HapError.Unavailable })
            Assert.DoesNotContain(detail, UI.PairingDialog.Describe(new HapPairingException(detail, error), starting: false));
        Assert.Equal(UI.PairingDialog.Describe(new IOException("x"), starting: true), UI.PairingDialog.Describe(new IOException("y"), starting: false));
        Assert.Contains("pair-setup M6", UI.PairingDialog.Describe(new HapPairingException("pair-setup M6: bad"), starting: false));
    }
}
