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
    let pairingMode = false;       // true → route decodes to OnPairingCodeScanned

    let lastScanAt = 0;            // Timestamp (ms since epoch) — global rapid-frame guard
    const DEBOUNCE_MS = 3000;

    // 2026-07-08: Per-badge client-side debounce. Matches the server-side
    // 60-second min-time-between-punches so we never send a redundant scan
    // that the server would only bounce back as TOO_SOON. Without this,
    // a rapid re-scan of the same badge sent both scans within 3s: the
    // server's TOO_SOON response (fast — one lookup) beat the success
    // response (slow — DB writes), so the user saw "just scanned" BEFORE
    // the welcome card. Different badges scan freely; only same-badge
    // re-scans within 60s are dropped client-side.
    const lastScanByPayload = new Map(); // payload → timestamp (ms)
    const PER_PAYLOAD_DEBOUNCE_MS = 60000;

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
                        if (pairingMode) {
                            // Only accept QRs that carry the pairing prefix.
                            // Silently ignore any other QR (badge, ad, random
                            // poster) so field IT can't accidentally pair.
                            if (payload.indexOf(PAIR_PREFIX) === 0) {
                                lastScanAt = now;
                                const code = payload.substring(PAIR_PREFIX.length);
                                log('Pairing QR decoded: ' + code);
                                playBeep();
                                try {
                                    await dotNetRef.invokeMethodAsync('OnPairingCodeScanned', code);
                                } catch (e) {
                                    log('OnPairingCodeScanned invoke failed: ' + e.message);
                                }
                            }
                        } else {
                            // 2026-07-08: Per-badge debounce. Silently drop
                            // any re-scan of the same payload within 60s so
                            // the server never sees the redundant round-trip
                            // (and the user never sees the TOO_SOON toast
                            // beat the actual welcome card back to the UI).
                            const lastForBadge = lastScanByPayload.get(payload) || 0;
                            if (now - lastForBadge < PER_PAYLOAD_DEBOUNCE_MS) {
                                log('Same-badge re-scan dropped client-side ('
                                    + Math.round((now - lastForBadge) / 1000) + 's ago)');
                            } else {
                                lastScanAt = now;
                                lastScanByPayload.set(payload, now);
                                log('QR decoded: ' + payload.substring(0, 40));
                                playBeep();
                                // A successful scan counts as activity — reset the idle dim.
                                armIdleDimTimer();

                                const photo = captureFrontPhoto();
                                try {
                                    await dotNetRef.invokeMethodAsync('OnQrScanned', payload, photo);
                                } catch (e) {
                                    log('OnQrScanned invoke failed: ' + e.message);
                                }
                            }
                        }
                    }
                }
            }
        } catch (e) {
            // Detector can throw transiently between frames — ignore + continue.
            log('decode tick error: ' + e.message);
        }

        if (running) {
            // 2026-07-08 (thermal tuning C): throttle BOTH paths to 10fps.
            // A QR only needs one clean frame to decode — 60fps via
            // requestAnimationFrame was overkill and kept the SoC hot on
            // the Pritom P7 during long runs. 100ms interval cuts CPU
            // ~85% with no observable scan-latency change.
            setTimeout(decodeTick, 100);
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
        pairingMode = false;

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
        pairingMode = false;
        log('Scanner stopped');
    }

    function getUserAgent() {
        return navigator.userAgent || '';
    }

    // ── Kiosk display-mode helpers (2026-07-08 Phase 2c) ──────────────
    //
    // enterKioskDisplayMode does three things, each best-effort:
    //   1. Request fullscreen so browser chrome (if any) hides.
    //   2. Lock orientation to landscape.
    //   3. Acquire a screen Wake Lock so the tablet doesn't sleep.
    //
    // All three fail silently on browsers that don't support them
    // (older Android WebView, some Chromium forks). The Wake Lock is
    // released automatically when the page is hidden and re-acquired
    // when it comes back — the visibility handler below rearms it.

    let wakeLockSentinel = null;

    async function acquireWakeLock() {
        try {
            if ('wakeLock' in navigator && !wakeLockSentinel) {
                wakeLockSentinel = await navigator.wakeLock.request('screen');
                wakeLockSentinel.addEventListener('release', () => {
                    wakeLockSentinel = null;
                    log('Wake Lock released');
                });
                log('Wake Lock acquired');
            }
        } catch (e) {
            log('Wake Lock request failed: ' + e.message);
        }
    }

    // Rearm Wake Lock whenever the tab regains visibility (Android often
    // releases it during backgrounding, screen-off, or lock-screen).
    document.addEventListener('visibilitychange', () => {
        if (document.visibilityState === 'visible') {
            acquireWakeLock();
        }
    });

    async function enterKioskDisplayMode() {
        // 1. Request fullscreen. Requires a user gesture in most browsers,
        //    but in TWA / kiosk-mode contexts this often succeeds silently.
        try {
            if (document.documentElement.requestFullscreen && !document.fullscreenElement) {
                await document.documentElement.requestFullscreen();
                log('Fullscreen requested');
            }
        } catch (e) {
            log('Fullscreen request failed: ' + e.message);
        }

        // 2. Lock orientation to landscape. Requires fullscreen on most
        //    Chromium browsers; that's why we do (1) first.
        try {
            if (screen.orientation && screen.orientation.lock) {
                await screen.orientation.lock('landscape');
                log('Orientation locked to landscape');
            }
        } catch (e) {
            log('Orientation lock failed: ' + e.message);
        }

        // 3. Acquire Wake Lock (screen keep-alive). This is the practical
        //    workaround for off-brand tablets whose OEM ignores Intune's
        //    Power Settings policy.
        await acquireWakeLock();
    }

    // ── Pairing / localStorage helpers (Phase 2b — 2026-07-08) ────────
    //
    // Single Intune policy points all tablets at /kiosk/tablet (no code).
    // The tablet reads localStorage on load: if a paired TerminalCode
    // exists, redirect to /kiosk/tablet/{code}. Otherwise start a
    // pairing-mode scan that only accepts QRs prefixed nhkiosk-pair:
    // (the admin page /admin/kiosks generates these). Unpair happens
    // via Intune factory reset (v1) — no in-app clear UI yet.
    const LS_KEY = 'nhKioskTerminalCode';
    const PAIR_PREFIX = 'nhkiosk-pair:';

    function getPairedCode() {
        try { return window.localStorage.getItem(LS_KEY); }
        catch { return null; }
    }

    function setPairedCode(code) {
        try { window.localStorage.setItem(LS_KEY, code); return true; }
        catch { return false; }
    }

    function clearPairedCode() {
        try { window.localStorage.removeItem(LS_KEY); return true; }
        catch { return false; }
    }

    // Same shape as start(...) but the decode callback filters QR
    // content to the pairing prefix and hands the raw code (prefix
    // stripped) to the C# side. Random QRs (badges, ads, etc.) are
    // silently ignored so field IT can't accidentally pair a tablet
    // by pointing it at a random poster.
    async function startPairing(videoEl, netRef) {
        // Reuse the same scanner startup path but override the QR
        // handler through the JSInvokable callback contract — the C#
        // side calls a different method for pairing decode.
        rearVideoEl = videoEl;
        dotNetRef = netRef;
        photoEnabled = false;
        frontVideoEl = null;
        pairingMode = true;

        try {
            rearStream = await openCamera('environment');
            rearVideoEl.srcObject = rearStream;
            await rearVideoEl.play();
            log('Rear camera opened (pairing mode)');

            await initDetector();

            running = true;
            decodeTick();
        } catch (e) {
            log('startPairing failed: ' + e.message);
            stop();
            throw e;
        }
    }

    // ── Admin-side helper: render a pairing QR into a target div ──────
    //
    // Called from KioskTerminals.razor via IJSRuntime after the pairing
    // modal opens. Lazy-loads qrcodejs from cdnjs the first time so the
    // rest of the app doesn't pay for it on every page load.
    let qrcodejsLoading = null;
    async function ensureQrcodejs() {
        if (window.QRCode) return;
        if (qrcodejsLoading) { await qrcodejsLoading; return; }
        qrcodejsLoading = new Promise((resolve, reject) => {
            const s = document.createElement('script');
            s.src = 'https://cdnjs.cloudflare.com/ajax/libs/qrcodejs/1.0.0/qrcode.min.js';
            s.async = true;
            s.onload = () => resolve();
            s.onerror = () => reject(new Error('Failed to load qrcodejs'));
            document.head.appendChild(s);
        });
        await qrcodejsLoading;
    }

    async function renderPairingQr(targetElId, text) {
        await ensureQrcodejs();
        const el = document.getElementById(targetElId);
        if (!el) { log('renderPairingQr: target #' + targetElId + ' not found'); return; }
        el.innerHTML = ''; // clear any prior render (re-opens of the modal)
        new window.QRCode(el, {
            text: text,
            width: 300,
            height: 300,
            correctLevel: window.QRCode.CorrectLevel.H
        });
    }

    // ── Client-side clock display (2026-07-08, choppy-video fix) ─────
    //
    // Previously the KioskTablet.razor clock timer called
    // InvokeAsync(StateHasChanged) every second, which for Blazor
    // Server = 1 Hz SignalR round-trip + full page diff render. On
    // the Pritom P7 that stole enough main-thread time to make the
    // camera preview visibly choppy. Now JS drives the clock display
    // directly by updating DOM textContent — no framework, no
    // server round-trip. The server-side schedule check still runs
    // every 15s (down from 1s) to catch off-hours transitions.
    let clockIntervalId = null;
    function startClockDisplay(timeElId, dateElId) {
        function tick() {
            const now = new Date();
            const timeEl = document.getElementById(timeElId);
            const dateEl = document.getElementById(dateElId);
            if (timeEl) {
                timeEl.textContent = now.toLocaleTimeString([], {
                    hour: 'numeric', minute: '2-digit', second: '2-digit', hour12: true
                });
            }
            if (dateEl) {
                dateEl.textContent = now.toLocaleDateString([], {
                    weekday: 'long', year: 'numeric', month: 'long', day: 'numeric'
                });
            }
        }
        if (clockIntervalId) clearInterval(clockIntervalId);
        tick();
        clockIntervalId = setInterval(tick, 1000);
    }

    function stopClockDisplay() {
        if (clockIntervalId) { clearInterval(clockIntervalId); clockIntervalId = null; }
    }

    // ── Camera-panel snapshot flash (2026-07-08) ─────────────────────
    // Briefly flashes the camera panel white after a successful scan
    // so the user gets an unmissable "photo taken" visual, even if
    // they're looking at the badge in their hand and not the screen.
    // Uses CSS class add/remove — animation-driven, no per-frame JS.
    function flashCameraPanel() {
        const panel = document.querySelector('.tk-cam-panel');
        if (!panel) return;
        panel.classList.remove('tk-flash');
        // Force reflow so the class re-add restarts the animation.
        void panel.offsetWidth;
        panel.classList.add('tk-flash');
        // Belt-and-suspenders: strip the class after the 400ms animation.
        // The CSS uses animation-fill-mode: forwards + opacity: 0 base, so
        // the panel is already visually clear when the animation ends. This
        // just ensures a subsequent flashCameraPanel() call can re-trigger.
        setTimeout(() => { panel.classList.remove('tk-flash'); }, 450);
    }

    // ── Idle screen dim (2026-07-08 thermal tuning D) ─────────────────
    //
    // After IDLE_DIM_MS with no user activity or successful scan, apply a
    // semi-transparent black overlay to the whole viewport. Camera keeps
    // running underneath so a badge scan still decodes and clears the
    // overlay instantly. Overlay is also cleared on any touch / mouse /
    // keyboard event so someone tapping the tablet wakes it visually
    // even before they scan.
    //
    // Honest caveat: on LCD tablets (Pritom P7 is LCD) this is a
    // visual dim only — actual backlight power draw is unchanged.
    // For real backlight thermal savings, set Intune device
    // restriction "Screen brightness → 20-30" on the enrollment
    // profile so Android reduces backlight when the ambient sensor
    // or lock-screen kicks in.
    const IDLE_DIM_MS = 3 * 60 * 1000;  // 3 minutes
    const IDLE_DIM_CLASS = 'nh-idle-dim';
    let idleDimTimer = null;
    let idleWatchInstalled = false;

    function armIdleDimTimer() {
        if (idleDimTimer) clearTimeout(idleDimTimer);
        document.body.classList.remove(IDLE_DIM_CLASS);
        idleDimTimer = setTimeout(() => {
            document.body.classList.add(IDLE_DIM_CLASS);
            log('Idle dim applied after ' + (IDLE_DIM_MS / 1000) + 's inactivity');
        }, IDLE_DIM_MS);
    }

    function installIdleWatch() {
        if (idleWatchInstalled) return;
        idleWatchInstalled = true;
        ['touchstart', 'mousedown', 'keydown', 'pointerdown'].forEach(evt => {
            document.addEventListener(evt, armIdleDimTimer, { passive: true });
        });
        // Inject the overlay CSS once. Prefixed nh- so it doesn't collide
        // with any Bootstrap / app class. The overlay covers everything
        // including the camera panel; touch/scan reveals the UI.
        const style = document.createElement('style');
        style.textContent =
            'body.' + IDLE_DIM_CLASS + '::after {' +
            '  content: "";' +
            '  position: fixed;' +
            '  inset: 0;' +
            '  background: rgba(0,0,0,0.70);' +
            '  pointer-events: none;' +
            '  z-index: 9999;' +
            '  transition: background 1.5s ease-in-out;' +
            '  animation: nhIdleDimFadeIn 1.5s ease-in-out;' +
            '}' +
            '@keyframes nhIdleDimFadeIn {' +
            '  from { background: rgba(0,0,0,0); }' +
            '  to   { background: rgba(0,0,0,0.70); }' +
            '}';
        document.head.appendChild(style);
        log('Idle dim watcher installed');
    }

    window.tabletScanner = {
        start: start,
        stop: stop,
        startPairing: startPairing,
        getUserAgent: getUserAgent,
        getPairedCode: getPairedCode,
        setPairedCode: setPairedCode,
        clearPairedCode: clearPairedCode,
        renderPairingQr: renderPairingQr,
        enterKioskDisplayMode: enterKioskDisplayMode,
        installIdleWatch: installIdleWatch,
        armIdleDimTimer: armIdleDimTimer,
        startClockDisplay: startClockDisplay,
        stopClockDisplay: stopClockDisplay,
        flashCameraPanel: flashCameraPanel
    };
})();
