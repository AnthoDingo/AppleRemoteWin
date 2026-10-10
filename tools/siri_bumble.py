"""
Siri Remote gen 1 probe through a dedicated USB Bluetooth dongle and the Bumble stack, bypassing the Windows Bluetooth stack.

Why: right after pairing, the Windows stack reads an Apple characteristic (handle 0x003A) that the remote never
answers; after 30 s Windows drops the link and the remote never comes back.
Here we only touch the HID service (0x1812).

Setup (once):
  1. Plug in the dongle (TP-Link UB500 = 2357:0604) and install WinUSB on it with Zadig (NOT the built-in adapter).
  2. python -m pip install bumble
  3. Realtek firmware, then loaded automatically by Bumble (the bumble-rtk-fw-download tool is broken in 0.0.235):
     rtl8761bu_fw.bin and rtl8761bu_config.bin from
     https://git.kernel.org/pub/scm/linux/kernel/git/firmware/linux-firmware.git/plain/rtl_bt/
     into %LOCALAPPDATA%\\google\\bumble\\firmware\\realtek\\

Usage:
  python tools/siri_bumble.py                       pairs the first time (Menu + Volume + ~5 s), then reconnects
  python tools/siri_bumble.py --capture c.jsonl     also records the reports, replayable in the application
"""

import argparse
import asyncio
import json
import logging
import pathlib
import time

from bumble import hci
from bumble.device import Device, Peer
from bumble.gatt import (
    GATT_HUMAN_INTERFACE_DEVICE_SERVICE,
    GATT_REPORT_CHARACTERISTIC,
    GATT_REPORT_REFERENCE_DESCRIPTOR,
)
from bumble.keys import JsonKeyStore
from bumble.pairing import PairingConfig, PairingDelegate
from bumble.transport import open_transport
from bumble.transport.common import TransportInitError

REPORT_INPUT, REPORT_FEATURE = 1, 3
UNLOCK_REPORT_ID, UNLOCK_VALUE = 0xF0, b"\xaf"   # without it, the remote stays silent (see HidEnumerator.Unlock)
# Public address of the dongle: the remote (Bluetooth 4.0) reconnects poorly to an address that changes.
OWN_ADDRESS_TYPE = hci.OwnAddressType.PUBLIC

t0 = time.monotonic()


def log(message: str) -> None:
    print(f"{time.monotonic() - t0:8.3f}  {message}", flush=True)


async def hid_reports(peer: Peer) -> list[tuple[int, int, object]]:
    """(id, type, characteristic) of each report of the HID service, from its Report Reference descriptor."""
    await peer.discover_services([GATT_HUMAN_INTERFACE_DEVICE_SERVICE])
    reports = []
    for service in peer.get_services_by_uuid(GATT_HUMAN_INTERFACE_DEVICE_SERVICE):
        await service.discover_characteristics()
        for characteristic in service.get_characteristics_by_uuid(GATT_REPORT_CHARACTERISTIC):
            await characteristic.discover_descriptors()
            reference = characteristic.get_descriptor(GATT_REPORT_REFERENCE_DESCRIPTOR)
            if reference is None:
                continue
            report_id, report_type = (await reference.read_value())[:2]
            reports.append((report_id, report_type, characteristic))
    return reports


async def session(device: Device, address: hci.Address, capture) -> None:
    """One full connection: connect, encrypt (or pair), subscribe, unlock, then wait."""
    log(f"Waiting for {address} (first time: Menu + Volume + ~5 s; afterwards: any button)…")
    connection = await device.connect(address, own_address_type=OWN_ADDRESS_TYPE, timeout=None)
    log(f"Connected ({connection.peer_address}).")

    try:
        await connection.encrypt()
        log("Encrypted with the saved keys.")
    except Exception as e:   # no keys, or the remote forgot the pairing
        log(f"No encryption with the known keys ({e}): pairing…")
        await connection.pair()
        log("Paired; keys saved.")

    peer = Peer(connection)
    reports = await hid_reports(peer)
    log("HID reports: " + ", ".join(f"0x{i:02X}/{'?ief'[t] if t < 4 else t}" for i, t, _ in reports))

    def on_report(report_id: int):
        def handler(value: bytes) -> None:
            report = bytes([report_id]) + bytes(value)   # same shape as hid.dll: ID first (0xFC…)
            log(report.hex())
            if capture:
                capture.write(json.dumps({"kind": "report", "t": round(time.monotonic() - t0, 3), "hex": report.hex()}) + "\n")
                capture.flush()
        return handler

    for report_id, report_type, characteristic in reports:
        if report_type == REPORT_INPUT:
            await characteristic.subscribe(on_report(report_id))

    unlock = next((c for i, t, c in reports if i == UNLOCK_REPORT_ID and t == REPORT_FEATURE), None)
    if unlock is None:
        log(f"Feature report 0x{UNLOCK_REPORT_ID:02X} not found: cannot unlock.")
    else:
        await unlock.write_value(UNLOCK_VALUE, with_response=True)
        log("Unlocked (0xF0 <- 0xAF): press the buttons, touch the touchpad.")

    await connection.sustain()
    log("Disconnected (sleep?).")


def configure(device: Device, keys_path: str) -> None:
    device.keystore = JsonKeyStore(filename=keys_path)
    # "Just Works" pairing (no display or keyboard), with key bonding.
    device.pairing_config_factory = lambda _: PairingConfig(
        mitm=False, bonding=True, delegate=PairingDelegate(PairingDelegate.IoCapability.NO_OUTPUT_NO_INPUT))


async def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--transport", default="usb:2357:0604", help="Bluetooth dongle (default: TP-Link UB500)")
    parser.add_argument("--address", default="14:9D:99:26:BF:CC", help="public address of the remote")
    parser.add_argument("--keys", default=str(pathlib.Path(__file__).with_name("siri-remote-keys.json")))
    parser.add_argument("--capture", help=".jsonl capture file (monitor --raw format)")
    parser.add_argument("--verbose", action="store_true")
    args = parser.parse_args()
    logging.basicConfig(level=logging.DEBUG if args.verbose else logging.WARNING)

    address = hci.Address(args.address, hci.Address.PUBLIC_DEVICE_ADDRESS)
    capture = open(args.capture, "a", encoding="utf-8") if args.capture else None

    async with await open_transport(args.transport) as (source, sink):
        # The random address is unused (we connect with the dongle's public address) but Bumble requires one.
        device = Device.with_hci("AppleRemoteWin", hci.Address("F0:F1:F2:F3:F4:F5"), source, sink)
        configure(device, args.keys)
        # 27-byte LE packets (Bluetooth 4.0, like the remote): the stock firmware of the Qualcomm QCA61x4A
        # rejects the 251 bytes Bumble asks for by default, and Bumble then aborts startup.
        device.host.suggested_max_tx_octets, device.host.suggested_max_tx_time = 27, 328
        await device.power_on()
        log(f"Dongle ready: {device.public_address}.")

        while True:
            try:
                await session(device, address, capture)
            except (Exception, asyncio.CancelledError) as e:
                # Bumble cancels the pending operation (CancelledError) when the link drops: start over.
                if isinstance(e, asyncio.CancelledError) and asyncio.current_task().cancelling():
                    raise   # Ctrl+C
                log(f"Error: {e!r}")
                await asyncio.sleep(1)


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        pass
    except TransportInitError as e:
        raise SystemExit(f"Bluetooth dongle not found ({e}): plugged in? WinUSB driver installed with Zadig?")
