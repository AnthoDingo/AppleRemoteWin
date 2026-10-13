// SPDX-License-Identifier: AGPL-3.0-or-later
// Case for the Seeed Studio XIAO ESP32S3 (non-Sense) used as the AppleRemoteEsp32 USB dongle.
// Tray + lid, two styles: "snap" (press fit, snap bead) or "slide" (server-chassis style: the lid drops in with its
// lugs in L slots of the walls, then slides towards the USB end over a detent). The FPC antenna is glued on the tray floor beyond the board (not under its ground plane),
// the U.FL cable is coiled above it. No pin headers: the lid ribs press on the castellated edges.
// Print: part = "both" lays the lid upside down next to the tray. PLA/PETG, 0.2 mm layers, no supports.
// Measure your board with calipers and adjust the parameters before printing.

part = "both"; // ["tray", "lid", "both"]
style = "snap"; // ["snap", "slide"]

/* [Board: measure yours] */
pcb_l = 21;          // along the USB axis
pcb_w = 17.8;
pcb_t = 1.2;
usb_w = 8.94;        // USB-C receptacle
usb_h = 3.26;
usb_overhang = 1.2;  // receptacle overhang past the PCB edge = thickness of the USB end wall
headroom = 4;        // free height above the PCB (shield, U.FL plug + cable)
// BOOT and RESET: [distance from the USB edge of the PCB, offset from the centre line]
buttons = [[2.2, -6.4], [2.2, 6.4]];
btn_d = 1.8;

/* [Antenna (FPC shipped with the board)] */
antenna = true;      // false: board only, no room for the antenna
sma = false;         // RP-SMA bulkhead in the rear wall (U.FL to RP-SMA pigtail), for an external antenna
sma_d = 6.5;         // hole for the 1/4"-36 thread
sma_z = 5;           // hole centre height: the ~8 mm nut (9.2 mm across corners) must clear the bottom
sma_room = 12;       // extra length behind the board for the connector body and cable
ant_l = 39;
ant_w = 19;

/* [Case] */
wall = 2;
floor_t = 1.2;
top_t = 1.2;
under = 1;      // gap under the PCB
fit = 0.2;      // clearance around the PCB
clr = 0.15;     // lid lip clearance
snap = 0.3;     // "snap": bead on the lip's long sides, clicks into a groove in the tray walls (0 = friction fit only)
slide = 5;      // "slide": travel of the lid, > lug_l
lug_l = 3;      // "slide": lugs on the lip's long sides
lug_d = 0.8;    // depth into the wall, < wall - 0.8
lug_h = 1.2;
r = 2;          // outer corner radius
window_l = 6;   // thinned lid strip over the LEDs, from the USB end (print the lid in a light colour)
window_t = 0.6;

$fn = 32;

in_l = pcb_l + fit + (antenna ? 2 + ant_l + 1 : 1.2 + 2) + (sma ? sma_room : 0);  // without antenna: rear stops + U.FL plug
in_w = (antenna ? max(pcb_w, ant_w) : pcb_w) + 2 * fit;
in_h = max(under + pcb_t + headroom, sma ? sma_z + sma_d / 2 + 0.8 - floor_t : 0);  // SMA hole stays in the tray
out_l = usb_overhang + in_l + wall;
out_w = in_w + 2 * wall;
tray_h = floor_t + in_h;
usb_z = floor_t + under + pcb_t;  // PCB top = receptacle bottom
x0 = usb_overhang;                // inner face of the USB end wall = PCB edge
notch_w = usb_w + 0.6;            // also centres the board sideways
ant_x = x0 + pcb_l + fit + 2;
snap_z = tray_h - 1.2;
slider = style == "slide";
lip_h = slider ? 3 : 2;                          // deeper on "slide": wall material above the lug channel
lip_l = in_l - 2 * clr - (slider ? slide : 0);   // shorter at the rear: room to drop the lid in before sliding
lug_z = tray_h - 2.2;
lug_e = antenna ? 8 : 2;                          // lug inset from the lip ends
lug_xs = [x0 + lug_e, x0 + lip_l - lug_e - lug_l]; // final positions (lid closed)

module snap_bead(rad) {           // horizontal half-round along both long sides
    e = antenna ? 8 : 3;
    for (s = [-1, 1]) translate([x0 + e, s * (in_w / 2 - clr - 0.6 + snap), snap_z])
        rotate([0, 90, 0]) cylinder(r = rad, h = in_l - 2 * e);
}

module rbox(l, w, h, rad = r) {   // rounded box, x from 0, y centred
    translate([0, -w / 2, 0]) linear_extrude(h) offset(rad) offset(-rad) square([l, w]);
}

module tray() {
    difference() {
        rbox(out_l, out_w, tray_h);
        translate([x0, 0, floor_t]) rbox(in_l, in_w, in_h + 1, 0.5);
        // USB notch, open to the top: the lid tab closes it
        translate([-1, -notch_w / 2, usb_z - 0.3]) cube([x0 + 2, notch_w, tray_h]);
        // antenna locating pocket
        if (antenna) translate([ant_x, -ant_w / 2 - 0.2, floor_t - 0.3]) cube([ant_l + 0.4, ant_w + 0.4, 1]);
        if (!slider && snap > 0) snap_bead(0.65);
        // nail notch under the lid edge: rear, or middle of a long side when the SMA nut takes the rear
        if (sma) translate([out_l / 2 - 5, out_w / 2, tray_h]) rotate([0, 90, 0]) cylinder(r = 1.5, h = 10);
        else translate([out_l, -5, tray_h]) rotate([-90, 0, 0]) cylinder(r = 1.5, h = 10);
        if (sma) translate([out_l - wall - 1, 0, sma_z]) rotate([0, 90, 0]) cylinder(d = sma_d, h = wall + 2);
        // L slots: drop in at +slide, slide to the closed position
        if (slider) for (m = [0, 1], x = lug_xs) mirror([0, m, 0]) {
            translate([x - clr, in_w / 2 - 0.5, lug_z - lug_h / 2 - clr]) cube([lug_l + slide + 2 * clr, lug_d + 0.5, lug_h + 2 * clr]);
            translate([x + slide - clr, in_w / 2 - 0.5, lug_z - lug_h / 2 - clr]) cube([lug_l + 2 * clr, lug_d + 0.5, tray_h]);
        }
    }
    // detents just behind the closed lugs (0.2 mm interference, the lip flexes)
    if (slider) for (m = [0, 1], x = lug_xs) mirror([0, m, 0])
        translate([x + lug_l + clr + 0.35, in_w / 2 + lug_d, lug_z - lug_h / 2 - clr]) cylinder(r = 0.35, h = lug_h + 2 * clr);
    for (s = [-1, 1]) {
        // rails under the long PCB edges
        translate([x0, s * (in_w / 2 - 0.75) - 0.75, floor_t]) cube([pcb_l, 1.5, under]);
        // rear stops at the edges, the U.FL cable passes in the middle
        translate([x0 + pcb_l + fit, s * (in_w / 2 - 1.5) - 1.5, floor_t]) cube([1.2, 3, under + pcb_t + 0.8]);
    }
}

// Modelled in its assembled position (plate on top of the tray, features hanging down).
module lid() {
    difference() {
        union() {
            translate([0, 0, tray_h]) rbox(out_l, out_w, top_t);
            // lip, without its USB end segment (the receptacle sits there)
            difference() {
                translate([x0 + clr, 0, tray_h - lip_h]) difference() {
                    rbox(lip_l, in_w - 2 * clr, lip_h + 0.01, 0.5);
                    translate([1, 0, -1]) rbox(lip_l - 2, in_w - 2 * clr - 2, lip_h + 2, 0.5);
                }
                translate([0, -out_w / 2, 0]) cube([x0 + clr + 1.01, out_w, tray_h]);
                // and without its rear segment when the SMA connector body sits there
                if (sma) translate([x0 + clr + lip_l - 1.01, -out_w / 2, 0]) cube([10, out_w, tray_h]);
            }
            if (!slider && snap > 0) snap_bead(0.6);
            if (slider) for (m = [0, 1], x = lug_xs) mirror([0, m, 0])
                translate([x, in_w / 2 - clr - 0.01, lug_z - lug_h / 2]) cube([lug_l, lug_d + 0.01, lug_h]);
            // tab closing the USB notch above the receptacle
            translate([0, -notch_w / 2 + 0.1, usb_z + usb_h + 0.2]) cube([x0, notch_w - 0.2, tray_h - usb_z - usb_h]);
            // ribs pressing the PCB edges down (shorter on "slide": they must clear the rear stops when dropped in)
            for (s = [-1, 1])
                translate([x0 + window_l + 0.5, s * (pcb_w / 2 - 0.5) - 0.4, usb_z + 0.1])
                    cube([pcb_l - window_l - 1 - (slider ? slide : 0), 0.8, tray_h - usb_z]);
        }
        translate([x0, -(in_w / 2 - clr - 1), tray_h - 0.01]) cube([window_l, in_w - 2 * clr - 2, top_t - window_t + 0.01]);
        for (b = buttons) translate([x0 + b[0], b[1], tray_h - 1]) cylinder(d = btn_d, h = top_t + 2);
        // "slide": thumb grip grooves at the rear (grooves, not ridges: the lid prints top down)
        if (slider) for (i = [1:4]) translate([out_l - 2 - 2 * i, -6, tray_h + top_t - 0.4]) cube([0.8, 12, 1]);
    }
}

if (part == "tray" || part == "both") tray();
if (part == "lid") translate([0, 0, tray_h + top_t]) rotate([180, 0, 0]) lid();
if (part == "both") translate([0, out_w + 5, tray_h + top_t]) rotate([180, 0, 0]) lid();
