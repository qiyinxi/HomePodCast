using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace HomePodCast.Protocol;

/// <summary>RTSP Digest authentication, including legacy challenges without qop.</summary>
public sealed class RaopDigest
{
    private readonly Dictionary<string, string> _parameters;
    private uint _count;
    public RaopDigest(string challenge)
    {
        if (!challenge.StartsWith("Digest ", StringComparison.OrdinalIgnoreCase)) throw new FormatException("Digest authentication required");
        _parameters = new(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(challenge[7..], "([\\w-]+)\\s*=\\s*(?:\"([^\"]*)\"|([^,\\s]+))"))
            _parameters[match.Groups[1].Value] = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value;
        if (!_parameters.ContainsKey("realm") || !_parameters.ContainsKey("nonce")) throw new FormatException("Digest realm and nonce required");
    }

    public string Authorization(string method, string uri, string password, string username = "HomePodCast", string? clientNonce = null)
    {
        string algorithm = _parameters.GetValueOrDefault("algorithm", "MD5");
        if (!algorithm.Equals("MD5", StringComparison.OrdinalIgnoreCase) && !algorithm.Equals("MD5-sess", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Unsupported Digest algorithm: " + algorithm);
        static string Hash(string text) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        static string Quote(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        string realm = _parameters["realm"], nonce = _parameters["nonce"];
        string cnonce = clientNonce ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        string a1 = Hash($"{username}:{realm}:{password}");
        if (algorithm.Equals("MD5-sess", StringComparison.OrdinalIgnoreCase)) a1 = Hash($"{a1}:{nonce}:{cnonce}");
        string a2 = Hash($"{method}:{uri}");
        bool qop = _parameters.ContainsKey("qop");
        if (qop && !_parameters["qop"].Split(',').Any(v => v.Trim().Equals("auth", StringComparison.OrdinalIgnoreCase)))
            throw new NotSupportedException("Digest qop=auth required");
        string nc = (++_count).ToString("x8");
        string response = qop ? Hash($"{a1}:{nonce}:{nc}:{cnonce}:auth:{a2}") : Hash($"{a1}:{nonce}:{a2}");
        string header = $"Digest username={Quote(username)}, realm={Quote(realm)}, nonce={Quote(nonce)}, uri={Quote(uri)}, response={Quote(response)}, algorithm={algorithm}";
        if (qop) header += $", qop=auth, nc={nc}, cnonce={Quote(cnonce)}";
        else if (algorithm.Equals("MD5-sess", StringComparison.OrdinalIgnoreCase)) header += $", cnonce={Quote(cnonce)}";
        if (_parameters.TryGetValue("opaque", out var opaque)) header += ", opaque=" + Quote(opaque);
        return header;
    }
}
