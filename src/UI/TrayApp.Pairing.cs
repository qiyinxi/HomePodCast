using HomePodCast.Net;

namespace HomePodCast.UI;

// Receivers that need a one-time PIN pairing (an Apple TV). A connect to one without a pairing (or whose pairing it
// refused) stops in the controller (StreamController.Pairing.cs). After the user's 连接 click the pairing dialog opens
// (PairingDialog.cs: the TV shows 4 digits, the user types them) and, once paired, connects; an automatic connect only says
// so in the status line, since starting a pairing lights up the TV. 设置 → 已配对的电视 lists the pairings and forgets one.
internal sealed partial class TrayApp
{
    /// <summary>The HAP pairings (pairings.dat next to config.json, DPAPI-encrypted).</summary>
    public PairingStore Pairings { get; } = PairingStore.Default;

    private bool _pairingOpen;

    /// <summary>Called once from the constructor (UI thread), before the first connect.</summary>
    private void InitPairing()
    {
        Controller.Pairings = Pairings;
        Controller.PairingRequired += request => _ui.Post(_ => OnPairingRequired(request), null);
    }

    private void OnPairingRequired(PairingRequest request)
    {
        if (_quitting) return;
        _wantConnected = false; // nothing reconnects by itself: 连接 starts the pairing
        bool ask = PairingPolicy.OffersPinPairing(request.Trigger);
        Log.Info($"pairing needed ({request.Reason}, {request.Trigger}): {(ask ? "asking" : "waiting for a click on 连接")}");
        if (ask) ShowPairing(request);
    }

    private void ShowPairing(PairingRequest request)
    {
        if (_pairingOpen) return;
        _pairingOpen = true;
        bool paired;
        try
        {
            paired = PairingDialog.Run(_form.Visible ? _form : null, request.Speaker, request.Reason,
                ct => PinPairingSession.StartAsync(request.Address, 7000, ct),
                pairing => Pairings.Save(request.DeviceId, request.Speaker, pairing));
        }
        finally
        {
            _pairingOpen = false;
        }
        if (_quitting) return;
        if (!paired)
        {
            Log.Info("pairing: cancelled");
            return;
        }
        // Connect if it is still the selected speaker (the selection may have changed while the dialog was open).
        if (Config.DeviceId is { } id && PairingStore.Key(id) == PairingStore.Key(request.DeviceId))
            Connect(request.Trigger == ConnectTrigger.TakeOver ? ConnectTrigger.TakeOver : ConnectTrigger.Manual);
    }

    /// <summary>设置 → 已配对的电视 → 忘记配对. The next connect to it asks for a new pairing.</summary>
    public void ForgetPairing(string deviceId)
    {
        try
        {
            Pairings.Forget(deviceId);
        }
        catch (Exception ex)
        {
            Log.Warn($"pairing: could not forget: {ex.Message}");
        }
    }
}
