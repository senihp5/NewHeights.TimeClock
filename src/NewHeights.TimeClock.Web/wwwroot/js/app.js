// Kiosk and Dashboard Interop Functions
window.kioskInterop = {
    focusInput: function (element) {
        if (element) {
            element.focus();
        }
    },

    getPosition: function () {
        return new Promise(function (resolve, reject) {
            if (!navigator.geolocation) {
                reject('Geolocation not supported');
                return;
            }
            navigator.geolocation.getCurrentPosition(
                function (pos) {
                    resolve({ latitude: pos.coords.latitude, longitude: pos.coords.longitude, accuracy: pos.coords.accuracy });
                },
                function (err) {
                    reject('GEO_ERROR_' + err.code + ': ' + (err.code === 1 ? 'denied - check browser site settings' : err.code === 2 ? 'position unavailable' : 'timeout after 15s') + ' [' + err.message + ']');
                },
                { enableHighAccuracy: true, timeout: 15000, maximumAge: 60000 }
            );
        });
    },

    isMobile: function () {
        return /Android|webOS|iPhone|iPad|iPod|BlackBerry|IEMobile|Opera Mini/i.test(navigator.userAgent)
            || (navigator.maxTouchPoints > 1 && /Macintosh/i.test(navigator.userAgent))
            || window.innerWidth <= 768;
    }
};

// File download helper for CSV exports
window.downloadFile = function (fileName, contentType, base64Content) {
    const link = document.createElement('a');
    link.download = fileName;
    link.href = `data:${contentType};base64,${base64Content}`;
    document.body.appendChild(link);
    link.click();
    document.body.removeChild(link);
};
