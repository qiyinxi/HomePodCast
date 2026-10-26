using HomePodCast.Net;

namespace HomePodCast.UI;

internal sealed partial class TrayApp
{
    private bool _groupInteractionOpen;

    private void InitializeGroupInteractions(StreamController? source = null)
    {
        var controller = source ?? Controller;
        controller.GroupReceiverActionRequired += request => _ui.Post(_ =>
        {
            bool Current() => !_quitting && _wantConnected && controller.MatchesGroupReceiverInteraction(request);
            if (!Current() || _groupInteractionOpen || _pairingOpen || _raopDialog
                || !PairingPolicy.OffersPinPairing(request.Trigger)) return;
            _groupInteractionOpen = true;
            bool resume = false;
            var trigger = request.Trigger;
            try
            {
                switch (request.Error)
                {
                    case RaopPasswordRequiredException:
                        using (var dialog = new RaopPasswordDialog(request.Device.Name))
                        {
                            if (dialog.ShowDialog(_form.Visible ? _form : null) != DialogResult.OK || !Current()) break;
                            Config.RaopPasswordsProtected[RaopPassword.Key(request.Device.DeviceId)] = RaopPassword.Protect(request.Device.DeviceId, dialog.Password);
                            Config.Save(); resume = true;
                        }
                        break;
                    case RaopBusyException when request.Trigger == ConnectTrigger.Manual:
                        resume = TakeoverDialog.Confirm(_form.Visible ? _form : null, request.Device.Name) && Current();
                        trigger = ConnectTrigger.TakeOver;
                        break;
                    case PairingRequiredException pairing:
                        bool thirdParty = request.Device.Kind is not (ReceiverKind.HomePod or ReceiverKind.AppleTv);
                        resume = PairingDialog.Run(_form.Visible ? _form : null, request.Device.Name, pairing.Reason,
                            ct => PinPairingSession.StartAsync(request.Device.Address, request.Device.Port, ct, pairingOverRtsp: thirdParty),
                            value => { if (Current()) Pairings.Save(request.Device.DeviceId, request.Device.Name, value); }, thirdParty) && Current();
                        break;
                }
                if (!resume || !Current()) return;
                // Optional sessions resume alone. Required failures can have ended the whole attempt already.
                if (!controller.ResumeGroupReceiverOutput(request, trigger))
                {
                    var key = Mixer?.KeyForController(controller);
                    if (key != null) PostConnection(() => Mixer!.ReconnectAsync(key, trigger).GetAwaiter().GetResult());
                    else Connect(trigger);
                }
            }
            finally { _groupInteractionOpen = false; }
        }, null);
    }
}
