using System.Net;
using HomePodCast.Net;

namespace HomePodCast;

/// <summary>
/// A connect that stopped because the receiver needs a PIN pairing first (an Apple TV): who, where, why, and what started
/// the connect (only the user's 连接 click may go on to the pairing dialog).
/// </summary>
public sealed record PairingRequest(string DeviceId, IPAddress Address, string Speaker, PairingReason Reason, ConnectTrigger Trigger);

/// <summary>When the app may start a PIN pairing: the TV lights up and shows a PIN, so only right after a click.</summary>
public static class PairingPolicy
{
    /// <summary>
    /// The user clicked 连接 (or 仍然连接): the pairing dialog opens. Automatic connects (startup, a speaker picked in the
    /// list) and reconnects only say in the status line that the speaker needs pairing.
    /// </summary>
    public static bool OffersPinPairing(ConnectTrigger trigger) => trigger is ConnectTrigger.Manual or ConnectTrigger.TakeOver;
}

// Receivers that need a one-time PIN pairing (an Apple TV): the stored pairings are passed to every session, and a connect
// that finds the receiver unpaired (or its pairing refused) stops, says so, and raises PairingRequired; it never retries
// by itself. UI/TrayApp.Pairing.cs decides whether to ask.
public sealed partial class StreamController
{
    /// <summary>Stored HAP pairings, looked up by device id on every connect; null: none (the command line, tests).</summary>
    public PairingStore? Pairings { get; set; }

    /// <summary>Raised on the thread pool when a connect stopped for a pairing (the state is Idle by then).</summary>
    public event Action<PairingRequest>? PairingRequired;

    private void StopForPairing(string deviceId, IPAddress address, PairingRequiredException ex, ConnectTrigger trigger)
    {
        DisposeCapture(); // nothing is sent until it is paired; routed apps play on this PC again
        Set(StreamState.Idle, ex.Message);
        PairingRequired?.Invoke(new PairingRequest(deviceId, address, ex.Speaker, ex.Reason, trigger));
    }
}
