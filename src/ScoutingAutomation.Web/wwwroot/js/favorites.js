// Favorite submissions are kept per browser in a cookie
window.duFavorites = {
    get: function () {
        var entry = document.cookie.split('; ').find(function (c) { return c.indexOf('du_favorites=') === 0; });
        return entry ? decodeURIComponent(entry.substring('du_favorites='.length)) : '';
    },
    set: function (value) {
        document.cookie = 'du_favorites=' + encodeURIComponent(value) + '; path=/; max-age=31536000; SameSite=Lax';
    }
};
