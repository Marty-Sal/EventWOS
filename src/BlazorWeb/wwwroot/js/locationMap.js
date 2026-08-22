/*
 * locationMap.js — the ONLY file in the app that knows Leaflet exists.
 *
 * Blazor components talk to this module through a tiny verb API (init, setMarker,
 * setCircle, destroy). That boundary is deliberate: replacing Leaflet with a
 * Google Maps or Mappls widget later means rewriting this file and nothing else.
 *
 * The map is a RENDERER and an input device. It never authorises anything — the
 * coordinates it reports go to our API, and the geofence is enforced server-side
 * against the Venue row.
 */
window.eventwosMap = (function () {
    'use strict';

    // elementId -> { map, marker, circle, dotNetRef }
    const instances = {};

    const DEFAULT_ZOOM = 16;

    // Fallback view when a venue has no coordinates yet: centred on India at a
    // country-wide zoom, so the admin sees a usable map rather than the
    // middle of the Atlantic (0,0).
    const FALLBACK = { lat: 20.5937, lng: 78.9629, zoom: 5 };

    function tileLayer() {
        // OSM tiles require visible attribution under their licence — this is a
        // licence obligation, not decoration. Do not remove it.
        return L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
            maxZoom: 19,
            attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors'
        });
    }

    /**
     * Create (or re-create) a map in the given element.
     *
     * @param {string} elementId   target div id
     * @param {number|null} lat
     * @param {number|null} lng
     * @param {boolean} draggable  true = admin can drag the pin (venue editing)
     * @param {object|null} dotNetRef  DotNetObjectReference; receives OnMarkerMoved
     */
    function init(elementId, lat, lng, draggable, dotNetRef) {
        const el = document.getElementById(elementId);
        if (!el || typeof L === 'undefined') return false;

        // Blazor re-renders can call init twice on the same element. Leaflet
        // throws "Map container is already initialized" in that case, so always
        // tear down first — cheaper than trying to diff the existing state.
        destroy(elementId);

        const hasPoint = isFiniteNumber(lat) && isFiniteNumber(lng);
        const center = hasPoint ? [lat, lng] : [FALLBACK.lat, FALLBACK.lng];
        const zoom = hasPoint ? DEFAULT_ZOOM : FALLBACK.zoom;

        const map = L.map(elementId, {
            center: center,
            zoom: zoom,
            // Scroll-wheel zoom off by default: the map often sits inside a
            // scrollable modal, and hijacking the wheel traps the user's scroll.
            // Ctrl+wheel and the +/- buttons still zoom.
            scrollWheelZoom: false
        });
        tileLayer().addTo(map);

        const inst = { map: map, marker: null, circle: null, dotNetRef: dotNetRef || null };
        instances[elementId] = inst;

        if (hasPoint) {
            placeMarker(inst, elementId, lat, lng, draggable);
        }

        if (draggable) {
            // Click-to-move as well as drag: faster than dragging across a long
            // distance, and it's the gesture most people try first.
            map.on('click', function (e) {
                placeMarker(inst, elementId, e.latlng.lat, e.latlng.lng, true);
                notify(inst, e.latlng.lat, e.latlng.lng);
            });
        }

        // Leaflet mis-measures its container when the map is created inside an
        // element that was hidden or still animating (exactly our case: modals).
        // A deferred invalidateSize is the documented remedy for the grey-tiles
        // symptom.
        setTimeout(function () { map.invalidateSize(); }, 120);
        return true;
    }

    function placeMarker(inst, elementId, lat, lng, draggable) {
        if (inst.marker) {
            inst.marker.setLatLng([lat, lng]);
        } else {
            inst.marker = L.marker([lat, lng], { draggable: !!draggable }).addTo(inst.map);
            if (draggable) {
                inst.marker.on('dragend', function () {
                    const p = inst.marker.getLatLng();
                    notify(inst, p.lat, p.lng);
                });
            }
        }
        if (inst.circle) inst.circle.setLatLng([lat, lng]);
    }

    /** Push the new position back to Blazor so it can reverse-geocode it. */
    function notify(inst, lat, lng) {
        if (!inst.dotNetRef) return;
        // 6 dp (~11 cm) matches what the server stores; more digits would be
        // false precision.
        inst.dotNetRef.invokeMethodAsync('OnMarkerMoved', round6(lat), round6(lng))
            .catch(function () { /* component disposed mid-drag — nothing to do */ });
    }

    /** Move the pin from Blazor (e.g. admin picked a search suggestion). */
    function setMarker(elementId, lat, lng, draggable, recenter) {
        const inst = instances[elementId];
        if (!inst || !isFiniteNumber(lat) || !isFiniteNumber(lng)) return false;

        placeMarker(inst, elementId, lat, lng, draggable);
        if (recenter !== false) inst.map.setView([lat, lng], DEFAULT_ZOOM);
        return true;
    }

    /**
     * Draw/update the geofence circle. This is a VISUALISATION of the event's
     * configured radius — the actual allow/reject decision is made server-side.
     * Pass a null/0 radius to remove it.
     */
    function setCircle(elementId, lat, lng, radiusMeters) {
        const inst = instances[elementId];
        if (!inst) return false;

        if (!isFiniteNumber(radiusMeters) || radiusMeters <= 0) {
            if (inst.circle) { inst.map.removeLayer(inst.circle); inst.circle = null; }
            return true;
        }

        const center = (isFiniteNumber(lat) && isFiniteNumber(lng))
            ? [lat, lng]
            : (inst.marker ? inst.marker.getLatLng() : null);
        if (!center) return false;

        if (inst.circle) {
            inst.circle.setLatLng(center);
            inst.circle.setRadius(radiusMeters);
        } else {
            inst.circle = L.circle(center, {
                radius: radiusMeters,
                color: '#4f46e5',       // indigo-600, matching the app's accent
                weight: 2,
                fillColor: '#6366f1',
                fillOpacity: 0.15
            }).addTo(inst.map);
        }

        // Frame the whole fence so the admin can judge whether the radius covers
        // the site — a 300 m circle at street zoom is just a blue wash.
        inst.map.fitBounds(inst.circle.getBounds(), { padding: [24, 24], maxZoom: DEFAULT_ZOOM });
        return true;
    }

    /** Recalculate size after a container becomes visible (tab/modal open). */
    function refresh(elementId) {
        const inst = instances[elementId];
        if (!inst) return false;
        inst.map.invalidateSize();
        return true;
    }

    /**
     * Tear down. Must be called from the component's DisposeAsync — a Leaflet map
     * left behind keeps DOM listeners and its DotNetObjectReference alive, which
     * leaks on every modal open in a long-lived WASM session.
     */
    function destroy(elementId) {
        const inst = instances[elementId];
        if (!inst) return false;
        try { inst.map.off(); inst.map.remove(); } catch (e) { /* already gone */ }
        delete instances[elementId];
        return true;
    }

    function isFiniteNumber(v) {
        return typeof v === 'number' && isFinite(v);
    }

    function round6(v) {
        return Math.round(v * 1e6) / 1e6;
    }

    return {
        init: init,
        setMarker: setMarker,
        setCircle: setCircle,
        refresh: refresh,
        destroy: destroy
    };
})();
