# Siri Remote (gen 1) for Windows 11

*[Version française](README.fr.md)*

A **WPF (.NET 10)** application plus a user-mode driver to use the gen 1 Siri Remote (Apple TV 2015/2017, A1513/A1962)
on Windows 11: touch surface, buttons, tvOS-style gestures and a virtual keyboard.

C# port of [a-funk/open-appleremote](https://github.com/a-funk/open-appleremote) (MIT, Python/Linux). The decoder and the
gesture engine are line-by-line ports of `decode.py` and `gestures.py`; the upstream `docs/PROTOCOL.md` describes the protocol.

## "Driver": what is provided

No kernel driver. Windows 11 already ships the HID over GATT driver (`HidBthLE`). Since the remote only exposes vendor-specific
reports (input `0xFC`, feature `0xF0`), it stays silent until it is unlocked:

| Linux (open-appleremote) | Windows (here) |
|---|---|
| `/dev/hidrawN`, `HIDIOCSFEATURE` | `SetupAPI` + `hid.dll`: `HidD_SetFeature`, asynchronous `ReadFile` |
| Unlock: report `0xF0` = `0xAF` | same, buffer padded to `FeatureReportByteLength` (required by Windows) |
| Reconnect after sleep | reconnect loop + link probe (unlock if silent for more than 8 s) |
| `/dev/uinput` (virtual keyboard) | `SendInput` |

A "real" virtual HID keyboard (VHF/KMDF) would need a signed driver; `SendInput` is enough to drive an application.

## Structure

```
src/AppleRemote.Core      decoder, gestures (One Euro), keymap, capture replay   (net10.0, portable)
src/AppleRemote.Windows   HID enumeration, unlock, reading, SendInput            (net10.0-windows)
src/AppleRemote.App       WPF window                                             (net10.0-windows)
tests/AppleRemote.Tests   xunit, runs on any OS (macOS included)
```

## Usage

1. **Pair**: hold **Menu + Volume +** for ~5 s near the PC, then Settings > Bluetooth & devices > Add device
   (the "Pair" button opens the page). The remote shows up under its serial number.
2. Start the application: it looks for the remote, unlocks it and displays keys, fingers and gestures.
3. Tick **Virtual keyboard** to send real key presses (default mapping below).

| Remote | Key |
|---|---|
| swipe / flick up, down, left, right | arrow keys |
| click (press on the surface) | Enter |
| Menu | Esc |
| TV / Home | Browser: home |
| Play/Pause, Volume + / − | media keys |
| Siri | nothing |

Custom mapping: `keymap.example.json` (entries `up down left right click menu tv playpause volup voldown siri`;
value = key name, VK code 1-254, or `null` to disable; names: `Keys.cs`).

Windows running **as administrator** ignore key presses from a non-elevated program (UIPI): run the app as administrator in that case.

## Build

```
dotnet build AppleRemote.sln                    # on Windows (Visual Studio 2026 or .NET 10 SDK)
dotnet test tests/AppleRemote.Tests             # on any OS
dotnet publish src/AppleRemote.App -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

From macOS/Linux: `-p:EnableWindowsTargeting=true` lets you compile (not run) the Windows projects.

## Diagnostics

- **"Apple HID devices"** lists what Windows sees (VID/PID, usages, report lengths). The gen 1 Siri Remote is
  `VID 004C PID 026D`. Nothing in the list = not paired/connected.
- **Scan Bluetooth LE** listens to advertisements around the PC (without pairing): a remote in pairing mode shows up
  with the Apple manufacturer (0x004C) and its serial number as name, even if "Add a device" does not list it.
- **Record a capture** writes a `.jsonl` in the format of `monitor --raw` (replayable here via "Replay a capture",
  or in the Linux tool). Siri audio frames are truncated: voice is never written.
- After a long sleep, the button press that wakes the remote may be lost: press again.

## Tests

35 xunit tests. `SampleTests` replays `sample.jsonl` (a real capture of an A1962, from the upstream project) through the whole
pipeline and compares every event (step, fingers, buttons, gestures) with `golden.json`, produced by the original Python code on the same
capture: the C# port is therefore checked against the reference.

## Limitations

- **Windows layer not tested on hardware**: it compiles, but the real HID access (collection seen by `HidBthLE`, unlock
  via `HidD_SetFeature`) has not been run with an actual remote. If unlocking fails, the log says so;
  in that case, send the output of "Apple HID devices" and a capture.
- Voice (Siri) not decoded; gen 2/3 Siri Remotes not supported (different reports and different unlock); a single remote only.
- Gesture constants tuned for a gen 1 remote under Linux: retune if needed (`Gestures.cs`).

## License

GNU AGPL v3 or later (`AGPL-3.0-or-later`), see `LICENSE`.

`Decoder.cs` and `Gestures.cs` are ports of [open-appleremote](https://github.com/a-funk/open-appleremote), released under the MIT license:
its copyright notice is kept in `LICENSE-MIT-open-appleremote`.

The kernel driver in `src/AppleRemoteDriver` is a separate component under the MIT license: see its `LICENSE` and `README.md`.
