using System.Text;
using System.Windows.Threading;
using AppleRemote.App.Models;
using AppleRemote.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Windows.Devices.Enumeration;

namespace AppleRemote.App.ViewModels;

public sealed partial class BleScanViewModel : ObservableObject, IDisposable
{
    public sealed record Row(string NameText, string Address, short Rssi, string Companies, string Note, bool Likely, bool AppleNoHid, ulong Key);

    readonly BleScanner _scanner = new();
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    readonly IDialogs _dialogs;
    bool _paused;

    [ObservableProperty] IReadOnlyList<Row> _rows = [];
    [ObservableProperty] Row? _selected;
    [ObservableProperty] string _toggleText = Loc.T("Ble.Pause");
    [ObservableProperty] string _countText = "";
    [ObservableProperty] string _pairText = "";
    [ObservableProperty] bool _onlyLikely;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PairCommand))]
    bool _isPairing;

    /// <summary>Levé (thread UI) après un appairage réussi ou si l'appareil l'était déjà.</summary>
    public event Action? Paired;

    public BleScanViewModel(IDialogs dialogs)
    {
        _dialogs = dialogs;
        _scanner.Failed += message => _ui.InvokeAsync(() =>
        {
            CountText = message;
            ToggleText = Loc.T("Ble.Relaunch");
            _paused = true;
        });
        _timer.Tick += (_, _) => Refresh();
    }

    public void Start()
    {
        _scanner.Start();
        _timer.Start();
    }

    public void Dispose()
    {
        _timer.Stop();
        _scanner.Dispose();
    }

    partial void OnOnlyLikelyChanged(bool value) => Refresh();

    List<Row> BuildRows()
    {
        var devices = _scanner.Snapshot();
        if (OnlyLikely) devices = devices.Where(d => d.AppleHid).ToList();
        return devices
            .Select(d => new Row(d.Name.Length > 0 ? d.Name : Loc.T("Ble.Unnamed"), d.AddressText, d.Rssi, d.CompaniesText, d.Note, d.Likely, d.IsApple && !d.HidService, d.Address))
            .ToList();
    }

    void Refresh()
    {
        var rows = BuildRows();
        // La liste est reconstruite à chaque tick : on garde la sélection par adresse.
        ulong? selected = Selected?.Key;
        Rows = rows;
        Selected = rows.FirstOrDefault(r => r.Key == selected);
        CountText = Loc.T("Ble.Count", rows.Count, rows.Count(r => r.Likely));
    }

    [RelayCommand]
    void Toggle()
    {
        if (_paused)
        {
            _scanner.Start();
            _timer.Start();
            ToggleText = Loc.T("Ble.Pause");
        }
        else
        {
            _scanner.Stop();
            ToggleText = Loc.T("Ble.Resume");
        }
        _paused = !_paused;
    }

    bool CanPair() => !IsPairing;

    [RelayCommand(CanExecute = nameof(CanPair))]
    Task Pair() => PairSelected(system: false);

    async Task PairSelected(bool system)
    {
        if (Selected is not { } row)
        {
            PairText = Loc.T("Ble.SelectRow");
            return;
        }

        IsPairing = true;
        // Scanner et se connecter en même temps gêne certains adaptateurs : on suspend le scan.
        bool resume = !_paused;
        _scanner.Stop();
        PairText = Loc.T("Ble.Pairing", row.NameText, row.Address);
        try
        {
            var (status, message) = await BleScanner.PairAsync(row.Key, system);
            // Appairé côté Windows alors que l'appareil diffuse en mode appairage : lien périmé, sans services GATT (donc sans HID).
            if (status == DevicePairingResultStatus.AlreadyPaired && await _dialogs.Confirm(Loc.T("Ble.AlreadyTitle"),
                    Loc.T("Ble.AlreadyAsk", row.NameText, row.Address)))
            {
                PairText = Loc.T("Ble.Unpairing");
                var unpaired = await BleScanner.UnpairAsync(row.Key);
                if (unpaired is not (DeviceUnpairingResultStatus.Unpaired or DeviceUnpairingResultStatus.AlreadyUnpaired))
                {
                    PairText = Loc.T("Ble.UnpairFailed", unpaired);
                    return;
                }
                PairText = Loc.T("Ble.Pairing", row.NameText, row.Address);
                (status, message) = await BleScanner.PairAsync(row.Key, system);
            }
            PairText = message;
            if (status is DevicePairingResultStatus.Paired or DevicePairingResultStatus.AlreadyPaired) Paired?.Invoke();
        }
        catch (Exception ex)
        {
            PairText = Loc.T("Ble.PairFailed", ex.Message);
        }
        finally
        {
            IsPairing = false;
            if (resume) _scanner.Start();
        }
    }

    [RelayCommand]
    void Clear()
    {
        _scanner.Clear();
        Refresh();
    }

    [RelayCommand]
    void Copy()
    {
        var sb = new StringBuilder();
        sb.AppendLine(Loc.T("Ble.TableHeader"));
        foreach (var r in BuildRows())
            sb.AppendLine($"{r.NameText} | {r.Address} | {r.Rssi} | {r.Companies} | {r.Note}");
        try
        {
            _dialogs.CopyText(sb.ToString());
            CountText = Loc.T("Ble.Copied");
        }
        catch (Exception ex)
        {
            CountText = Loc.T("Ble.CopyFailed", ex.Message);
        }
    }
}
