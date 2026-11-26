using System.Runtime.InteropServices;

namespace HomePodCast.Audio;

/// <summary>
/// The level Windows shows for an output device (its peak meter), read without capturing anything: how the app notices
/// sound while no stream is up (AppleTvIdlePolicy). The meter sits before the device's volume and mute, so a muted
/// output still shows what plays. The device is opened again every few seconds, so a new default output is followed.
/// </summary>
internal sealed class OutputMeter : IDisposable
{
    private static readonly long ReopenMs = 5000;

    private IAudioMeterInformation? _meter;
    private string? _deviceId;
    private long _openedMs;

    /// <summary>The peak of what <paramref name="deviceId"/> (null: the default output) plays now, 0…1; null: it can't be read.</summary>
    public float? Peak(string? deviceId)
    {
        long now = Environment.TickCount64;
        if (_meter == null || _deviceId != deviceId || now - _openedMs >= ReopenMs)
        {
            Close();
            _meter = Open(deviceId);
            _deviceId = deviceId;
            _openedMs = now;
        }
        if (_meter == null) return null;
        try
        {
            if (_meter.GetPeakValue(out float peak) >= 0) return peak;
        }
        catch (COMException) { }
        catch (InvalidCastException) { }
        Close();
        return null;
    }

    private static IAudioMeterInformation? Open(string? deviceId)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            device = AudioEndpoints.Open(enumerator, EDataFlow.Render, deviceId);
            if (device == null) return null;
            var iid = typeof(IAudioMeterInformation).GUID;
            return device.Activate(ref iid, CoreAudio.ClsCtxAll, IntPtr.Zero, out var obj) >= 0 ? obj as IAudioMeterInformation : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
        finally
        {
            if (device != null) Marshal.ReleaseComObject(device);
            if (enumerator != null) Marshal.ReleaseComObject(enumerator);
        }
    }

    private void Close()
    {
        if (_meter != null) Marshal.ReleaseComObject(_meter);
        _meter = null;
    }

    public void Dispose() => Close();
}
