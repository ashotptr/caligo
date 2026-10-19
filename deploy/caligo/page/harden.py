import sys

DOMPURIFY = (
    '<script src="https://cdnjs.cloudflare.com/ajax/libs/dompurify/3.4.16/purify.min.js" '
    'integrity="sha512-flQmhkXNRQ3iUfvtdCobtpMjCb6kE+zLnTiAulAQJ3WykVfy84JvCMOo6vhwWXHTXjR25h9Zj+LyGFRWKjRoag==" '
    'crossorigin="anonymous" referrerpolicy="no-referrer"></script>'
)

HELPERS = r"""<script>
            function caligoText(value) {
                return String(value ?? '').replace(/[&<>"']/g, character => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[character]);
            }
            function caligoRich(value) {
                return window.DOMPurify ? window.DOMPurify.sanitize(String(value ?? '')) : caligoText(value);
            }
            function caligoJs(value) {
                return caligoText(String(value ?? '').replace(/\\/g, '\\\\').replace(/'/g, "\\'").replace(/[\r\n\u2028\u2029]/g, ' '));
            }
            function caligoUrl(value) {
                const url = String(value ?? '').trim();
                return /^[a-z][a-z0-9+.-]*:/i.test(url) && !/^(https?|mailto):/i.test(url) ? '#' : caligoText(url);
            }
        </script>"""

JSZIP = '<script src="https://cdnjs.cloudflare.com/ajax/libs/jszip/3.10.1/jszip.min.js"></script>'

REPLACEMENTS = [
    (JSZIP, DOMPURIFY + "\n        " + HELPERS + "\n        " + JSZIP),
    ('const meta = `<span class="msg-meta">${sender}</span>`;',
     'const meta = `<span class="msg-meta">${caligoText(sender)}</span>`;'),
    ('div.innerHTML = (isMe ? "" : meta) + text;',
     'div.innerHTML = (isMe ? "" : meta) + caligoRich(text);'),
    ('<div class="chat-room-title">#${room.name}</div>',
     '<div class="chat-room-title">#${caligoText(room.name)}</div>'),
    ('<div class="chat-room-users" id="count-${room.name}">',
     '<div class="chat-room-users" id="count-${caligoText(room.name)}">'),
    ('<span>${currentUser}</span>',
     '<span>${caligoText(currentUser)}</span>'),
    ('margin-bottom: 4px;">You</div>${query}`;',
     'margin-bottom: 4px;">You</div>${caligoText(query)}`;'),
    ('text-overflow:ellipsis;">${song.artist}</span>',
     'text-overflow:ellipsis;">${caligoText(song.artist)}</span>'),
    ('<span style="color:#aaa; font-size:11px;">${song.title}</span>',
     '<span style="color:#aaa; font-size:11px;">${caligoText(song.title)}</span>'),
    ('<a href="${song.url}" target="_blank"',
     '<a href="${caligoUrl(song.url)}" target="_blank" rel="noopener noreferrer"'),
    ('color:#eee;">${cleanAnswer}</div>',
     'color:#eee;">${caligoRich(cleanAnswer)}</div>'),
    ('<img src="${img}">',
     '<img src="${caligoText(img)}">'),
    ('<div class="card-title">${item.name}</div>',
     '<div class="card-title">${caligoText(item.name)}</div>'),
    ('<div class="card-sub">${item.artist}</div>',
     '<div class="card-sub">${caligoText(item.artist)}</div>'),
    ('const safeName = album.name.replace(/\'/g, "\\\\\'");',
     'const safeName = caligoJs(album.name);'),
    ('<span style="font-weight:500">${t.title}</span>',
     '<span style="font-weight:500">${caligoText(t.title)}</span>'),
    ('<button class="btn-live-control ${className}"',
     '<button class="btn-live-control ${caligoText(className)}"'),
    ("onclick=\"event.stopPropagation(); addToRadio('${safeUrl}', '${safeTitle}', this)\"",
     "onclick=\"event.stopPropagation(); addToRadio('${caligoJs(t.audio_url)}', '${caligoJs(t.title)}', this)\""),
    ('<span class="status-text">${statusText}</span>',
     '<span class="status-text">${caligoText(statusText)}</span>'),
    ("onclick=\"event.stopPropagation(); downloadTrack('${safeUrl}', '${safeTitle}.mp3')\"",
     "onclick=\"event.stopPropagation(); downloadTrack('${caligoJs(t.audio_url)}', '${caligoJs(t.title)}.mp3')\""),
    ('select.innerHTML += `<option value="${a}">${a}</option>`;',
     'select.innerHTML += `<option value="${caligoText(a)}">${caligoText(a)}</option>`;'),
]


def harden(text):
    for old, new in REPLACEMENTS:
        count = text.count(old)

        if count != 1:
            raise SystemExit(f"harden: expected one occurrence of {old!r}, found {count}")

        text = text.replace(old, new)

    return text


def main():
    path = sys.argv[1]
    output = sys.argv[2] if len(sys.argv) > 2 else path

    with open(path, encoding="utf-8", newline="") as handle:
        original = handle.read()

    newline = "\r\n" if "\r\n" in original else "\n"
    hardened = harden(original.replace("\r\n", "\n")).replace("\n", newline)

    with open(output, "w", encoding="utf-8", newline="") as handle:
        handle.write(hardened)

    print(f"harden: {len(REPLACEMENTS)} changes applied to {path}")


if __name__ == "__main__":
    main()
