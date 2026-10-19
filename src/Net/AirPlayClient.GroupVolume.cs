using System.Globalization;
using System.Text;

namespace HomePodCast.Net;

public sealed partial class AirPlayClient
{
    /// <summary>Group audio gates may open only after the receiver accepted the capped volume.</summary>
    internal void SetGroupVolumePercentConfirmed(double percent)
    {
        double db = VolumeLimit.ClampDb(PercentToDb(percent), VolumeCapPercent);
        var body = Encoding.UTF8.GetBytes($"volume: {db.ToString("F6", CultureInfo.InvariantCulture)}");
        Expect(_rtsp.Rtsp("SET_PARAMETER", contentType: "text/parameters", body: body), "SET_PARAMETER volume");
    }
}
