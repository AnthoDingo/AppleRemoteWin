using System.Diagnostics;
using Microsoft.Win32.SafeHandles;

namespace AppleRemote.Windows;

/// <summary>
/// « Pilote » user-mode de la Siri Remote gen 1 : trouve la collection HID, la déverrouille (0xF0 &lt;- 0xAF), lit les reports 0xFC
/// en asynchrone, et se reconnecte seul après un sommeil. Windows fournit déjà le pilote noyau (HidBthLE, HID over GATT) :
/// ce code fait côté utilisateur ce que BlueZ/hidraw + open-appleremote font sous Linux.
/// </summary>
public sealed class SiriRemoteDriver
{
    /// <summary>Silence (ms) au bout duquel on sonde la liaison par un nouveau déverrouillage.</summary>
    const int ProbeAfterMs = 8000;

    public static double HostNow => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    readonly Action<double, byte[]?> _onReport;
    readonly Action<string> _onStatus;
    string? _lastStatus;

    /// <param name="onReport">(temps hôte, report). Tableau vide = lien coupé. Appelé depuis un thread du pool.</param>
    /// <param name="onStatus">Messages d'état, dédoublonnés.</param>
    public SiriRemoteDriver(Action<double, byte[]?> onReport, Action<string> onStatus)
    {
        _onReport = onReport;
        _onStatus = onStatus;
    }

    void Status(string message)
    {
        if (message == _lastStatus) return;
        _lastStatus = message;
        _onStatus(message);
    }

    /// <summary>Boucle de connexion : ne rend la main qu'à l'annulation.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var info = HidEnumerator.FindRemote();
                if (info is null)
                {
                    Status(Loc.T("Status.WaitHid"));
                    await Task.Delay(1500, ct);
                    continue;
                }
                await RunConnectionAsync(info, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                Status(Loc.T("Status.Error", e.Message));
                try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { break; }
            }
        }
    }

    async Task RunConnectionAsync(HidDeviceInfo info, CancellationToken ct)
    {
        using SafeFileHandle handle = HidEnumerator.Open(info.Path);
        if (handle.IsInvalid)
        {
            Status(Loc.T("Status.CannotOpen", System.Runtime.InteropServices.Marshal.GetLastWin32Error()));
            await Task.Delay(2000, ct);
            return;
        }

        // Le nœud HID peut exister alors que la télécommande dort : le déverrouillage ne passe que si la liaison est vivante.
        bool unlocked = false;
        int error = 0;
        for (int attempt = 0; attempt < 3 && !unlocked && !ct.IsCancellationRequested; attempt++)
        {
            unlocked = HidEnumerator.Unlock(handle, info.FeatureLength);
            if (!unlocked)
            {
                error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                await Task.Delay(300, ct);
            }
        }
        if (!unlocked)
        {
            Status(Loc.T("Status.Unreachable", error));
            // Court : la télécommande ne reste éveillée que quelques secondes après un appui, il faut la rattraper vite.
            await Task.Delay(500, ct);
            return;
        }

        Status(Loc.T("Status.ConnectedHid", string.IsNullOrWhiteSpace(info.Product) ? "Siri Remote" : info.Product));

        int length = Math.Max(info.InputLength, 21);
        using var linkCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        long lastReport = Environment.TickCount64;

        // Sonde : si rien n'arrive depuis un moment, un déverrouillage qui échoue = liaison tombée -> reconnexion.
        var probe = Task.Run(async () =>
        {
            try
            {
                while (!linkCts.IsCancellationRequested)
                {
                    await Task.Delay(ProbeAfterMs, linkCts.Token);
                    if (Environment.TickCount64 - Volatile.Read(ref lastReport) < ProbeAfterMs) continue;
                    if (!HidEnumerator.Unlock(handle, info.FeatureLength))
                    {
                        linkCts.Cancel();
                        return;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        }, CancellationToken.None);

        try
        {
            using var stream = new FileStream(handle, FileAccess.ReadWrite, bufferSize: 0, isAsync: true);
            var buffer = new byte[length];
            while (!linkCts.IsCancellationRequested)
            {
                int n = await stream.ReadAsync(buffer.AsMemory(0, length), linkCts.Token);
                if (n <= 0) throw new IOException("fin du flux HID");
                Volatile.Write(ref lastReport, Environment.TickCount64);
                _onReport(HostNow, buffer.AsSpan(0, n).ToArray());
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            // périphérique parti (veille, déconnexion) : traité comme un lien coupé
        }
        finally
        {
            linkCts.Cancel();
            try { await probe; } catch { /* sonde déjà arrêtée */ }
        }

        _onReport(HostNow, Array.Empty<byte>());   // relâche boutons et doigts
        if (!ct.IsCancellationRequested)
        {
            Status(Loc.T("Status.Disconnected"));
            await Task.Delay(1000, ct);
        }
    }
}
