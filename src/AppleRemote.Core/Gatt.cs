// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 AnthoDingo

namespace AppleRemote;

/// <summary>ATT request rejected by the device (Error Response).</summary>
public sealed class AttException(byte request, ushort handle, byte error)
    : Exception($"ATT request 0x{request:X2} rejected (handle 0x{handle:X4}, error 0x{error:X2}).")
{
    public byte Error { get; } = error;
}

public sealed record GattService(ushort Start, ushort End, byte[] Uuid);
public sealed record GattCharacteristic(ushort Declaration, byte Properties, ushort Value, byte[] Uuid);

/// <summary>
/// Minimal GATT client on a connection's ATT channel (default MTU, 23 bytes): discovery, read, write,
/// notifications. Also answers the requests the device sends us (we expose no service), otherwise it
/// would wait 30 s and drop the link.
/// </summary>
public sealed class GattClient
{
    const ushort PrimaryService = 0x2800, CharacteristicDeclaration = 0x2803;
    const byte ErrorResponse = 0x01, AttributeNotFound = 0x0A, RequestNotSupported = 0x06;

    readonly LeCentral _central;
    readonly ushort _connection;
    readonly SemaphoreSlim _gate = new(1, 1);
    (byte Request, TaskCompletionSource<byte[]> Done)? _pending;

    public GattClient(LeCentral central, ushort connection)
    {
        _central = central;
        _connection = connection;
        central.L2capData += OnL2cap;
    }

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Notification or indication: (value handle, value). Raised from the transport thread.</summary>
    public event Action<ushort, byte[]>? Notification;

    /// <summary>Call when the connection ends: stops listening to the channel.</summary>
    public void Detach() => _central.L2capData -= OnL2cap;

    public static bool Is(byte[] uuid, ushort uuid16) => uuid.Length == 2 && (uuid[0] | uuid[1] << 8) == uuid16;

    public async Task<List<GattService>> DiscoverPrimaryServicesAsync()
    {
        var services = new List<GattService>();
        for (ushort start = 1; ;)
        {
            byte[]? r = await RequestOrNotFoundAsync([0x10, .. U16(start), 0xFF, 0xFF, .. U16(PrimaryService)]);   // Read By Group Type
            if (r is null) return services;
            int size = r[1];
            for (int i = 2; i + size <= r.Length; i += size)
                services.Add(new GattService(U16(r, i), U16(r, i + 2), r[(i + 4)..(i + size)]));
            if (services[^1].End == 0xFFFF) return services;
            start = (ushort)(services[^1].End + 1);
        }
    }

    public async Task<List<GattCharacteristic>> DiscoverCharacteristicsAsync(GattService service)
    {
        var characteristics = new List<GattCharacteristic>();
        for (ushort start = service.Start; start <= service.End;)
        {
            byte[]? r = await RequestOrNotFoundAsync([0x08, .. U16(start), .. U16(service.End), .. U16(CharacteristicDeclaration)]);
            if (r is null) break;
            int size = r[1];
            for (int i = 2; i + size <= r.Length; i += size)
                characteristics.Add(new GattCharacteristic(U16(r, i), r[i + 2], U16(r, i + 3), r[(i + 5)..(i + size)]));
            start = (ushort)(characteristics[^1].Declaration + 1);
        }
        return characteristics;
    }

    /// <summary>Descriptors between two handles: (handle, UUID).</summary>
    public async Task<List<(ushort Handle, byte[] Uuid)>> DiscoverDescriptorsAsync(ushort start, ushort end)
    {
        var descriptors = new List<(ushort, byte[])>();
        while (start <= end)
        {
            byte[]? r = await RequestOrNotFoundAsync([0x04, .. U16(start), .. U16(end)]);   // Find Information
            if (r is null) break;
            int size = r[1] == 1 ? 4 : 18;   // 16- or 128-bit UUID
            for (int i = 2; i + size <= r.Length; i += size)
                descriptors.Add((U16(r, i), r[(i + 2)..(i + size)]));
            if (descriptors[^1].Item1 == 0xFFFF) break;
            start = (ushort)(descriptors[^1].Item1 + 1);
        }
        return descriptors;
    }

    public async Task<byte[]> ReadAsync(ushort handle) => (await RequestAsync([0x0A, .. U16(handle)]))[1..];

    public Task WriteAsync(ushort handle, params byte[] value) => RequestAsync([0x12, .. U16(handle), .. value]);

    async Task<byte[]?> RequestOrNotFoundAsync(byte[] pdu)
    {
        try { return await RequestAsync(pdu); }
        catch (AttException e) when (e.Error == AttributeNotFound) { return null; }   // end of a discovery
    }

    /// <summary>One request at a time (ATT rule); returns the whole response, opcode included.</summary>
    async Task<byte[]> RequestAsync(byte[] pdu)
    {
        await _gate.WaitAsync();
        try
        {
            var done = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending = (pdu[0], done);
            _central.SendL2cap(_connection, LeCentral.CidAtt, pdu);
            return await done.Task.WaitAsync(RequestTimeout);
        }
        finally
        {
            _pending = null;
            _gate.Release();
        }
    }

    void OnL2cap(ushort connection, ushort cid, byte[] pdu)
    {
        if (connection != _connection || cid != LeCentral.CidAtt || pdu.Length == 0) return;
        byte opcode = pdu[0];
        switch (opcode)
        {
            case ErrorResponse when pdu.Length >= 5 && _pending is { } p && p.Request == pdu[1]:
                p.Done.TrySetException(new AttException(pdu[1], U16(pdu, 2), pdu[4]));
                break;
            case 0x1B or 0x1D when pdu.Length >= 3:   // notification, indication
                if (opcode == 0x1D) _central.SendL2cap(_connection, LeCentral.CidAtt, [0x1E]);   // confirmation
                Notification?.Invoke(U16(pdu, 1), pdu[3..]);
                break;
            case 0x02:   // Exchange MTU: stay at the minimum
                _central.SendL2cap(_connection, LeCentral.CidAtt, [0x03, 23, 0]);
                break;
            case 0x04 or 0x06 or 0x08 or 0x0A or 0x0C or 0x0E or 0x10 or 0x12 or 0x16 or 0x18 or 0x20:   // request from the device
                byte error = opcode is 0x04 or 0x06 or 0x08 or 0x10 ? AttributeNotFound : RequestNotSupported;
                ushort handle = pdu.Length >= 3 ? U16(pdu, 1) : (ushort)0;
                _central.SendL2cap(_connection, LeCentral.CidAtt, [ErrorResponse, opcode, .. U16(handle), error]);
                break;
            default:
                if (_pending is { } q && opcode == q.Request + 1) q.Done.TrySetResult(pdu);   // response = request + 1
                break;
        }
    }

    static byte[] U16(ushort v) => [(byte)v, (byte)(v >> 8)];
    static ushort U16(byte[] b, int i) => (ushort)(b[i] | b[i + 1] << 8);
}
