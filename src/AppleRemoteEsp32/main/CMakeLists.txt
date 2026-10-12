# SPDX-License-Identifier: AGPL-3.0-or-later
# Copyright (C) 2026 AnthoDingo
idf_component_register(SRCS "main.c" "ota.c" PRIV_REQUIRES bt nvs_flash app_update esp_app_format)
