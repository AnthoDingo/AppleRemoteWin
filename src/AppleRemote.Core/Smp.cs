using System.Security.Cryptography;
using System.Threading.Channels;

namespace AppleRemote;

/// <summary>Appairage refusé ou interrompu (Pairing Failed, valeur de confirmation fausse, clé non distribuée…).</summary>
public sealed class SmpException(string message) : Exception(message);

/// <summary>
/// Fonctions de la norme (Core, vol. 3, partie H, 2.2) sur des valeurs petit-boutistes, telles qu'elles circulent :
/// e = AES-128 (sur valeurs grand-boutistes, d'où les retournements), c1 = valeur de confirmation, s1 = clé courte (STK).
/// </summary>
public static class SmpCrypto
{
    public static byte[] E(byte[] key, byte[] data)
    {
        using var aes = Aes.Create();
        aes.Key = key.Reverse().ToArray();
        return aes.EncryptEcb(data.Reverse().ToArray(), PaddingMode.None).Reverse().ToArray();
    }

    /// <param name="preq">Pairing Request (7 octets, opcode compris).</param>
    /// <param name="pres">Pairing Response (7 octets).</param>
    /// <param name="ia">Adresse de l'initiateur (nous), 6 octets ; <paramref name="iat"/> : 0 publique, 1 aléatoire.</param>
    public static byte[] C1(byte[] k, byte[] r, byte[] preq, byte[] pres, byte iat, byte[] ia, byte rat, byte[] ra)
    {
        byte[] p1 = [iat, rat, .. preq, .. pres];
        byte[] p2 = [.. ra, .. ia, 0, 0, 0, 0];
        return E(k, Xor(E(k, Xor(r, p1)), p2));
    }

    /// <summary>STK = e(k, r1' || r2'), r' = 64 bits de poids faible.</summary>
    public static byte[] S1(byte[] k, byte[] r1, byte[] r2) => E(k, [.. r2[..8], .. r1[..8]]);

    static byte[] Xor(byte[] a, byte[] b) => a.Zip(b, (x, y) => (byte)(x ^ y)).ToArray();
}

/// <summary>
/// Appairage LE « legacy » en « Just Works » (aucun écran ni clavier, comme la télécommande, Bluetooth 4.0), côté initiateur :
/// échange des confirmations, chiffrement avec la clé courte, puis réception de la LTK distribuée par la télécommande.
/// Nous ne distribuons aucune clé : on se reconnecte toujours en central, avec la sienne.
/// </summary>
public static class SmpPairing
{
    const byte PairingRequest = 0x01, PairingResponse = 0x02, PairingConfirm = 0x03, PairingRandom = 0x04, PairingFailed = 0x05,
        EncryptionInformation = 0x06, MasterIdentification = 0x07;
    const byte NoInputNoOutput = 0x03, Bonding = 0x01, EncKey = 0x01;

    public static async Task<BondKeys> PairAsync(LeCentral central, ushort handle, byte[] localAddress, byte[] remoteAddress)
    {
        var inbox = Channel.CreateUnbounded<byte[]>();
        void OnData(ushort h, ushort cid, byte[] pdu) { if (h == handle && cid == LeCentral.CidSmp && pdu.Length > 0) inbox.Writer.TryWrite(pdu); }
        void Send(byte[] pdu) => central.SendL2cap(handle, LeCentral.CidSmp, pdu);

        async Task<byte[]> Expect(byte opcode, int length)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));   // délai SMP de la norme
            while (true)
            {
                byte[] pdu;
                try { pdu = await inbox.Reader.ReadAsync(timeout.Token); }
                catch (OperationCanceledException)
                {
                    throw new SmpException("La télécommande ne répond pas à l'appairage : mettez-la en mode appairage (Menu + Volume + ~5 s).");
                }
                if (pdu[0] == PairingFailed) throw new SmpException($"Appairage refusé par la télécommande (raison 0x{(pdu.Length > 1 ? pdu[1] : 0):X2}).");
                if (pdu[0] == opcode && pdu.Length >= length) return pdu;
                // Autres messages (Security Request, clés non demandées…) : ignorés.
            }
        }

        central.L2capData += OnData;
        try
        {
            byte[] preq = [PairingRequest, NoInputNoOutput, 0x00, Bonding, 16, 0x00, EncKey];
            Send(preq);
            byte[] pres = (await Expect(PairingResponse, 7))[..7];
            if ((pres[6] & EncKey) == 0) throw new SmpException("La télécommande ne distribue pas de clé : appairage sans mémorisation impossible.");

            byte[] tk = new byte[16];   // Just Works : clé temporaire nulle
            byte[] mrand = RandomNumberGenerator.GetBytes(16);
            byte[] Confirm(byte[] rand) => SmpCrypto.C1(tk, rand, preq, pres, 0, localAddress, 0, remoteAddress);

            Send([PairingConfirm, .. Confirm(mrand)]);
            byte[] sconfirm = (await Expect(PairingConfirm, 17))[1..17];
            Send([PairingRandom, .. mrand]);
            byte[] srand = (await Expect(PairingRandom, 17))[1..17];
            if (!Confirm(srand).SequenceEqual(sconfirm))
            {
                Send([PairingFailed, 0x04]);   // Confirm Value Failed
                throw new SmpException("Valeur de confirmation de la télécommande incorrecte.");
            }

            // Clé courte réduite à la taille négociée (octets de poids fort à zéro), puis chiffrement avec elle.
            byte[] stk = SmpCrypto.S1(tk, srand, mrand);
            int keySize = Math.Min(16, (int)pres[4]);
            Array.Clear(stk, keySize, 16 - keySize);
            await central.EncryptAsync(handle, new BondKeys(stk, 0, new byte[8]));

            byte[] ltk = (await Expect(EncryptionInformation, 17))[1..17];
            byte[] master = await Expect(MasterIdentification, 11);
            return new BondKeys(ltk, (ushort)(master[1] | master[2] << 8), master[3..11]);
        }
        finally
        {
            central.L2capData -= OnData;
        }
    }
}
