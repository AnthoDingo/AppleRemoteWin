// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 AnthoDingo

// Siri Remote gen 1 -> USB dongle on ESP32-S3.
// BLE central (NimBLE): pairs "Just Works" with the remote, keeps the bond in NVS, reconnects after sleep,
// subscribes to the 0xFC input report and unlocks it (0xAF -> 0xF0) on every connection.
// USB device (TinyUSB): one vendor HID collection (usage page 0xFF52) read by the Windows app, see README.md.

#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "esp_log.h"
#include "nvs_flash.h"
#include "tinyusb.h"
#include "class/hid/hid_device.h"
#include "nimble/nimble_port.h"
#include "nimble/nimble_port_freertos.h"
#include "host/ble_hs.h"
#include "host/util/util.h"
#include "ota.h"

void ble_store_config_init(void);   // NimBLE NVS store, not declared in a public header

static const char *TAG = "appleremote";

// ---- remote GATT database (gen 1) ------------------------------------------------------------
// ponytail: fixed handles of the gen 1 database (the kernel filter relies on the same ones);
// switch to HID service discovery (Report Reference 0x2908) if a remote firmware ever moves them.
#define H_UNLOCK        0x001D  // feature report 0xF0
#define H_REPORT        0x0023  // input report 0xFC (notifications)
#define H_REPORT_CCCD   0x0024
#define H_BATTERY       0x0028  // battery level, %
#define H_BATTERY_CCCD  0x0029
#define REMOTE_REPORT_ID 0xFC
#define UNLOCK_VALUE     0xAF
#define REPORT_MAX       20     // ATT MTU 23 - 3: the remote never sends more in one notification

// ---- USB protocol (mirrored in src/AppleRemote.Windows/EspDongle.cs) ------------------------
#define USB_REPORT_REMOTE  1    // in:  [remote report ID, 20 bytes zero-padded]
#define USB_REPORT_STATE   2    // in:  [state, battery %, last disconnect reason]
#define USB_REPORT_COMMAND 3    // feature (out): [command]
#define USB_REPORT_OTA     4    // feature: firmware update, see ota.h
enum { CMD_STATUS = 0, CMD_PAIR = 1, CMD_FORGET = 2 };
enum { ST_IDLE = 0, ST_PAIRING = 1, ST_WAITING = 2, ST_CONNECTED = 3 };

static uint8_t own_addr_type;
static uint16_t conn = BLE_HS_CONN_HANDLE_NONE;
static uint8_t state = ST_IDLE, battery, last_reason;
static bool scan_allowed = true;   // false after "forget": stay idle until "pair"
static volatile uint8_t pending_command;
static struct ble_npl_event command_event;

static int on_gap(struct ble_gap_event *event, void *arg);

// ---- USB ------------------------------------------------------------------------------------

static const uint8_t hid_report_descriptor[] = {
    0x06, 0x52, 0xFF,                    // Usage Page (vendor 0xFF52)
    0x09, 0x01,                          // Usage (1)
    0xA1, 0x01,                          // Collection (Application)
    0x15, 0x00, 0x26, 0xFF, 0x00,        //   Logical 0..255
    0x75, 0x08,                          //   Report Size 8
    0x85, USB_REPORT_REMOTE, 0x09, 0x01, 0x95, 1 + REPORT_MAX, 0x81, 0x02,   // Input
    0x85, USB_REPORT_STATE, 0x09, 0x02, 0x95, 3, 0x81, 0x02,                 // Input
    0x85, USB_REPORT_COMMAND, 0x09, 0x03, 0x95, 1, 0xB1, 0x02,               // Feature
    0x85, USB_REPORT_OTA, 0x09, 0x04, 0x95, OTA_REPORT_SIZE, 0xB1, 0x02,     // Feature
    0xC0,
};

static const tusb_desc_device_t device_descriptor = {
    .bLength = sizeof(tusb_desc_device_t),
    .bDescriptorType = TUSB_DESC_DEVICE,
    .bcdUSB = 0x0200,
    .bMaxPacketSize0 = CFG_TUD_ENDPOINT0_SIZE,
    .idVendor = 0x303A,      // Espressif; the app matches VID + usage page, not the PID
    .idProduct = 0x8A52,     // ponytail: unregistered test PID, request one from Espressif before distributing
    .bcdDevice = 0x0100,
    .iManufacturer = 1,
    .iProduct = 2,
    .iSerialNumber = 3,
    .bNumConfigurations = 1,
};

static const char *string_descriptors[] = {
    (const char[]){0x09, 0x04},  // English
    "AnthoDingo",
    "AppleRemote ESP32-S3",
    "000001",
    "AppleRemote control",
};

#define CONFIG_TOTAL_LEN (TUD_CONFIG_DESC_LEN + TUD_HID_DESC_LEN)
static const uint8_t configuration_descriptor[] = {
    TUD_CONFIG_DESCRIPTOR(1, 1, 0, CONFIG_TOTAL_LEN, 0, 100),
    TUD_HID_DESCRIPTOR(0, 4, HID_ITF_PROTOCOL_NONE, sizeof(hid_report_descriptor), 0x81, 32, 1),
};

uint8_t const *tud_hid_descriptor_report_cb(uint8_t instance) { return hid_report_descriptor; }

uint16_t tud_hid_get_report_cb(uint8_t instance, uint8_t report_id, hid_report_type_t type, uint8_t *buffer, uint16_t length)
{
    return report_id == USB_REPORT_OTA && type == HID_REPORT_TYPE_FEATURE ? ota_get(buffer, length) : 0;
}

// The new firmware is kept only once USB and BLE both came up: otherwise the next reset rolls back.
static volatile bool usb_up, ble_up;

static void confirm_if_working(void)
{
    if (usb_up && ble_up) ota_confirm_running();
}

void tud_mount_cb(void)
{
    usb_up = true;
    confirm_if_working();
}

// Commands come from the TinyUSB task: hand them to the NimBLE host task, which owns all BLE state.
void tud_hid_set_report_cb(uint8_t instance, uint8_t report_id, hid_report_type_t type, uint8_t const *buffer, uint16_t length)
{
    if (report_id == USB_REPORT_OTA && type == HID_REPORT_TYPE_FEATURE) { ota_set(buffer, length); return; }
    if (report_id != USB_REPORT_COMMAND || length < 1) return;
    pending_command = buffer[0];
    ble_npl_eventq_put(nimble_port_get_dflt_eventq(), &command_event);
}

static void usb_send(uint8_t report_id, const void *data, uint16_t length)
{
    if (!tud_mounted()) return;
    // ponytail: short busy-wait for the IN endpoint (1 ms polling), a queue if reports ever get dropped
    for (int i = 0; i < 10 && !tud_hid_ready(); i++) vTaskDelay(1);
    if (!tud_hid_report(report_id, data, length)) ESP_LOGW(TAG, "USB report %u dropped", report_id);
}

static void send_state(void)
{
    uint8_t report[3] = {state, battery, last_reason};
    usb_send(USB_REPORT_STATE, report, sizeof report);
}

static void set_state(uint8_t new_state)
{
    state = new_state;
    send_state();
}

// ---- BLE ------------------------------------------------------------------------------------

// Port of SiriRemoteBle.IsSiriRemoteAdvertisement: HID service 0x1812 listed and Apple manufacturer data (0x004C).
static bool is_siri_remote_advertisement(const uint8_t *data, uint8_t length)
{
    bool hid = false, apple = false;
    for (int i = 0; i + 1 < length && data[i] > 0; i += data[i] + 1) {
        int field = data[i], type = data[i + 1];
        if (i + 1 + field > length) break;
        if (type == 0x02 || type == 0x03)
            for (int j = i + 2; j + 1 <= i + field; j += 2)
                hid |= (data[j] | data[j + 1] << 8) == 0x1812;
        if (type == 0xFF && field >= 3)
            apple |= data[i + 2] == 0x4C && data[i + 3] == 0x00;
    }
    return hid && apple;
}

// Bonded remote: wait for it (it advertises when a button is pressed). Otherwise scan for one in pairing mode.
static void start(void)
{
    ble_addr_t peer;
    int bonded = 0, rc;
    ble_store_util_bonded_peers(&peer, &bonded, 1);
    if (bonded > 0) {
        set_state(ST_WAITING);
        rc = ble_gap_connect(own_addr_type, &peer, BLE_HS_FOREVER, NULL, on_gap, NULL);
    } else if (scan_allowed) {
        set_state(ST_PAIRING);
        struct ble_gap_disc_params params = {.filter_duplicates = 1};   // active scan: scan responses too
        rc = ble_gap_disc(own_addr_type, BLE_HS_FOREVER, &params, on_gap, NULL);
    } else {
        set_state(ST_IDLE);
        return;
    }
    if (rc != 0) ESP_LOGE(TAG, "start: %d", rc);
}

// Forgets the bond and starts over: an open connection or a pending connect restarts from its GAP event.
static void forget(bool then_scan)
{
    ble_store_clear();
    scan_allowed = then_scan;
    last_reason = 0;
    if (conn != BLE_HS_CONN_HANDLE_NONE) { ble_gap_terminate(conn, BLE_ERR_REM_USER_CONN_TERM); return; }
    if (ble_gap_conn_active()) { ble_gap_conn_cancel(); return; }
    if (ble_gap_disc_active()) ble_gap_disc_cancel();
    start();
}

static void on_command(struct ble_npl_event *event)
{
    switch (pending_command) {
    case CMD_PAIR:   ESP_LOGI(TAG, "command: pair");   forget(true);  break;
    case CMD_FORGET: ESP_LOGI(TAG, "command: forget"); forget(false); break;
    default:         send_state(); break;
    }
}

// Set-up after encryption, one ATT request at a time: subscribe, unlock, then battery (optional).
static int setup(uint16_t handle, const struct ble_gatt_error *error, struct ble_gatt_attr *attr, void *arg)
{
    static const uint8_t notify[2] = {0x01, 0x00}, unlock = UNLOCK_VALUE;
    int step = (int)(intptr_t)arg;
    if (error->status != 0) {
        ESP_LOGW(TAG, "set-up step %d: ATT status %d", step, error->status);
        if (step <= 2) { ble_gap_terminate(handle, BLE_ERR_REM_USER_CONN_TERM); return 0; }
        if (step == 3) return 0;   // no battery level: the remote stays usable
    }
    switch (step) {
    case 0: return ble_gattc_write_flat(handle, H_REPORT_CCCD, notify, sizeof notify, setup, (void *)1);
    case 1: return ble_gattc_write_flat(handle, H_UNLOCK, &unlock, 1, setup, (void *)2);
    case 2:
        ESP_LOGI(TAG, "remote unlocked");
        set_state(ST_CONNECTED);
        return ble_gattc_read(handle, H_BATTERY, setup, (void *)3);
    case 3:
        os_mbuf_copydata(attr->om, 0, 1, &battery);
        send_state();
        return ble_gattc_write_flat(handle, H_BATTERY_CCCD, notify, sizeof notify, setup, (void *)4);
    default: return 0;
    }
}

static int on_gap(struct ble_gap_event *event, void *arg)
{
    struct ble_gap_conn_desc desc;
    switch (event->type) {
    case BLE_GAP_EVENT_DISC:
        if (!is_siri_remote_advertisement(event->disc.data, event->disc.length_data)) return 0;
        ESP_LOGI(TAG, "remote in pairing mode found");
        ble_gap_disc_cancel();
        if (ble_gap_connect(own_addr_type, &event->disc.addr, 30000, NULL, on_gap, NULL) != 0) start();
        return 0;

    case BLE_GAP_EVENT_CONNECT:
        if (event->connect.status != 0) { start(); return 0; }   // failed or cancelled
        conn = event->connect.conn_handle;
        ble_gap_security_initiate(conn);   // stored keys, or "Just Works" pairing without them
        return 0;

    case BLE_GAP_EVENT_ENC_CHANGE:
        if (event->enc_change.status == 0) return setup(conn, &(struct ble_gatt_error){0}, NULL, (void *)0);
        // Keys refused (the remote was paired elsewhere): pair again on the same connection, like SiriRemoteBle.
        if (event->enc_change.status == BLE_HS_HCI_ERR(BLE_ERR_PINKEY_MISSING) ||
            event->enc_change.status == BLE_HS_HCI_ERR(BLE_ERR_AUTH_FAIL)) {
            ESP_LOGW(TAG, "keys refused (%d): pairing again", event->enc_change.status);
            if (ble_gap_conn_find(conn, &desc) == 0) ble_store_util_delete_peer(&desc.peer_id_addr);
            ble_gap_security_initiate(conn);
        } else {
            ble_gap_terminate(conn, BLE_ERR_REM_USER_CONN_TERM);
        }
        return 0;

    case BLE_GAP_EVENT_REPEAT_PAIRING:
        if (ble_gap_conn_find(event->repeat_pairing.conn_handle, &desc) == 0) ble_store_util_delete_peer(&desc.peer_id_addr);
        return BLE_GAP_REPEAT_PAIRING_RETRY;

    case BLE_GAP_EVENT_NOTIFY_RX: {
        uint16_t length = OS_MBUF_PKTLEN(event->notify_rx.om);
        if (event->notify_rx.attr_handle == H_REPORT) {
            uint8_t report[1 + REPORT_MAX] = {REMOTE_REPORT_ID};
            os_mbuf_copydata(event->notify_rx.om, 0, length < REPORT_MAX ? length : REPORT_MAX, report + 1);
            usb_send(USB_REPORT_REMOTE, report, sizeof report);
        } else if (event->notify_rx.attr_handle == H_BATTERY && length > 0) {
            os_mbuf_copydata(event->notify_rx.om, 0, 1, &battery);
            send_state();
        }
        return 0;
    }

    case BLE_GAP_EVENT_DISCONNECT:
        ESP_LOGI(TAG, "disconnected, reason 0x%02X", event->disconnect.reason & 0xFF);
        conn = BLE_HS_CONN_HANDLE_NONE;
        last_reason = event->disconnect.reason & 0xFF;
        start();
        return 0;
    }
    return 0;
}

static void on_sync(void)
{
    ble_hs_util_ensure_addr(0);
    ble_hs_id_infer_auto(0, &own_addr_type);
    ble_up = true;
    confirm_if_working();
    start();
}

static void on_reset(int reason) { ESP_LOGE(TAG, "NimBLE reset, reason %d", reason); }

static void host_task(void *param)
{
    nimble_port_run();
    nimble_port_freertos_deinit();
}

void app_main(void)
{
    esp_err_t err = nvs_flash_init();   // NimBLE keeps the bond there
    if (err == ESP_ERR_NVS_NO_FREE_PAGES || err == ESP_ERR_NVS_NEW_VERSION_FOUND) {
        ESP_ERROR_CHECK(nvs_flash_erase());
        err = nvs_flash_init();
    }
    ESP_ERROR_CHECK(err);

    const tinyusb_config_t usb = {
        .device_descriptor = &device_descriptor,
        .string_descriptor = string_descriptors,
        .string_descriptor_count = sizeof string_descriptors / sizeof string_descriptors[0],
        .external_phy = false,
        .configuration_descriptor = configuration_descriptor,
    };
    ESP_ERROR_CHECK(tinyusb_driver_install(&usb));

    ESP_ERROR_CHECK(nimble_port_init());
    ble_hs_cfg.sync_cb = on_sync;
    ble_hs_cfg.reset_cb = on_reset;
    ble_hs_cfg.store_status_cb = ble_store_util_status_rr;
    ble_hs_cfg.sm_io_cap = BLE_HS_IO_NO_INPUT_OUTPUT;   // "Just Works", legacy pairing like Smp.cs
    ble_hs_cfg.sm_bonding = 1;
    ble_hs_cfg.sm_sc = 0;
    ble_hs_cfg.sm_our_key_dist = BLE_SM_PAIR_KEY_DIST_ENC | BLE_SM_PAIR_KEY_DIST_ID;
    ble_hs_cfg.sm_their_key_dist = BLE_SM_PAIR_KEY_DIST_ENC | BLE_SM_PAIR_KEY_DIST_ID;
    ble_store_config_init();
    ble_npl_event_init(&command_event, on_command, NULL);
    nimble_port_freertos_init(host_task);
}
