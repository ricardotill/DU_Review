// The message editor: formatted text as you type, turned into Slack mrkdwn when sent
import Picker from '../lib/emoji-picker-element/picker.js';
import nl from '../lib/emoji-picker-element/i18n/nl.js';

const dataSource = new URL('../lib/emoji-picker-element-data/nl/cldr/data.json', import.meta.url).href;

// Commands that toggle with the selection; the toolbar shows which are on
const stateCommands = ['bold', 'italic', 'strikeThrough', 'insertUnorderedList', 'insertOrderedList'];

/** Starts the editor in <paramref>root</paramref>, filled with <paramref>initial</paramref> (Slack mrkdwn). */
export function init(root, dotNet, initial) {
    const editor = root.querySelector('.slack-composer-editor');
    if (initial) editor.innerHTML = fromMrkdwn(initial);
    const popover = root.querySelector('.slack-composer-emoji');
    const emojiButton = root.querySelector('[data-emoji-toggle]');
    let savedRange = null;
    // After dispose, .NET has let go of the component, so it is not called any more
    let disposed = false;

    document.execCommand('defaultParagraphSeparator', false, 'div');

    const notify = () => {
        if (!disposed) dotNet.invokeMethodAsync('OnContentChanged', toMrkdwn(editor)).catch(() => { });
    };

    const saveSelection = () => {
        const selection = window.getSelection();
        if (selection.rangeCount && editor.contains(selection.anchorNode)) {
            savedRange = selection.getRangeAt(0).cloneRange();
        }
    };

    const restoreSelection = () => {
        editor.focus();
        const selection = window.getSelection();
        selection.removeAllRanges();
        if (savedRange && editor.contains(savedRange.startContainer)) {
            selection.addRange(savedRange);
        } else {
            const range = document.createRange();
            range.selectNodeContents(editor);
            range.collapse(false);
            selection.addRange(range);
        }
    };

    const updateToolbar = () => {
        for (const button of root.querySelectorAll('[data-cmd]')) {
            const cmd = button.dataset.cmd;
            const active = stateCommands.includes(cmd) ? document.queryCommandState(cmd) : cmd === 'code' && !!closest(editor, 'CODE');
            button.classList.toggle('active', active);
            button.setAttribute('aria-pressed', active);
        }
    };

    const onSelectionChange = () => {
        if (!editor.contains(window.getSelection().anchorNode)) return;
        saveSelection();
        updateToolbar();
    };
    document.addEventListener('selectionchange', onSelectionChange);

    editor.addEventListener('input', notify);

    // Only plain text comes in, pasted or dropped, so what is sent matches what is shown
    editor.addEventListener('paste', e => {
        e.preventDefault();
        document.execCommand('insertText', false, e.clipboardData.getData('text/plain'));
    });
    editor.addEventListener('drop', e => {
        e.preventDefault();
        const text = e.dataTransfer?.getData('text/plain');
        if (!text) return;
        const range = document.caretRangeFromPoint?.(e.clientX, e.clientY);
        if (range && editor.contains(range.startContainer)) {
            const selection = window.getSelection();
            selection.removeAllRanges();
            selection.addRange(range);
        }
        editor.focus();
        document.execCommand('insertText', false, text);
    });

    editor.addEventListener('keydown', e => {
        if ((e.ctrlKey || e.metaKey) && e.shiftKey && e.key.toLowerCase() === 'x') {
            e.preventDefault();
            format(editor, 'strikeThrough');
        }
    });

    // Keeps the selection in the editor when a toolbar button is clicked
    root.addEventListener('mousedown', e => {
        if (e.target.closest('[data-cmd], [data-emoji-toggle]')) e.preventDefault();
    });

    root.addEventListener('click', e => {
        const button = e.target.closest('[data-cmd]');
        if (!button) return;
        restoreSelection();
        format(editor, button.dataset.cmd);
        updateToolbar();
        notify();
    });

    const picker = new Picker({ locale: 'nl', i18n: nl, dataSource });
    picker.classList.add('dark');
    popover.appendChild(picker);

    const closePopover = () => {
        popover.hidden = true;
        emojiButton.setAttribute('aria-expanded', 'false');
    };

    emojiButton.addEventListener('click', () => {
        popover.hidden = !popover.hidden;
        emojiButton.setAttribute('aria-expanded', String(!popover.hidden));
        if (!popover.hidden) picker.shadowRoot?.querySelector('input')?.focus();
    });

    picker.addEventListener('emoji-click', e => {
        restoreSelection();
        document.execCommand('insertText', false, e.detail.unicode);
        saveSelection();
        closePopover();
        notify();
    });

    const onDocumentClick = e => {
        if (!popover.hidden && !popover.contains(e.target) && !emojiButton.contains(e.target)) closePopover();
    };
    const onKeyDown = e => {
        if (e.key === 'Escape' && !popover.hidden) {
            closePopover();
            emojiButton.focus();
        }
    };
    document.addEventListener('click', onDocumentClick);
    document.addEventListener('keydown', onKeyDown);

    return {
        dispose() {
            disposed = true;
            document.removeEventListener('selectionchange', onSelectionChange);
            document.removeEventListener('click', onDocumentClick);
            document.removeEventListener('keydown', onKeyDown);
        }
    };
}

export function format(editor, cmd) {
    document.execCommand('styleWithCSS', false, false);

    if (cmd === 'code') {
        const code = closest(editor, 'CODE');
        if (code) {
            code.replaceWith(document.createTextNode(code.textContent));
            return;
        }
        const text = window.getSelection().toString();
        if (text) document.execCommand('insertHTML', false, '<code>' + escapeHtml(text) + '</code>&#8203;');
        return;
    }

    if (cmd === 'link') {
        let url = (window.prompt('Adres van de link (https://…)') || '').trim();
        if (!url) return;
        if (!/^(https?:|mailto:)/i.test(url)) url = 'https://' + url;
        if (window.getSelection().isCollapsed) {
            document.execCommand('insertHTML', false, '<a href="' + escapeHtml(url) + '">' + escapeHtml(url) + '</a>&#8203;');
        } else {
            document.execCommand('createLink', false, url);
        }
        return;
    }

    document.execCommand(cmd, false, null);
}

/** Empties the editor; takes the editor or the composer around it. */
export function clear(element) {
    editorOf(element).innerHTML = '';
}

/**
 * The editor's content as Slack mrkdwn (https://api.slack.com/reference/surfaces/formatting);
 * takes the editor or the composer around it.
 */
export function toMrkdwn(element) {
    return serialize(editorOf(element), 0)
        .replace(/​/g, '')
        .replace(/[ \t]+\n/g, '\n')
        // A line starting with > is a quote in Slack, as when typed there
        .replace(/^&gt; /gm, '> ')
        .trim();
}

/** Slack mrkdwn as editor content: the opposite of toMrkdwn, so a saved text opens as it was. */
export function fromMrkdwn(text) {
    return text.replace(/\r\n?/g, '\n').split('\n')
        .map(line => '<div>' + (line ? inline(line) : '<br>') + '</div>')
        .join('');
}

// Links and Slack tokens such as <!here> or <@U123> first, then `code`, then the other markers
function inline(text) {
    let html = '';
    let rest = text;
    // Anything else between < and > is plain text, as Slack would show it
    const token = /<((?:https?:|mailto:|[!@#])[^<>\s|]*(?:\|[^<>\n]*)?)>|`([^`\n]+)`/i;
    let match;
    while ((match = token.exec(rest)) !== null) {
        html += markers(rest.slice(0, match.index));
        if (match[1] !== undefined) {
            const [target, label] = splitToken(match[1]);
            html += /^(https?:|mailto:)/i.test(target)
                ? '<a href="' + escapeHtml(target) + '">' + markers(label ?? target) + '</a>'
                : '<span class="slack-token" contenteditable="false" data-raw="' + escapeHtml(match[0]) + '">'
                    + escapeHtml(label ?? tokenLabel(target)) + '</span>';
        } else {
            html += '<code>' + escapeHtml(unescapeSlack(match[2])) + '</code>';
        }
        rest = rest.slice(match.index + match[0].length);
    }
    return html + markers(rest);
}

// <!here> shows as @here, <@U123> as @U123, <#C123> as #C123
function tokenLabel(target) {
    return target[0] === '#' ? target : '@' + target.slice(1).split('^')[0];
}

function splitToken(content) {
    const bar = content.indexOf('|');
    return bar < 0 ? [content, null] : [content.slice(0, bar), content.slice(bar + 1)];
}

// Slack only sees a marker at a word boundary, with no space just inside it
const markerTags = [['\\*', 'b'], ['_', 'i'], ['~', 's']];

function markers(text) {
    let html = escapeHtml(unescapeSlack(text));
    for (const [marker, tag] of markerTags) {
        const pattern = new RegExp('(^|[\\s(\\[{"\'>])' + marker + '([^\\s' + marker + '](?:[^' + marker + ']*?[^\\s' + marker + '])?)' + marker + '(?=$|[\\s.,!?;:)\\]}"\'<])', 'g');
        html = html.replace(pattern, (_, before, inner) => before + '<' + tag + '>' + inner + '</' + tag + '>');
    }
    return html;
}

function unescapeSlack(text) {
    return text.replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&amp;/g, '&');
}

const blocks = new Set(['DIV', 'P', 'LI', 'UL', 'OL', 'H1', 'H2', 'H3', 'BLOCKQUOTE', 'PRE']);

// Blocks start on a new line and end with one; an empty block is an empty line
function serialize(node, depth) {
    let out = '';
    for (const child of node.childNodes) {
        let text = serializeNode(child, depth);
        if (child.nodeType === Node.ELEMENT_NODE && blocks.has(child.tagName)) {
            if (out && !out.endsWith('\n')) out += '\n';
            text = text.replace(/\n$/, '') + '\n';
        }
        out += text;
    }
    return out;
}

function serializeNode(node, depth) {
    if (node.nodeType === Node.TEXT_NODE) {
        return escapeSlack(node.textContent.replace(/ /g, ' '));
    }
    if (node.nodeType !== Node.ELEMENT_NODE) return '';

    // Kept exactly as Slack wrote it: a mention, a channel or a special such as <!here>
    if (node.dataset?.raw) return node.dataset.raw;

    switch (node.tagName) {
        case 'BR':
            // A last <br> in a block only gives the line its height; serialize drops its newline
            return '\n';
        case 'B':
        case 'STRONG':
            return wrap('*', serialize(node, depth));
        case 'I':
        case 'EM':
            return wrap('_', serialize(node, depth));
        case 'S':
        case 'STRIKE':
        case 'DEL':
            return wrap('~', serialize(node, depth));
        case 'CODE':
            return wrap('`', escapeSlack(node.textContent.replace(/ /g, ' ')));
        case 'A': {
            const href = node.getAttribute('href') || '';
            const text = node.textContent.replace(/​/g, '').trim();
            if (!href) return escapeSlack(text);
            return !text || text === href ? '<' + href + '>' : '<' + href + '|' + escapeSlack(text).replace(/\|/g, '/') + '>';
        }
        case 'UL':
        case 'OL':
            return listItems(node, depth);
        case 'DIV':
        case 'P':
        case 'H1':
        case 'H2':
        case 'H3':
        case 'BLOCKQUOTE':
        case 'PRE':
            return serialize(node, depth);
        default:
            return serialize(node, depth);
    }
}

function listItems(list, depth) {
    const indent = '    '.repeat(depth);
    let number = 1;
    let out = '';
    for (const item of list.children) {
        if (item.tagName !== 'LI') {
            out += serializeNode(item, depth + 1);
            continue;
        }
        const bullet = list.tagName === 'OL' ? (number++) + '. ' : '• ';
        // Nested lists come after the item's own text, each on their own line
        let text = '';
        let nested = '';
        for (const child of item.childNodes) {
            if (child.tagName === 'UL' || child.tagName === 'OL') nested += listItems(child, depth + 1);
            else text += serializeNode(child, depth);
        }
        out += indent + bullet + text.replace(/\n+$/, '').replace(/\n/g, '\n' + indent + '  ') + '\n' + nested;
    }
    return out;
}

// Slack ignores markers with a space just inside them, and markers do not span lines
function wrap(marker, content) {
    return content.split('\n').map(line => {
        const match = line.match(/^(\s*)([\s\S]*?)(\s*)$/);
        return match[2] ? match[1] + marker + match[2] + marker + match[3] : line;
    }).join('\n');
}

function editorOf(element) {
    return element.classList.contains('slack-composer-editor') ? element : element.querySelector('.slack-composer-editor');
}

function escapeSlack(text) {
    return text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

function escapeHtml(text) {
    return text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}

function closest(editor, tagName) {
    let node = window.getSelection().anchorNode;
    while (node && node !== editor) {
        if (node.tagName === tagName) return node;
        node = node.parentNode;
    }
    return null;
}
