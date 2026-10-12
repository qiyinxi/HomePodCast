using System.Net;
using System.Security.Cryptography;
using System.Text;
using HomePodCast.Audio;
using HomePodCast.Net;
using HomePodCast.Protocol;

namespace HomePodCast.Tests;

/// <summary>The TLV8 framing of the pairing messages and the parsing of the receiver's answers.</summary>
public class HapMessageTests
{
    private static readonly byte[] SessionKey = Enumerable.Range(0, 64).Select(i => (byte)(i * 3 + 1)).ToArray();
    private static readonly byte[] Rfc8032Seed1 = Convert.FromHexString("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60");
    private static readonly byte[] Rfc8032Public1 = Convert.FromHexString("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a");
    private static readonly byte[] Rfc8032Seed2 = Convert.FromHexString("4ccd089b28ff96da9db6c346ec114e0f5b8a319f35aba624da8cf6ed4fb8a6fb");

    /// <summary>Independent of HapMessages: 4 zero bytes and the 8 ASCII characters.</summary>
    private static byte[] Nonce(string label) => [0, 0, 0, 0, .. Encoding.ASCII.GetBytes(label)];

    private static byte[] Hkdf(byte[] secret, string salt, string info) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA512, secret, 32, Encoding.UTF8.GetBytes(salt), Encoding.UTF8.GetBytes(info));

    private static byte[] Seal(byte[] key, string label, byte[] plain)
    {
        using var aead = new ChaCha20Poly1305(key);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        aead.Encrypt(Nonce(label), plain, cipher, tag);
        return [.. cipher, .. tag];
    }

    private static byte[] Open(byte[] key, string label, byte[] sealedData)
    {
        using var aead = new ChaCha20Poly1305(key);
        var plain = new byte[sealedData.Length - 16];
        aead.Decrypt(Nonce(label), sealedData[..^16], sealedData[^16..], plain);
        return plain;
    }

    [Fact]
    public void M1_is_method_0_and_state_1_without_the_transient_flag() =>
        Assert.Equal(Convert.FromHexString("000100060101"), HapPairSetup.M1());

    [Fact]
    public void M3_carries_state_3_and_the_srp_public_key_in_255_byte_fragments_then_the_proof()
    {
        var a = Enumerable.Range(0, 384).Select(i => (byte)(i * 7)).ToArray();
        var proof = Enumerable.Range(0, 64).Select(i => (byte)(255 - i)).ToArray();
        var m3 = HapPairSetup.M3(a, proof);

        Assert.Equal(3 + (2 + 255) + (2 + 129) + (2 + 64), m3.Length);
        Assert.Equal(new byte[] { 0x06, 0x01, 0x03, 0x03, 0xFF }, m3[..5]);
        Assert.Equal(new byte[] { 0x03, 129 }, m3[(3 + 257)..(3 + 259)]);
        Assert.Equal(new byte[] { 0x04, 64 }, m3[(3 + 257 + 131)..(3 + 257 + 133)]);
        var back = Tlv8.Read(m3);
        Assert.Equal(a, back[Tlv8.PublicKey]);
        Assert.Equal(proof, back[Tlv8.Proof]);
    }

    [Fact]
    public void M5_encrypts_the_controller_identity_signed_over_the_controller_sign_key()
    {
        const string controllerId = "1b0e5a3c-6f1d-4c5e-9a1b-2c3d4e5f6a7b";
        var m5 = Tlv8.Read(HapPairSetup.M5(SessionKey, controllerId, Rfc8032Seed1));
        Assert.Equal([5], m5[Tlv8.State]);
        Assert.Equal(2, m5.Count);

        var inner = Tlv8.Read(Open(Hkdf(SessionKey, "Pair-Setup-Encrypt-Salt", "Pair-Setup-Encrypt-Info"), "PS-Msg05", m5[Tlv8.EncryptedData]));
        Assert.Equal(Encoding.UTF8.GetBytes(controllerId), inner[Tlv8.Identifier]);
        Assert.Equal(Rfc8032Public1, inner[Tlv8.PublicKey]);
        var signed = HapMessages.Concat(Hkdf(SessionKey, "Pair-Setup-Controller-Sign-Salt", "Pair-Setup-Controller-Sign-Info"),
            Encoding.UTF8.GetBytes(controllerId), Rfc8032Public1);
        Assert.True(Ed25519.Verify(Rfc8032Public1, signed, inner[Tlv8.Signature]));
        Assert.Equal(Ed25519.Sign(Rfc8032Seed1, signed), inner[Tlv8.Signature]); // deterministic
    }

    [Fact]
    public void M2_gives_salt_and_public_key_and_reports_back_off()
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var b = RandomNumberGenerator.GetBytes(384);
        var (gotSalt, gotB) = HapPairSetup.ReadM2(Tlv8.Write((Tlv8.State, [2]), (Tlv8.Salt, salt), (Tlv8.PublicKey, b)));
        Assert.Equal(salt, gotSalt);
        Assert.Equal(b, gotB);

        var backOff = Assert.Throws<HapPairingException>(() =>
            HapPairSetup.ReadM2(Tlv8.Write((Tlv8.State, [2]), (Tlv8.Error, [3]), (Tlv8.BackOff, [0x2C, 0x01]))));
        Assert.Equal(HapError.BackOff, backOff.Error);
        Assert.Equal(300, backOff.RetryAfterSeconds);
        Assert.Throws<HapPairingException>(() => HapPairSetup.ReadM2(Tlv8.Write((Tlv8.State, [4]), (Tlv8.Salt, salt), (Tlv8.PublicKey, b))));
        Assert.Throws<HapPairingException>(() => HapPairSetup.ReadM2(Tlv8.Write((Tlv8.State, [2]), (Tlv8.Salt, salt))));
        Assert.Throws<HapPairingException>(() => HapPairSetup.ReadM2([0x02, 0x10, 0x01]));
    }

    [Fact]
    public void M4_gives_the_proof_and_a_wrong_pin_is_an_authentication_error()
    {
        var proof = RandomNumberGenerator.GetBytes(64);
        Assert.Equal(proof, HapPairSetup.ReadM4(Tlv8.Write((Tlv8.State, [4]), (Tlv8.Proof, proof))));
        var wrong = Assert.Throws<HapPairingException>(() => HapPairSetup.ReadM4(Tlv8.Write((Tlv8.State, [4]), (Tlv8.Error, [2]))));
        Assert.Equal(HapError.Authentication, wrong.Error);
        Assert.Equal(HapError.MaxTries,
            Assert.Throws<HapPairingException>(() => HapPairSetup.ReadM4(Tlv8.Write((Tlv8.State, [4]), (Tlv8.Error, [5])))).Error);
    }

    private static byte[] M6(byte[] key, byte[] id, byte[] seed, byte[]? signWith = null)
    {
        var ltpk = Ed25519.PublicKey(seed);
        var x = Hkdf(key, "Pair-Setup-Accessory-Sign-Salt", "Pair-Setup-Accessory-Sign-Info");
        var signature = Ed25519.Sign(signWith ?? seed, [.. x, .. id, .. ltpk]);
        var inner = Tlv8.Write((Tlv8.Identifier, id), (Tlv8.PublicKey, ltpk), (Tlv8.Signature, signature));
        return Tlv8.Write((Tlv8.State, [6]),
            (Tlv8.EncryptedData, Seal(Hkdf(key, "Pair-Setup-Encrypt-Salt", "Pair-Setup-Encrypt-Info"), "PS-Msg06", inner)));
    }

    [Fact]
    public void M6_gives_the_receiver_identity_only_when_its_signature_verifies()
    {
        var id = Encoding.UTF8.GetBytes("3E:B7:8E:9F:64:72");
        var (gotId, gotKey) = HapPairSetup.ReadM6(M6(SessionKey, id, Rfc8032Seed2), SessionKey);
        Assert.Equal(id, gotId);
        Assert.Equal(Ed25519.PublicKey(Rfc8032Seed2), gotKey);

        var forged = Assert.Throws<HapPairingException>(() => HapPairSetup.ReadM6(M6(SessionKey, id, Rfc8032Seed2, signWith: Rfc8032Seed1), SessionKey));
        Assert.Null(forged.Error); // not a wrong PIN: the dialog says the pairing failed instead of asking for a new PIN
        var otherKey = SessionKey.Select(b => (byte)(b ^ 1)).ToArray();
        Assert.Throws<HapPairingException>(() => HapPairSetup.ReadM6(M6(otherKey, id, Rfc8032Seed2), SessionKey));
    }

    // Pair-verify with RFC 7748's Alice (us) and Bob (the receiver): the shared secret is the RFC's K.
    private static readonly byte[] Alice = Convert.FromHexString("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a");
    private static readonly byte[] AlicePublic = Convert.FromHexString("8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a");
    private static readonly byte[] BobPublic = Convert.FromHexString("de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f");
    private static readonly byte[] SharedK = Convert.FromHexString("4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742");
    private static readonly byte[] TvId = Encoding.UTF8.GetBytes("3E:B7:8E:9F:64:72");

    private static HapPairing Pairing() => new("1b0e5a3c-6f1d-4c5e-9a1b-2c3d4e5f6a7b", Rfc8032Seed1, TvId, Ed25519.PublicKey(Rfc8032Seed2));

    private static byte[] VerifyM2(byte[] id, byte[] signerSeed)
    {
        var signature = Ed25519.Sign(signerSeed, [.. BobPublic, .. id, .. AlicePublic]);
        var inner = Tlv8.Write((Tlv8.Identifier, id), (Tlv8.Signature, signature));
        var key = Hkdf(SharedK, "Pair-Verify-Encrypt-Salt", "Pair-Verify-Encrypt-Info");
        return Tlv8.Write((Tlv8.State, [2]), (Tlv8.PublicKey, BobPublic), (Tlv8.EncryptedData, Seal(key, "PV-Msg02", inner)));
    }

    [Fact]
    public void Pair_verify_m1_is_state_1_and_the_ephemeral_key() =>
        Assert.Equal([0x06, 0x01, 0x01, 0x03, 0x20, .. AlicePublic], new HapPairVerify(Pairing(), Alice).M1());

    [Fact]
    public void Pair_verify_checks_the_receiver_and_answers_with_our_signed_identity()
    {
        var pairing = Pairing();
        var verify = new HapPairVerify(pairing, Alice);
        var m3 = Tlv8.Read(verify.ReadM2(VerifyM2(TvId, Rfc8032Seed2)));
        Assert.Equal(SharedK, verify.SharedSecret);
        Assert.Equal([3], m3[Tlv8.State]);

        var inner = Tlv8.Read(Open(Hkdf(SharedK, "Pair-Verify-Encrypt-Salt", "Pair-Verify-Encrypt-Info"), "PV-Msg03", m3[Tlv8.EncryptedData]));
        var id = Encoding.UTF8.GetBytes(pairing.ControllerId);
        Assert.Equal(id, inner[Tlv8.Identifier]);
        Assert.True(Ed25519.Verify(Rfc8032Public1, [.. AlicePublic, .. id, .. BobPublic], inner[Tlv8.Signature]));

        HapPairVerify.ReadM4(Tlv8.Write((Tlv8.State, [4])));
        Assert.Equal(HapError.Authentication,
            Assert.Throws<HapPairingException>(() => HapPairVerify.ReadM4(Tlv8.Write((Tlv8.State, [4]), (Tlv8.Error, [2])))).Error);
    }

    [Fact]
    public void Pair_verify_refuses_another_identity_or_a_signature_by_another_key()
    {
        var otherId = Assert.Throws<HapPairingException>(() =>
            new HapPairVerify(Pairing(), Alice).ReadM2(VerifyM2(Encoding.UTF8.GetBytes("AA:BB:CC:DD:EE:FF"), Rfc8032Seed2)));
        Assert.Equal(HapError.Authentication, otherId.Error);
        var reset = Assert.Throws<HapPairingException>(() => new HapPairVerify(Pairing(), Alice).ReadM2(VerifyM2(TvId, Rfc8032Seed1)));
        Assert.Equal(HapError.Authentication, reset.Error);
    }

    [Fact]
    public void Srp_client_checks_the_server_proof()
    {
        var server = new SrpServer("Pair-Setup", "0482");
        var client = new SrpClient("Pair-Setup", "0482");
        var proof = client.ComputeProof(server.Salt, server.PublicKey);
        var result = server.Verify(client.PublicKey, proof);
        Assert.NotNull(result);
        Assert.Equal(result.Value.Key, client.SessionKey);
        Assert.True(client.VerifyServerProof(result.Value.Proof));
        Assert.False(client.VerifyServerProof(RandomNumberGenerator.GetBytes(64)));

        var wrongPin = new SrpClient("Pair-Setup", "4820");
        Assert.Null(server.Verify(wrongPin.PublicKey, wrongPin.ComputeProof(server.Salt, server.PublicKey)));
    }
}

public class PairingPolicyTests
{
    [Theory]
    [InlineData(FakeAirPlayReceiver.AppleTvFlags, true)]   // the user's Apple TV: bit 9
    [InlineData(0x8L, true)]                               // bit 3, PINRequired
    [InlineData(FakeAirPlayReceiver.HomePodFlags, false)]
    [InlineData(0x1A0404L, false)]                         // a HomePod while another sender plays
    [InlineData(0x18404L, false)]
    [InlineData(0L, false)]
    public void Status_flags_say_which_receivers_need_a_pin_pairing(long flags, bool needs)
    {
        Assert.Equal(needs, PairingFlags.NeedsPinPairing(flags));
        Assert.Equal(needs, PairingFlags.NeedsPinPairing(new Dictionary<string, object?> { ["statusFlags"] = flags }));
    }

    [Fact]
    public void Info_without_status_flags_falls_back_to_its_txt_record()
    {
        byte[] txt = [.. TxtEntry("deviceid=3E:B7:8E:9F:64:72"), .. TxtEntry("flags=0x644")];
        Assert.True(PairingFlags.NeedsPinPairing(new Dictionary<string, object?> { ["txtAirPlay"] = txt }));
        Assert.False(PairingFlags.NeedsPinPairing(new Dictionary<string, object?>()));
    }

    private static byte[] TxtEntry(string entry)
    {
        var bytes = Encoding.UTF8.GetBytes(entry);
        return [(byte)bytes.Length, .. bytes];
    }

    [Theory]
    [InlineData(ConnectTrigger.Manual, true)]
    [InlineData(ConnectTrigger.TakeOver, true)]
    [InlineData(ConnectTrigger.Automatic, false)]
    [InlineData(ConnectTrigger.Reconnect, false)]
    public void Only_a_click_on_connect_opens_the_pairing(ConnectTrigger trigger, bool offers) =>
        Assert.Equal(offers, PairingPolicy.OffersPinPairing(trigger));

    [Fact]
    public void The_status_line_names_the_speaker()
    {
        Assert.Contains("客厅 Apple TV", PairingRequiredException.Describe("客厅 Apple TV", PairingReason.NotPaired));
        Assert.Contains("客厅 Apple TV", PairingRequiredException.Describe("客厅 Apple TV", PairingReason.PairingRejected));
        var ex = new PairingRequiredException("客厅 Apple TV", "3E:B7:8E:9F:64:72", PairingReason.NotPaired);
        Assert.Contains(Log.Name("客厅 Apple TV"), AirPlayException.ForLog(ex));
    }
}

public class PairingStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hpc-pairings-" + Guid.NewGuid().ToString("N"));

    private PairingStore NewStore() => new(Path.Combine(_dir, PairingStore.FileName));

    private static HapPairing Pairing(string controllerId = "1b0e5a3c-6f1d-4c5e-9a1b-2c3d4e5f6a7b") =>
        new(controllerId, RandomNumberGenerator.GetBytes(32), Encoding.UTF8.GetBytes("3E:B7:8E:9F:64:72"), RandomNumberGenerator.GetBytes(32));

    [Fact]
    public void Round_trips_through_dpapi_and_finds_the_device_however_its_id_is_written()
    {
        var store = NewStore();
        Assert.Null(store.Find("3E:B7:8E:9F:64:72"));
        Assert.Empty(store.List());
        int changes = 0;
        store.Changed += () => changes++;

        var pairing = Pairing();
        store.Save("3E:B7:8E:9F:64:72", "客厅 Apple TV", pairing);
        Assert.Equal(1, changes);

        var again = NewStore(); // another instance reads the file
        foreach (var id in new[] { "3E:B7:8E:9F:64:72", "3eb78e9f6472", "3e-b7-8e-9f-64-72" })
        {
            var found = again.Find(id);
            Assert.NotNull(found);
            Assert.Equal(pairing.ControllerId, found.ControllerId);
            Assert.Equal(pairing.ControllerSeed, found.ControllerSeed);
            Assert.Equal(pairing.AccessoryId, found.AccessoryId);
            Assert.Equal(pairing.AccessoryPublicKey, found.AccessoryPublicKey);
        }
        var listed = Assert.Single(again.List());
        Assert.Equal("客厅 Apple TV", listed.Name);
        Assert.Equal("3EB78E9F6472", listed.DeviceId);
        Assert.Null(again.Find("AA:BB:CC:DD:EE:FF"));
    }

    [Fact]
    public void Saving_again_replaces_the_pairing()
    {
        var store = NewStore();
        store.Save("3E:B7:8E:9F:64:72", "客厅", Pairing("first"));
        store.Save("3eb78e9f6472", "客厅 Apple TV", Pairing("second"));
        store.Save("11:22:33:44:55:66", "卧室 Apple TV", Pairing("third"));
        Assert.Equal("second", store.Find("3E:B7:8E:9F:64:72")!.ControllerId);
        Assert.Equal(2, store.List().Count);
        Assert.Equal("卧室 Apple TV", store.List()[0].Name); // most recent first
    }

    [Fact]
    public void The_file_holds_no_key_name_or_identifier_in_the_clear()
    {
        var store = NewStore();
        var pairing = Pairing();
        store.Save("3E:B7:8E:9F:64:72", "客厅 Apple TV", pairing);
        var file = File.ReadAllBytes(store.FilePath);
        Assert.False(Contains(file, pairing.ControllerSeed));
        Assert.False(Contains(file, pairing.AccessoryPublicKey));
        Assert.False(Contains(file, Encoding.UTF8.GetBytes(pairing.ControllerId)));
        Assert.False(Contains(file, Encoding.UTF8.GetBytes("客厅")));
        Assert.False(Contains(file, Encoding.UTF8.GetBytes(Convert.ToBase64String(pairing.ControllerSeed))));
        Assert.False(Contains(file, "3EB78E9F6472"u8.ToArray()));
        Assert.False(File.Exists(store.FilePath + ".tmp"));
    }

    [Fact]
    public void A_file_it_cannot_decrypt_counts_as_no_pairings()
    {
        var store = NewStore();
        store.Save("3E:B7:8E:9F:64:72", "客厅 Apple TV", Pairing());
        File.WriteAllBytes(store.FilePath, RandomNumberGenerator.GetBytes(300)); // another user's or PC's, or damaged
        Assert.Null(store.Find("3E:B7:8E:9F:64:72"));
        Assert.Empty(store.List());
        store.Save("11:22:33:44:55:66", "卧室 Apple TV", Pairing()); // and is replaced by the next save
        Assert.NotNull(NewStore().Find("11:22:33:44:55:66"));
    }

    [Fact]
    public void Forget_removes_one_pairing_and_the_file_with_the_last()
    {
        var store = NewStore();
        store.Save("3E:B7:8E:9F:64:72", "客厅 Apple TV", Pairing());
        store.Save("11:22:33:44:55:66", "卧室 Apple TV", Pairing());
        Assert.True(store.Forget("3eb78e9f6472"));
        Assert.False(store.Forget("3E:B7:8E:9F:64:72"));
        Assert.Null(store.Find("3E:B7:8E:9F:64:72"));
        Assert.NotNull(store.Find("11:22:33:44:55:66"));
        Assert.True(store.Forget("11:22:33:44:55:66"));
        Assert.False(File.Exists(store.FilePath));
        Assert.Empty(store.List());
    }

    private static bool Contains(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle) >= 0;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (DirectoryNotFoundException) { }
    }
}

/// <summary>
/// The whole client flow against <see cref="FakeAirPlayReceiver"/> in-process: the automatic connect that finds an Apple TV
/// unpaired (and shows nothing on the TV), the PIN pairing with a wrong PIN first, pair-verify and the encrypted session on
/// the next connects, a pairing the TV no longer knows, and the HomePod's transient path, unchanged.
/// </summary>
public class PairingFlowTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hpc-pairing-flow-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private PairingStore NewStore() => new(Path.Combine(_dir, PairingStore.FileName));

    private static AudioFifo Fifo() => new(RtpSender.SampleRate, targetMs: 20, capMs: 60);

    private static Task<AirPlayClient> Connect(FakeAirPlayReceiver tv, PairingStore? store, string? deviceId = "3E:B7:8E:9F:64:72") =>
        AirPlayClient.ConnectAsync(IPAddress.Loopback, tv.Port, new StreamOptions(150, 20) { Pairings = store, DeviceId = deviceId },
            Fifo(), CancellationToken.None).WaitAsync(Timeout);

    private static async Task<HapPairing> PairAsync(FakeAirPlayReceiver tv, string pin)
    {
        using var session = await PinPairingSession.StartAsync(IPAddress.Loopback, tv.Port, CancellationToken.None).WaitAsync(Timeout);
        return session.Finish(pin);
    }

    [Fact]
    public async Task An_unpaired_apple_tv_stops_the_connect_before_anything_shows_on_the_tv()
    {
        using var tv = new FakeAirPlayReceiver(appleTv: true);
        var ex = await Assert.ThrowsAsync<PairingRequiredException>(() => Connect(tv, NewStore()));
        Assert.Equal(PairingReason.NotPaired, ex.Reason);
        Assert.Equal("客厅 Apple TV", ex.Speaker);
        Assert.Equal("3E:B7:8E:9F:64:72", ex.DeviceId);
        Assert.Equal("GET /info", tv.Requests.First());
        Assert.DoesNotContain(tv.Requests, r => r.Contains("/pair-", StringComparison.Ordinal)); // no /pair-pin-start, no pair-setup
        Assert.Equal(0, tv.PinStarts);

        await Assert.ThrowsAsync<PairingRequiredException>(() => Connect(tv, store: null)); // no store at all (the command line)
        Assert.Equal(0, tv.PinStarts);
        Assert.Empty(tv.Errors);
    }

    [Fact]
    public async Task An_apple_tv_pairs_once_with_its_pin_then_verifies_and_streams_encrypted()
    {
        using var tv = new FakeAirPlayReceiver(appleTv: true) { Pin = "0482" };
        var store = NewStore();

        // A wrong PIN: the TV refuses at M4; the next attempt starts over and the TV shows a new PIN.
        var wrong = await Assert.ThrowsAsync<HapPairingException>(() => PairAsync(tv, "4820"));
        Assert.Equal(HapError.Authentication, wrong.Error);
        Assert.Equal(1, tv.PinStarts);

        var pairing = await PairAsync(tv, "0482");
        Assert.Equal(2, tv.PinStarts);
        Assert.True(tv.IsPaired(pairing.ControllerId));
        Assert.True(Guid.TryParse(pairing.ControllerId, out _));
        Assert.Equal(tv.AccessoryId, pairing.AccessoryId);
        Assert.Equal(tv.AccessoryPublicKey, pairing.AccessoryPublicKey);
        Assert.Equal(Ed25519.SeedSize, pairing.ControllerSeed.Length);
        Assert.Equal(
            ["POST /pair-pin-start", "POST /pair-setup M1", "POST /pair-setup M3",
             "POST /pair-pin-start", "POST /pair-setup M1", "POST /pair-setup M3", "POST /pair-setup M5"],
            tv.Requests.ToArray());
        // pyatv's headers for HAP pairing: X-Apple-HKP 3, its user agent, octet-stream bodies.
        Assert.All(tv.PairingHeaders, h => Assert.Equal(("3", "AirPlay/320.20", "application/octet-stream"), (h.Hkp, h.UserAgent, h.ContentType)));
        store.Save(tv.DeviceId, tv.Name, pairing);

        // The next connect: pair-verify with the stored pairing, then the whole session over the encrypted channel.
        var before = tv.Requests.Count;
        using (var client = await Connect(tv, store))
        {
            Assert.Equal(1, tv.Verifications);
            Assert.Equal(2, tv.PinStarts); // never a new PIN
            Assert.Equal(tv.ArrivalToRenderMs, client.ArrivalToRenderMs);
            Assert.Equal("客厅 Apple TV", client.Name);
            var session = tv.Requests.Skip(before).Where(r => r != "POST /feedback [encrypted]").ToArray();
            Assert.Equal(
                ["GET /info", "POST /pair-verify M1", "POST /pair-verify M3",
                 "SETUP session [encrypted]", "SETUP stream [encrypted]", "RECORD [encrypted]", "FLUSH [encrypted]", "SET_PARAMETER [encrypted]"],
                session);

            // The stream key went over the encrypted channel, and the audio decrypts with it.
            Assert.NotNull(tv.StreamKey);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (tv.Rtp.Audio.IsEmpty && DateTime.UtcNow < deadline) await Task.Delay(20);
            Assert.True(tv.Rtp.Audio.TryPeek(out var packet), "no RTP arrived");
            Assert.Equal(RtpSender.FramesPerPacket * 4, FakeReceiver.Decrypt(packet.Bytes, tv.StreamKey).Length);
        }
        Assert.Contains("TEARDOWN [encrypted]", tv.Requests);

        // The command line passes no device id: the one /info reports finds the same pairing.
        using (await Connect(tv, store, deviceId: null))
            Assert.Equal(2, tv.Verifications);
        Assert.Equal(2, tv.PinStarts);
        Assert.Empty(tv.Errors);
    }

    [Fact]
    public async Task A_pairing_the_tv_no_longer_knows_asks_to_pair_again()
    {
        using var tv = new FakeAirPlayReceiver(appleTv: true);
        var store = NewStore();
        store.Save(tv.DeviceId, tv.Name, await PairAsync(tv, tv.Pin));
        int pinStarts = tv.PinStarts;

        tv.ForgetControllers(); // this PC removed in the Apple TV's settings: refused at pair-verify M4
        var removed = await Assert.ThrowsAsync<PairingRequiredException>(() => Connect(tv, store));
        Assert.Equal(PairingReason.PairingRejected, removed.Reason);

        tv.Reset(); // a factory reset: a new identity, refused at pair-verify M2 (its signature)
        var reset = await Assert.ThrowsAsync<PairingRequiredException>(() => Connect(tv, store));
        Assert.Equal(PairingReason.PairingRejected, reset.Reason);
        Assert.Equal(pinStarts, tv.PinStarts); // never a PIN on the TV without asking
        Assert.DoesNotContain(tv.Requests, r => r.Contains("SETUP", StringComparison.Ordinal));

        // Pairing again replaces the stored one, and connecting works.
        store.Save(tv.DeviceId, tv.Name, await PairAsync(tv, tv.Pin));
        using (await Connect(tv, store))
            Assert.Equal(1, tv.Verifications);
        Assert.Empty(tv.Errors);
    }

    [Fact]
    public async Task A_homepod_keeps_the_transient_pairing_exactly_as_before()
    {
        using var homePod = new FakeAirPlayReceiver(appleTv: false);
        var store = NewStore();
        store.Save("11:22:33:44:55:66", "卧室 Apple TV", new HapPairing("x", RandomNumberGenerator.GetBytes(32), [1], RandomNumberGenerator.GetBytes(32)));

        using (var client = await Connect(homePod, store))
        {
            Assert.Equal(homePod.ArrivalToRenderMs, client.ArrivalToRenderMs);
            // Today's sequence, untouched: /info, then the transient pair-setup (with the /pair-pin-start it always sent,
            // as pyatv's transient path does too), and no pair-verify or PIN pair-setup.
            Assert.Equal(
                ["GET /info", "POST /pair-pin-start", "POST /pair-setup M1 transient", "POST /pair-setup M3",
                 "SETUP session [encrypted]", "SETUP stream [encrypted]", "RECORD [encrypted]", "FLUSH [encrypted]", "SET_PARAMETER [encrypted]"],
                homePod.Requests.Where(r => r != "POST /feedback [encrypted]").ToArray());
        }
        Assert.All(homePod.PairingHeaders, h => Assert.Equal("4", h.Hkp)); // the transient pairing's X-Apple-HKP
        Assert.DoesNotContain(homePod.Requests, r => r.Contains("pair-verify", StringComparison.Ordinal));
        Assert.Equal(0, homePod.Verifications);

        using (await Connect(homePod, store: null, deviceId: null)) { } // the command line: the same
        Assert.DoesNotContain(homePod.Requests, r => r.Contains("pair-verify", StringComparison.Ordinal) || r == "POST /pair-setup M1");
        Assert.Empty(homePod.Errors);
    }

    [Fact]
    public async Task A_receiver_that_refuses_the_transient_pairing_without_flagging_it_asks_for_pairing()
    {
        using var tv = new FakeAirPlayReceiver(appleTv: true) { StatusFlags = 0x44 }; // bit 9 not advertised
        var ex = await Assert.ThrowsAsync<PairingRequiredException>(() => Connect(tv, NewStore()));
        Assert.Equal(PairingReason.NotPaired, ex.Reason);
        // (The failed connect's own TEARDOWN, in plaintext, may follow; AirPlayClient always sends one on Dispose.)
        Assert.Equal(["GET /info", "POST /pair-pin-start", "POST /pair-setup M1 transient"],
            tv.Requests.Where(r => !r.StartsWith("TEARDOWN", StringComparison.Ordinal)).ToArray());
        Assert.Empty(tv.Errors);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (DirectoryNotFoundException) { }
    }
}
