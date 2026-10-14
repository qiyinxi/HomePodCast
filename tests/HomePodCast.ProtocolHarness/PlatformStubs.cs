// The integration harness uses transient pairing only and never accesses Windows settings or user files.
using HomePodCast.Protocol;

namespace HomePodCast
{
    internal static class Log
    {
        public static void Info(string text) => Console.WriteLine(text);
        public static void Warn(string text) => Console.WriteLine(text);
        public static void Debug(string text) => Console.WriteLine(text);
        public static string Name(object? name) => name?.ToString() ?? "";
    }
    internal static class AppEnvironment { public static bool IsPackaged => false; }
    internal static class Native
    {
        public static IDisposable EnterMmcss(string name) => new PreciseTimer();
        internal sealed class PreciseTimer : IDisposable
        {
            public void Sleep(long ticks)
            {
                long deadline = System.Diagnostics.Stopwatch.GetTimestamp() + ticks;
                while (System.Diagnostics.Stopwatch.GetTimestamp() < deadline)
                {
                    double ms = (deadline - System.Diagnostics.Stopwatch.GetTimestamp()) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    if (ms > 2) Thread.Sleep((int)ms - 1); else Thread.SpinWait(64);
                }
            }
            public void Dispose() { }
        }
    }
}

namespace HomePodCast.Net
{
    public sealed class PairingStore
    {
        public HapPairing? Find(string id) => throw new InvalidOperationException("CI uses no stored pairings");
    }
    public enum NetworkCategory { Public }
    public sealed class NetworkProfile
    {
        public NetworkCategory Category => NetworkCategory.Public;
        public string DisplayName => "CI loopback";
        public static NetworkProfile? ForLocalAddress(System.Net.IPAddress address) => null;
    }
    internal static class Firewall
    {
        internal sealed record Inspection(bool PublicAllowed);
        public static Inspection Inspect() => new(true);
    }
}
