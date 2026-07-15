# kiosk-firmware-v2

Second-generation kiosk firmware. Forked from `kiosk-firmware-v1` after
v1.0 was tagged complete on 2026-05-22. v1 stays in its folder unchanged
and remains the production firmware until v2 is ready to ship.

**Target hardware:** Same as v1 — Waveshare ESP32-S3-Touch-LCD-3.5
(ST7796 panel, AXP2101 PMU, TCA9554 I/O expander, ES8311 audio codec,
OV2640 camera, FT6336 capacitive touch).

## What v2 adds over v1

- **Capacitive touch input** via the FT6336 panel (unused in v1)
- **Secret gesture + numeric PIN** to enter config mode
- **On-device config UI** for Campus + Terminal ID (Phase v2.0)
- **WiFi QR provisioning** via the existing camera (Phase v2.1)
- **NVS persistence** so each kiosk can be flashed with the same image
  and configured in the field

## Build

```powershell
arduino-cli compile --fqbn "esp32:esp32:esp32s3:USBMode=hwcdc,CDCOnBoot=cdc,MSCOnBoot=default,DFUOnBoot=default,UploadMode=cdc,CPUFreq=240,FlashMode=qio,FlashSize=16M,PartitionScheme=app3M_fat9M_16MB,PSRAM=opi,LoopCore=1,EventsCore=1,DebugLevel=none,EraseFlash=none" "kiosk-firmware-v2.ino"
```

## Phase status

- [x] v2.0 Phase A — Touch driver + diagnostic logging
- [ ] v2.0 Phase B — Gesture detector (corner-sequence unlock)
- [ ] v2.0 Phase C — PIN entry screen
- [ ] v2.0 Phase D — Campus + Terminal config UI
- [ ] v2.0 Phase E — NVS persistence
- [ ] v2.1 — WiFi QR provisioning
- [ ] v2.2 — TC_Rooms server schema + RoomId per terminal
- [ ] v2.3 — Optional admin knobs (server URL, secret, brightness, timeout)
