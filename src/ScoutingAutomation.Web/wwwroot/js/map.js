// The Avonturiers maps: a thin layer over Leaflet for the Blazor MapView component and the public group map.
// Popups are built from text nodes only, so names and hints typed by admins can never run as HTML.
window.duMap = (function () {
    const maps = new Map();
    const defaultCenter = [52.1, 5.3];

    function create(elementId, options) {
        dispose(elementId);
        const element = document.getElementById(elementId);
        if (!element || !window.L) return false;

        const map = L.map(element, { scrollWheelZoom: true }).setView(defaultCenter, 8);
        // OpenStreetMap refuses tiles (403) without a Referer, and the app's Referrer-Policy (same-origin) sends none.
        // The tiles alone send the site's origin, never the page path: the group map's address holds its secret token.
        L.tileLayer(options.tileUrl, {
            attribution: options.attribution,
            maxZoom: 19,
            referrerPolicy: 'strict-origin-when-cross-origin'
        }).addTo(map);
        const state = { map, layer: L.layerGroup().addTo(map), pick: null, picked: null, fitted: false };
        maps.set(elementId, state);
        // A map in a container that was hidden or resized while loading draws grey tiles until told
        setTimeout(() => map.invalidateSize(), 100);
        return true;
    }

    function popup(marker) {
        const root = document.createElement('div');
        root.className = 'du-map-popup';
        const title = document.createElement('div');
        title.className = 'fw-bold';
        title.textContent = `${marker.number}. ${marker.name}${marker.super ? ' ★' : ''}`;
        root.appendChild(title);

        for (const line of marker.lines || []) {
            const div = document.createElement('div');
            div.className = 'small';
            div.textContent = line;
            root.appendChild(div);
        }

        if (marker.hint) {
            const hint = document.createElement('div');
            hint.className = 'small fst-italic mt-1';
            // A description from My Maps has several lines
            hint.style.whiteSpace = 'pre-line';
            hint.textContent = marker.hint;
            root.appendChild(hint);
        }

        for (const photo of marker.photos || []) {
            const link = document.createElement('a');
            link.href = photo.link || photo.url;
            link.target = '_blank';
            link.rel = 'noopener';
            link.className = 'd-block mt-2';
            if (photo.isImage) {
                const img = document.createElement('img');
                img.src = photo.url;
                img.alt = photo.caption || '';
                img.loading = 'lazy';
                img.style.maxWidth = '220px';
                img.style.maxHeight = '160px';
                img.style.borderRadius = '6px';
                link.appendChild(img);
            }
            const caption = document.createElement('div');
            caption.className = 'small';
            caption.textContent = photo.caption || 'Bekijk inzending';
            link.appendChild(caption);
            root.appendChild(link);
        }

        return root;
    }

    function setMarkers(elementId, markers, fit) {
        const state = maps.get(elementId);
        if (!state) return;

        state.layer.clearLayers();
        const bounds = [];
        for (const marker of markers) {
            const circle = L.circleMarker([marker.lat, marker.lng], {
                radius: marker.super ? 13 : 11, color: marker.super ? '#facc15' : '#0f141c', weight: marker.super ? 3 : 2,
                fillColor: marker.color, fillOpacity: 0.95
            });
            circle.bindTooltip(String(marker.number), { permanent: true, direction: 'center', className: 'du-map-label' });
            circle.bindPopup(() => popup(marker));
            circle.addTo(state.layer);
            bounds.push([marker.lat, marker.lng]);
        }

        // Fitted once, or when asked: a live reload must not move the map the user just panned
        if (bounds.length > 0 && (fit || !state.fitted)) {
            // The container may have got its size only now; fitting to a stale size cuts markers off
            state.map.invalidateSize();
            state.map.fitBounds(bounds, { padding: [30, 30], maxZoom: 16 });
            state.fitted = true;
        }
    }

    // Clicking the map reports the spot to .NET, e.g. to place a location's pin
    function enablePick(elementId, dotnet) {
        const state = maps.get(elementId);
        if (!state) return;
        state.pick = (e) => {
            showPicked(elementId, e.latlng.lat, e.latlng.lng);
            dotnet.invokeMethodAsync('OnMapPicked', e.latlng.lat, e.latlng.lng);
        };
        state.map.on('click', state.pick);
    }

    function showPicked(elementId, lat, lng) {
        const state = maps.get(elementId);
        if (!state) return;
        if (state.picked) state.picked.remove();
        if (lat === null || lng === null || lat === undefined || lng === undefined) {
            state.picked = null;
            return;
        }
        state.picked = L.marker([lat, lng], {
            icon: L.divIcon({ className: 'du-map-pick', html: '<i class="bi bi-geo-alt-fill"></i>', iconSize: [28, 28], iconAnchor: [14, 28] })
        }).addTo(state.map);
    }

    function dispose(elementId) {
        const state = maps.get(elementId);
        if (!state) return;
        state.map.remove();
        maps.delete(elementId);
    }

    // The public map of one group: no live connection, so it reads its data again every half minute
    async function publicMap(elementId, dataUrl, listId) {
        const element = document.getElementById(elementId);
        if (!element) return;
        const first = await load(dataUrl);
        if (!first) return;
        create(elementId, first.tiles);
        render(elementId, first, listId);
        setInterval(async () => {
            const data = await load(dataUrl);
            // The start was moved to later: the page shows the waiting text then
            if (data && data.startsAt) location.reload();
            else if (data) render(elementId, data, listId);
        }, 30000);
    }

    async function load(dataUrl) {
        try {
            const response = await fetch(dataUrl, { cache: 'no-store' });
            return response.ok ? await response.json() : null;
        } catch {
            return null;
        }
    }

    function render(elementId, data, listId) {
        setMarkers(elementId, data.markers, false);
        const list = listId && document.getElementById(listId);
        if (!list) return;

        list.replaceChildren();
        if (data.markers.length === 0) {
            const empty = document.createElement('li');
            empty.className = 'list-group-item text-secondary';
            empty.textContent = 'Er zijn nog geen locaties vrijgespeeld.';
            list.appendChild(empty);
        }
        for (const marker of data.markers) {
            const item = document.createElement('li');
            item.className = 'list-group-item d-flex justify-content-between gap-2 align-items-center';
            const name = document.createElement('span');
            const dot = document.createElement('span');
            dot.className = 'du-map-dot me-2';
            dot.style.background = marker.color;
            name.appendChild(dot);
            name.appendChild(document.createTextNode(`${marker.number}. ${marker.name}${marker.super ? ' ★' : ''}`));
            const status = document.createElement('span');
            status.className = 'small text-secondary text-end';
            status.textContent = marker.lines.join(' · ');
            item.append(name, status);
            list.appendChild(item);
        }

        const updated = document.getElementById(listId + '-updated');
        if (updated) updated.textContent = data.updated;
    }

    return { create, setMarkers, enablePick, showPicked, dispose, publicMap };
})();
