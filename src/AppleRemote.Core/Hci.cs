// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 AnthoDingo

namespace AppleRemote;

/// <summary>HCI opcodes used (OGF &lt;&lt; 10 | OCF).</summary>
public static class HciOpcode
{
    public const ushort Disconnect = 0x0406;
    public const ushort SetEventMask = 0x0C01;
    public const ushort Reset = 0x0C03;
    public const ushort ReadLocalVersion = 0x1001;
    public const ushort ReadBdAddr = 0x1009;
    public const ushort LeSetEventMask = 0x2001;
    public const ushort LeReadLocalFeatures = 0x2003;
    public const ushort LeSetScanParameters = 0x200B;
    public const ushort LeSetScanEnable = 0x200C;
    public const ushort LeCreateConnection = 0x200D;
    public const ushort LeCreateConnectionCancel = 0x200E;
    public const ushort LeConnectionUpdate = 0x2013;
    public const ushort LeEnableEncryption = 0x2019;
    public const ushort LeSetExtendedScanParameters = 0x2041;
    public const ushort LeSetExtendedScanEnable = 0x2042;
    public const ushort LeExtendedCreateConnection = 0x2043;
}

/// <summary>An HCI command rejected by the controller (non-zero status).</summary>
public sealed class HciException(ushort opcode, byte status)
    : Exception($"HCI command 0x{opcode:X4} rejected by the controller (status 0x{status:X2}).")
{
    public ushort Opcode { get; } = opcode;
    public byte Status { get; } = status;
}

/// <summary>
/// Minimal HCI host, transport-independent: sends commands and awaits their response, reassembles event and
/// data (ACL) packets from the received chunks. The transport (WinUSB, tests) provides sending and
/// calls <see cref="ReceiveEventBytes"/> / <see cref="ReceiveAclBytes"/> from its reader threads.
/// </summary>
public sealed class HciHost(Action<byte[]> sendCommand, Action<byte[]> sendAcl)
{
    const byte EvtCommandComplete = 0x0E, EvtCommandStatus = 0x0F;

    readonly SemaphoreSlim _commandGate = new(1, 1);
    readonly PacketAssembler _events = new(2, h => h[1]);
    readonly PacketAssembler _acl = new(4, h => h[2] | h[3] << 8);
    (ushort Opcode, TaskCompletionSource<byte[]> Done)? _pending;

    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>HCI event other than Command Complete / Command Status: (code, parameters). Raised from the transport thread.</summary>
    public event Action<byte, byte[]>? Event;

    /// <summary>ACL packet received: (connection handle, PB flag, L2CAP data). Raised from the transport thread.</summary>
    public event Action<ushort, int, byte[]>? AclData;

    /// <summary>
    /// Sends a command and awaits its response. Command Complete: returns the return parameters (status first).
    /// Command Status: returns an empty array, the rest arrives through <see cref="Event"/>. Non-zero status: <see cref="HciException"/>.
    /// </summary>
    public async Task<byte[]> CommandAsync(ushort opcode, params byte[] parameters)
    {
        if (parameters.Length > 255) throw new ArgumentException("HCI parameters limited to 255 bytes.", nameof(parameters));
        await _commandGate.WaitAsync();
        try
        {
            var done = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending = (opcode, done);
            sendCommand([(byte)opcode, (byte)(opcode >> 8), (byte)parameters.Length, .. parameters]);
            return await done.Task.WaitAsync(CommandTimeout);
        }
        finally
        {
            _pending = null;
            _commandGate.Release();
        }
    }

    /// <summary>
    /// Sends L2CAP data on an LE connection, as a single ACL packet (PB flag 00: first fragment, non-flushable).
    /// ponytail: no fragmentation or flow control (Number Of Completed Packets); the remote's ATT PDUs
    /// (MTU 23) fit in one packet and only one is sent at a time. Add if the LE buffer (123 bytes here) is exceeded.
    /// </summary>
    public void SendAcl(ushort handle, byte[] data) =>
        sendAcl([(byte)handle, (byte)(handle >> 8 & 0x0F), (byte)data.Length, (byte)(data.Length >> 8), .. data]);

    public void ReceiveEventBytes(ReadOnlySpan<byte> chunk)
    {
        foreach (byte[] packet in _events.Add(chunk)) OnEvent(packet[0], packet[2..]);
    }

    public void ReceiveAclBytes(ReadOnlySpan<byte> chunk)
    {
        foreach (byte[] packet in _acl.Add(chunk))
        {
            int header = packet[0] | packet[1] << 8;
            AclData?.Invoke((ushort)(header & 0x0FFF), (header >> 12) & 0x3, packet[4..]);
        }
    }

    void OnEvent(byte code, byte[] p)
    {
        switch (code)
        {
            case EvtCommandComplete when p.Length >= 3:
                Complete((ushort)(p[1] | p[2] << 8), p[3..], p.Length > 3 ? p[3] : (byte)0);
                break;
            case EvtCommandStatus when p.Length >= 4:
                Complete((ushort)(p[2] | p[3] << 8), [], p[0]);
                break;
            default:
                Event?.Invoke(code, p);
                break;
        }
    }

    void Complete(ushort opcode, byte[] result, byte status)
    {
        // Opcode 0 ("ready") or a response with no pending request: nothing to do.
        if (_pending is not { } pending || pending.Opcode != opcode) return;
        if (status != 0) pending.Done.TrySetException(new HciException(opcode, status));
        else pending.Done.TrySetResult(result);
    }
}

/// <summary>Reassembles "header + length" packets from an arbitrarily chunked stream (USB transfers).</summary>
sealed class PacketAssembler(int headerSize, Func<List<byte>, int> payloadLength)
{
    readonly List<byte> _buffer = new();
    readonly object _gate = new();

    public List<byte[]> Add(ReadOnlySpan<byte> chunk)
    {
        var packets = new List<byte[]>();
        lock (_gate)
        {
            _buffer.AddRange(chunk);
            while (_buffer.Count >= headerSize)
            {
                int total = headerSize + payloadLength(_buffer);
                if (_buffer.Count < total) break;
                packets.Add(_buffer.GetRange(0, total).ToArray());
                _buffer.RemoveRange(0, total);
            }
        }
        return packets;
    }
}
