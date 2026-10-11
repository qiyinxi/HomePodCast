using System.Reflection;

namespace HomePodCast.Update;

/// <summary>
/// A SemVer 2.0 version as the releases use it: X.Y.Z, optionally with a prerelease part (0.2.2-beta.1). Build
/// metadata (+abc123, added to the informational version by the SDK) is dropped. Ordered by SemVer precedence:
/// 0.2.2-beta.1 &lt; 0.2.2-beta.2 &lt; 0.2.2-beta.10 &lt; 0.2.2-rc.1 &lt; 0.2.2 &lt; 0.2.3-beta.1.
/// </summary>
internal sealed class ReleaseVersion : IComparable<ReleaseVersion>, IEquatable<ReleaseVersion>
{
    private ReleaseVersion(int major, int minor, int patch, string[] prerelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease;
    }

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }

    /// <summary>The dot-separated prerelease identifiers ("beta", "1"); empty for a stable version.</summary>
    public IReadOnlyList<string> Prerelease { get; }

    public bool IsPrerelease => Prerelease.Count > 0;

    /// <summary>
    /// A version that a release tag produces (build.yml): X.Y.Z, X.Y.Z-beta.N or X.Y.Z-rc.N. Branch, PR and local
    /// builds (0.2.0-dev.57, 0.2.0-pr.3.12, 0.2.0-local.1) are not.
    /// </summary>
    public bool IsReleaseBuild => Prerelease.Count == 0 ||
        Prerelease is [var kind, var n] && kind is "beta" or "rc" && n.All(char.IsAsciiDigit);

    /// <summary>"0.2.2", "v0.2.2-beta.1", "0.2.2-beta.1+abc"; null when it is not one.</summary>
    public static ReleaseVersion? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim();
        if (s[0] is 'v' or 'V') s = s[1..];
        int plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        int dash = s.IndexOf('-');
        var core = (dash >= 0 ? s[..dash] : s).Split('.');
        if (core.Length != 3) return null;
        var numbers = new int[3];
        for (int i = 0; i < 3; i++)
            if (core[i].Length == 0 || !core[i].All(char.IsAsciiDigit) || !int.TryParse(core[i], out numbers[i])) return null;
        string[] pre = [];
        if (dash >= 0)
        {
            pre = s[(dash + 1)..].Split('.');
            if (pre.Any(id => id.Length == 0 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))) return null;
        }
        return new ReleaseVersion(numbers[0], numbers[1], numbers[2], pre);
    }

    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null) return 1;
        int c = Major.CompareTo(other.Major);
        if (c == 0) c = Minor.CompareTo(other.Minor);
        if (c == 0) c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        // A stable version ranks above its prereleases.
        if (Prerelease.Count == 0 || other.Prerelease.Count == 0)
            return Prerelease.Count == other.Prerelease.Count ? 0 : Prerelease.Count == 0 ? 1 : -1;
        for (int i = 0; i < Math.Min(Prerelease.Count, other.Prerelease.Count); i++)
        {
            c = CompareIdentifier(Prerelease[i], other.Prerelease[i]);
            if (c != 0) return c;
        }
        return Prerelease.Count.CompareTo(other.Prerelease.Count);
    }

    /// <summary>Numeric identifiers compare as numbers and rank below alphanumeric ones; others compare in ASCII order.</summary>
    private static int CompareIdentifier(string a, string b)
    {
        bool an = a.All(char.IsAsciiDigit), bn = b.All(char.IsAsciiDigit);
        if (an && bn)
        {
            var x = a.TrimStart('0');
            var y = b.TrimStart('0');
            return x.Length != y.Length ? x.Length.CompareTo(y.Length) : Math.Sign(string.CompareOrdinal(x, y));
        }
        if (an != bn) return an ? -1 : 1;
        return Math.Sign(string.CompareOrdinal(a, b));
    }

    public bool Equals(ReleaseVersion? other) => other is not null && CompareTo(other) == 0;
    public override bool Equals(object? obj) => obj is ReleaseVersion v && Equals(v);
    public override int GetHashCode() => ToString().GetHashCode();

    public static bool operator >(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) > 0;
    public static bool operator <(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) < 0;
    public static bool operator >=(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) >= 0;
    public static bool operator <=(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) <= 0;

    public override string ToString() =>
        Prerelease.Count == 0 ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{string.Join('.', Prerelease)}";

    /// <summary>
    /// The running build's version, as CI passed it (-p:Version=0.2.2-beta.1): the informational version without
    /// its build metadata. The assembly version (0.2.2.0) loses the prerelease part.
    /// </summary>
    public static string CurrentText { get; } = ReadCurrent();

    private static string ReadCurrent()
    {
        var asm = typeof(ReleaseVersion).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(info))
        {
            int plus = info.IndexOf('+');
            return plus >= 0 ? info[..plus] : info;
        }
        return asm.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    /// <summary>
    /// Why this build never checks for updates by itself (null: it may): a test profile (HOMEPODCAST_PROFILE), a debug
    /// build, or a version no release tag produces (branch, PR and local builds).
    /// </summary>
    public static string? WhyNotAutomatic(string version, string? profile, bool debugBuild)
    {
        if (profile != null) return $"test profile \"{profile}\"";
        if (debugBuild) return "debug build";
        var v = Parse(version);
        if (v == null) return $"version \"{version}\" is not X.Y.Z";
        return v.IsReleaseBuild ? null : $"development build {v}";
    }
}
