namespace HomePodCast.Net;

public class AirPlayException(string message) : Exception(message)
{
    /// <summary>
    /// The message for the log when the user's <see cref="Exception.Message"/> names a speaker: the same with the names
    /// marked by <see cref="Log.Name"/>, so that a diagnostic report leaves them out (Message never has the markers).
    /// </summary>
    public string? LogMessage { get; init; }

    /// <summary>What the log says about <paramref name="ex"/>.</summary>
    public static string ForLog(Exception ex) => ex is AirPlayException { LogMessage: { } m } ? m : ex.Message;
}
