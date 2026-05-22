# kiosk-firmware-v1

Camera-based QR kiosk firmware for NewHeights.TimeClock.

**Target hardware:** Waveshare ESP32-S3-Touch-LCD-3.5 (ST7796 panel,
AXP2101 PMU, TCA9554 I/O expander, ES8311 audio codec, OV2640 camera)

**Server endpoint:** `POST https://clock.newheightsed.com/api/v1/punch`
(see `src/NewHeights.TimeClock.Web/Program.cs` near the `MapPost` for
`/api/v1/punch` and `Services/PhotoThumbnailService.cs` for the
thumbnail logic that pairs with this firmware).

## Build

```powershell
arduino-cli compile --fqbn "esp32:esp32:esp32s3:USBMode=hwcdc,CDCOnBoot=cdc,MSCOnBoot=default,DFUOnBoot=default,UploadMode=default,CPUFreq=240,FlashMode=qio,FlashSize=16M,PartitionScheme=app3M_fat9M_16MB,PSRAM=opi,LoopCore=1,EventsCore=1,DebugLevel=none,EraseFlash=none" "kiosk-firmware-v1.ino"
```

## Files

- `kiosk-firmware-v1.ino` — main sketch (WiFi, HTTP, camera, LCD, audio,
  QR decode, scan dispatch).
- `quirc.{c,h}`, `quirc_internal.h`, `decode.c`, `identify.c`,
  `version_db.c` — vendored [quirc](https://github.com/dlbeer/quirc) QR
  decoder library.

## Pin map

LCD/touch/audio/IMU/RTC share I2C on pins 7 (SCL) and 8 (SDA) — same bus
also feeds the OV2640 SCCB.
