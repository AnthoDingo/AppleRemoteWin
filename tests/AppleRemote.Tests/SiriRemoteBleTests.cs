using Xunit;

namespace AppleRemote.Tests;

/// <summary>
/// Fausse Siri Remote vue depuis l'hôte HCI : contrôleur (connexion, chiffrement, déconnexion), appairage « Just Works »
/// côté répondeur et serveur GATT reprenant la disposition de la vraie (trace HCI Windows, handles de
/// Jack-R1/SiriRemoteDriver), MTU 23.
/// </summary>
sealed class FakeRemote
{
    public const string Address = "14:9D:99:26:BF:CC", PcAddress = "8C:C8:4B:3B:A0:50";
    /// <summary>Publicité réelle de la télécommande en mode appairage (trace HCI) : drapeaux, service 0x1812, données Apple.</summary>
    public static readonly byte[] PairingAdvertisement = Convert.FromHexString("02010603021218" + "0FFF4C008A076D0202149D9926BFCC00");
    /// <summary>Publicité Apple sans service HID (un iPhone de la même trace).</summary>
    public static readonly byte[] PhoneAdvertisement = Convert.FromHexString("02011A020A0C0AFF4C001005661CB97462A7");
    /// <summary>Clés que la télécommande distribue à l'appairage (et seules acceptées ensuite).</summary>
    public static readonly BondKeys Bond = new(Enumerable.Range(0x30, 16).Select(i => (byte)i).ToArray(), 0x4D3E, [1, 2, 3, 4, 5, 6, 7, 8]);
    public readonly HciHost Hci;
    public readonly List<string> Writes = new();
    public int Pairings;
    readonly SortedDictionary<ushort, (byte[] Type, byte[] Value)> _db = new();
    bool _connected;
    byte[] _preq = [], _pres = [], _mconfirm = [], _srand = [], _stk = [];

    public FakeRemote()
    {
        Hci = new HciHost(OnCommand, OnAcl);
        Service(0x0001, 0x1800); Service(0x0006, 0x1801); Service(0x000A, 0x180A);
        Service(0x0015, 0x1812);
        Characteristic(0x0016, 0x02, 0x2A4A, [0x11, 0x01, 0x00, 0x00]);       // HID Information
        Characteristic(0x0018, 0x02, 0x2A4B, [0x05, 0x01]);                   // Report Map (extrait)
        Characteristic(0x001A, 0x04, 0x2A4C, [0x00]);                         // HID Control Point
        Characteristic(0x001C, 0x0A, 0x2A4D, [0x00]); Descriptor(0x001E, 0x2908, [0xF0, 3]);   // fonctionnalité 0xF0
        Characteristic(0x001F, 0x0A, 0x2A4D, [0x00]); Descriptor(0x0021, 0x2908, [0xF1, 3]);   // fonctionnalité 0xF1
        Characteristic(0x0022, 0x12, 0x2A4D, [0x00]);                                          // entrée 0xFC
        Descriptor(0x0024, 0x2902, [0x00, 0x00]); Descriptor(0x0025, 0x2908, [0xFC, 1]);
        Service(0x0026, 0x180F);
        Characteristic(0x0027, 0x12, 0x2A19, [87]); Descriptor(0x0029, 0x2902, [0x00, 0x00]);   // niveau de batterie, notifiable
        Service(0x002D, 0x181E);
        _db[0x0032] = (U16(0x2800), Convert.FromHexString("DC262EB4CDC49781044F13C0B4F24183"));   // service Apple (128 bits)
    }

    public void Event(params byte[] e) => Hci.ReceiveEventBytes(e);
    public void Att(params byte[] pdu) => L2cap(0x04, pdu);
    void Smp(params byte[] pdu) => L2cap(0x06, pdu);
    void L2cap(byte cid, byte[] pdu) => Hci.ReceiveAclBytes([0x40, 0x20, (byte)(pdu.Length + 4), 0, (byte)pdu.Length, 0, cid, 0x00, .. pdu]);

    void Service(ushort handle, ushort uuid) => _db[handle] = (U16(0x2800), U16(uuid));
    void Descriptor(ushort handle, ushort uuid, byte[] value) => _db[handle] = (U16(uuid), value);
    void Characteristic(ushort declaration, byte properties, ushort uuid, byte[] value)
    {
        _db[declaration] = (U16(0x2803), [properties, .. U16((ushort)(declaration + 1)), .. U16(uuid)]);
        _db[(ushort)(declaration + 1)] = (U16(uuid), value);
    }

    void OnCommand(byte[] c)
    {
        ushort op = (ushort)(c[0] | c[1] << 8);
        void Status() => Event(0x0F, 0x04, 0x00, 0x01, c[0], c[1]);
        switch (op)
        {
            case HciOpcode.LeCreateConnection:
                Status();
                if (_connected) return;   // une seule connexion dans ce test : ensuite on attend (annulation)
                _connected = true;
                Event([0x3E, 0x13, 0x01, 0x00, 0x40, 0x00, 0x00, 0x00, .. BdAddr.Parse(Address), 0x18, 0, 0, 0, 0xC8, 0, 0]);
                break;
            case HciOpcode.LeEnableEncryption:
                Status();
                byte[] rand = c[5..13], ltk = c[15..31];
                ushort ediv = (ushort)(c[13] | c[14] << 8);
                bool withStk = ltk.SequenceEqual(_stk) && ediv == 0 && rand.All(b => b == 0);
                bool withBond = ltk.SequenceEqual(Bond.Ltk) && ediv == Bond.Ediv && rand.SequenceEqual(Bond.Rand);
                Event(0x08, 0x04, withStk || withBond ? (byte)0x00 : (byte)0x06, 0x40, 0x00, withStk || withBond ? (byte)1 : (byte)0);
                if (withStk)   // fin d'appairage : distribution de la LTK
                {
                    Smp([0x06, .. Bond.Ltk]);
                    Smp([0x07, (byte)Bond.Ediv, (byte)(Bond.Ediv >> 8), .. Bond.Rand]);
                    Pairings++;
                }
                break;
            case HciOpcode.LeSetScanEnable when c[3] == 1:
                Event(0x0E, 0x04, 0x01, c[0], c[1], 0x00);
                Advertise(0x01, BdAddr.Parse("7D:7B:01:30:5A:4E"), PhoneAdvertisement);   // aléatoire, sans HID : ignorée
                Advertise(0x00, BdAddr.Parse(Address), PairingAdvertisement);
                break;
            case HciOpcode.ReadBdAddr:
                Event([0x0E, 0x0A, 0x01, c[0], c[1], 0x00, .. BdAddr.Parse(PcAddress)]);
                break;
            default:
                Event(0x0E, 0x04, 0x01, c[0], c[1], 0x00);
                break;
        }
    }

    void Advertise(byte addressType, byte[] address, byte[] data) =>
        Event([0x3E, (byte)(data.Length + 12), 0x02, 0x01, 0x00, addressType, .. address, (byte)data.Length, .. data, 0xC5]);

    void OnAcl(byte[] packet)
    {
        byte[] pdu = packet[8..];
        if (packet[6] == 0x06) { OnSmp(pdu); return; }
        Assert.Equal(0x04, packet[6]);   // canal ATT
        ushort start = pdu.Length >= 3 ? (ushort)(pdu[1] | pdu[2] << 8) : (ushort)0;
        ushort end = pdu.Length >= 5 ? (ushort)(pdu[3] | pdu[4] << 8) : (ushort)0;
        switch (pdu[0])
        {
            case 0x10:   // Read By Group Type : services
                Respond(0x11, _db.Where(a => a.Key >= start && a.Key <= end && Is(a.Value.Type, 0x2800))
                    .Select(a => (byte[])[.. U16(a.Key), .. U16(GroupEnd(a.Key)), .. a.Value.Value]), pdu[0], start);
                break;
            case 0x08:   // Read By Type
                ushort type = (ushort)(pdu[5] | pdu[6] << 8);
                Respond(0x09, _db.Where(a => a.Key >= start && a.Key <= end && Is(a.Value.Type, type))
                    .Select(a => (byte[])[.. U16(a.Key), .. a.Value.Value]), pdu[0], start);
                break;
            case 0x04:   // Find Information
                Respond(0x05, _db.Where(a => a.Key >= start && a.Key <= end)
                    .Select(a => (byte[])[.. U16(a.Key), .. a.Value.Type]), pdu[0], start, format: true);
                break;
            case 0x0A:
                Att([0x0B, .. _db[start].Value]);
                break;
            case 0x12:
                Writes.Add($"0x{start:X4} {Convert.ToHexString(pdu[3..])}");
                Att(0x13);
                if (start == 0x001D && pdu[3] == 0xAF) Att(0x1B, 0x23, 0x00, 0xE0, 0x20, 0x60, 0x02);   // réponse au déverrouillage
                break;
        }
    }

    void OnSmp(byte[] pdu)
    {
        byte[] tk = new byte[16];
        byte[] Confirm(byte[] r) => SmpCrypto.C1(tk, r, _preq, _pres, 0, BdAddr.Parse(PcAddress), 0, BdAddr.Parse(Address));
        switch (pdu[0])
        {
            case 0x01:   // Pairing Request -> réponse : sans entrée/sortie, liaison, clé 16, distribue sa LTK
                _preq = pdu;
                _pres = [0x02, 0x03, 0x00, 0x01, 16, 0x00, 0x01];
                Smp(_pres);
                break;
            case 0x03:
                _mconfirm = pdu[1..];
                _srand = Enumerable.Range(0xA0, 16).Select(i => (byte)i).ToArray();
                Smp([0x03, .. Confirm(_srand)]);
                break;
            case 0x04:
                Assert.Equal(_mconfirm, Confirm(pdu[1..]));   // l'hôte a bien calculé sa confirmation
                Smp([0x04, .. _srand]);
                _stk = SmpCrypto.S1(tk, _srand, pdu[1..]);
                break;
        }
    }

    /// <summary>Réponse ATT : entrées de même taille, autant que le MTU 23 en laisse passer ; aucune : Attribute Not Found.</summary>
    void Respond(byte opcode, IEnumerable<byte[]> entries, byte request, ushort start, bool format = false)
    {
        var list = entries.ToList();
        if (list.Count == 0) { Att(0x01, request, (byte)start, (byte)(start >> 8), 0x0A); return; }
        int size = list[0].Length;
        var same = list.TakeWhile(e => e.Length == size).Take(21 / size).SelectMany(e => e);
        Att([opcode, format ? (byte)(size == 4 ? 1 : 2) : (byte)size, .. same]);
    }

    ushort GroupEnd(ushort service) =>
        _db.Keys.Where(h => h > service && Is(_db[h].Type, 0x2800)).Select(h => (ushort)(h - 1)).DefaultIfEmpty((ushort)0xFFFF).First();

    static bool Is(byte[] type, ushort uuid) => type.Length == 2 && (type[0] | type[1] << 8) == uuid;
    static byte[] U16(ushort v) => [(byte)v, (byte)(v >> 8)];
}

public class SiriRemoteBleTests
{
    public static TheoryData<string, BondKeys?, int> Bonds => new()
    {
        { "clés valides", FakeRemote.Bond, 0 },
        { "aucune clé : appairage", null, 1 },
        { "clés périmées : nouvel appairage", new BondKeys(new byte[16], 1, new byte[8]), 1 },
    };

    [Theory]
    [MemberData(nameof(Bonds))]
    public async Task Connects_secures_subscribes_unlocks_and_forwards_reports_until_disconnection(string _, BondKeys? stored, int pairings)
    {
        string path = Path.Combine(Path.GetTempPath(), $"appleremote-bonds-{Guid.NewGuid():N}.json");
        try
        {
            var bonds = new BondStore(path);
            if (stored is not null) bonds.Save(FakeRemote.Address, stored);

            var remote = new FakeRemote();
            var reports = new List<string>();
            var battery = new List<int>();
            var statuses = new List<string>();
            var connected = new TaskCompletionSource();
            var disconnected = new TaskCompletionSource();
            var driver = new SiriRemoteBle(remote.Hci, bonds,
                (_, report) => { lock (reports) reports.Add(Convert.ToHexString(report ?? []).ToLowerInvariant()); },
                status =>
                {
                    lock (statuses) statuses.Add(status);
                    if (status.StartsWith("Connecté")) connected.TrySetResult();
                    if (status.StartsWith("Déconnectée")) disconnected.TrySetResult();
                },
                level => { lock (battery) battery.Add(level); });

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            Task run = driver.RunAsync(cts.Token);
            await connected.Task.WaitAsync(cts.Token);

            Assert.Equal(pairings, remote.Pairings);
            Assert.Equal(FakeRemote.Bond, bonds.Load(FakeRemote.Address), new BondComparer());   // clés à jour
            // abonnement, déverrouillage, puis abonnement au niveau de batterie
            Assert.Equal(new[] { "0x0024 0100", "0x001D AF", "0x0029 0100" }, remote.Writes);
            remote.Att(0x1B, 0x23, 0x00, 0x00, 0x20);                            // Menu
            remote.Att(0x1B, 0x28, 0x00, 86);                                    // la batterie baisse
            remote.Att(0x1B, 0x20, 0x00, 0x99);                                  // autre handle : ignoré
            remote.Event(0x05, 0x04, 0x00, 0x40, 0x00, 0x13);                    // mise en veille
            await disconnected.Task.WaitAsync(cts.Token);

            cts.Cancel();
            await run;
            Assert.Equal(new[] { "fce0206002", "fc0020", "" }, reports);         // "" : lien coupé, tout relâcher
            Assert.Equal(new[] { 87, 86 }, battery);                             // lu à la connexion, puis notifié
            Assert.DoesNotContain(statuses, s => s.StartsWith("Erreur"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Only_a_siri_remote_in_pairing_mode_is_recognised()
    {
        Assert.True(SiriRemoteBle.IsSiriRemoteAdvertisement(FakeRemote.PairingAdvertisement));
        Assert.False(SiriRemoteBle.IsSiriRemoteAdvertisement(FakeRemote.PhoneAdvertisement));
        Assert.False(SiriRemoteBle.IsSiriRemoteAdvertisement(Convert.FromHexString("0201060302121805FF06000102")));   // HID, pas Apple
        Assert.False(SiriRemoteBle.IsSiriRemoteAdvertisement([0x09, 0x03, 0x12]));                                     // tronquée
    }

    sealed class BondComparer : IEqualityComparer<BondKeys?>
    {
        public bool Equals(BondKeys? a, BondKeys? b) =>
            a is not null && b is not null && a.Ltk.SequenceEqual(b.Ltk) && a.Ediv == b.Ediv && a.Rand.SequenceEqual(b.Rand);
        public int GetHashCode(BondKeys? k) => 0;
    }
}
