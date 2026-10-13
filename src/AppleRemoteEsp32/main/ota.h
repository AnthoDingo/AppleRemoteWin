// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 AnthoDingo

// Firmware update over USB (feature report 4). Any process on the PC can write HID reports, so the dongle is the only
// judge: the image goes to the inactive OTA slot and is booted only if ESP-IDF accepts its signature (key of the running
// firmware), it is this project's, and it is not older than the running one. Rollback if it never confirms itself.
#pragma once
#include <stdint.h>

#define OTA_REPORT_SIZE 63   // payload of feature report 4 (64 bytes with the ID = TinyUSB's control buffer)

void ota_set(const uint8_t *data, uint16_t length);   // SET_REPORT payload, TinyUSB task
uint16_t ota_get(uint8_t *buffer, uint16_t length);   // GET_REPORT payload: status, bytes written, running version
void ota_confirm_running(void);                       // the new firmware works (USB + BLE up): cancel the rollback
