# AppleRemoteEsp32

ESP32-S3 firmware (ESP-IDF, NimBLE + TinyUSB): a USB dongle that keeps the Siri Remote gen 1 by itself.
No WinUSB adapter, no kernel filter: the Windows app reads it as a plain HID device and drives it.

- BLE central: "Just Works" pairing (legacy, like `Smp.cs`), bond kept in NVS, reconnection after sleep,
  subscription to `0xFC` and unlock (`0xAF` -> `0xF0`) on every connection.
- USB device: one vendor HID collection, VID `0x303A` (Espressif), usage page `0xFF52`.

## USB protocol (mirrored in `src/AppleRemote.Windows/EspDongle.cs`)

| Report | Direction | Content |
|---|---|---|
| `1` | in | remote report in `hid.dll` format: `0xFC` + 20 bytes (zero-padded) |
| `2` | in | state (`0` idle, `1` pairing scan, `2` waiting for the bonded remote, `3` connected), battery %, last disconnect reason |
| `3` | feature | command: `0` send state, `1` pair (forget + scan), `2` forget (then idle) |

No bond at boot: scans for a remote in pairing mode (Menu + Volume+ ~5 s).

## Build

ESP-IDF >= 5.1, board with the S3's native USB (e.g. DevKitC-1: flash and logs on the "UART" port, the app talks to the "USB" port).

```
idf.py set-target esp32s3
idf.py build flash monitor
```

## Not done yet

- Standalone keyboard mode (USB HID keyboard/consumer without the app): needs `Decoder`/`Gestures` ported to C.
- GATT handles are fixed (gen 1 database, see `main.c`), no HID service discovery.
- Test PID `0x8A52`: request a PID from Espressif before distributing.
