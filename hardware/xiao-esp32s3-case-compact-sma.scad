// SPDX-License-Identifier: AGPL-3.0-or-later
// Board-only "snap" version of the XIAO ESP32S3 case with an RP-SMA bulkhead for an external antenna.
// Same geometry and parameters as xiao-esp32s3-case.scad: edit them there, all versions stay in step.

include <xiao-esp32s3-case.scad>
style = "snap";
antenna = false;
sma = true;
