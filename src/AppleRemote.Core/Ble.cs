// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 AnthoDingo

using System.Text.Json;
using System.Text.Json.Nodes;

namespace AppleRemote;

/// <summary>Bluetooth address: text "14:9D:99:26:BF:CC" (most significant byte first) &lt;-&gt; 6 little-endian bytes (HCI).</summary>
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

/// <summary>Keys of a "legacy" LE pairing: LTK distributed by the remote, with its EDIV and Rand.</summary>
public sealed record BondKeys(byte[] Ltk, ushort Ediv, byte[] Rand);

/// <summary>Pairing keys per address, in a JSON file (hexadecimal values, HCI byte order).</summary>
public sealed class BondStore(string path)
{
    // JsonNode rather than reflection-based serialisation: also works in AOT / single-file publishing.
    public BondKeys? Load(string address) =>
        Read()[address] is JsonObject e
            ? new BondKeys(Convert.FromHexString((string)e["ltk"]!), (ushort)e["ediv"]!, Convert.FromHexString((string)e["rand"]!))
            : null;

    /// <summary>Paired addresses (a single remote in practice).</summary>
    public IReadOnlyList<string> Addresses => Read().Select(e => e.Key).ToList();

    /// <summary>Forgets all bonds.</summary>
    public void Clear() => File.Delete(path);

    /// <summary>Stores the address's keys; null forgets them.</summary>
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
/// LE "central" role on top of <see cref="HciHost"/>: controller initialisation, connection to a public address,
/// encryption with known keys, disconnection tracking, L2CAP frames. One connection at a time.
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

    /// <summary>Our public address (6 little-endian bytes), read by <see cref="InitializeAsync"/>.</summary>
    public byte[] LocalAddress { get; private set; } = new byte[6];

    /// <summary>Disconnection: (handle, HCI reason). Raised from the transport thread.</summary>
    public event Action<ushort, byte>? Disconnected;

    /// <summary>Complete L2CAP frame received (except the signalling channel, handled here): (handle, channel, data).</summary>
    public event Action<ushort, ushort, byte[]>? L2capData;

    public void SendL2cap(ushort handle, ushort cid, byte[] payload) =>
        _hci.SendAcl(handle, [(byte)payload.Length, (byte)(payload.Length >> 8), (byte)cid, (byte)(cid >> 8), .. payload]);

    public async Task InitializeAsync()
    {
        await _hci.CommandAsync(HciOpcode.Reset);
        // Default mask + "LE Meta" (bit 61, no LE event without it) + "Encryption Key Refresh" (bit 47).
        await _hci.CommandAsync(HciOpcode.SetEventMask, BitConverter.GetBytes(0x2000_9FFF_FFFF_FFFFUL));
        LocalAddress = (await _hci.CommandAsync(HciOpcode.ReadBdAddr))[1..7];
    }

    /// <summary>
    /// Connects as soon as the device (public address) shows up: advertising in pairing mode, or a button press
    /// after sleep. Waits indefinitely; cancellation abandons the attempt. Returns the connection handle.
    /// </summary>
    public async Task<ushort> ConnectAsync(string address, CancellationToken ct = default)
    {
        var connecting = _connecting = new TaskCompletionSource<ushort>(TaskCreationOptions.RunContinuationsAsynchronously);
        byte[] p =
        [
            0x60, 0x00, 0x30, 0x00,     // scan: 60 ms interval, 30 ms window
            0x00,                       // no filter list: the address below
            0x00, .. BdAddr.Parse(address),   // adresse publique de l'appareil
            0x00,                       // our public address: the remote (BT 4.0) reconnects poorly to a changing address
            0x0C, 0x00, 0x18, 0x00,     // intervalle de connexion 15–30 ms
            0x00, 0x00,                 // latence 0
            0xC8, 0x00,                 // supervision 2 s
            0x00, 0x00, 0x00, 0x00,     // event length: up to the controller
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
    /// Scans advertisements until one whose data matches is found; returns the sender's (public) address.
    /// </summary>
    public async Task<string> ScanAsync(Func<byte[], bool> match, CancellationToken ct = default)
    {
        var found = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _scan = (match, found);
        try
        {
            // Active (the scan response may carry data), 60 ms / 30 ms, our public address, no filtering.
            await _hci.CommandAsync(HciOpcode.LeSetScanParameters, 0x01, 0x60, 0x00, 0x30, 0x00, 0x00, 0x00);
            await _hci.CommandAsync(HciOpcode.LeSetScanEnable, 0x01, 0x00);
            return await found.Task.WaitAsync(ct);
        }
        finally
        {
            _scan = null;
            try { await _hci.CommandAsync(HciOpcode.LeSetScanEnable, 0x00, 0x00); } catch (Exception) { /* controller gone */ }
        }
    }

    /// <summary>Encrypts the link with the pairing LTK; fails if the remote no longer recognises it.</summary>
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
        _hci.CommandAsync(HciOpcode.Disconnect, (byte)handle, (byte)(handle >> 8), 0x13);   // 0x13: terminated by the remote user

    void OnAcl(ushort handle, int pb, byte[] data)
    {
        byte[] frame;
        lock (_rx)
        {
            if (pb != 0b01) _rx.Clear();   // start of frame (otherwise: continuation of the previous one)
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
    /// LE signalling: we accept the remote's connection parameter update request (otherwise it may drop the link)
    /// and reject the rest ("command not understood").
    /// </summary>
    void OnSignaling(ushort handle, byte[] p)
    {
        if (p.Length < 4) return;
        byte code = p[0], id = p[1];
        if (code == 0x12 && p.Length >= 12)   // Connection Parameter Update Request
        {
            SendL2cap(handle, CidSignaling, [0x13, id, 0x02, 0x00, 0x00, 0x00]);   // accepted
            _ = _hci.CommandAsync(HciOpcode.LeConnectionUpdate,
                [(byte)handle, (byte)(handle >> 8), .. p[4..12], 0x00, 0x00, 0x00, 0x00]);
        }
        else if (code is not (0x01 or 0x13))   // neither a reject nor a response: a request we don't handle
        {
            SendL2cap(handle, CidSignaling, [0x01, id, 0x02, 0x00, 0x00, 0x00]);
        }
    }

    void OnEvent(byte code, byte[] p)
    {
        switch (code)
        {
            case EvtLeMeta when p.Length >= 2 && p[0] == LeAdvertisingReport && _scan is { } scan:
                for (int i = 2, n = 0; n < p[1] && i + 9 <= p.Length; n++)   // type, address type, address, length, data, RSSI
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
                else if (p[3] == 0) _encrypting?.TrySetException(new InvalidOperationException("Encryption disabled by the remote."));
                else _encrypting?.TrySetResult();
                break;
            case EvtDisconnectionComplete when p.Length >= 4 && p[0] == 0:
                _encrypting?.TrySetException(new InvalidOperationException($"Disconnected during encryption (reason 0x{p[3]:X2})."));
                Disconnected?.Invoke((ushort)(p[1] | p[2] << 8), p[3]);
                break;
        }
    }
}
