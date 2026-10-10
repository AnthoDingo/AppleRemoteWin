# SPDX-License-Identifier: AGPL-3.0-or-later
# Copyright (C) 2026 AnthoDingo

"""
Checks siri_bumble.py without hardware: a fake Siri Remote on virtual Bumble controllers.
1st session: pairing, subscription, unlock 0xF0 <- 0xAF, reception of a 0xFC report.
2nd session: encrypted reconnection with the saved keys, without pairing again.

    python tools/test_siri_bumble.py
"""

import asyncio
import io
import pathlib
import sys
import tempfile

sys.path.insert(0, str(pathlib.Path(__file__).parent))
import siri_bumble as sb  # noqa: E402

from bumble import gatt, hci  # noqa: E402
from bumble.att import Attribute, AttributeValue  # noqa: E402
from bumble.controller import Controller  # noqa: E402
from bumble.device import Device  # noqa: E402
from bumble.host import Host  # noqa: E402
from bumble.link import LocalLink  # noqa: E402
from bumble.transport.common import AsyncPipeSink  # noqa: E402

# Bumble's virtual link only routes LE data from random addresses: here the PC and the fake remote only have
# a static random address (real hardware: public addresses), which also serves as identity.
REMOTE = "C1:C2:C3:C4:C5:C6"
ENCRYPTED = Attribute.Permissions.READ_REQUIRES_ENCRYPTION | Attribute.Permissions.WRITE_REQUIRES_ENCRYPTION


def report(report_id: int, report_type: int, properties, value) -> gatt.Characteristic:
    reference = gatt.Descriptor(gatt.GATT_REPORT_REFERENCE_DESCRIPTOR, Attribute.Permissions.READABLE,
                                bytes([report_id, report_type]))
    return gatt.Characteristic(gatt.GATT_REPORT_CHARACTERISTIC, properties, ENCRYPTED, value, [reference])


async def main() -> None:
    link = LocalLink()
    controllers = [Controller("PC", link=link), Controller("REMOTE", link=link)]
    pc, remote = (Device(address=hci.Address(a), host=Host(c, AsyncPipeSink(c)))
                  for a, c in zip(["F0:F1:F2:F3:F4:F5", REMOTE], controllers))

    # Fake remote: input report 0xFC (notifications) and feature report 0xF0 (unlock).
    props = gatt.Characteristic.Properties
    input_report = report(0xFC, 1, props.READ | props.NOTIFY, b"\x00\x00")
    unlocked = []

    async def press_then_sleep(connection):
        await remote.notify_subscribers(input_report, b"\x00\x20")   # a button
        await asyncio.sleep(0.2)
        await connection.disconnect()                                 # goes to sleep

    def on_unlock(connection, value):
        unlocked.append(bytes(value))
        asyncio.get_running_loop().create_task(press_then_sleep(connection))

    remote.add_service(gatt.Service(gatt.GATT_HUMAN_INTERFACE_DEVICE_SERVICE, [
        input_report, report(0xF0, 3, props.READ | props.WRITE, AttributeValue(write=on_unlock))]))

    with tempfile.TemporaryDirectory() as tmp:
        sb.configure(pc, f"{tmp}/pc.json")
        sb.configure(remote, f"{tmp}/remote.json")
        for d in (pc, remote):
            await d.power_on()
        await remote.start_advertising(auto_restart=True)

        messages = []
        sb.log = messages.append
        sb.OWN_ADDRESS_TYPE = hci.OwnAddressType.RANDOM
        capture = io.StringIO()

        target = hci.Address(REMOTE)
        for _ in range(2):
            await asyncio.wait_for(sb.session(pc, target, capture), 10)

    print("\n".join(messages))
    assert unlocked == [b"\xaf", b"\xaf"], unlocked
    assert capture.getvalue().count('"hex": "fc0020"') == 2, capture.getvalue()
    assert sum("pairing" in m for m in messages) == 1, "the 2nd session must not pair again"
    assert any("saved keys" in m and "Encrypted" in m for m in messages)
    print("OK")


asyncio.run(main())
