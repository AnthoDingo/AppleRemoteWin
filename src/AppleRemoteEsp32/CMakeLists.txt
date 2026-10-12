# SPDX-License-Identifier: AGPL-3.0-or-later
# Copyright (C) 2026 AnthoDingo
cmake_minimum_required(VERSION 3.16)
include($ENV{IDF_PATH}/tools/cmake/project.cmake)
# Compared on update: the dongle refuses an older signed image.
set(PROJECT_VER "1.2.0")
project(AppleRemoteEsp32)
