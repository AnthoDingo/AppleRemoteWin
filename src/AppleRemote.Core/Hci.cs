namespace AppleRemote;

/// <summary>Opcodes HCI utilisés (OGF &lt;&lt; 10 | OCF).</summary>
public static class HciOpcode
{
    public const ushort Disconnect = 0x0406;
    public const ushort SetEventMask = 0x0C01;
    public const ushort Reset = 0x0C03;
    public const ushort ReadLocalVersion = 0x1001;
    public const ushort ReadBdAddr = 0x1009;
    public const ushort LeSetScanParameters = 0x200B;
    public const ushort LeSetScanEnable = 0x200C;
    public const ushort LeCreateConnection = 0x200D;
    public const ushort LeCreateConnectionCancel = 0x200E;
    public const ushort LeConnectionUpdate = 0x2013;
    public const ushort LeEnableEncryption = 0x2019;
}

/// <summary>Une commande HCI refusée par le contrôleur (statut non nul).</summary>
public sealed class HciException(ushort opcode, byte status)
    : Exception($"Commande HCI 0x{opcode:X4} refusée par le contrôleur (statut 0x{status:X2}).")
{
    public ushort Opcode { get; } = opcode;
    public byte Status { get; } = status;
}

/// <summary>
/// Hôte HCI minimal, indépendant du transport : envoie les commandes et attend leur réponse, reconstitue les paquets
/// d'événements et de données (ACL) à partir des morceaux reçus. Le transport (WinUSB, tests) fournit l'envoi et
/// appelle <see cref="ReceiveEventBytes"/> / <see cref="ReceiveAclBytes"/> depuis ses threads de lecture.
/// </summary>
public sealed class HciHost(Action<byte[]> sendCommand, Action<byte[]> sendAcl)
{
    const byte EvtCommandComplete = 0x0E, EvtCommandStatus = 0x0F;

    readonly SemaphoreSlim _commandGate = new(1, 1);
    readonly PacketAssembler _events = new(2, h => h[1]);
    readonly PacketAssembler _acl = new(4, h => h[2] | h[3] << 8);
    (ushort Opcode, TaskCompletionSource<byte[]> Done)? _pending;

    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Événement HCI autre que Command Complete / Command Status : (code, paramètres). Levé depuis le thread du transport.</summary>
    public event Action<byte, byte[]>? Event;

    /// <summary>Paquet ACL reçu : (handle de connexion, drapeau PB, données L2CAP). Levé depuis le thread du transport.</summary>
    public event Action<ushort, int, byte[]>? AclData;

    /// <summary>
    /// Envoie une commande et attend sa réponse. Command Complete : renvoie les paramètres de retour (statut en tête).
    /// Command Status : renvoie un tableau vide, la suite arrive par <see cref="Event"/>. Statut non nul : <see cref="HciException"/>.
    /// </summary>
    public async Task<byte[]> CommandAsync(ushort opcode, params byte[] parameters)
    {
        if (parameters.Length > 255) throw new ArgumentException("Paramètres HCI limités à 255 octets.", nameof(parameters));
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
    /// Envoie des données L2CAP sur une connexion LE, en un seul paquet ACL (drapeau PB 00 : premier fragment, non vidable).
    /// ponytail: pas de découpage ni de contrôle de flux (Number Of Completed Packets) ; les PDU ATT de la télécommande
    /// (MTU 23) tiennent dans un paquet et on n'en envoie qu'un à la fois. À ajouter si l'on dépasse le tampon LE (123 octets ici).
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
        // Opcode 0 (« prêt ») ou réponse sans demande en cours : rien à faire.
        if (_pending is not { } pending || pending.Opcode != opcode) return;
        if (status != 0) pending.Done.TrySetException(new HciException(opcode, status));
        else pending.Done.TrySetResult(result);
    }
}

/// <summary>Reconstitue des paquets « en-tête + longueur » à partir d'un flux découpé arbitrairement (transferts USB).</summary>
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
