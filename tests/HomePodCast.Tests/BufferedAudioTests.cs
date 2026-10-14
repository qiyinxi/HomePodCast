using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Net;
using System.Net.Sockets;
using HomePodCast.Audio;
using HomePodCast.Net;
using HomePodCast.Protocol;

namespace HomePodCast.Tests;

public sealed class BufferedAudioTests
{
    private static AirPlayDevice Device(int port) => new("CI", "Software receiver", IPAddress.Loopback, port, "Shairport Sync",
        new Dictionary<string, string> { ["features"] = "0x0,0x200" });

    [Fact]
    public async Task BufferedSessionPairsNegotiatesAnchorsAndDecryptsAudio()
    {
        using var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        using var fake = new FakeAirPlayReceiver(false)
        {
            ModelOverride = "Shairport Sync", BufferedDataPort = ((IPEndPoint)tcp.LocalEndpoint).Port,
        };
        var fifo = new AudioFifo(44100, 0, 999);
        fifo.Write(Enumerable.Repeat(.1f, 30000).ToArray());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var accept = tcp.AcceptTcpClientAsync(timeout.Token).AsTask();
        using var client = await AirPlayClient.ConnectBufferedAsync(Device(fake.Port), new StreamOptions(1500, 99)
            { VolumeCapPercent = 10 }, fifo, timeout.Token, timingPorts: fake.TimingPorts);
        using var receiver = await accept;
        Assert.Equal("PTP", fake.SessionSetup!["timingProtocol"]);
        Assert.Equal(103L, fake.StreamSetup!["type"]);
        Assert.Equal(3L, fake.StreamSetup["ct"]);
        Assert.Equal(10, client.VolumeCapPercent);
        Assert.NotNull(fake.RateAnchor);
        Assert.Contains("SETPEERS [encrypted]", fake.Requests);
        Assert.Contains("SETRATEANCHORTIME [encrypted]", fake.Requests);
        byte[] size = new byte[2];
        await receiver.GetStream().ReadExactlyAsync(size, timeout.Token);
        byte[] packet = new byte[BinaryPrimitives.ReadUInt16BigEndian(size) - 2];
        await receiver.GetStream().ReadExactlyAsync(packet, timeout.Token);
        byte[] nonce = new byte[12], decoded = new byte[packet.Length - 36];
        packet.AsSpan(packet.Length - 8).CopyTo(nonce.AsSpan(4));
        using var cipher = new ChaCha20Poly1305(fake.StreamKey!);
        cipher.Decrypt(nonce, packet.AsSpan(12, decoded.Length), packet.AsSpan(12 + decoded.Length, 16), decoded, packet.AsSpan(4, 8));
        Assert.InRange(decoded.Length, 4, 8192);
        Assert.Empty(fake.Errors);
    }

    [Fact]
    public async Task BufferedPathRejectsAppleAndMissingPtpBeforeConnecting()
    {
        var fifo = new AudioFifo(44100, 0, 50);
        await Assert.ThrowsAsync<InvalidOperationException>(() => AirPlayClient.ConnectBufferedAsync(
            Device(1) with { Model = "AudioAccessory6,1" }, new(2000, 10), fifo, default));
        await Assert.ThrowsAsync<AirPlayException>(() => AirPlayClient.ConnectBufferedAsync(
            Device(1) with { Txt = new Dictionary<string, string>() }, new(2000, 10), fifo, default));
    }

    [Fact]
    public void TcpFrameDecryptsWithHeaderAuthenticationAndExplicitNonce()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32), aac = RandomNumberGenerator.GetBytes(511);
        using var cipher = new CounterCipher(key);
        _ = BufferedAudio.Packet(cipher, 0xffffff, uint.MaxValue, 42, aac);
        var wire = BufferedAudio.Packet(cipher, 0x1000000, 1023, 42, aac);
        Assert.Equal(wire.Length, BinaryPrimitives.ReadUInt16BigEndian(wire));
        Assert.Equal(new byte[] { 0x80, 0, 0, 0 }, wire[2..6]);
        Assert.Equal(1UL, BinaryPrimitives.ReadUInt64LittleEndian(wire.AsSpan(wire.Length - 8)));
        byte[] decoded = new byte[aac.Length], nonce = new byte[12];
        wire.AsSpan(wire.Length - 8).CopyTo(nonce.AsSpan(4));
        using var aead = new ChaCha20Poly1305(key);
        aead.Decrypt(nonce, wire.AsSpan(14, aac.Length), wire.AsSpan(14 + aac.Length, 16), decoded, wire.AsSpan(6, 8));
        Assert.Equal(aac, decoded);
        wire[6] ^= 1;
        Assert.Throws<AuthenticationTagMismatchException>(() => aead.Decrypt(nonce, wire.AsSpan(14, aac.Length), wire.AsSpan(14 + aac.Length, 16), decoded, wire.AsSpan(6, 8)));
    }

    [Fact]
    public void AnchorFractionRoundTripsToWithinOneNanosecond()
    {
        const ulong time = 1_700_000_001_987_654_321, clock = 0xfedcba9876543210;
        var anchor = BPlist.ReadDict(BPlist.Write(BufferedAudio.Anchor(clock, time, uint.MaxValue)));
        Assert.Equal(clock, unchecked((ulong)(long)anchor["networkTimeTimelineID"]!));
        ulong seconds = (ulong)(long)anchor["networkTimeSecs"]!;
        ulong fraction = unchecked((ulong)(long)anchor["networkTimeFrac"]!);
        ulong recovered = seconds * 1_000_000_000 + (ulong)(((UInt128)fraction * 1_000_000_000) >> 64);
        Assert.InRange(time - recovered, 0UL, 1UL);
        Assert.Equal((long)uint.MaxValue, anchor["rtpTime"]);
    }

    [Fact]
    public void WindowsEncoderProducesRawAacWithoutAdts()
    {
        // A synthetic buffer is encoded in memory only; this never opens an audio endpoint.
        using var encoder = new AacEncoder();
        var pcm = new short[2048];
        var packets = new List<byte[]>();
        for (int block = 0; block < 12; block++)
        {
            for (int i = 0; i < 1024; i++) pcm[2 * i] = pcm[2 * i + 1] = (short)(4000 * Math.Sin((block * 1024 + i) * 2 * Math.PI * 440 / 44100));
            packets.AddRange(encoder.Encode(pcm));
        }
        Assert.InRange(packets.Count, 8, 12);
        Assert.All(packets, p => Assert.InRange(p.Length, 4, 8192));
        Assert.All(packets, p => Assert.False(p[0] == 0xff && (p[1] & 0xf0) == 0xf0));
        Assert.Throws<ArgumentException>(() => encoder.Encode(new short[2]));
    }
}
