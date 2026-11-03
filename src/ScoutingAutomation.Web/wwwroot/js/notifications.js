// Live notifications: preferences per browser, a soft chime, and whether the tab is visible
window.duNotifications = (function () {
    // The jury's choices (new submissions) and a group's (new assignments) are kept apart
    var storageKey = 'du_notifications';
    var groupStorageKey = 'du_notifications_group';
    // A burst of submissions (e.g. catching up after a reconnect) gives one chime, not many
    var minChimeGapMs = 3000;
    var audio = null;
    var lastChime = 0;

    function readPrefs(key) {
        try {
            return JSON.parse(localStorage.getItem(key || storageKey) || 'null');
        } catch (e) {
            return null;
        }
    }

    function context() {
        if (!audio) {
            var AudioContext = window.AudioContext || window.webkitAudioContext;
            if (!AudioContext) return null;
            audio = new AudioContext();
        }
        return audio;
    }

    // Browsers only allow sound once the person interacted with the page, and some (Safari) only
    // start audio inside that interaction. So start it there, but only when sound is wanted.
    function unlock(e) {
        var jury = readPrefs(storageKey);
        var group = readPrefs(groupStorageKey);
        var wanted = (jury && jury.sound) || (group && group.sound) || (e.target.closest && e.target.closest('[data-du-sound-toggle]'));
        if (!wanted) return;
        var ctx = context();
        if (ctx && ctx.state === 'suspended') {
            ctx.resume();
        }
    }
    document.addEventListener('pointerdown', unlock, true);
    document.addEventListener('keydown', unlock, true);

    function tone(ctx, frequency, start, duration) {
        var osc = ctx.createOscillator();
        var gain = ctx.createGain();
        osc.type = 'sine';
        osc.frequency.value = frequency;
        // Fade in and out so the tone does not click
        gain.gain.setValueAtTime(0, start);
        gain.gain.linearRampToValueAtTime(0.06, start + 0.02);
        gain.gain.exponentialRampToValueAtTime(0.0001, start + duration);
        osc.connect(gain).connect(ctx.destination);
        osc.start(start);
        osc.stop(start + duration + 0.05);
    }

    return {
        getPrefs: readPrefs,
        setPrefs: function (prefs, key) {
            try {
                localStorage.setItem(key || storageKey, JSON.stringify(prefs));
            } catch (e) {
                // Private window or blocked storage: the choice lasts until the page closes
            }
        },
        // Two soft rising notes (E5, A5), about a third of a second in total
        chime: function () {
            var now = Date.now();
            if (now - lastChime < minChimeGapMs) return;
            lastChime = now;

            var ctx = context();
            if (!ctx) return;
            var play = function () {
                var t = ctx.currentTime;
                tone(ctx, 659.25, t, 0.25);
                tone(ctx, 880, t + 0.12, 0.3);
            };
            if (ctx.state === 'running') {
                play();
            } else {
                // Still blocked when nobody clicked on the page yet: then this chime is skipped
                // and a chime that only gets through on a later click is too late to mean anything
                ctx.resume().then(function () {
                    if (ctx.state === 'running' && Date.now() - now < 1000) play();
                }).catch(function () { });
            }
        },
        // System notifications (outside the page) for a group whose tab is in the background.
        // Browsers only allow them on https or localhost, and only after the person said yes once.
        systemPermission: function () {
            return 'Notification' in window ? Notification.permission : 'unsupported';
        },
        requestSystemPermission: function () {
            if (!('Notification' in window)) return Promise.resolve('unsupported');
            return Promise.resolve(Notification.requestPermission()).catch(function () { return Notification.permission; });
        },
        systemNotify: function (title, body, url) {
            if (!('Notification' in window) || Notification.permission !== 'granted' || document.visibilityState === 'visible') return;
            try {
                var n = new Notification(title, { body: body, tag: 'du-unlock' });
                n.onclick = function () {
                    window.focus();
                    if (url) window.location.href = url;
                    n.close();
                };
            } catch (e) {
                // Some mobile browsers only allow notifications from a service worker: the toast in the page remains
            }
        },
        isVisible: function () {
            return document.visibilityState === 'visible';
        },
        watchVisibility: function (dotNet) {
            var handler = function () {
                dotNet.invokeMethodAsync('OnVisibilityChanged', document.visibilityState === 'visible');
            };
            document.addEventListener('visibilitychange', handler);
            return { dispose: function () { document.removeEventListener('visibilitychange', handler); } };
        }
    };
})();
