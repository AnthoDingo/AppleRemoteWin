using System.Text.Json;
using System.Text.Json.Nodes;

namespace AppleRemote;

/// <summary>Adresse Bluetooth : texte « 14:9D:99:26:BF:CC » (octet de poids fort en tête) &lt;-&gt; 6 octets petit-boutistes (HCI).</summary>
public static class BdAddr
{
    public static byte[] Parse(string text)
    {
        byte[] bytes = Convert.FromHexString(text.Replace(":", ""));
        if (bytes.Length != 6) throw new FormatException($"Adresse Bluetooth invalide : {text}");
        Array.Reverse(bytes);
        return bytes;
    }

    public static string Format(ReadOnlySpan<byte> littleEndian) =>
        string.Join(":", littleEndian[..6].ToArray().Reverse().Select(b => b.ToString("X2")));
}

/// <summary>Clés d'un appairage LE « legacy » : LTK distribuée par la télécommande, avec son EDIV et son Rand.</summary>
public sealed record BondKeys(byte[] Ltk, ushort Ediv, byte[] Rand);

/// <summary>Clés d'appairage par adresse, dans un fichier JSON (valeurs en hexadécimal, ordre des octets HCI).</summary>
public sealed class BondStore(string path)
{
    // JsonNode plutôt que la sérialisation par réflexion : fonctionne aussi en publication AOT / application « fichier ».
    public BondKeys? Load(string address) =>
        Read()[address] is JsonObject e
            ? new BondKeys(Convert.FromHexString((string)e["ltk"]!), (ushort)e["ediv"]!, Convert.FromHexString((string)e["rand"]!))
            : null;

    /// <summary>Adresses appairées (une seule télécommande en pratique).</summary>
    public IReadOnlyList<string> Addresses => Read().Select(e => e.Key).ToList();

    /// <summary>Oublie tous les appairages.</summary>
    public void Clear() => File.Delete(path);

    /// <summary>Enregistre les clés de l'adresse ; null les oublie.</summary>
    public void Save(string address, BondKeys? keys)
    {
        JsonObject all = Read();
        all.Remove(address);
        if (keys is not null)
            all[address] = new JsonObject
            {
                ["ltk"] = Convert.ToHexString(keys.Ltk), ["ediv"] = keys.Ediv, ["rand"] = Convert.ToHexString(keys.Rand),
            };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, all.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    JsonObject Read() => File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new() : new();
}

/// <summary>
/// Rôle « central » LE au-dessus de <see cref="HciHost"/> : initialisation du contrôleur, connexion à une adresse publique,
/// chiffrement avec des clés connues, suivi des déconnexions, trames L2CAP. Une seule connexion à la fois.
/// </summary>
public sealed class LeCentral
{
    const byte EvtDisconnectionComplete = 0x05, EvtEncryptionChange = 0x08, EvtLeMeta = 0x3E, LeConnectionComplete = 0x01,
        LeAdvertisingReport = 0x02;
    public const ushort CidAtt = 0x0004, CidSignaling = 0x0005, CidSmp = 0x0006;

    readonly HciHost _hci;
    readonly List<byte> _rx = new();   // trame L2CAP en cours de reconstitution
    TaskCompletionSource<ushort>? _connecting;
    (Func<byte[], bool> Match, TaskCompletionSource<string> Found)? _scan;
    TaskCompletionSource? _encrypting;

    public LeCentral(HciHost hci)
    {
        _hci = hci;
        _hci.Event += OnEvent;
        _hci.AclData += OnAcl;
    }

    /// <summary>Notre adresse publique (6 octets petit-boutistes), lue par <see cref="InitializeAsync"/>.</summary>
    public byte[] LocalAddress { get; private set; } = new byte[6];

    /// <summary>Déconnexion : (handle, raison HCI). Levé depuis le thread du transport.</summary>
    public event Action<ushort, byte>? Disconnected;

    /// <summary>Trame L2CAP complète reçue (hors canal de signalisation, traité ici) : (handle, canal, données).</summary>
    public event Action<ushort, ushort, byte[]>? L2capData;

    public void SendL2cap(ushort handle, ushort cid, byte[] payload) =>
        _hci.SendAcl(handle, [(byte)payload.Length, (byte)(payload.Length >> 8), (byte)cid, (byte)(cid >> 8), .. payload]);

    public async Task InitializeAsync()
    {
        await _hci.CommandAsync(HciOpcode.Reset);
        // Masque par défaut + « LE Meta » (bit 61, sans lui aucun événement LE) + « Encryption Key Refresh » (bit 47).
        await _hci.CommandAsync(HciOpcode.SetEventMask, BitConverter.GetBytes(0x2000_9FFF_FFFF_FFFFUL));
        LocalAddress = (await _hci.CommandAsync(HciOpcode.ReadBdAddr))[1..7];
    }

    /// <summary>
    /// Se connecte dès que l'appareil (adresse publique) se manifeste : publicité en mode appairage, ou appui sur un bouton
    /// après une mise en veille. Attend indéfiniment ; l'annulation abandonne la tentative. Renvoie le handle de connexion.
    /// </summary>
    public async Task<ushort> ConnectAsync(string address, CancellationToken ct = default)
    {
        var connecting = _connecting = new TaskCompletionSource<ushort>(TaskCreationOptions.RunContinuationsAsynchronously);
        byte[] p =
        [
            0x60, 0x00, 0x30, 0x00,     // balayage : intervalle 60 ms, fenêtre 30 ms
            0x00,                       // pas de liste de filtrage : l'adresse ci-dessous
            0x00, .. BdAddr.Parse(address),   // adresse publique de l'appareil
            0x00,                       // notre adresse publique : la télécommande (BT 4.0) se reconnecte mal à une adresse qui change
            0x0C, 0x00, 0x18, 0x00,     // intervalle de connexion 15–30 ms
            0x00, 0x00,                 // latence 0
            0xC8, 0x00,                 // supervision 2 s
            0x00, 0x00, 0x00, 0x00,     // durée d'événement : au choix du contrôleur
        ];
        await _hci.CommandAsync(HciOpcode.LeCreateConnection, p);
        await using var cancel = ct.Register(() => _ = _hci.CommandAsync(HciOpcode.LeCreateConnectionCancel));
        try
        {
            return await connecting.Task.WaitAsync(ct);
        }
        finally
        {
            _connecting = null;
        }
    }

    /// <summary>
    /// Balaye les publicités jusqu'à en trouver une dont les données correspondent ; renvoie l'adresse (publique) de l'émetteur.
    /// </summary>
    public async Task<string> ScanAsync(Func<byte[], bool> match, CancellationToken ct = default)
    {
        var found = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _scan = (match, found);
        try
        {
            // Actif (la réponse de balayage peut porter des données), 60 ms / 30 ms, notre adresse publique, sans filtrage.
            await _hci.CommandAsync(HciOpcode.LeSetScanParameters, 0x01, 0x60, 0x00, 0x30, 0x00, 0x00, 0x00);
            await _hci.CommandAsync(HciOpcode.LeSetScanEnable, 0x01, 0x00);
            return await found.Task.WaitAsync(ct);
        }
        finally
        {
            _scan = null;
            try { await _hci.CommandAsync(HciOpcode.LeSetScanEnable, 0x00, 0x00); } catch (Exception) { /* contrôleur parti */ }
        }
    }

    /// <summary>Chiffre la liaison avec la LTK de l'appairage ; échoue si la télécommande ne la reconnaît plus.</summary>
    public async Task EncryptAsync(ushort handle, BondKeys keys)
    {
        var encrypting = _encrypting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await _hci.CommandAsync(HciOpcode.LeEnableEncryption,
                [(byte)handle, (byte)(handle >> 8), .. keys.Rand, (byte)keys.Ediv, (byte)(keys.Ediv >> 8), .. keys.Ltk]);
            await encrypting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            _encrypting = null;
        }
    }

    public Task DisconnectAsync(ushort handle) =>
        _hci.CommandAsync(HciOpcode.Disconnect, (byte)handle, (byte)(handle >> 8), 0x13);   // 0x13 : fin demandée par l'utilisateur

    void OnAcl(ushort handle, int pb, byte[] data)
    {
        byte[] frame;
        lock (_rx)
        {
            if (pb != 0b01) _rx.Clear();   // début de trame (sinon : suite de la précédente)
            _rx.AddRange(data);
            if (_rx.Count < 4 || _rx.Count < 4 + (_rx[0] | _rx[1] << 8)) return;
            frame = _rx.ToArray();
            _rx.Clear();
        }
        ushort cid = (ushort)(frame[2] | frame[3] << 8);
        byte[] payload = frame[4..(4 + (frame[0] | frame[1] << 8))];
        if (cid == CidSignaling) OnSignaling(handle, payload);
        else L2capData?.Invoke(handle, cid, payload);
    }

    /// <summary>
    /// Signalisation LE : on accepte la demande de paramètres de connexion de la télécommande (sinon elle peut couper)
    /// et on refuse le reste (« commande non comprise »).
    /// </summary>
    void OnSignaling(ushort handle, byte[] p)
    {
        if (p.Length < 4) return;
        byte code = p[0], id = p[1];
        if (code == 0x12 && p.Length >= 12)   // Connection Parameter Update Request
        {
            SendL2cap(handle, CidSignaling, [0x13, id, 0x02, 0x00, 0x00, 0x00]);   // accepté
            _ = _hci.CommandAsync(HciOpcode.LeConnectionUpdate,
                [(byte)handle, (byte)(handle >> 8), .. p[4..12], 0x00, 0x00, 0x00, 0x00]);
        }
        else if (code is not (0x01 or 0x13))   // pas un rejet ni une réponse : une demande qu'on ne gère pas
        {
            SendL2cap(handle, CidSignaling, [0x01, id, 0x02, 0x00, 0x00, 0x00]);
        }
    }

    void OnEvent(byte code, byte[] p)
    {
        switch (code)
        {
            case EvtLeMeta when p.Length >= 2 && p[0] == LeAdvertisingReport && _scan is { } scan:
                for (int i = 2, n = 0; n < p[1] && i + 9 <= p.Length; n++)   // type, type d'adresse, adresse, longueur, données, RSSI
                {
                    int length = p[i + 8];
                    if (i + 9 + length > p.Length) break;
                    if (p[i + 1] == 0x00 && scan.Match(p[(i + 9)..(i + 9 + length)])) scan.Found.TrySetResult(BdAddr.Format(p.AsSpan(i + 2, 6)));
                    i += 10 + length;
                }
                break;
            case EvtLeMeta when p.Length >= 4 && p[0] == LeConnectionComplete:
                if (p[1] != 0) _connecting?.TrySetException(new HciException(HciOpcode.LeCreateConnection, p[1]));
                else _connecting?.TrySetResult((ushort)(p[2] | p[3] << 8));
                break;
            case EvtEncryptionChange when p.Length >= 4:
                if (p[0] != 0) _encrypting?.TrySetException(new HciException(HciOpcode.LeEnableEncryption, p[0]));
                else if (p[3] == 0) _encrypting?.TrySetException(new InvalidOperationException("Chiffrement désactivé par la télécommande."));
                else _encrypting?.TrySetResult();
                break;
            case EvtDisconnectionComplete when p.Length >= 4 && p[0] == 0:
                _encrypting?.TrySetException(new InvalidOperationException($"Déconnectée pendant le chiffrement (raison 0x{p[3]:X2})."));
                Disconnected?.Invoke((ushort)(p[1] | p[2] << 8), p[3]);
                break;
        }
    }
}
