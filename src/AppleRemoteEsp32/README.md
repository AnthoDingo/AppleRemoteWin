# AppleRemoteEsp32

ESP32-S3 firmware (ESP-IDF, NimBLE + TinyUSB): a USB dongle that keeps the Siri Remote gen 1 by itself.
No WinUSB adapter, no kernel filter: the Windows app reads it as a plain HID device and drives it.

- BLE central: "Just Works" pairing (legacy, like `Smp.cs`), bond kept in NVS, reconnection after sleep,
  subscription to `0xFC` and unlock (`0xAF` -> `0xF0`) on every connection.
- USB device: one vendor HID collection, VID `0x303A` (Espressif), usage page `0xFF52`.
- Firmware update **only from the Windows app, over USB**, signed images only (see below). The dongle never goes
  online: Wi-Fi is never started, and BLE carries no update path.

## USB protocol (mirrored in `src/AppleRemote.Windows/EspDongle.cs`)

| Report | Direction | Content |
|---|---|---|
| `1` | in | remote report in `hid.dll` format: `0xFC` + 20 bytes (zero-padded) |
| `2` | in | state (`0` idle, `1` pairing scan, `2` waiting for the bonded remote, `3` connected), battery %, last disconnect reason |
| `3` | feature | command: `0` send state, `1` pair (forget + scan), `2` forget (then idle) |
| `4` | feature | firmware update. SET: `1` begin + size (u32 LE), `2` data + offset (u32 LE) + length + ≤ 57 bytes, `3` end, `4` abort. GET: status, bytes written (u32 LE), running version (32 bytes) |

Update status: `0` idle, `1` receiving, `2` verifying, `3` accepted (reboots), `0x81`… refused
(`0x82` size, `0x83` chunk out of order, `0x84` flash write, `0x85` incomplete, `0x86` signature/integrity,
`0x87` older version, `0x88` other project, `0x89` boot partition).

No bond at boot: scans for a remote in pairing mode (Menu + Volume+ ~5 s).

## Build

ESP-IDF >= 5.1, board with the S3's native USB and ≥ 4 MB of flash (XIAO ESP32S3, DevKitC-1…).
The signing key must exist before the first build (see below), the build refuses to produce an unsigned updatable firmware.

```
idf.py set-target esp32s3
idf.py build flash monitor
```

The version compared on update is `PROJECT_VER` in `CMakeLists.txt`: bump it for every release.

The app ships the signed `build/AppleRemoteEsp32.bin` as `firmware\AppleRemoteEsp32.bin` (`EspFirmware` property of
`AppleRemote.App.csproj`, override with `-p:EspFirmware=<signed .bin>`). It offers the update when that version is newer
than the dongle's; there is no other channel (no file picker, no download by the dongle).

## Firmware update: security model

The firmware travels with the app (the app itself is updated from GitHub); the dongle only receives it over USB.
Any process on the PC can write to a HID device, so **the dongle is the only judge**; the app is just a pipe.

| Threat | Defence |
|---|---|
| Malicious program on the PC flashes its own firmware | Image must be signed (RSA-3072, Secure Boot V2 format) with the key of the **running** firmware: checked by ESP-IDF in `esp_ota_end` (`CONFIG_SECURE_SIGNED_ON_UPDATE_NO_SECURE_BOOT`). Unsigned or foreign-signed: refused, the dongle keeps its firmware. |
| Downgrade to an older, signed but flawed firmware | `ota.c` refuses a version lower than the running one (`PROJECT_VER`, after the signature check). |
| Another signed app of the same key | `ota.c` compares `project_name`. |
| Corrupted / truncated transfer | Chunks must arrive in order, without gap or overlap; size announced first; SHA-256 of the image checked by ESP-IDF. |
| New firmware that boots but is broken | Rollback (`CONFIG_BOOTLOADER_APP_ROLLBACK_ENABLE`): it is kept only once USB **and** BLE came up (`ota_confirm_running`); otherwise the next reset returns to the previous one. |
| Updatable build without these protections | `ota.c` stops the build (`#error`) if signing or rollback is disabled. |
| Bundled file damaged or replaced | The app pre-checks it (`FirmwareImage`: ESP32-S3, this project, signed) and logs its SHA-256; a replaced file still has to pass the dongle's signature check. |

Not covered: someone with **physical access** can still flash anything through the ROM download mode (BOOT button + USB).
Closing that needs hardware Secure Boot V2 + flash encryption, which burn eFuses **irreversibly**: a separate production decision,
not enabled here. A program on the PC can also abort an update in progress (local denial of service, no compromise).

### Signing key

Generated once (OpenSSL ships with Git for Windows: `C:\Program Files\Git\mingw64in\openssl.exe`):

```
openssl genrsa -out ota_signing_key.pem 3072
```

Project key, public fingerprint (SHA-256 of the PEM public key; not secret):

```
4b8547045f39a213e8d81a3e28bb70ff0cbfa1b6bc8d45de1d241506b474046d
```

Check that a key file is the project's one:

```
openssl rsa -in ota_signing_key.pem -pubout | openssl dgst -sha256
```

- In this folder (path set by `CONFIG_SECURE_BOOT_SIGNING_KEY`); `*.pem` is ignored by git. **Never commit it.**
- Keep an offline backup (password manager, encrypted USB key). Lost key: dongles in the field can no longer be
  updated from the app, only reflashed by hand. Leaked key: anyone can build a firmware your dongles accept.
- The public key is not in the source: the dongle trusts the key that signed its own running firmware.
  Key rotation not designed yet (Secure Boot V2 allows several signature blocks per image, to be studied):
  until then, changing keys means reflashing by hand.
- `idf.py build` signs `build/AppleRemoteEsp32.bin`: that file is what the app ships.

## Not done yet

- Standalone keyboard mode (USB HID keyboard/consumer without the app): needs `Decoder`/`Gestures` ported to C.
- GATT handles are fixed (gen 1 database, see `main.c`), no HID service discovery.
- Test PID `0x8A52`: request a PID from Espressif before distributing.
- Signing in CI (GitHub secret or HSM) instead of a local key.
