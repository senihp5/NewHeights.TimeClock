// Kiosk JavaScript for TimeClock

window.kioskInterop = {
    focusInput: function(element) {
        if (element) element.focus();
    },

    getPosition: function() {
        console.log('kioskInterop.getPosition called');
        return new Promise((resolve, reject) => {
            if (!navigator.geolocation) {
                reject('Geolocation not supported');
                return;
            }
            navigator.geolocation.getCurrentPosition(
                (pos) => {
                    console.log('Geolocation success:', pos.coords.latitude, pos.coords.longitude);
                    resolve({ latitude: pos.coords.latitude, longitude: pos.coords.longitude, accuracy: pos.coords.accuracy });
                },
                (err) => {
                    console.error('Geolocation error:', err.code, err.message);
                    reject('GEO_ERROR_' + err.code + ': ' + (err.code === 1 ? 'denied - check browser site settings' : err.code === 2 ? 'position unavailable' : 'timeout after 15s') + ' [' + err.message + ']');
                },
                { enableHighAccuracy: true, timeout: 15000, maximumAge: 60000 }
            );
        });
    }
};

window.qrScanner = {
    scanner: null,
    dotNetHelper: null,
    isStarting: false,
    isScanning: false,

    init: async function(elementId, dotNetRef) {
        console.log('QR Scanner init for element:', elementId);
        
        // Prevent multiple initializations
        if (this.isStarting) {
            console.log('Scanner already starting, skipping');
            return false;
        }
        
        this.dotNetHelper = dotNetRef;
        
        var element = document.getElementById(elementId);
        if (!element) {
            console.error('QR reader element not found:', elementId);
            return false;
        }

        // Stop existing scanner if any
        await this.stop();

        if (typeof Html5Qrcode === 'undefined') {
            console.log('Loading html5-qrcode library...');
            await this.loadScript('https://unpkg.com/html5-qrcode@2.3.8/html5-qrcode.min.js');
        }

        this.scanner = new Html5Qrcode(elementId);
        console.log('QR Scanner initialized');
        return true;
    },

    start: async function() {
        if (!this.scanner) {
            console.error('Scanner not initialized');
            return false;
        }
        
        if (this.isStarting || this.isScanning) {
            console.log('Scanner already running');
            return true;
        }

        this.isStarting = true;
        
        try {
            console.log('Starting QR scanner...');
            await this.scanner.start(
                { facingMode: "environment" },
                { fps: 10, qrbox: { width: 250, height: 250 }, aspectRatio: 1.0 },
                (decodedText) => {
                    console.log('QR Code scanned:', decodedText);
                    this.onScanSuccess(decodedText);
                },
                (errorMessage) => { /* QR not found - ignore */ }
            );
            this.isScanning = true;
            this.isStarting = false;
            console.log('QR Scanner started');
            return true;
        } catch (err) {
            console.error('Failed to start QR scanner:', err);
            this.isStarting = false;
            return false;
        }
    },

    stop: async function() {
        this.isStarting = false;
        if (this.scanner && this.isScanning) {
            try {
                await this.scanner.stop();
                console.log('QR Scanner stopped');
            } catch (e) {
                console.log('Scanner stop error (ignored):', e);
            }
        }
        this.isScanning = false;
    },

    onScanSuccess: function(decodedText) {
        if (this.dotNetHelper) {
            this.dotNetHelper.invokeMethodAsync('OnQrCodeScanned', decodedText);
        }
    },

    loadScript: function(src) {
        return new Promise((resolve, reject) => {
            if (document.querySelector(`script[src="${src}"]`)) {
                resolve();
                return;
            }
            const script = document.createElement('script');
            script.src = src;
            script.onload = () => { console.log('Script loaded:', src); resolve(); };
            script.onerror = reject;
            document.head.appendChild(script);
        });
    }
};