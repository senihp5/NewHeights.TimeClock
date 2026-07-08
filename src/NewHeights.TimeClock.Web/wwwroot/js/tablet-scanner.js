// tablet-scanner.js
// 2026-07-08 (Phase 2): Camera + QR scanner for the tablet kiosk route
// (/kiosk/tablet/{terminalCode}). Called from KioskTablet.razor via
// IJSRuntime. Uses the browser-native BarcodeDetector API on Chromium
// 83+ (all Android Chrome / Edge on tablets shipped in the last few
// years); falls back to jsQR loaded from a CDN if BarcodeDetector is
// missing.
//
// Public API (exposed on window.tabletScanner):
//   start(videoEl, dotNetRef, photoEnabled, photoVideoEl)
//   stop()
//
// videoEl        — <video> element bound to rear camera.
// dotNetRef      — DotNetObjectReference to the KioskTablet page. On a
//                  successful QR decode, we invoke OnQrScanned(payload,
//                  photoBase64OrNull) on it via .invokeMethodAsync.
// photoEnabled   — boolean; when true also opens the front camera and
//                  captures a small JPEG per scan.
// photoVideoEl   — <video> for the front camera. Ignored when
//                  photoEnabled = false.
//
// Debounce: 3 seconds after a successful decode. The C# side ALSO
// enforces its own 3-second debounce; this is the client-side quick
// win so we don't hammer the SignalR circuit with rapid duplicates.

(function () {
    'use strict';

    // ── State ─────────────────────────────────────────────────────────
    let rearStream = null;         // MediaStream from rear camera
    let frontStream = null;        // MediaStream from front camera (photo)
    let rearVideoEl = null;
    let frontVideoEl = null;
    let dotNetRef = null;
    let photoEnabled = false;

    let running = false;
    let animationFrameId = null;
    let barcodeDetector = null;    // Native or jsQR shim
    let usingJsQrFallback = false;

    let lastScanAt = 0;            // Timestamp (ms since epoch)
    const DEBOUNCE_MS = 3000;

    // Debug console via existing kioskInterop pattern from reception page.
    // If the page doesn't provide it, log() is a no-op.
    function log(msg) {
        if (window.console && window.console.log) {
            window.console.log('[TABLET_SCANNER] ' + msg);
        }
    }

    // ── Camera open ───────────────────────────────────────────────────
    async function openCamera(facingMode) {
        // Prefer environment (rear) or user (front). Some low-end tablets
        // only expose a single camera and will happily hand us that
        // one regardless of the constraint — acceptable degradation.
        const constraints = {
            audio: false,
            video: {
                facingMode: { ideal: facingMode },
                width:  { ideal: 1280 },
                height: { ideal: 960 }
            }
        };
        return await navigator.mediaDevices.getUserMedia(constraints);
    }

    // ── BarcodeDetector shim over jsQR (lazy-loaded from CDN) ─────────
    async function loadJsQr() {
        return new Promise((resolve, reject) => {
            if (window.jsQR) { resolve(window.jsQR); return; }
            const s = document.createElement('script');
            s.src = 'https://cdn.jsdelivr.net/npm/jsqr@1.4.0/dist/jsQR.js';
            s.async = true;
            s.onload = () => resolve(window.jsQR);
            s.onerror = () => reject(new Error('Failed to load jsQR'));
            document.head.appendChild(s);
        });
    }

    async function initDetector() {
        if ('BarcodeDetector' in window) {
            try {
                const formats = await window.BarcodeDetector.getSupportedFormats();
                if (formats && formats.indexOf('qr_code') !== -1) {
                    barcodeDetector = new window.BarcodeDetector({ formats: ['qr_code'] });
                    log('Using native BarcodeDetector');
                    return;
                }
            } catch (e) {
                log('Native BarcodeDetector init failed: ' + e.message);
            }
        }

        // Fallback: jsQR-based shim that mimics BarcodeDetector.detect().
        log('Falling back to jsQR');
        const jsQR = await loadJsQr();
        usingJsQrFallback = true;

        const canvas = document.createElement('canvas');
        const ctx = canvas.getContext('2d', { willReadFrequently: true });

        barcodeDetector = {
            detect: async function (videoEl) {
                if (!videoEl.videoWidth || !videoEl.videoHeight) return [];
                canvas.width  = videoEl.videoWidth;
                canvas.height = videoEl.videoHeight;
                ctx.drawImage(videoEl, 0, 0, canvas.width, canvas.height);
                const imageData = ctx.getImageData(0, 0, canvas.width, canvas.height);
                const code = jsQR(imageData.data, canvas.width, canvas.height, {
                    inversionAttempts: 'attemptBoth'
                });
                if (code && code.data) {
                    return [{ rawValue: code.data }];
                }
                return [];
            }
        };
    }

    // ── Photo snapshot (front camera) ─────────────────────────────────
    function captureFrontPhoto() {
        if (!photoEnabled || !frontVideoEl) return null;
        if (!frontVideoEl.videoWidth || !frontVideoEl.videoHeight) return null;
        try {
            const canvas = document.createElement('canvas');
            // Keep photo small — enough for verification, not full res.
            const targetWidth = 320;
            const scale = targetWidth / frontVideoEl.videoWidth;
            canvas.width  = targetWidth;
            canvas.height = Math.floor(frontVideoEl.videoHeight * scale);
            const ctx = canvas.getContext('2d');
            ctx.drawImage(frontVideoEl, 0, 0, canvas.width, canvas.height);
            // JPEG 0.75 quality → typical <20KB payload.
            const dataUrl = canvas.toDataURL('image/jpeg', 0.75);
            // Strip the "data:image/jpeg;base64," prefix so C# gets pure base64.
            const commaIdx = dataUrl.indexOf(',');
            return commaIdx >= 0 ? dataUrl.substring(commaIdx + 1) : null;
        } catch (e) {
            log('captureFrontPhoto failed: ' + e.message);
            return null;
        }
    }

    // ── Optional beep on successful scan ──────────────────────────────
    let beepAudio = null;
    function playBeep() {
        try {
            if (!beepAudio) {
                beepAudio = new Audio('/sounds/scan-beep.mp3');
                beepAudio.volume = 0.6;
            }
            // Reset in case previous play is still finishing.
            beepAudio.currentTime = 0;
            const p = beepAudio.play();
            if (p && p.catch) { p.catch(() => { /* autoplay policy blocked */ }); }
        } catch (e) { /* silent */ }
    }

    // ── Decode loop ───────────────────────────────────────────────────
    async function decodeTick() {
        if (!running) return;

        try {
            const results = barcodeDetector && rearVideoEl.readyState >= 2
                ? await barcodeDetector.detect(rearVideoEl)
                : [];

            if (results && results.length > 0) {
                const now = Date.now();
                if (now - lastScanAt >= DEBOUNCE_MS) {
                    const payload = results[0].rawValue || '';
                    if (payload) {
                        lastScanAt = now;
                        log('QR decoded: ' + payload.substring(0, 40));
                        playBeep();

                        const photo = captureFrontPhoto();
                        try {
                            await dotNetRef.invokeMethodAsync('OnQrScanned', payload, photo);
                        } catch (e) {
                            log('OnQrScanned invoke failed: ' + e.message);
                        }
                    }
                }
            }
        } catch (e) {
            // Detector can throw transiently between frames — ignore + continue.
            log('decode tick error: ' + e.message);
        }

        if (running) {
            // Native BarcodeDetector is cheap; jsQR is heavier. Throttle
            // fallback path to ~10fps by using setTimeout instead of rAF.
            if (usingJsQrFallback) {
                setTimeout(decodeTick, 100);
            } else {
                animationFrameId = requestAnimationFrame(decodeTick);
            }
        }
    }

    // ── Public entry points ───────────────────────────────────────────
    async function start(videoEl, netRef, photoOn, photoVideoElOrNull) {
        if (running) {
            log('start called while already running — ignoring');
            return;
        }

        rearVideoEl = videoEl;
        dotNetRef = netRef;
        photoEnabled = !!photoOn;
        frontVideoEl = photoOn ? photoVideoElOrNull : null;

        try {
            rearStream = await openCamera('environment');
            rearVideoEl.srcObject = rearStream;
            await rearVideoEl.play();
            log('Rear camera opened');

            if (photoEnabled && frontVideoEl) {
                try {
                    frontStream = await openCamera('user');
                    frontVideoEl.srcObject = frontStream;
                    await frontVideoEl.play();
                    log('Front camera opened for photo capture');
                } catch (e) {
                    log('Front camera unavailable: ' + e.message + ' — photo capture disabled for this session');
                    photoEnabled = false;
                }
            }

            await initDetector();

            running = true;
            decodeTick();
        } catch (e) {
            log('start failed: ' + e.message);
            stop();
            throw e;
        }
    }

    function stop() {
        running = false;
        if (animationFrameId) {
            cancelAnimationFrame(animationFrameId);
            animationFrameId = null;
        }
        if (rearStream) {
            rearStream.getTracks().forEach(t => t.stop());
            rearStream = null;
        }
        if (frontStream) {
            frontStream.getTracks().forEach(t => t.stop());
            frontStream = null;
        }
        rearVideoEl = null;
        frontVideoEl = null;
        dotNetRef = null;
        barcodeDetector = null;
        usingJsQrFallback = false;
        log('Scanner stopped');
    }

    function getUserAgent() {
        return navigator.userAgent || '';
    }

    window.tabletScanner = { start: start, stop: stop, getUserAgent: getUserAgent };
})();
