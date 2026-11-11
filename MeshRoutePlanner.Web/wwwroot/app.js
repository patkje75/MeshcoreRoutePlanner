const state = {
  from: null, // { public_key, name, lat, lon }
  to: null,
  pathByteSize: 1,
  pickMode: 'from',
};

// Drag the floating control panel around by its header, clamped so it can't be dragged
// fully off-screen. Pointer Events cover mouse, touch and pen with one code path.
(function enablePanelDrag() {
  const panel = document.getElementById('controlPanel');
  const handle = document.getElementById('panelDragHandle');
  let dragOffsetX = 0, dragOffsetY = 0, dragging = false;

  handle.addEventListener('pointerdown', (e) => {
    dragging = true;
    const rect = panel.getBoundingClientRect();
    dragOffsetX = e.clientX - rect.left;
    dragOffsetY = e.clientY - rect.top;
    panel.style.left = rect.left + 'px';
    panel.style.top = rect.top + 'px';
    handle.setPointerCapture(e.pointerId);
  });

  handle.addEventListener('pointermove', (e) => {
    if (!dragging) return;
    const maxX = window.innerWidth - panel.offsetWidth;
    const maxY = window.innerHeight - panel.offsetHeight;
    const x = Math.min(Math.max(0, e.clientX - dragOffsetX), Math.max(0, maxX));
    const y = Math.min(Math.max(0, e.clientY - dragOffsetY), Math.max(0, maxY));
    panel.style.left = x + 'px';
    panel.style.top = y + 'px';
  });

  handle.addEventListener('pointerup', (e) => {
    dragging = false;
    handle.releasePointerCapture(e.pointerId);
  });
})();

document.querySelectorAll('#byteSizeSelector button').forEach(btn => {
  btn.addEventListener('click', () => {
    document.querySelectorAll('#byteSizeSelector button').forEach(b => b.classList.remove('active'));
    btn.classList.add('active');
    state.pathByteSize = Number(btn.dataset.bytes);
    refreshPathPreviews();
  });
});

// Routes currently on screen, indexed so the byte-size selector can update every card's
// preview text live without re-running the search.
let renderedRoutes = [];

function refreshPathPreviews() {
  document.querySelectorAll('.route-card').forEach(card => {
    const route = renderedRoutes[Number(card.dataset.idx)];
    const preview = card.querySelector('.path-preview');
    if (route && preview) preview.textContent = pathPreviewText(route);
  });
}

function pathPreviewText(route) {
  return buildPathString(route.hops, state.pathByteSize);
}

// The companion app's custom-path field takes every hop in the route, origin through
// destination, as comma-separated hex prefixes of `bytes` length each.
function buildPathString(hops, bytes) {
  return hops
    .map(h => h.pubkey.slice(0, bytes * 2).toLowerCase())
    .join(',');
}

async function copyToClipboard(text) {
  try {
    await navigator.clipboard.writeText(text);
    return true;
  } catch {
    try {
      const ta = document.createElement('textarea');
      ta.value = text;
      ta.style.position = 'fixed';
      ta.style.opacity = '0';
      document.body.appendChild(ta);
      ta.select();
      document.execCommand('copy');
      document.body.removeChild(ta);
      return true;
    } catch {
      return false;
    }
  }
}

function debounce(fn, ms) {
  let timer;
  return (...args) => {
    clearTimeout(timer);
    timer = setTimeout(() => fn(...args), ms);
  };
}

function setupSearchBox(inputId, suggestionsId, onPick) {
  const input = document.getElementById(inputId);
  const box = document.getElementById(suggestionsId);

  const runSearch = debounce(async (q) => {
    if (!q || q.length < 2) {
      box.classList.remove('open');
      box.innerHTML = '';
      return;
    }
    const res = await fetch(`/api/search?q=${encodeURIComponent(q)}`);
    const nodes = await res.json();
    renderSuggestions(nodes);
  }, 250);

  function renderSuggestions(nodes) {
    box.innerHTML = '';
    if (nodes.length === 0) {
      box.classList.remove('open');
      return;
    }
    for (const node of nodes.slice(0, 15)) {
      const item = document.createElement('div');
      item.className = 'suggestion-item';
      const label = document.createElement('span');
      label.textContent = node.name || node.public_key.slice(0, 10);
      const role = document.createElement('span');
      role.className = 'role';
      role.textContent = node.role || '';
      item.append(label, role);
      item.addEventListener('click', () => {
        input.value = node.name || node.public_key.slice(0, 10);
        box.classList.remove('open');
        box.innerHTML = '';
        onPick(node);
      });
      box.appendChild(item);
    }
    box.classList.add('open');
  }

  input.addEventListener('input', () => {
    onPick(null); // clear selection until a suggestion is chosen again
    runSearch(input.value.trim());
  });

  document.addEventListener('click', (e) => {
    if (!box.contains(e.target) && e.target !== input) {
      box.classList.remove('open');
    }
  });
}

function updateFindButton() {
  document.getElementById('findBtn').disabled = !(state.from && state.to);
}

const fromInput = document.getElementById('fromInput');
const toInput = document.getElementById('toInput');

function setFrom(node) {
  state.from = node;
  if (node) fromInput.value = node.name || node.public_key.slice(0, 10);
  updateFindButton();
  updateEndpointHighlights();
  if (node) setPickMode('to'); // next map click sets the other end
}

function setTo(node) {
  state.to = node;
  if (node) toInput.value = node.name || node.public_key.slice(0, 10);
  updateFindButton();
  updateEndpointHighlights();
  if (node) setPickMode('from');
}

setupSearchBox('fromInput', 'fromSuggestions', setFrom);
setupSearchBox('toInput', 'toSuggestions', setTo);

document.querySelectorAll('#pickModeSelector button').forEach(btn => {
  btn.addEventListener('click', () => setPickMode(btn.dataset.mode));
});

function setPickMode(mode) {
  state.pickMode = mode;
  document.querySelectorAll('#pickModeSelector button').forEach(b => b.classList.toggle('active', b.dataset.mode === mode));
}

function handleRepeaterClick(node) {
  if (state.pickMode === 'from') setFrom(node);
  else setTo(node);
}

const statusEl = document.getElementById('status');
const resultsEl = document.getElementById('results');
const mapHintEl = document.getElementById('mapHint');
const clearBtn = document.getElementById('clearBtn');
let map, routeLayer, allRepeatersLayer, allRepeatersBounds, fromMarker, toMarker;
let mapMode = 'overview'; // 'overview' (browse all repeaters) | 'route' (showing a found route)

async function initOverviewMap() {
  map = L.map('map');
  L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
    attribution: '© OpenStreetMap contributors',
  }).addTo(map);

  try {
    const res = await fetch('/api/repeaters');
    const repeaters = await res.json();
    allRepeatersLayer = L.markerClusterGroup({ maxClusterRadius: 45 });
    repeaters.forEach(node => {
      const marker = L.marker([node.lat, node.lon]);
      marker.bindTooltip(node.name || node.public_key.slice(0, 8));
      marker.on('click', () => handleRepeaterClick(node));
      allRepeatersLayer.addLayer(marker);
    });
    allRepeatersLayer.addTo(map);
    if (repeaters.length) {
      allRepeatersBounds = L.latLngBounds(repeaters.map(n => [n.lat, n.lon]));
      map.fitBounds(allRepeatersBounds, { padding: [20, 20] });
    }
    mapHintEl.textContent = `Click any of ${repeaters.length} repeaters to set it as From or To.`;
  } catch (err) {
    mapHintEl.textContent = `Could not load the repeater list: ${err.message}`;
  }
}

function updateEndpointHighlights() {
  if (fromMarker) { map.removeLayer(fromMarker); fromMarker = null; }
  if (toMarker) { map.removeLayer(toMarker); toMarker = null; }
  if (mapMode !== 'overview') return;

  if (state.from?.lat != null && state.from?.lon != null) {
    fromMarker = L.circleMarker([state.from.lat, state.from.lon], { radius: 10, color: '#3fb950', weight: 3, fillOpacity: 0.3 })
      .bindTooltip('FROM: ' + (state.from.name || state.from.public_key.slice(0, 8)), { permanent: true, className: 'map-pin-tooltip from-tooltip', direction: 'top' })
      .addTo(map);
  }
  if (state.to?.lat != null && state.to?.lon != null) {
    toMarker = L.circleMarker([state.to.lat, state.to.lon], { radius: 10, color: '#f85149', weight: 3, fillOpacity: 0.3 })
      .bindTooltip('TO: ' + (state.to.name || state.to.public_key.slice(0, 8)), { permanent: true, className: 'map-pin-tooltip to-tooltip', direction: 'top' })
      .addTo(map);
  }
}

function enterRouteMode() {
  mapMode = 'route';
  if (allRepeatersLayer) map.removeLayer(allRepeatersLayer);
  updateEndpointHighlights(); // removes from/to highlight markers (not shown in route mode)
  clearBtn.hidden = false;
  mapHintEl.textContent = 'Click a route below to show it here.';
}

function enterOverviewMode() {
  mapMode = 'overview';
  if (routeLayer) { routeLayer.remove(); routeLayer = null; }
  document.getElementById('mapMessage').hidden = true;
  if (allRepeatersLayer) allRepeatersLayer.addTo(map);
  updateEndpointHighlights();
  clearBtn.hidden = true;
  mapHintEl.textContent = 'Click a repeater to set it as From or To.';
}

function clearSelection() {
  setFrom(null);
  setTo(null);
  fromInput.value = '';
  toInput.value = '';
  setPickMode('from');
  resultsEl.innerHTML = '';
  renderedRoutes = [];
  setStatus('');
  enterOverviewMode();
  if (allRepeatersBounds) map.fitBounds(allRepeatersBounds, { padding: [20, 20] });
}

clearBtn.addEventListener('click', clearSelection);
initOverviewMap();

const nodeInfoOverlay = document.getElementById('nodeInfoOverlay');
const nodeInfoBody = document.getElementById('nodeInfoBody');

function closeNodeInfo() { nodeInfoOverlay.hidden = true; }

document.getElementById('nodeInfoClose').addEventListener('click', closeNodeInfo);
nodeInfoOverlay.addEventListener('click', (e) => { if (e.target === nodeInfoOverlay) closeNodeInfo(); });
document.addEventListener('keydown', (e) => { if (e.key === 'Escape') closeNodeInfo(); });

async function showNodeInfo(pubkey) {
  nodeInfoOverlay.hidden = false;
  nodeInfoBody.innerHTML = '<p class="node-info-loading">Loading…</p>';

  try {
    const res = await fetch(`/api/node/${pubkey}`);
    if (!res.ok) {
      nodeInfoBody.innerHTML = '<p class="node-info-error">Could not load this node (it may not be in CoreScope\'s database).</p>';
      return;
    }
    const node = await res.json();
    const rows = [
      ['Public key', `<span class="path-preview">${node.public_key}</span>`],
      ['Location', node.lat != null && node.lon != null ? `${node.lat.toFixed(5)}, ${node.lon.toFixed(5)}` : 'unknown'],
      ['First seen', node.first_seen ? new Date(node.first_seen).toLocaleString() : 'unknown'],
      ['Last seen', node.last_seen ? `${new Date(node.last_seen).toLocaleString()} (${relativeTime(node.last_seen)})` : 'unknown'],
      ['Advert count', node.advert_count ?? 0],
    ];
    nodeInfoBody.innerHTML = `
      <h2>${escapeHtml(node.name || node.public_key.slice(0, 10))}</h2>
      <span class="role-badge">${escapeHtml(node.role || 'unknown')}</span>
      <table class="node-info-table">
        ${rows.map(([k, v]) => `<tr><td>${k}</td><td>${v}</td></tr>`).join('')}
      </table>
      <a class="node-info-link" href="https://meshcore.meshat.se/#/nodes/${node.public_key}" target="_blank" rel="noopener">
        View full details on CoreScope →
      </a>`;
  } catch (err) {
    nodeInfoBody.innerHTML = `<p class="node-info-error">Error: ${escapeHtml(err.message)}</p>`;
  }
}

function escapeHtml(s) {
  return String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

function setStatus(text, isError) {
  statusEl.textContent = text || '';
  statusEl.classList.toggle('error', !!isError);
}

function relativeTime(iso) {
  if (!iso) return 'unknown';
  const diffMs = Date.now() - new Date(iso).getTime();
  const mins = Math.round(diffMs / 60000);
  if (mins < 60) return `${mins}m ago`;
  const hours = Math.round(mins / 60);
  if (hours < 48) return `${hours}h ago`;
  return `${Math.round(hours / 24)}d ago`;
}

function sourceLabel(source) {
  return { Observed: 'observed', Stitched: 'stitched', Computed: 'computed' }[source] || source.toLowerCase();
}

function renderRoute(route, isBest) {
  const card = document.createElement('div');
  card.className = isBest ? 'route-card best' : 'route-card';
  card.dataset.idx = renderedRoutes.push(route) - 1;
  card.addEventListener('click', (e) => {
    if (e.target.closest('.copy-btn')) return;
    selectRoute(Number(card.dataset.idx));
  });

  const header = document.createElement('div');
  header.className = 'route-card-header';

  if (isBest) {
    const star = document.createElement('span');
    star.className = 'badge best-badge';
    star.textContent = '★ best route';
    header.appendChild(star);
  }

  const badge = document.createElement('span');
  const label = sourceLabel(route.source);
  badge.className = `badge ${label}`;
  badge.textContent = label;
  header.appendChild(badge);

  const meta = document.createElement('span');
  meta.className = 'route-meta';
  meta.textContent = `${route.hopCount} hop${route.hopCount === 1 ? '' : 's'}`
    + (route.lastSeen ? ` · last seen ${relativeTime(route.lastSeen)}` : '')
    + (route.minObservationCount ? ` · seen ${route.minObservationCount}×` : '');
  header.appendChild(meta);

  card.appendChild(header);

  const chain = document.createElement('div');
  chain.className = 'hop-chain';
  route.hops.forEach((hop, i) => {
    if (i > 0) {
      const arrow = document.createElement('span');
      arrow.className = 'hop-arrow';
      arrow.textContent = '→';
      chain.appendChild(arrow);
    }
    const hopEl = document.createElement('span');
    hopEl.className = 'hop';
    hopEl.textContent = hop.name || hop.pubkey.slice(0, 8);
    hopEl.addEventListener('click', (e) => {
      e.stopPropagation();
      showNodeInfo(hop.pubkey);
    });
    chain.appendChild(hopEl);
  });
  card.appendChild(chain);

  const copyRow = document.createElement('div');
  copyRow.className = 'copy-row';

  const copyBtn = document.createElement('button');
  copyBtn.className = 'copy-btn';
  copyBtn.textContent = 'Copy path';

  const preview = document.createElement('span');
  preview.className = 'path-preview';
  preview.textContent = pathPreviewText(route);

  copyBtn.addEventListener('click', async () => {
    const text = buildPathString(route.hops, state.pathByteSize);
    const ok = await copyToClipboard(text);
    copyBtn.textContent = ok ? 'Copied!' : 'Copy failed';
    copyBtn.classList.toggle('copied', ok);
    setTimeout(() => {
      copyBtn.textContent = 'Copy path';
      copyBtn.classList.remove('copied');
    }, 1500);
  });

  copyRow.append(copyBtn, preview);
  card.appendChild(copyRow);

  if (route.note) {
    const note = document.createElement('div');
    note.className = 'note';
    note.textContent = route.note;
    card.appendChild(note);
  }

  return card;
}

let selectedIdx = -1;

function selectRoute(idx) {
  selectedIdx = idx;
  document.querySelectorAll('.route-card').forEach(card => {
    card.classList.toggle('selected', Number(card.dataset.idx) === idx);
  });
  drawMap(renderedRoutes[idx]);
}

function drawMap(route) {
  if (routeLayer) routeLayer.remove();

  const mapMessage = document.getElementById('mapMessage');
  const knownHops = (route?.hops ?? [])
    .map((hop, i) => ({ hop, i }))
    .filter(({ hop }) => hop.lat != null && hop.lon != null);

  if (!route || knownHops.length === 0) {
    mapMessage.textContent = 'None of this route\'s hops have a known location.';
    mapMessage.hidden = false;
    return;
  }

  const missingCount = route.hops.length - knownHops.length;
  if (missingCount > 0) {
    mapMessage.textContent = `${missingCount} hop${missingCount === 1 ? '' : 's'} on this route `
      + `${missingCount === 1 ? "isn't" : "aren't"} shown - no known location.`;
    mapMessage.hidden = false;
  } else {
    mapMessage.hidden = true;
  }

  routeLayer = L.layerGroup();

  // Only connect hops that are consecutive AND both known, so a gap in the middle shows as a
  // break in the line rather than a straight line faking a path across the unknown hop.
  let segment = [];
  let prevIndex = null;
  for (const { hop, i } of knownHops) {
    if (prevIndex !== null && i !== prevIndex + 1) {
      if (segment.length > 1) L.polyline(segment, { color: '#58a6ff', weight: 3 }).addTo(routeLayer);
      segment = [];
    }
    segment.push([hop.lat, hop.lon]);
    prevIndex = i;
  }
  if (segment.length > 1) L.polyline(segment, { color: '#58a6ff', weight: 3 }).addTo(routeLayer);

  knownHops.forEach(({ hop, i }) => {
    const marker = L.circleMarker([hop.lat, hop.lon], {
      radius: 6,
      color: i === 0 || i === route.hops.length - 1 ? '#3fb950' : '#58a6ff',
      fillOpacity: 0.9,
    }).bindTooltip(hop.name || hop.pubkey.slice(0, 8));
    marker.on('click', () => showNodeInfo(hop.pubkey));
    marker.addTo(routeLayer);
  });

  routeLayer.addTo(map);
  map.fitBounds(knownHops.map(({ hop }) => [hop.lat, hop.lon]), { padding: [30, 30] });
}

const findBtn = document.getElementById('findBtn');
const stopBtn = document.getElementById('stopBtn');
let searchController = null;

stopBtn.addEventListener('click', () => searchController?.abort());

findBtn.addEventListener('click', async () => {
  if (!state.from || !state.to) return;
  setStatus('Searching for routes…');
  resultsEl.innerHTML = '';
  renderedRoutes = [];
  searchController = new AbortController();
  findBtn.hidden = true;
  stopBtn.hidden = false;

  try {
    const params = new URLSearchParams({
      from: state.from.public_key, to: state.to.public_key,
      fromName: state.from.name || '', toName: state.to.name || '',
    });
    if (state.from.lat != null) params.set('fromLat', state.from.lat);
    if (state.from.lon != null) params.set('fromLon', state.from.lon);
    if (state.to.lat != null) params.set('toLat', state.to.lat);
    if (state.to.lon != null) params.set('toLon', state.to.lon);

    const res = await fetch(`/api/route?${params}`, { signal: searchController.signal });
    if (!res.ok) {
      const body = await res.json().catch(() => ({}));
      setStatus(body.error || `Request failed (${res.status})`, true);
      return;
    }
    const { best, alternatives } = await res.json();
    if (!best) {
      setStatus('No known or plausible route found between these nodes within the search bounds.', true);
      return;
    }
    const total = 1 + alternatives.length;
    setStatus(`Found ${total} candidate route${total === 1 ? '' : 's'}.`);
    resultsEl.appendChild(renderRoute(best, true));
    alternatives.forEach(r => resultsEl.appendChild(renderRoute(r, false)));
    enterRouteMode();
    selectRoute(0);
  } catch (err) {
    setStatus(err.name === 'AbortError' ? 'Search stopped.' : `Error: ${err.message}`, true);
  } finally {
    stopBtn.hidden = true;
    findBtn.hidden = false;
    searchController = null;
  }
});
