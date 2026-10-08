using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using HomePodCast.Net;
using HomePodCast.Protocol;

namespace HomePodCast.Tests;

/// <summary>
/// In-process stand-in for the RTP side of one speaker: a data port and a control port on loopback that
/// record every datagram with its arrival time, plus the sender-side control socket that an
/// AirPlayClient would normally own. No RTSP or pairing.
/// </summary>
internal sealed class FakeReceiver : IDisposable
{
    public sealed record Packet(long Qpc, byte[] Bytes);

    public readonly UdpClient Data = new(new IPEndPoint(IPAddress.Loopback, 0));
    public readonly UdpClient Control = new(new IPEndPoint(IPAddress.Loopback, 0));
    public readonly UdpClient SenderControl = new(new IPEndPoint(IPAddress.Loopback, 0));
    public readonly byte[] Key;
    public readonly uint Ssrc;
    public readonly ConcurrentQueue<Packet> Audio = new();
    public readonly ConcurrentQueue<Packet> Sync = new();
    public readonly ConcurrentQueue<Packet> Resent = new();

    public FakeReceiver(byte[]? key = null, uint ssrc = 0)
    {
        Key = key ?? RandomNumberGenerator.GetBytes(32);
        Ssrc = ssrc != 0 ? ssrc : (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue);
        Listen(Data, p => Audio.Enqueue(p));
        Listen(Control, p => (p.Bytes.Length > 1 && p.Bytes[1] == 0xD6 ? Resent : Sync).Enqueue(p));
    }

    public int DataPort => ((IPEndPoint)Data.Client.LocalEndPoint!).Port;
    public int ControlPort => ((IPEndPoint)Control.Client.LocalEndPoint!).Port;
    public IPEndPoint SenderControlEndPoint => (IPEndPoint)SenderControl.Client.LocalEndPoint!;

    public RtpTarget Target(ChannelMode channels = ChannelMode.Stereo, ushort? firstSeq = null) =>
        new(SenderControl, IPAddress.Loopback, DataPort, ControlPort, Key, Ssrc, channels) { FirstSeq = firstSeq };

    /// <summary>Ask the sender to resend packets, the way a speaker does (0xD5 on the control port).</summary>
    public void RequestResend(ushort firstLost, ushort count)
    {
        var req = new byte[8];
        req[0] = 0x80;
        req[1] = 0xD5;
        BinaryPrimitives.WriteUInt16BigEndian(req.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(req.AsSpan(4), firstLost);
        BinaryPrimitives.WriteUInt16BigEndian(req.AsSpan(6), count);
        Control.Send(req, req.Length, SenderControlEndPoint);
    }

    /// <summary>Decrypt an audio packet: nonce = the trailing 64-bit LE counter, AAD = RTP timestamp + SSRC.</summary>
    public byte[] Decrypt(byte[] packet) => Decrypt(packet, Key);

    public static byte[] Decrypt(byte[] packet, byte[] key)
    {
        int payload = packet.Length - 12 - CounterCipher.TagSize - 8;
        Span<byte> nonce = stackalloc byte[12];
        CounterCipher.MakeNonce(nonce, Counter(packet));
        using var aead = new ChaCha20Poly1305(key);
        var plain = new byte[payload];
        aead.Decrypt(nonce, packet.AsSpan(12, payload), packet.AsSpan(12 + payload, CounterCipher.TagSize), plain,
            packet.AsSpan(4, 8));
        return plain;
    }

    public static ushort Seq(byte[] p) => BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(2));
    public static uint Rtp(byte[] p) => BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(4));
    public static uint SsrcOf(byte[] p) => BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(8));
    public static ulong Counter(byte[] p) => BinaryPrimitives.ReadUInt64LittleEndian(p.AsSpan(p.Length - 8));

    private static void Listen(UdpClient udp, Action<Packet> into)
    {
        new Thread(() =>
        {
            while (true)
            {
                try
                {
                    IPEndPoint? from = null;
                    var bytes = udp.Receive(ref from);
                    into(new Packet(MediaClock.Now, bytes));
                }
                catch (SocketException) { }
                catch (ObjectDisposedException) { return; }
            }
        }) { IsBackground = true, Name = "fake receiver" }.Start();
    }

    public void Dispose()
    {
        Data.Dispose();
        Control.Dispose();
        SenderControl.Dispose();
    }
}

internal static class TestAudio
{
    /// <summary>Deterministic stereo with different left and right content, so any channel mix-up shows.</summary>
    public static float[] Signal(int frames)
    {
        var x = new float[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            x[i * 2] = (float)(0.5 * Math.Sin(2 * Math.PI * 440 * i / 44100.0));
            x[i * 2 + 1] = (float)(0.3 * Math.Sin(2 * Math.PI * 1000 * i / 44100.0) - 0.1);
        }
        return x;
    }

    /// <summary>The big-endian 16-bit PCM the sender should produce for one packet of this signal.</summary>
    public static byte[] ExpectedPayload(float[] signal, int packet, ChannelMode channels)
    {
        var dst = new byte[352 * 4];
        // A slow machine (CI) can collect packets past the end of the test signal: the drained FIFO sends silence,
        // and the packet that straddles the end carries the signal's last frames followed by silence.
        int start = packet * 352 * 2;
        if (start >= signal.Length) return dst;
        var src = signal.AsSpan(start, Math.Min(352 * 2, signal.Length - start));
        for (int i = 0; i < src.Length / 2; i++)
        {
            float l = src[i * 2], r = src[i * 2 + 1];
            (float a, float b) = channels switch
            {
                ChannelMode.LeftOnly => (l, l),
                ChannelMode.RightOnly => (r, r),
                _ => (l, r),
            };
            BinaryPrimitives.WriteInt16BigEndian(dst.AsSpan(i * 4), S16(a));
            BinaryPrimitives.WriteInt16BigEndian(dst.AsSpan(i * 4 + 2), S16(b));
        }
        return dst;
    }

    private static short S16(float x)
    {
        float v = x * 32767f;
        return v >= 32767f ? short.MaxValue : v <= -32768f ? short.MinValue : (short)MathF.Round(v);
    }
}
