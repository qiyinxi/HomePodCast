namespace HomePodCast.Net;

// Preserve the safety decision at the transport boundary; retry rules must never depend on translated text.
internal sealed class DlnaBusyException(string message = "The DLNA renderer is in use; explicit takeover is required.")
    : InvalidOperationException(message);
internal sealed class DlnaTakenOverException(string message = "The DLNA renderer was taken over by another controller.")
    : InvalidOperationException(message);
internal sealed class DlnaPolicyException(string message = "DLNA streaming requires a private network; the endpoint is closed.")
    : InvalidOperationException(message);
internal sealed class DlnaUnsupportedException(string message) : NotSupportedException(message);
internal sealed class DlnaStoppedException(string message) : IOException(message);

internal enum DlnaFailureKind { Transient, Busy, TakenOver, Unsupported, Policy, Stopped }

internal static class DlnaFailures
{
    public static bool RequiresUserAction(Exception? error) => error is DlnaBusyException or DlnaTakenOverException
        or DlnaPolicyException or DlnaUnsupportedException or DlnaStoppedException or ArgumentException;
    public static DlnaFailureKind Kind(Exception? error) => error switch
    {
        DlnaBusyException => DlnaFailureKind.Busy,
        DlnaTakenOverException => DlnaFailureKind.TakenOver,
        DlnaUnsupportedException => DlnaFailureKind.Unsupported,
        DlnaPolicyException => DlnaFailureKind.Policy,
        DlnaStoppedException => DlnaFailureKind.Stopped,
        _ => DlnaFailureKind.Transient,
    };
}
