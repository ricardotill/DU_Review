// Favorite submissions are kept per browser in a cookie
window.duFavorites = {
    get: function () {
        var entry = document.cookie.split('; ').find(function (c) { return c.indexOf('du_favorites=') === 0; });
        if (!entry) return '';
        try {
            return decodeURIComponent(entry.substring('du_favorites='.length));
        } catch (e) {
            // A damaged cookie (edited by hand, or cut off) must not break every page with stars: start empty
            return '';
        }
    },
    set: function (value) {
        var secure = location.protocol === 'https:' ? '; Secure' : '';
        document.cookie = 'du_favorites=' + encodeURIComponent(value) + '; path=/; max-age=31536000; SameSite=Lax' + secure;
    }
};
