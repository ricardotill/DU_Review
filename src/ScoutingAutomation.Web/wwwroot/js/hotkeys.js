// Keyboard shortcuts: a key clicks the visible element with a matching data-hotkey, e.g. data-hotkey="ArrowRight".
// Keys typed in a form field, or with Ctrl, Alt or Cmd, are left alone.
document.addEventListener('keydown', function (e) {
    if (e.defaultPrevented || e.ctrlKey || e.altKey || e.metaKey) return;

    var target = e.target;
    if (target && (target.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(target.tagName))) return;

    var key = e.key.length === 1 ? e.key.toLowerCase() : e.key;
    var element = document.querySelector('[data-hotkey="' + key + '"]:not(.disabled)');
    if (!element || element.offsetParent === null) return;

    e.preventDefault();
    element.click();
});
