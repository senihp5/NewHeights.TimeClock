// Geolocation and location verification helpers
// Registered under both window.geolocation and window.kioskInterop for compatibility

(function () {

    function getPosition() {
        return new Promise(function (resolve, reject) {
            if (!navigator.geolocation) {
                reject('Geolocation is not supported by this browser');
                return;
            }
            navigator.geolocation.getCurrentPosition(
                function (position) {
                    resolve({
                        latitude: position.coords.latitude,
                        longitude: position.coords.longitude,
                        accuracy: position.coords.accuracy,
                        timestamp: position.timestamp
                    });
                },
                function (error) {
                    var message;
                    switch (error.code) {
                        case error.PERMISSION_DENIED:
                            message = 'Location permission denied. Please allow location access and try again.';
                            break;
                        case error.POSITION_UNAVAILABLE:
                            message = 'Location information is unavailable.';
                            break;
                        case error.TIMEOUT:
                            message = 'Location request timed out. Please try again.';
                            break;
                        default:
                            message = 'Unable to determine location.';
                            break;
                    }
                    reject(message);
                },
                {
                    enableHighAccuracy: true,
                    timeout: 12000,
                    maximumAge: 60000
                }
            );
        });
    }

    function getWifiSSID() {
        // Browser WiFi SSID access is not available via standard Web APIs.
        // The Network Information API provides connection type (wifi/cellular)
        // but not the SSID. SSID fallback must be handled server-side or
        // via a native app wrapper. We return connection type only.
        var connection = navigator.connection ||
                         navigator.mozConnection ||
                         navigator.webkitConnection;
        if (connection) {
            return {
                type: connection.type || 'unknown',
                effectiveType: connection.effectiveType || 'unknown',
                isWifi: connection.type === 'wifi'
            };
        }
        return { type: 'unknown', effectiveType: 'unknown', isWifi: false };
    }

    function watchPosition(dotNetHelper) {
        if (!navigator.geolocation) return -1;
        return navigator.geolocation.watchPosition(
            function (position) {
                dotNetHelper.invokeMethodAsync('OnPositionUpdate', {
                    latitude: position.coords.latitude,
                    longitude: position.coords.longitude,
                    accuracy: position.coords.accuracy
                });
            },
            function (error) {
                dotNetHelper.invokeMethodAsync('OnPositionError', error.message);
            },
            { enableHighAccuracy: true, timeout: 10000, maximumAge: 30000 }
        );
    }

    function clearWatch(watchId) {
        if (navigator.geolocation && watchId >= 0) {
            navigator.geolocation.clearWatch(watchId);
        }
    }

    function focusElement(element) {
        if (element) element.focus();
    }

    // Register under window.geolocation (original)
    window.geolocation = {
        getPosition: getPosition,
        watchPosition: watchPosition,
        clearWatch: clearWatch,
        getWifiSSID: getWifiSSID
    };

    // Register under window.kioskInterop (used by MobileCheckin.razor and MobileClock.razor)
    window.kioskInterop = window.kioskInterop || {};
    window.kioskInterop.getPosition = getPosition;
    window.kioskInterop.getWifiSSID = getWifiSSID;
    window.kioskInterop.focusElement = focusElement;

    // Scanner focus helper
    window.scannerHelpers = {
        focusElement: focusElement
    };

})();
