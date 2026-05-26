// =====================================================================
// TimeClock Kiosk Firmware v1 — Camera-based QR scanner
// =====================================================================
// Board:     Waveshare ESP32-S3-Touch-LCD-3.5 (ESP32-S3R8, 8 MB PSRAM, 16 MB flash)
// Camera:    OV2640 160 deg wide-angle on FPC
// Battery:   LiPo via MX1.25 (AXP2101 PMIC handles charging)
// Endpoint:  POST https://clock.newheightsed.com/api/v1/punch
//
// Required Arduino libraries (Manage Libraries):
//   - ArduinoJson v7.x (Benoit Blanchon)
//
// Bundled into THIS sketch folder (download from github.com/dlbeer/quirc/lib):
//   - quirc.h, quirc_internal.h, quirc.c, decode.c, identify.c, version_db.c
//
// Required board package:
//   - esp32 by Espressif Systems v3.0 or later
//
// Board settings (Tools menu):
//   Board:              ESP32S3 Dev Module
//   USB CDC On Boot:    Disabled
//   CPU Frequency:      240MHz (WiFi)
//   Flash Mode:         QIO 80MHz
//   Flash Size:         16MB (128Mb)
//   Partition Scheme:   16M Flash (3MB APP/9.9MB FATFS)
//   PSRAM:              OPI PSRAM   ← required for camera frame buffers
//   USB Mode:           Hardware CDC and JTAG
//
// =====================================================================

#include <WiFi.h>
#include <WiFiClientSecure.h>
#include <HTTPClient.h>
#include <ArduinoJson.h>
#include <Wire.h>

// XPowersLib needs the chip variant defined BEFORE the include
#define XPOWERS_CHIP_AXP2101
#include "XPowersLib.h"

#define LGFX_USE_V1
#include <LovyanGFX.hpp>
#include "TCA9554.h"
#include "esp_camera.h"

#include "ESP_I2S.h"
#include "es8311.h"
#include <math.h>

extern "C" {
    #include "quirc.h"
}

#include "mbedtls/base64.h"
#include "esp_heap_caps.h"

// Quirc's decode internals are stack-heavy (Reed-Solomon, codeword
// arrays). The Arduino loopTask defaults to 8 KB which is not enough.
// 24 KB gives plenty of headroom for QVGA decoding without paying a
// huge RAM cost (PSRAM has 8 MB anyway).
SET_LOOP_TASK_STACK_SIZE(24 * 1024);

// =====================================================================
// 1. CONFIGURATION — fill in your values before flashing
// =====================================================================

const char* WIFI_SSID    = "NHHS-Staff";
const char* WIFI_PASS    = "NH3908wifi";

const char* SERVER_URL    = "https://clock.newheightsed.com/api/v1/punch";
const char* DEVICE_SECRET = "copper-thunder-violet-anchor-fox";

// 1001 = MCCART-RECEPTION, 1002 = STOPSIX-RECEPTION
const int   TERMINAL_ID  = 1001;
const char* FIRMWARE_VER = "v1.0";

const unsigned long SCAN_DEBOUNCE_MS = 8000;
const unsigned long HTTP_TIMEOUT_MS  = 25000;  // 25s to ride out Azure cold-start
const unsigned long WIFI_RETRY_MS    = 30000;

// =====================================================================
// 2. CAMERA PIN MAPPING — CONFIRMED from Waveshare 3.5 official demo
// =====================================================================
// Source: Waveshare wiki ESP32-S3-Touch-LCD-3.5, sample 03_camera_web_server
// Same FPC socket so OV2640 uses identical pin map to OV5640.

#define CAM_PIN_PWDN    -1
#define CAM_PIN_RESET   -1
#define CAM_PIN_XCLK    38
#define CAM_PIN_SIOD     8   // SCCB SDA (shared I2C bus with touch/IMU/RTC)
#define CAM_PIN_SIOC     7   // SCCB SCL
#define CAM_PIN_D7      21   // Y9
#define CAM_PIN_D6      39   // Y8
#define CAM_PIN_D5      40   // Y7
#define CAM_PIN_D4      42   // Y6
#define CAM_PIN_D3      46   // Y5
#define CAM_PIN_D2      48   // Y4
#define CAM_PIN_D1      47   // Y3
#define CAM_PIN_D0      45   // Y2
#define CAM_PIN_VSYNC   17
#define CAM_PIN_HREF    18
#define CAM_PIN_PCLK    41

// =====================================================================
// 3. STATE
// =====================================================================

static struct quirc *qr = nullptr;
static String        lastScan      = "";
static unsigned long lastScanMs    = 0;
static unsigned long lastWifiCheck = 0;
static bool          cameraReady   = false;

// =====================================================================
// 4. WIFI
// =====================================================================

// Throttle disconnect-event logging so a rejecting AP doesn't fill the
// Serial buffer with thousands of lines per minute. setAutoReconnect(true)
// in connectWifi() handles the actual retry; we just log occasionally.
static unsigned long _lastDisconnectLogMs = 0;

void onWifiEvent(WiFiEvent_t event) {
    switch (event) {
        case ARDUINO_EVENT_WIFI_STA_DISCONNECTED: {
            unsigned long now = millis();
            if (now - _lastDisconnectLogMs > 10000) {
                _lastDisconnectLogMs = now;
                Serial.println("WiFi event: DISCONNECTED (suppressing further logs for 10s)");
            }
            break;
        }
        case ARDUINO_EVENT_WIFI_STA_GOT_IP:
            Serial.printf("WiFi event: GOT_IP  IP=%s  RSSI=%d\n",
                          WiFi.localIP().toString().c_str(), WiFi.RSSI());
            break;
        default:
            break;
    }
}

bool connectWifi(unsigned long timeoutMs = 30000) {
    Serial.printf("Connecting to %s ", WIFI_SSID);
    WiFi.mode(WIFI_STA);
    WiFi.setSleep(false);
    WiFi.setAutoReconnect(true);
    WiFi.persistent(true);
    WiFi.setMinSecurity(WIFI_AUTH_WPA2_PSK);
    WiFi.onEvent(onWifiEvent);
    WiFi.begin(WIFI_SSID, WIFI_PASS);

    unsigned long start = millis();
    while (WiFi.status() != WL_CONNECTED && (millis() - start) < timeoutMs) {
        delay(500);
        Serial.print(".");
    }
    Serial.println();

    if (WiFi.status() == WL_CONNECTED) {
        Serial.printf("WiFi OK  IP=%s  RSSI=%d\n",
                      WiFi.localIP().toString().c_str(), WiFi.RSSI());
        return true;
    }
    Serial.println("WiFi FAILED");
    return false;
}

// Quick synchronous reconnect — called just before a POST when WiFi
// has dropped. Returns true if connected within timeoutMs.
bool ensureWifi(unsigned long timeoutMs = 5000) {
    if (WiFi.status() == WL_CONNECTED) return true;

    Serial.print("WiFi offline at POST time — reconnect ");
    WiFi.reconnect();
    unsigned long start = millis();
    while (WiFi.status() != WL_CONNECTED && (millis() - start) < timeoutMs) {
        delay(200);
        Serial.print(".");
    }
    Serial.println();
    bool ok = (WiFi.status() == WL_CONNECTED);
    if (ok) Serial.printf("Reconnected  RSSI=%d\n", WiFi.RSSI());
    else    Serial.println("Reconnect failed");
    return ok;
}

// =====================================================================
// 5. CAMERA INIT
// =====================================================================

bool initCamera() {
    camera_config_t config = {};
    config.ledc_channel = LEDC_CHANNEL_0;
    config.ledc_timer   = LEDC_TIMER_0;
    config.pin_d0       = CAM_PIN_D0;
    config.pin_d1       = CAM_PIN_D1;
    config.pin_d2       = CAM_PIN_D2;
    config.pin_d3       = CAM_PIN_D3;
    config.pin_d4       = CAM_PIN_D4;
    config.pin_d5       = CAM_PIN_D5;
    config.pin_d6       = CAM_PIN_D6;
    config.pin_d7       = CAM_PIN_D7;
    config.pin_xclk     = CAM_PIN_XCLK;
    config.pin_pclk     = CAM_PIN_PCLK;
    config.pin_vsync    = CAM_PIN_VSYNC;
    config.pin_href     = CAM_PIN_HREF;
    config.pin_sccb_sda = CAM_PIN_SIOD;
    config.pin_sccb_scl = CAM_PIN_SIOC;
    config.pin_pwdn     = CAM_PIN_PWDN;
    config.pin_reset    = CAM_PIN_RESET;
    config.xclk_freq_hz = 16000000;
    config.frame_size   = FRAMESIZE_QVGA;       // 320x240
    config.pixel_format = PIXFORMAT_RGB565;     // color viewfinder; converted to grayscale per-frame for quirc
    config.fb_count     = 2;
    config.fb_location  = CAMERA_FB_IN_PSRAM;
    config.grab_mode    = CAMERA_GRAB_LATEST;
    config.jpeg_quality = 12;

    esp_err_t err = esp_camera_init(&config);
    if (err != ESP_OK) {
        Serial.printf("Camera init FAILED: 0x%x\n", err);
        Serial.println("Verify CAM_PIN_* against Waveshare 3.5 official demo.");
        return false;
    }

    sensor_t *s = esp_camera_sensor_get();
    if (!s) {
        Serial.println("Sensor get failed");
        return false;
    }

    // Identify the sensor
    Serial.printf("Sensor PID=0x%02x  VER=0x%02x  MIDH=0x%02x  MIDL=0x%02x\n",
                  s->id.PID, s->id.VER, s->id.MIDH, s->id.MIDL);
    if (s->id.PID == 0x26) Serial.println("  -> OV2640 detected");
    else if (s->id.PID == 0x56) Serial.println("  -> OV5640 detected");
    else Serial.println("  -> Unknown sensor — config may not stick");

    // Configure the sensor — order matters, no reset (that knocks it back to defaults)
    s->set_pixformat(s, PIXFORMAT_RGB565);
    delay(100);
    s->set_framesize(s, FRAMESIZE_QVGA);
    delay(100);
    s->set_hmirror(s, 1);  // case is upside-down vs. sensor native
    s->set_vflip(s, 1);

    Serial.println("Camera initialized OK");

    Serial.println("Probing first frame...");
    camera_fb_t *fb = esp_camera_fb_get();
    if (fb) {
        Serial.printf("Probe frame: width=%d height=%d len=%lu format=%d\n",
                      fb->width, fb->height, (unsigned long)fb->len, (int)fb->format);
        size_t expected = (size_t)fb->width * fb->height * 2;  // RGB565 = 2 bytes/pixel
        if (fb->format == PIXFORMAT_RGB565 && fb->len < expected) {
            Serial.printf("WARNING: RGB565 buffer is %lu bytes but %ux%u needs %lu bytes\n",
                          (unsigned long)fb->len, fb->width, fb->height, (unsigned long)expected);
            Serial.println("Sensor did not honor RGB565 — likely still in compressed mode");
        }
        esp_camera_fb_return(fb);
    } else {
        Serial.println("Probe frame failed — esp_camera_fb_get returned null");
    }

    Serial.println("Warming up sensor (10 frames for auto-exposure)...");
    for (int i = 0; i < 10; i++) {
        camera_fb_t *warm = esp_camera_fb_get();
        if (warm) {
            esp_camera_fb_return(warm);
        }
        delay(40);
    }
    Serial.println("Sensor warmup complete");
    return true;
}

// =====================================================================
// 6. QUIRC INIT
// =====================================================================

bool initQuirc() {
    qr = quirc_new();
    if (!qr) {
        Serial.println("quirc_new failed");
        return false;
    }
    if (quirc_resize(qr, 320, 240) < 0) {
        Serial.println("quirc_resize failed");
        return false;
    }
    Serial.println("Quirc initialized");
    return true;
}

// Decode scratch buffers kept in BSS (~6.7 KB combined) so they don't
// blow the Arduino loopTask's 8 KB stack budget.
static struct quirc_code qrCode;
static struct quirc_data qrData;

bool tryDecodeQr(camera_fb_t *fb, String &outPayload) {
    if (!qr || !fb) return false;

    if (fb->format != PIXFORMAT_RGB565) return false;
    if (fb->width != 320 || fb->height != 240) return false;
    if (fb->len < (size_t)320 * 240 * 2) return false;

    uint8_t *image = quirc_begin(qr, nullptr, nullptr);
    if (!image) return false;

    // Camera outputs RGB565 in big-endian (high byte first in memory). Read
    // bytes directly rather than as uint16_t (which would be byte-swapped on
    // little-endian ESP32). We use the green channel (6 bits) as the luma
    // approximation — QR detection only cares about contrast, and green has
    // the highest bit-depth of the three channels in RGB565.
    const uint8_t *rgb = fb->buf;
    const size_t pixels = (size_t)320 * 240;
    for (size_t i = 0; i < pixels; i++) {
        uint8_t hi = rgb[i * 2];      // RRRRR GGG
        uint8_t lo = rgb[i * 2 + 1];  // GGG BBBBB
        // 6-bit green spans bits 5..0 of hi and bits 7..5 of lo
        uint8_t g6 = ((hi & 0x07) << 3) | (lo >> 5);
        // Scale 0..63 -> 0..255 for quirc (replicate top bits into low bits)
        image[i] = (uint8_t)((g6 << 2) | (g6 >> 4));
    }
    quirc_end(qr);

    int count = quirc_count(qr);
    for (int i = 0; i < count; i++) {
        quirc_extract(qr, i, &qrCode);
        if (quirc_decode(&qrCode, &qrData) == 0) {
            outPayload = String((const char *)qrData.payload);
            return true;
        }
    }
    return false;
}

// =====================================================================
// 7. API CALL
// =====================================================================

void postPunch(const String &rawScan) {
    if (!ensureWifi(5000)) {
        Serial.println("POST skipped — WiFi offline after retry");
        soundBadScan();
        lcdShowError("WiFi offline", "Please try again");
        return;
    }

    // HTTPS — ESP32 has no built-in CA store, so use WiFiClientSecure
    // with setInsecure() for v1. Production v2 should pin the actual
    // Azure App Service certificate chain.
    WiFiClientSecure tlsClient;
    tlsClient.setInsecure();
    tlsClient.setTimeout(HTTP_TIMEOUT_MS / 1000);

    HTTPClient http;
    http.begin(tlsClient, SERVER_URL);
    http.setTimeout(HTTP_TIMEOUT_MS);
    http.addHeader("Content-Type", "application/json");
    http.addHeader("X-Device-Secret", DEVICE_SECRET);

    JsonDocument doc;
    doc["rawScan"]    = rawScan;
    doc["terminalId"] = TERMINAL_ID;
    doc["scanMethod"] = "ESP32";

    String body;
    serializeJson(doc, body);

    Serial.printf("POST %s\n  body=%s\n", SERVER_URL, body.c_str());
    unsigned long postStart = millis();
    int code = http.POST(body);
    String resp = http.getString();
    http.end();
    unsigned long postElapsed = millis() - postStart;

    Serial.printf("HTTP %d  elapsed=%lu ms  respLen=%u\n",
                  code, postElapsed, (unsigned)resp.length());
    if (resp.length() > 400) {
        Serial.print("  resp(first 400)=");
        Serial.println(resp.substring(0, 400));
        Serial.printf("  ...truncated, total %u bytes\n", (unsigned)resp.length());
    } else {
        Serial.printf("  resp=%s\n", resp.c_str());
    }

    if (code == 200) {
        JsonDocument respDoc;
        DeserializationError jerr = deserializeJson(respDoc, resp);
        if (jerr == DeserializationError::Ok) {
            bool ok = respDoc["success"] | false;
            const char *msg     = respDoc["message"]            | "";
            const char *name    = respDoc["personName"]         | "";
            const char *type    = respDoc["scanType"]           | "";
            const char *display = respDoc["scanTypeDisplay"]    | "";
            const char *ptype   = respDoc["personTypeDisplay"]  | "";
            const char *photo   = respDoc["photoBase64"]        | "";
            size_t photoLen = strlen(photo);
            Serial.printf(ok ? "PUNCH OK: %s [%s] %s  photoB64Len=%u\n"
                              : "PUNCH FAIL: %s\n",
                          name, type, msg, (unsigned)photoLen);
            if (ok) {
                soundGoodScan();
                lcdShowSuccess(name, display, ptype, photo);
            } else {
                const char *errorCode = respDoc["errorCode"] | "";
                Serial.printf("Server returned success=false (code=%s): %s\n", errorCode, msg);
                soundBadScan();
                if (strcmp(errorCode, "NOT_FOUND") == 0) {
                    lcdShowError("Card not recognized", "Please see reception");
                } else if (strcmp(errorCode, "INVALID_FORMAT") == 0) {
                    lcdShowError("Invalid card", "Try again");
                } else {
                    lcdShowError(msg && *msg ? msg : "Server rejected", "");
                }
            }
        } else {
            Serial.printf("JSON parse FAILED: %s\n", jerr.c_str());
            Serial.print("  full body: ");
            Serial.println(resp);
            soundBadScan();
            lcdShowError("Server error", "Please try again");
        }
    } else if (code == 401) {
        Serial.println("Unauthorized — check DEVICE_SECRET matches App Service config");
        soundBadScan();
        lcdShowError("Device error", "Contact admin");
    } else if (code == 404) {
        Serial.println("Terminal not registered — check TERMINAL_ID matches TC_Terminals");
        soundBadScan();
        lcdShowError("Setup error", "Contact admin");
    } else if (code == 403) {
        Serial.println("Terminal inactive — check TC_Terminals.IsActive");
        soundBadScan();
        lcdShowError("Kiosk offline", "Contact admin");
    } else {
        Serial.printf("Unexpected HTTP %d\n", code);
        char buf[40];
        snprintf(buf, sizeof(buf), "Network error %d", code);
        soundBadScan();
        lcdShowError("Network error", buf);
    }
}

// =====================================================================
// 8. SCAN DISPATCH
// =====================================================================

void handleScan(const String &raw) {
    unsigned long now = millis();
    if (raw == lastScan && (now - lastScanMs) < SCAN_DEBOUNCE_MS) return;
    lastScan   = raw;
    lastScanMs = now;
    Serial.printf("\nSCAN [%s]\n", raw.c_str());
    lcdShowScanning(raw);
    postPunch(raw);
}

// =====================================================================
// 9. SETUP + LOOP
// =====================================================================

// LCD pins per Waveshare ESP32-S3-Touch-LCD-3.5 demo (08_gfx_helloworld).
// LCD_CS = -1 (the panel is permanently selected via board wiring).
// LCD_RST is on TCA9554 EXIO1, pulsed manually before gfx.init().
#define GFX_BL_PIN     6      // backlight (direct GPIO, active HIGH)
#define LCD_SPI_MISO   2
#define LCD_SPI_MOSI   1
#define LCD_SPI_SCLK   5
#define LCD_DC_PIN     3
#define LCD_CS_PIN    -1
#define LCD_RST_PIN   -1
#define LCD_HOR_RES   320
#define LCD_VER_RES   480

#define I2C_SDA_PIN    8
#define I2C_SCL_PIN    7

// Color constants. LovyanGFX provides TFT_BLACK etc. but we keep
// COL_* names so the rest of the sketch is unchanged.
#define COL_BLACK   0x0000
#define COL_WHITE   0xFFFF
#define COL_RED     0xF800
#define COL_GREEN   0x07E0
#define COL_BLUE    0x001F
#define COL_YELLOW  0xFFE0
#define COL_GRAY    0x7BEF

// Viewfinder rectangle (drawn from camera frames each loop while idle)
#define VIEWFINDER_X    0
#define VIEWFINDER_Y    0
#define VIEWFINDER_W    320
#define VIEWFINDER_H    240

// LovyanGFX panel + bus + backlight config for Waveshare ESP32-S3-Touch-LCD-3.5.
// Proven working with dummy_read_pixel=8 + BGR + invert=true.
class LGFX_Waveshare35 : public lgfx::LGFX_Device {
    lgfx::Panel_ST7796 _panel_instance;
    lgfx::Bus_SPI      _bus_instance;
    lgfx::Light_PWM    _light_instance;

public:
    LGFX_Waveshare35(void) {
        {
            auto cfg = _bus_instance.config();
            cfg.spi_host    = SPI2_HOST;
            cfg.spi_mode    = 0;
            cfg.freq_write  = 40000000;
            cfg.freq_read   = 16000000;
            cfg.spi_3wire   = false;
            cfg.use_lock    = true;
            cfg.dma_channel = SPI_DMA_CH_AUTO;
            cfg.pin_sclk    = LCD_SPI_SCLK;
            cfg.pin_mosi    = LCD_SPI_MOSI;
            cfg.pin_miso    = LCD_SPI_MISO;
            cfg.pin_dc      = LCD_DC_PIN;
            _bus_instance.config(cfg);
            _panel_instance.setBus(&_bus_instance);
        }
        {
            auto cfg = _panel_instance.config();
            cfg.pin_cs           = LCD_CS_PIN;
            cfg.pin_rst          = LCD_RST_PIN;
            cfg.pin_busy         = -1;
            cfg.panel_width      = LCD_HOR_RES;
            cfg.panel_height     = LCD_VER_RES;
            cfg.offset_x         = 0;
            cfg.offset_y         = 0;
            cfg.offset_rotation  = 0;
            cfg.dummy_read_pixel = 8;
            cfg.dummy_read_bits  = 1;
            cfg.readable         = false;
            cfg.invert           = true;
            cfg.rgb_order        = false;
            cfg.dlen_16bit       = false;
            cfg.bus_shared       = false;
            _panel_instance.config(cfg);
        }
        {
            auto cfg = _light_instance.config();
            cfg.pin_bl      = GFX_BL_PIN;
            cfg.invert      = false;
            cfg.freq        = 5000;
            cfg.pwm_channel = 7;
            _light_instance.config(cfg);
            _panel_instance.setLight(&_light_instance);
        }
        setPanel(&_panel_instance);
    }
};

LGFX_Waveshare35   gfx;
TCA9554            ioex(0x20);
XPowersPMU         pmu;

bool          lcdReady   = false;
unsigned long lcdResetMs = 0;
const unsigned long LCD_IDLE_TIMEOUT_MS = 6000;

// Pulse the LCD reset line through the TCA9554 expander
void lcdResetPulse() {
    ioex.write1(1, 1);
    delay(10);
    ioex.write1(1, 0);
    delay(10);
    ioex.write1(1, 1);
    delay(200);
}

// =====================================================================
// Audio (ES8311 codec + NS4150 PA + speaker via I2S)
// =====================================================================
// Status LED (2-pin anti-parallel bicolor: red one direction, green the other)
// =====================================================================
// Wiring: one LED leg through a 220-330 ohm resistor to LED_PIN_A,
// the other leg directly to LED_PIN_B. No ground wire needed - the two
// GPIOs alternate between HIGH and LOW to source/sink current through
// the LED. If colors come out swapped (red on success, green on failure),
// physically swap the two leads OR swap LED_PIN_A and LED_PIN_B below.
//
// Pin choice history:
//   - First tried GPIO 9 + 11. Board hung at "Connecting WiFi" with LED
//     installed. Cause: the 10K pull-up on GPIO 11 (R21, SD card socket)
//     leaks ~3.3V through the LED into GPIO 9, which floats to ~1.3V at
//     boot. GPIO 9 has alt functions tied to internal SPI peripherals and
//     the indeterminate level confused the bootloader/WiFi PHY init.
//   - Moved to GPIO 43 + 44 (U0TXD/U0RXD). With USBMode=hwcdc the chip
//     uses USB-CDC for Serial, so the UART0 pins are completely unused.
//     Neither pin has any onboard pull-up or peripheral connection. Both
//     are broken out on J8:
//       LED_PIN_A = GPIO 43 -> J8 pin 25
//       LED_PIN_B = GPIO 44 -> J8 pin 27
#define LED_PIN_A  43
#define LED_PIN_B  44

void ledInit() {
    pinMode(LED_PIN_A, OUTPUT);
    pinMode(LED_PIN_B, OUTPUT);
    digitalWrite(LED_PIN_A, LOW);
    digitalWrite(LED_PIN_B, LOW);
    Serial.printf("LED: bicolor on GPIO %d (via resistor) + GPIO %d\n",
                  LED_PIN_A, LED_PIN_B);
}

// Current flows LED_PIN_A -> LED -> LED_PIN_B (sunk to ground)
void ledGreen() {
    digitalWrite(LED_PIN_A, HIGH);
    digitalWrite(LED_PIN_B, LOW);
}

// Current flows LED_PIN_B -> LED -> LED_PIN_A (reverse direction)
void ledRed() {
    digitalWrite(LED_PIN_A, LOW);
    digitalWrite(LED_PIN_B, HIGH);
}

void ledOff() {
    digitalWrite(LED_PIN_A, LOW);
    digitalWrite(LED_PIN_B, LOW);
}

// =====================================================================
#define I2S_MCK_PIN        12
#define I2S_BCK_PIN        13
#define I2S_LRCK_PIN       15
#define I2S_DOUT_PIN       16   // data ESP -> codec
#define I2S_DIN_PIN        14   // data codec -> ESP (unused for tones)
#define AUDIO_SAMPLE_RATE  44100
#define AUDIO_MCLK_FREQ    (AUDIO_SAMPLE_RATE * 256)
#define AUDIO_VOLUME       70   // 0..100

I2SClass i2s;
bool     audioReady = false;

bool audioInit() {
    Serial.println("Audio: ES8311 codec init");
    es8311_handle_t es = es8311_create(I2C_NUM_0, ES8311_ADDRESS_0);
    if (!es) {
        Serial.println("Audio: es8311_create FAILED");
        return false;
    }
    es8311_clock_config_t clk = {};
    clk.mclk_inverted      = false;
    clk.sclk_inverted      = false;
    clk.mclk_from_mclk_pin = true;
    clk.mclk_frequency     = AUDIO_MCLK_FREQ;
    clk.sample_frequency   = AUDIO_SAMPLE_RATE;

    if (es8311_init(es, &clk, ES8311_RESOLUTION_16, ES8311_RESOLUTION_16) != ESP_OK) {
        Serial.println("Audio: es8311_init FAILED");
        return false;
    }
    es8311_voice_volume_set(es, AUDIO_VOLUME, NULL);
    es8311_microphone_config(es, false);
    es8311_voice_mute(es, false);  // make sure output isn't muted (default varies)

    // Enable the NS4150 power amplifier via TCA9554 EXIO7.
    // If no sound after boot test, try flipping PA_CTRL_ON_LEVEL from 1 to 0.
    #define PA_CTRL_ON_LEVEL 1
    ioex.pinMode1(7, OUTPUT);
    ioex.write1(7, PA_CTRL_ON_LEVEL);
    Serial.printf("Audio: PA_CTRL (EXIO7) set to %d\n", PA_CTRL_ON_LEVEL);

    Serial.println("Audio: I2S setup");
    i2s.setPins(I2S_BCK_PIN, I2S_LRCK_PIN, I2S_DOUT_PIN, I2S_DIN_PIN, I2S_MCK_PIN);
    if (!i2s.begin(I2S_MODE_STD, AUDIO_SAMPLE_RATE,
                   I2S_DATA_BIT_WIDTH_16BIT, I2S_SLOT_MODE_STEREO, I2S_STD_SLOT_BOTH)) {
        Serial.println("Audio: I2S begin FAILED");
        return false;
    }
    Serial.println("Audio: initialized OK");
    return true;
}

// Play a sine wave at frequency Hz for durationMs milliseconds.
// Generates stereo 16-bit PCM samples and pushes them through I2S.
void playTone(uint32_t frequency, uint32_t durationMs) {
    if (!audioReady) {
        Serial.println("playTone: audio not ready, skipping");
        return;
    }
    Serial.printf("playTone: %u Hz / %u ms\n", (unsigned)frequency, (unsigned)durationMs);

    const size_t chunkSamples = 256;
    int16_t      buf[chunkSamples * 2];   // stereo interleaved

    // PRIME the DMA with silence before the tone. After heavy camera/WiFi/
    // HTTP/LCD work the I2S DMA can be in a stale state; pushing 4 silence
    // buffers (~23 ms) ensures the codec has fresh samples flowing before
    // the first real audio byte arrives. 2 buffers used to be enough but
    // wasn't reliable post-scan.
    memset(buf, 0, sizeof(buf));
    for (int i = 0; i < 4; i++) {
        i2s.write((uint8_t *)buf, sizeof(buf));
    }

    uint32_t     totalSamples = (AUDIO_SAMPLE_RATE * durationMs) / 1000;
    float        angleStep    = (2.0f * (float)M_PI * frequency) / AUDIO_SAMPLE_RATE;
    float        angle        = 0.0f;
    size_t       totalWritten = 0;

    while (totalSamples > 0) {
        size_t chunk = (totalSamples < chunkSamples) ? totalSamples : chunkSamples;
        for (size_t j = 0; j < chunk; j++) {
            int16_t sample = (int16_t)(sinf(angle) * 22000.0f);
            buf[j * 2]     = sample;
            buf[j * 2 + 1] = sample;
            angle += angleStep;
            if (angle > 2.0f * (float)M_PI) angle -= 2.0f * (float)M_PI;
        }
        size_t bytes = chunk * 2 * sizeof(int16_t);
        size_t w = i2s.write((uint8_t *)buf, bytes);
        totalWritten += w;
        totalSamples -= chunk;
    }

    // Flush trailing silence so the last tone sample isn't cut short and
    // so the NS4150 PA settles cleanly before the DMA goes idle. 3 buffers
    // (~17 ms) instead of 1 - prevents the descending double-tone gap
    // click we saw with the old 220 Hz / 180 Hz pattern.
    memset(buf, 0, sizeof(buf));
    for (int i = 0; i < 3; i++) {
        i2s.write((uint8_t *)buf, sizeof(buf));
    }

    Serial.printf("playTone: wrote %u bytes\n", (unsigned)totalWritten);
}

void soundGoodScan() {
    // Green LED on for the duration of the result display; cleared by the
    // idle-return path in loop().
    ledGreen();
    // Rising two-note chime: A5 -> E6
    playTone(880, 80);
    playTone(1320, 120);
}

void soundBadScan() {
    // Red LED on for the duration of the result display; cleared by the
    // idle-return path in loop().
    ledRed();
    // Descending double-tone, both frequencies proven loud on the Waveshare
    // speaker. History:
    //   - Original: 220 -> 180 Hz (silent: below speaker's reproduction range)
    //   - Attempt 2: 660 -> 440 Hz (very quiet: speaker's low end rolls off
    //     sharply below ~700 Hz)
    //   - This version: 1320 -> 880 Hz, mirroring the good-scan rising
    //     pattern in reverse. Both notes are within the speaker's good
    //     response range; the descending direction makes it instantly
    //     distinguishable from the success chime.
    playTone(1320, 100);
    playTone(880, 200);
}

// Configure the AXP2101 PMIC. The Waveshare 3.5 board's LCD, touch,
// camera, and audio rails are all gated by LDOs that default to OFF
// after a hard power-cycle. We mirror the demo's enable list verbatim
// so every peripheral that the factory firmware powered up gets the
// same voltages here.
bool axp2101Init() {
    Serial.println("PMU: AXP2101 begin()");
    if (!pmu.begin(Wire, AXP2101_SLAVE_ADDRESS, I2C_SDA_PIN, I2C_SCL_PIN)) {
        Serial.println("PMU: AXP2101 not responding — LCD/camera may stay dark");
        return false;
    }
    Serial.printf("PMU: AXP2101 OK  chipId=0x%02X\n", pmu.getChipID());

    // Also try DC3 explicitly — on some Waveshare boards this is the
    // main LCD/panel rail, NOT the ALDOs. Demo doesn't set it because
    // it's normally already on, but if power state is lost we need to
    // re-enable it.
    pmu.setDC1Voltage(3300);
    pmu.setDC3Voltage(3300);

    pmu.setALDO1Voltage(3300);
    pmu.setALDO2Voltage(3300);
    pmu.setALDO3Voltage(3300);
    pmu.setALDO4Voltage(3300);
    pmu.setBLDO1Voltage(1500);
    pmu.setBLDO2Voltage(2800);
    pmu.setDLDO1Voltage(3300);
    pmu.setDLDO2Voltage(3300);

    pmu.enableDC3();    // main rail candidate
    pmu.enableALDO1();
    pmu.enableALDO2();
    pmu.enableALDO3();
    pmu.enableALDO4();
    pmu.enableBLDO1();
    pmu.enableBLDO2();
    pmu.enableDLDO1();
    pmu.enableDLDO2();

    // Diagnostic: dump every rail's state so we can see what's actually on
    Serial.println("PMU rail status:");
    Serial.printf("  DC1   : %s  %u mV\n", pmu.isEnableDC1()   ? "+" : "-", pmu.getDC1Voltage());
    Serial.printf("  DC2   : %s  %u mV\n", pmu.isEnableDC2()   ? "+" : "-", pmu.getDC2Voltage());
    Serial.printf("  DC3   : %s  %u mV\n", pmu.isEnableDC3()   ? "+" : "-", pmu.getDC3Voltage());
    Serial.printf("  DC4   : %s  %u mV\n", pmu.isEnableDC4()   ? "+" : "-", pmu.getDC4Voltage());
    Serial.printf("  DC5   : %s  %u mV\n", pmu.isEnableDC5()   ? "+" : "-", pmu.getDC5Voltage());
    Serial.printf("  ALDO1 : %s  %u mV\n", pmu.isEnableALDO1() ? "+" : "-", pmu.getALDO1Voltage());
    Serial.printf("  ALDO2 : %s  %u mV\n", pmu.isEnableALDO2() ? "+" : "-", pmu.getALDO2Voltage());
    Serial.printf("  ALDO3 : %s  %u mV\n", pmu.isEnableALDO3() ? "+" : "-", pmu.getALDO3Voltage());
    Serial.printf("  ALDO4 : %s  %u mV\n", pmu.isEnableALDO4() ? "+" : "-", pmu.getALDO4Voltage());
    Serial.printf("  BLDO1 : %s  %u mV\n", pmu.isEnableBLDO1() ? "+" : "-", pmu.getBLDO1Voltage());
    Serial.printf("  BLDO2 : %s  %u mV\n", pmu.isEnableBLDO2() ? "+" : "-", pmu.getBLDO2Voltage());
    Serial.printf("  DLDO1 : %s  %u mV\n", pmu.isEnableDLDO1() ? "+" : "-", pmu.getDLDO1Voltage());
    Serial.printf("  DLDO2 : %s  %u mV\n", pmu.isEnableDLDO2() ? "+" : "-", pmu.getDLDO2Voltage());

    Serial.println("PMU: rails enabled — LCD/camera/audio powered");
    return true;
}

// LovyanGFX uses 0-255 brightness via setBrightness(); we keep the
// percent API used elsewhere in the sketch for clarity.
void backlightSet(uint8_t percent) {
    if (percent > 100) percent = 100;
    uint8_t val = (percent * 255U) / 100U;
    gfx.setBrightness(val);
    Serial.printf("LCD: backlight set to %u%% (val=%u/255)\n",
                  (unsigned)percent, (unsigned)val);
}

void lcdInit() {
    Serial.println("LCD: TCA9554.begin()");
    bool tcaOk = ioex.begin();
    Serial.printf("LCD: TCA9554 begin -> %s\n", tcaOk ? "OK" : "FAIL");
    if (tcaOk) {
        ioex.pinMode1(1, OUTPUT);
        Serial.println("LCD: pulsing reset via TCA EXIO1");
        lcdResetPulse();
    } else {
        Serial.println("LCD: skipping reset pulse (no TCA9554) — panel may not init");
    }

    Serial.println("LCD: gfx.init()");
    bool gfxOk = gfx.init();
    Serial.printf("LCD: gfx.init() -> %s\n", gfxOk ? "OK" : "FAIL");
    if (!gfxOk) {
        Serial.println("LCD: LovyanGFX init failed");
        return;
    }

    gfx.setRotation(2);  // flipped 180 degrees - camera at bottom of case
    gfx.fillScreen(COL_BLACK);
    backlightSet(80);

    lcdReady = true;
    Serial.println("LCD initialized OK — ready for draw calls");
}

// Map RSSI (dBm, negative) to a 0-4 bar count for the corner indicator.
int rssiToBars(int rssi) {
    if (rssi >= -55) return 4;
    if (rssi >= -65) return 3;
    if (rssi >= -75) return 2;
    if (rssi >= -85) return 1;
    return 0;
}

void lcdDrawWifiStatus() {
    if (!lcdReady) return;

    // 80x24 status area in the top-right corner
    const int x = LCD_HOR_RES - 84;
    const int y = 6;

    // Clear behind the indicator
    gfx.fillRect(x, y, 80, 24, COL_BLACK);

    if (WiFi.status() != WL_CONNECTED) {
        gfx.setTextColor(COL_RED);
        gfx.setTextSize(2);
        gfx.setCursor(x + 4, y + 4);
        gfx.print("WiFi --");
        return;
    }

    int rssi = WiFi.RSSI();
    int bars = rssiToBars(rssi);

    // 4 vertical bars next to "WiFi"
    int bx = x + 56;
    int by = y + 20;
    for (int i = 0; i < 4; i++) {
        int barH = 4 + i * 4;
        uint16_t col = (i < bars) ? COL_GREEN : 0x39E7; // dark gray for inactive
        gfx.fillRect(bx + i * 6, by - barH, 4, barH, col);
    }

    gfx.setTextColor(COL_WHITE);
    gfx.setTextSize(2);
    gfx.setCursor(x, y + 4);
    gfx.printf("%ddB", rssi);
}

void lcdShowIdle() {
    if (!lcdReady) return;
    gfx.fillScreen(COL_BLACK);

    // Top 320x240 is the live camera viewfinder (drawn each loop iteration).
    // Outline the active region so it is visible before the first frame.
    gfx.drawRect(0, 0, VIEWFINDER_W, VIEWFINDER_H, COL_GRAY);

    // Blue instruction bar below the viewfinder.
    // "Scan ID QRcode" at text size 3 is 14 chars * 18 px = 252 px wide,
    // so we left-pad by ~34 px to center it in the 320 px screen width.
    const int barY = 250;
    const int barH = 40;
    gfx.fillRect(0, barY, LCD_HOR_RES, barH, COL_BLUE);
    gfx.setTextColor(COL_WHITE);
    gfx.setTextSize(3);
    gfx.setCursor(34, barY + 8);
    gfx.print("Scan ID QRcode");

    // Filled downward arrow pointing to the camera (camera lives at the
    // bottom of the case; after the 180-deg LCD rotation, "down" on the
    // visible screen corresponds to the camera's physical position).
    const int arrowCenterX     = LCD_HOR_RES / 2;
    const int arrowShaftTop    = 310;
    const int arrowShaftBottom = 380;
    const int arrowShaftHalf   = 15;   // 30 px wide shaft
    const int arrowHeadHalf    = 50;   // 100 px wide head
    const int arrowHeadTipY    = 440;
    gfx.fillRect(arrowCenterX - arrowShaftHalf, arrowShaftTop,
                 arrowShaftHalf * 2, arrowShaftBottom - arrowShaftTop, COL_BLUE);
    gfx.fillTriangle(arrowCenterX - arrowHeadHalf, arrowShaftBottom,
                     arrowCenterX + arrowHeadHalf, arrowShaftBottom,
                     arrowCenterX, arrowHeadTipY, COL_BLUE);

    lcdDrawWifiStatus();
}

void lcdShowBoot(const char *msg) {
    if (!lcdReady) return;
    gfx.fillScreen(COL_BLACK);
    gfx.setTextColor(COL_WHITE);
    gfx.setTextSize(2);
    gfx.setCursor(20, 220);
    gfx.println("TimeClock Kiosk");
    gfx.setTextColor(COL_GRAY);
    gfx.setCursor(20, 260);
    gfx.println(msg);
}

void lcdShowScanning(const String &payload) {
    if (!lcdReady) return;
    gfx.fillScreen(COL_BLACK);
    gfx.setTextColor(COL_YELLOW);
    gfx.setTextSize(4);
    gfx.setCursor(40, 160);
    gfx.println("READING");

    int lastPipe = payload.lastIndexOf('|');
    String label = (lastPipe >= 0 && lastPipe < (int)payload.length() - 1)
                       ? payload.substring(lastPipe + 1)
                       : payload;
    if (label.length() > 14) label = label.substring(0, 14);

    gfx.setTextColor(COL_WHITE);
    gfx.setTextSize(3);
    gfx.setCursor(40, 230);
    gfx.print("ID: ");
    gfx.println(label);

    gfx.setTextColor(COL_GRAY);
    gfx.setTextSize(2);
    gfx.setCursor(40, 320);
    gfx.println("Checking server...");
}

// =====================================================================
// Live viewfinder (camera QVGA grayscale -> LCD RGB565)
// (constants moved to LCD section near top of file for early visibility)
// =====================================================================

// Push a 320x240 RGB565 camera frame onto the top of the LCD. The camera
// driver writes pixel bytes in the order the panel expects, so we send the
// buffer through pushImage with setSwapBytes(false) - empirically confirmed
// 2026-05-22 that setSwapBytes(true) produced a solarized image (the byte
// swap was unwanted, channel bits got scrambled).
// Called from loop() while on the idle screen so users see what the camera
// sees and can center their QR code over the lens.
void lcdDrawViewfinder(camera_fb_t *fb) {
    if (!lcdReady || !fb || !fb->buf) return;
    if (fb->format != PIXFORMAT_RGB565) return;
    if (fb->width != VIEWFINDER_W || fb->height != VIEWFINDER_H) return;

    gfx.startWrite();
    gfx.setSwapBytes(false);
    gfx.pushImage(VIEWFINDER_X, VIEWFINDER_Y, VIEWFINDER_W, VIEWFINDER_H,
                  (const uint16_t *)fb->buf);
    gfx.endWrite();
}

void lcdDrawPhotoFromBase64(const char *b64, int x, int y) {
    if (!lcdReady || !b64 || !*b64) return;
    size_t encLen = strlen(b64);
    size_t maxOut = (encLen / 4) * 3 + 4;

    uint8_t *jpeg = (uint8_t *)heap_caps_malloc(maxOut, MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT);
    if (!jpeg) {
        jpeg = (uint8_t *)heap_caps_malloc(maxOut, MALLOC_CAP_8BIT);
    }
    if (!jpeg) {
        Serial.printf("Photo: malloc %u failed\n", (unsigned)maxOut);
        return;
    }

    size_t outLen = 0;
    int rc = mbedtls_base64_decode(jpeg, maxOut, &outLen,
                                   (const unsigned char *)b64, encLen);
    if (rc != 0) {
        Serial.printf("Photo: base64 decode failed rc=%d encLen=%u\n", rc, (unsigned)encLen);
        heap_caps_free(jpeg);
        return;
    }
    Serial.printf("Photo: decoded %u bytes JPEG\n", (unsigned)outLen);
    gfx.drawJpg(jpeg, outLen, x, y);
    heap_caps_free(jpeg);
}

void lcdShowSuccess(const char *name, const char *scanDisplay, const char *personType, const char *photoBase64) {
    if (!lcdReady) return;
    gfx.fillScreen(COL_BLACK);
    gfx.fillRect(0, 0, LCD_HOR_RES, 60, COL_GREEN);
    gfx.setTextColor(COL_BLACK);
    gfx.setTextSize(3);
    gfx.setCursor(20, 16);
    gfx.println(scanDisplay && *scanDisplay ? scanDisplay : "OK");

    if (photoBase64 && *photoBase64) {
        lcdDrawPhotoFromBase64(photoBase64, 60, 80);
    } else {
        gfx.fillRect(60, 80, 200, 200, COL_GRAY);
        gfx.setTextColor(COL_WHITE);
        gfx.setTextSize(2);
        gfx.setCursor(110, 170);
        gfx.println("NO PHOTO");
    }

    gfx.setTextColor(COL_WHITE);
    gfx.setTextSize(3);
    gfx.setCursor(20, 310);
    gfx.println(name && *name ? name : "Welcome");

    gfx.setTextColor(COL_GRAY);
    gfx.setTextSize(2);
    gfx.setCursor(20, 370);
    gfx.println(personType && *personType ? personType : "");

    lcdResetMs = millis() + LCD_IDLE_TIMEOUT_MS;
}

// Draw a yellow filled warning triangle with a black "!" inside.
// Centered horizontally; vertical position controlled by caller.
static void drawWarningIcon(int centerX, int topY, int height) {
    // Triangle: apex on top, base 1.0x the height wide
    int half = height / 2;
    gfx.fillTriangle(centerX,        topY,
                     centerX - half, topY + height,
                     centerX + half, topY + height,
                     COL_YELLOW);
    // Black "!" inside: bar + dot. Sized as fractions of triangle height.
    int barW = height / 10;             // ~12 px wide for height=120
    int barH = (height * 45) / 100;     // ~54 px tall
    int barTopY = topY + (height * 25) / 100;
    gfx.fillRect(centerX - barW / 2, barTopY, barW, barH, COL_BLACK);
    // Dot below the bar
    int dotTopY = barTopY + barH + (height / 12);
    gfx.fillRect(centerX - barW / 2, dotTopY, barW, barW, COL_BLACK);
}

void lcdShowError(const char *title, const char *subtitle) {
    if (!lcdReady) return;
    gfx.fillScreen(COL_BLACK);

    // -------- Red header bar with friendly "TRY AGAIN" --------
    gfx.fillRect(0, 0, LCD_HOR_RES, 70, COL_RED);
    gfx.setTextColor(COL_WHITE);
    gfx.setTextSize(4);
    // "TRY AGAIN" = 9 chars * 24 px = 216 px wide; center at x=52
    gfx.setCursor(52, 18);
    gfx.println("TRY AGAIN");

    // -------- Yellow warning triangle with black "!" --------
    drawWarningIcon(LCD_HOR_RES / 2, 90, 130);

    // -------- Title (white, size 2 so longer text fits in 320 px) --------
    gfx.setTextColor(COL_WHITE);
    gfx.setTextSize(2);
    if (title && *title) {
        // Center horizontally based on character count (size 2 = 12 px/char)
        int titleLen = (int)strlen(title);
        int titleX = (LCD_HOR_RES - titleLen * 12) / 2;
        if (titleX < 4) titleX = 4;
        gfx.setCursor(titleX, 250);
        gfx.println(title);
    }

    // -------- Subtitle (gray, size 2) --------
    if (subtitle && *subtitle) {
        gfx.setTextColor(COL_GRAY);
        gfx.setTextSize(2);
        int subLen = (int)strlen(subtitle);
        int subX = (LCD_HOR_RES - subLen * 12) / 2;
        if (subX < 4) subX = 4;
        gfx.setCursor(subX, 295);
        gfx.println(subtitle);
    }

    lcdResetMs = millis() + LCD_IDLE_TIMEOUT_MS;
}

void setup() {
    Serial.begin(115200);
    delay(1500);
    Serial.println();
    Serial.println("============================================");
    Serial.printf( "TimeClock Kiosk %s  TerminalId=%d\n", FIRMWARE_VER, TERMINAL_ID);
    Serial.println("============================================");

    // Initialize the I2C bus ONCE for all the chips that share it:
    // AXP2101 (0x34), TCA9554 (0x20), touch, IMU, RTC, audio codec.
    Serial.println("I2C: Wire.begin(SDA=8, SCL=7)");
    Wire.begin(I2C_SDA_PIN, I2C_SCL_PIN);
    delay(50);

    // CRITICAL: enable LCD/camera/audio power rails on the AXP2101 PMIC
    // before touching any peripheral. After a hard power-cycle these
    // default to OFF; without this the LCD stays completely dark.
    axp2101Init();
    delay(100);  // let the rails settle

    // Initialize the LCD so the user has something to look at while
    // WiFi + camera come up.
    lcdInit();
    lcdShowBoot("Booting...");

    // Status LED (independent of any peripheral, just two GPIOs)
    ledInit();

    // Audio (ES8311 codec + I2S). After TCA9554 is up so PA enable works.
    audioReady = audioInit();
    if (audioReady) {
        Serial.println("Audio: playing 880 Hz / 200 ms boot test tone");
        playTone(880, 200);
        Serial.println("Audio: boot test tone done");
    }

    lcdShowBoot("Connecting WiFi...");
    if (!connectWifi()) {
        Serial.println("Continuing without WiFi — will retry in loop");
        lcdShowBoot("WiFi offline");
    } else {
        lcdShowBoot("WiFi OK");
    }

    lcdShowBoot("Starting camera...");
    cameraReady = initCamera();
    if (cameraReady) {
        if (!initQuirc()) {
            Serial.println("Quirc init failed — disabling scan");
            cameraReady = false;
            lcdShowError("QR init failed", "Power-cycle kiosk");
        } else {
            Serial.println("Ready. Present a badge to the camera.");
            lcdShowIdle();
        }
    } else {
        Serial.println("Camera failed — fix pin mapping and reflash.");
        lcdShowError("Camera init failed", "Check connection");
    }
}

void loop() {
    unsigned long now = millis();
    if (WiFi.status() != WL_CONNECTED && (now - lastWifiCheck) > WIFI_RETRY_MS) {
        lastWifiCheck = now;
        Serial.println("WiFi reconnect attempt...");
        WiFi.reconnect();
    }

    // Auto-return-to-idle after a successful or failed result has been shown
    if (lcdResetMs > 0 && now >= lcdResetMs) {
        lcdResetMs = 0;
        ledOff();
        lcdShowIdle();
    }

    // Refresh the WiFi status indicator every 5 seconds while on the
    // idle screen (when no result is being displayed).
    static unsigned long _lastWifiLcdRefreshMs = 0;
    if (lcdResetMs == 0 && lcdReady && (now - _lastWifiLcdRefreshMs > 5000)) {
        _lastWifiLcdRefreshMs = now;
        lcdDrawWifiStatus();
    }

    if (!cameraReady) {
        delay(1000);
        return;
    }

    if (lcdResetMs > 0) {
        camera_fb_t *drainFb = esp_camera_fb_get();
        if (drainFb) esp_camera_fb_return(drainFb);
        delay(50);
        return;
    }

    camera_fb_t *fb = esp_camera_fb_get();
    if (!fb) {
        delay(50);
        return;
    }

    // Live viewfinder - draws the grayscale frame onto the top of the LCD
    // before quirc runs. Only reached when lcdResetMs == 0 (idle screen).
    lcdDrawViewfinder(fb);

    String payload;
    if (tryDecodeQr(fb, payload)) {
        handleScan(payload);
    }

    esp_camera_fb_return(fb);
}
