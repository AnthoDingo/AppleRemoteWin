# CLAUDE.md

WPF application + user-mode driver for Windows 11 for the **Siri Remote gen 1** (Apple TV, A1513/A1962).
C# port of [a-funk/open-appleremote](https://github.com/a-funk/open-appleremote) (MIT, Python/Linux): `Decoder.cs` and `Gestures.cs`
are ports of `decode.py` and `gestures.py`. Reference protocol: `docs/PROTOCOL.md` in the upstream repository.

## Structure

```
src/AppleRemote.Core      decoder, gestures (One Euro), keymap, capture replay,  net10.0, portable, NO Windows dependency
                          direct Bluetooth driver: Hci.cs (HCI), Ble.cs (LE central, L2CAP), Gatt.cs (ATT client),
                          Smp.cs ("Just Works" pairing), SiriRemoteBle.cs (connection, encryption/pairing, HID)
src/AppleRemote.Windows   HID (SetupAPI + hid.dll), unlock, reading, SendInput, BLE scanner,   net10.0-windows10.0.19041.0
                          HCI transport over WinUSB (UsbHci.cs, any adapter under the WinUSB driver)
src/AppleRemote.App       WPF window (startup project)                             net10.0-windows10.0.19041.0
                          WPF-UI + MVVM: MainWindow = NavigationView menu; Remote, Settings, Debug pages
                          (menu footer), one view-model per page (ViewModels/Page) owned by MainViewModel (settings + session); common styles in Views/Styles.xaml
src/AppleRemote.DriverInstaller  installs / removes the kernel filter through its package (pnputil), requires elevation; launched by the app's button
(driver)                  KMDF kernel filter in Rust: separate repo AnthoDingo/AppleRemoteWin-Driver (MIT, signed package = test certificate)
tests/AppleRemote.Tests   xunit, run on any OS (sample.jsonl + golden.json)
tools/siri_bumble.py      Python probe (Bumble): remote through an adapter under WinUSB, outside the Windows Bluetooth stack
tools/test_siri_bumble.py its hardware-free test (fake remote on virtual Bumble controllers)
```

Dependencies: `App` -> `Windows` -> `Core`. Never make `Core` depend on Windows: it must stay testable everywhere.

## Commands

```
dotnet build AppleRemote.sln
dotnet test tests/AppleRemote.Tests
dotnet run --project src/AppleRemote.App
```

Kernel filter: built and released in AnthoDingo/AppleRemoteWin-Driver (`cargo make --profile release` there; LLVM + WDK required).
The Release build of `App` downloads its latest release (`AppleRemoteDriver.zip`; `-p:AppleRemoteDriver=false` to skip it); publishing copies it to `Publish\driver\`
(the 4 files go together: every build re-stamps the INF and re-signs the `.cat`).
Releases are published **self-contained** with a RID (`--self-contained true`, no single-file): the app runs without .NET installed;
`App`, `MacroEditor` and `DriverInstaller` share the bundled runtime in one folder, so all three must stay self-contained together
(`SelfContained` set in each csproj whenever a RID is given; mixing them fails with NETSDK1150/1151).

Outside Windows: add `-p:EnableWindowsTargeting=true` to compile (not run) `Windows` and `App`.
Required SDK: **.NET 10**. Run the tests after any change to `Core`.

## Protocol (do not break)

- Input reports `0xFC`, feature report `0xF0`. The remote stays **mute** until `0xF0 0xAF` has been sent
  (`HidEnumerator.Unlock`). Redo it on every connection.
- VID `0x004C` (Apple, Bluetooth SIG) / PID `0x026D`. It broadcasts its **serial number** as its name.
- On Windows, `ReadFile` always returns `InputReportByteLength` bytes (zero-padded): the decoder only reads what byte 1 announces.
- `HidD_SetFeature` requires a buffer of `FeatureReportByteLength` bytes (fallback: 2 bare bytes). On the Windows stack it fails
  (error 87): fallback unlock over GATT (`FilterUnlock`: `0xAF` written to the battery level, which the filter redirects).
- Siri (voice) is not decoded over HID (frames are truncated to 20 bytes by the filter).

## Conventions

- User interface (displayed texts, `Strings*.resx` resources) in **French** (and English via `Strings.en.resx`).
- **Log and debug messages**, **code comments** and identifiers in **English**.
- `Nullable` enabled, `ImplicitUsings` enabled. In `App` (WPF), `System.IO` is **not** implicitly imported: add `using System.IO;`.
- The TFM must stay `net10.0-windows10.0.19041.0` (a separate `SupportedOSPlatformVersion` causes NETSDK1135).
- `Core`: any new logic = a test. The `Whole_pipeline_matches_the_python_reference` test compares against `golden.json`
  (output of the original Python code on `sample.jsonl`): do not regenerate `golden.json` to make a test pass without understanding the discrepancy.
- Driver events are raised from thread-pool threads: marshal to the UI with `Dispatcher`.
- Every source file starts with a license header (SPDX: `AGPL-3.0-or-later`, `MIT` in the driver repo).

## Diagnostic features

- **Apple HID devices**: lists what Windows sees (`HidEnumerator.ListApple`).
- **Scan Bluetooth LE** (`BleScanner`, `BleScanWindow`): listens to LE advertisements without pairing, highlights
  Apple (0x004C), the HID service (0x1812) and serial-number-shaped names. Tells whether the remote is transmitting.
  Double-click / "Pair": pairing without Settings (`BleScanner.PairAsync`, `DeviceInformationCustomPairing`).

## Known state

- **Without the filter, the Windows Bluetooth stack does not keep the remote** (observed 2026-10-09, Windows 11 build 26300,
  Qualcomm QCA61x4A adapter, backed by an HCI trace): pairing succeeds, then Windows reads the Apple characteristic
  `ce5ad0bc…` (handle `0x003A`, service `8341f2b4…`); the remote never answers, Windows drops the link after 30 s
  (ATT timeout), deletes the pairing key, and no HID collection is created. No API can avoid this.
- **`AppleRemoteDriver` kernel filter + Windows HID validated on hardware** (2026-10-10, dev PC, test-signing, QCA61x4A
  under its original BTHUSB driver + `BtFilter`): pairing, unlock, all buttons, touchpad (click, drags) and Siri.
  What the filter fixes, and why (each point was measured with the filter's counters):
  - ATT read `0x3A` redirected to `0x28`: Windows finishes its discovery and creates the HID collection;
  - unlock and subscription (`0xAF` on `0x28` -> `0xF0` report at `0x1D`, CCCD `0x29` -> `0x24`) written by the app over GATT;
    redone on every reconnection (the app does it as soon as Windows signals it);
  - voice notifications longer than the remote's ATT MTU (23) are truncated, otherwise Windows drops the link after 1 to 2 s;
  - short reports (buttons: 2 to 4 bytes, touchpad: 13) padded to 20 bytes: Windows' HID driver rejects any report
    shorter than the declared size (only full-length touchpad and truncated voice got through);
  - the ACL connection handle changes on every connection: no rule tests it. The rules only touch the remote's connection
    (an LE mouse shares the adapter): address learned at the first unlock, kept in
    `Services\AppleRemoteDriver\Parameters` (`RemoteAddressLow/High`; delete them to switch remotes).
  KMDF driver pitfalls: IOCTL code must be read from the IRP stack in completion (not from the WDF parameters); parallel queue with
  unlimited `NumberOfPresentedRequests` (otherwise BTHUSB init timeout, code 10); **extension** INF (a full driver
  evicts `BtFilter` from the Qualcomm driver: code 10). Debugger-free diagnosis: `Parameters` (`Stage` = 3, `Hit*`).
- **Filter installation** (`AppleRemote.DriverInstaller`, or the app's button, which closes the session first): updates
  over the old package; install, uninstall and reinstall without a PC restart (validated 2026-10-10). `bthserv` and
  `DeviceAssociationService` keep the adapter open and veto its removal (Kernel-PnP event 225) whenever pnputil binds,
  unbinds or restarts the driver: Windows then leaves the adapter "pending restart" (`restart-device` refused, code 50)
  until the next boot. The installer disables both for the whole pnputil operation (stopping is not enough: `bthserv`
  is trigger-started and comes straight back), then restores their startup type and state. A handle still open on the
  remote (the app's session) can veto too. The `BTH\MS_*` enumerators
  sometimes stay at code 19 (0xC0000034) after an adapter restart: the installer recreates them.
- **`tools/siri_bumble.py` validated on hardware** (2026-10-10, QCA61x4A `0CF3:E007` switched to WinUSB with Zadig, ROM
  firmware, PC restart needed): pairing, encrypted reconnection with the stored keys after sleep,
  unlock, buttons, touchpad and voice; the capture replayed in `Core` yields the right events. This firmware rejects
  251-byte LE packets (Bumble stops at startup): the probe asks for 27 bytes.
- **C# driver (`UsbHci` + `SiriRemoteBle`) validated on hardware** (2026-10-10): "Just Works" pairing (keys in a JSON
  `BondStore`), connection, encryption, HID discovery, unlock, all buttons and gestures decoded by
  `RemotePipeline`, encrypted reconnection after sleep. Right after a pairing, the remote drops once or twice
  (reason 0x13): the loop reconnects by itself. Only re-pair on key rejection (0x05/0x06), not on 0x3E.
- **In the app**: `RemoteSession.Start` uses direct Bluetooth if `UsbHci.Open(AdapterId)` succeeds (adapter chosen in Settings
  if under WinUSB, else the first one under WinUSB, detected by its `E0/01/01` class, any VID/PID), otherwise Windows' HID (with the filter). Keys in `%AppData%\AppleRemote\bonds.json`; without keys, scan for a
  remote in pairing mode (service 0x1812 + Apple data 0x004C). "Pair" forgets the keys and restarts.
  Several adapters: `WinUsbDriver.List` (PnP, compatible ID `USB\Class_E0&SubClass_01&Prot_01`), the Settings ComboBox picks one
  (PnP instance ID in `settings.json` `Adapter`), WinUSB install/restore target it (`--driver install|restore <instanceId>`).
  WinUSB is installed by VID/PID: two identical dongles switch together. Bonds are tied to the adapter: re-pair after switching. Realtek RTL8761B (TP-Link UB500): its ROM firmware answers HCI but
  can't hold an LE connection; `LeCentral.InitializeAsync` sends the patch (`RealtekFirmware`, port of Bumble's `rtk.py`) from
  `rtl8761bu_fw.bin` + `rtl8761bu_config.bin` (linux-firmware `rtl_bt/`) in `%AppData%\AppleRemote\firmware\` or `firmware\` next
  to the exe. RAM only: lost on unplug, kept across HCI Reset. Patched, it accepts the legacy LE Create Connection but never
  connects: on controllers with LE Extended Advertising (feature bit 12) `LeCentral` scans and connects with the extended
  commands (0x2041-0x2043, extended report 0x0D), like Bumble; the Qualcomm keeps the legacy ones. Validated 2026-10-12
  (reconnection with keys from a Bumble pairing; pairing from the app itself not yet re-tested on the UB500).
  `FakeRemote` (tests) reproduces the real one's GATT database (handles `0x1D` F0, `0x20` F1, `0x23`/`0x24` FC).
- Reports received over GATT: the ID (`0xFC`) is not in the notification; it is put back at the front to keep the `hid.dll` format.
- Application windows running as administrator ignore `SendInput` from a non-elevated program (UIPI).
- One remote at a time; Siri Remote gen 2/3 not supported (other reports, other unlock).
- Gesture constants (`Gestures.cs`) tuned on Linux: to be readjusted on hardware.
