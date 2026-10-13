// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 AnthoDingo

#include <stdio.h>
#include <string.h>
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "esp_log.h"
#include "esp_ota_ops.h"
#include "esp_app_desc.h"
#include "esp_system.h"
#include "sdkconfig.h"
#include "ota.h"

// Never build an updatable firmware that would accept unsigned images, or one that cannot fall back.
#if !CONFIG_SECURE_SIGNED_ON_UPDATE
#error "USB update requires signed images: CONFIG_SECURE_SIGNED_ON_UPDATE_NO_SECURE_BOOT (see README)"
#endif
#if !CONFIG_BOOTLOADER_APP_ROLLBACK_ENABLE
#error "USB update requires CONFIG_BOOTLOADER_APP_ROLLBACK_ENABLE"
#endif

// Protocol mirrored in src/AppleRemote.Windows/EspDongle.cs
enum { OP_BEGIN = 1, OP_DATA = 2, OP_END = 3, OP_ABORT = 4 };
enum {
    ST_IDLE = 0, ST_RECEIVING = 1, ST_VERIFYING = 2, ST_DONE = 3,
    ERR_BEGIN = 0x81, ERR_SIZE = 0x82, ERR_CHUNK = 0x83, ERR_WRITE = 0x84, ERR_INCOMPLETE = 0x85,
    ERR_SIGNATURE = 0x86, ERR_DOWNGRADE = 0x87, ERR_PROJECT = 0x88, ERR_BOOT = 0x89,
};
#define DATA_HEADER 6                          // op, offset (u32 LE), length
#define DATA_MAX (OTA_REPORT_SIZE - DATA_HEADER)

static const char *TAG = "ota";
static volatile uint8_t status = ST_IDLE;
static esp_ota_handle_t handle;                // open only while status == ST_RECEIVING / ST_VERIFYING
static const esp_partition_t *target;
static uint32_t expected, written;

static void fail(uint8_t error)
{
    if (status == ST_RECEIVING) esp_ota_abort(handle);
    status = error;
    ESP_LOGW(TAG, "update refused: 0x%02X", error);
}

// "major.minor.patch" -> comparable number; anything else is 0, i.e. older than any real release.
static uint32_t version_number(const char *version)
{
    unsigned major, minor, patch;
    if (sscanf(version, "%u.%u.%u", &major, &minor, &patch) != 3 || major > 1023 || minor > 1023 || patch > 1023) return 0;
    return major << 20 | minor << 10 | patch;
}

// Hashing and the RSA check take a while: out of the TinyUSB task, the app polls the status meanwhile.
static void verify_task(void *param)
{
    const esp_app_desc_t *running = esp_app_get_description();
    esp_app_desc_t incoming;
    esp_err_t err = esp_ota_end(handle);   // image integrity + signature (CONFIG_SECURE_SIGNED_ON_UPDATE); frees the handle
    if (err != ESP_OK)
        status = err == ESP_ERR_OTA_VALIDATE_FAILED ? ERR_SIGNATURE : ERR_WRITE;
    else if (esp_ota_get_partition_description(target, &incoming) != ESP_OK)
        status = ERR_SIGNATURE;
    else if (strncmp(incoming.project_name, running->project_name, sizeof incoming.project_name) != 0)
        status = ERR_PROJECT;
    else if (version_number(incoming.version) < version_number(running->version))
        status = ERR_DOWNGRADE;   // an older signed image may carry a fixed flaw
    else if (esp_ota_set_boot_partition(target) != ESP_OK)
        status = ERR_BOOT;
    else
        status = ST_DONE;

    if (status == ST_DONE) {
        ESP_LOGI(TAG, "firmware %s accepted, rebooting", incoming.version);
        vTaskDelay(pdMS_TO_TICKS(1000));   // lets the app read "done"
        esp_restart();
    }
    ESP_LOGW(TAG, "update refused: 0x%02X", status);
    vTaskDelete(NULL);
}

void ota_set(const uint8_t *data, uint16_t length)
{
    if (length < 1 || status == ST_VERIFYING || status == ST_DONE) return;   // too late: let the check finish
    switch (data[0]) {
    case OP_BEGIN: {
        uint32_t size;
        if (status == ST_RECEIVING) esp_ota_abort(handle);
        status = ST_IDLE;
        if (length < 5) { fail(ERR_SIZE); return; }
        memcpy(&size, data + 1, 4);
        target = esp_ota_get_next_update_partition(NULL);
        if (target == NULL || size == 0 || size > target->size) { fail(ERR_SIZE); return; }
        // Sequential: erases sector by sector while writing, no multi-second erase inside a USB control transfer.
        if (esp_ota_begin(target, OTA_WITH_SEQUENTIAL_WRITES, &handle) != ESP_OK) { fail(ERR_BEGIN); return; }
        expected = size;
        written = 0;
        status = ST_RECEIVING;
        ESP_LOGI(TAG, "receiving %lu bytes into %s", (unsigned long)size, target->label);
        return;
    }
    case OP_DATA: {
        uint32_t offset;
        if (status != ST_RECEIVING) return;
        if (length < DATA_HEADER) { fail(ERR_CHUNK); return; }
        memcpy(&offset, data + 1, 4);
        uint8_t n = data[5];
        // In order, no gap, no overlap, never past the announced size.
        if (offset != written || n == 0 || n > DATA_MAX || DATA_HEADER + n > length || n > expected - written) {
            fail(ERR_CHUNK);
            return;
        }
        if (esp_ota_write(handle, data + DATA_HEADER, n) != ESP_OK) { fail(ERR_WRITE); return; }   // also checks the 0xE9 magic
        written += n;
        return;
    }
    case OP_END:
        if (status != ST_RECEIVING) return;
        if (written != expected) { fail(ERR_INCOMPLETE); return; }
        status = ST_VERIFYING;
        if (xTaskCreate(verify_task, "ota_verify", 8192, NULL, 5, NULL) != pdPASS) {
            esp_ota_abort(handle);
            status = ERR_WRITE;
        }
        return;
    case OP_ABORT:
        if (status == ST_RECEIVING) esp_ota_abort(handle);
        status = ST_IDLE;
        return;
    }
}

uint16_t ota_get(uint8_t *buffer, uint16_t length)
{
    uint8_t report[OTA_REPORT_SIZE] = {status};
    memcpy(report + 1, &written, 4);
    strncpy((char *)report + 5, esp_app_get_description()->version, 32);
    uint16_t n = length < sizeof report ? length : sizeof report;
    memcpy(buffer, report, n);
    return n;
}

void ota_confirm_running(void)
{
    esp_ota_img_states_t state;
    if (esp_ota_get_state_partition(esp_ota_get_running_partition(), &state) == ESP_OK && state == ESP_OTA_IMG_PENDING_VERIFY) {
        esp_ota_mark_app_valid_cancel_rollback();
        ESP_LOGI(TAG, "new firmware confirmed");
    }
}
