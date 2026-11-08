import { HighbridgeClient, HighbridgeError, barcodePointsOnImage } from './lib/highbridge.js';
import { installerUrl } from './config.js';

const params = new URLSearchParams(location.search);
const launchTimeoutMs = Number(params.get('launchTimeoutMs'));
const client = new HighbridgeClient({
  port: Number(params.get('port')) || undefined,
  installerUrl,
  ...(launchTimeoutMs > 0 ? { launchTimeoutMs } : {}),
});

const $ = (id) => document.getElementById(id);
const form = $('scan-form');
const scanButton = $('scan');
const connectButton = $('connect');
const scannerSelect = $('scanner');
const progress = $('progress');
const errorBox = $('error');
const pagesList = $('pages');
const empty = $('empty');
const clearButton = $('clear');
const connection = $('connection');

const STATUS_TEXT = {
  queued: 'Waiting for the scanner…',
  scanning: 'Scanning…',
};

const objectUrls = [];

function setConnection(kind, text) {
  connection.className = `pill pill-${kind}`;
  connection.textContent = text;
}

function showError(box, err) {
  box.replaceChildren();
  const message = err instanceof HighbridgeError ? err.message : `Unexpected error: ${err?.message ?? err}`;
  const text = document.createElement('p');
  text.textContent = message;
  box.append(text);
  if (err instanceof HighbridgeError && err.code === 'setup_required' && err.details?.installerUrl) {
    const link = document.createElement('a');
    link.href = err.details.installerUrl;
    link.target = '_blank';
    link.rel = 'noopener';
    link.textContent = 'Download the highbridge installer';
    box.append(link);
    const hint = document.createElement('p');
    hint.className = 'hint';
    hint.textContent = 'If highbridge is already installed, start it from the Start menu and try again.';
    box.append(hint);
  }
  box.hidden = false;
  box.scrollIntoView({ block: 'nearest' });
}

function clearError(box) {
  box.hidden = true;
  box.replaceChildren();
}

async function refreshStatus() {
  try {
    const status = await client.status();
    const devices = deviceLabel(status);
    if (!status) setConnection('off', 'highbridge not running');
    else if (!status.paired) setConnection('warn', `Connected (${devices}) · not approved`);
    else setConnection('ok', `Ready · ${devices} · v${status.version}`);
    return status;
  } catch (err) {
    setConnection('off', 'Unavailable');
    return null;
  }
}

function deviceLabel(status) {
  if (!status?.devices?.length) return status?.backend ?? 'highbridge';
  const names = { scanner: 'scanner', cardReader: 'card reader' };
  return status.devices.map((device) => `${device.backend} ${names[device.kind] ?? device.kind}`).join(', ');
}

async function loadScanners() {
  const scanners = await client.listScanners();
  const current = scannerSelect.value;
  scannerSelect.replaceChildren(new Option('Default scanner', ''));
  for (const s of scanners) {
    scannerSelect.add(new Option(`${s.name}${s.isDefault ? ' (default)' : ''}`, s.id));
  }
  scannerSelect.value = scanners.some((s) => s.id === current) ? current : '';
  scannerSelect.disabled = false;
}

function readOptions() {
  const data = new FormData(form);
  return {
    scannerId: data.get('scannerId') || undefined,
    dpi: Number(data.get('dpi')),
    colorMode: data.get('colorMode'),
    source: data.get('source'),
    duplex: data.get('duplex') === 'on',
    showDriverUi: data.get('showDriverUi') === 'on',
    stamp: data.get('stamp') ? { position: data.get('stamp') } : undefined,
    ...(data.get('barcodes') === 'on' ? { barcodes: true } : {}),
  };
}

/** Shows what highbridge stamped on the page (values come from highbridge, not from this page). */
function provenanceList(p) {
  const list = document.createElement('dl');
  list.className = 'provenance';
  const rows = [
    ['Scan ID', p.scanId],
    ['Scanned', new Date(p.scannedAt).toLocaleString()],
    ['Computer', p.workstation],
    ['User', p.user],
    ['Scanner', p.scanner],
    ['Page', `${p.page} of ${p.pageCount}`],
    ['SHA-256', `${p.originalSha256.slice(0, 16)}…`],
  ];
  for (const [label, value] of rows) {
    const dt = document.createElement('dt');
    dt.textContent = label;
    const dd = document.createElement('dd');
    dd.textContent = value;
    if (label === 'SHA-256') {
      dd.className = 'hash';
      dd.title = p.originalSha256;
    }
    list.append(dt, dd);
  }
  return list;
}

const SVG = 'http://www.w3.org/2000/svg';

/** Outlines each barcode on the thumbnail. Points are page pixels; 1D codes have two (the scan line). */
function barcodeOverlay(page) {
  const svg = document.createElementNS(SVG, 'svg');
  svg.setAttribute('viewBox', `0 0 ${page.width} ${page.height}`);
  svg.setAttribute('preserveAspectRatio', 'xMidYMid meet');
  const stroke = Math.max(4, page.width / 150);
  for (const code of page.barcodes ?? []) {
    // Points are on the unstamped scan. A top band pushes that image down.
    let points = barcodePointsOnImage(page.provenance, code.points);
    if (points.length === 2) {
      const line = document.createElementNS(SVG, 'line');
      [['x1', points[0].x], ['y1', points[0].y], ['x2', points[1].x], ['y2', points[1].y]].forEach(([k, v]) => line.setAttribute(k, v));
      line.setAttribute('stroke-width', stroke * 2);
      svg.append(line);
      continue;
    }
    // QR codes report three finder patterns: add the fourth corner.
    if (points.length === 3) points = [...points, { x: points[0].x + points[2].x - points[1].x, y: points[0].y + points[2].y - points[1].y }];
    const polygon = document.createElementNS(SVG, 'polygon');
    polygon.setAttribute('points', convexHull(points).map((p) => `${p.x},${p.y}`).join(' '));
    polygon.setAttribute('stroke-width', stroke);
    svg.append(polygon);
  }
  return svg;
}

function convexHull(points) {
  const sorted = [...points].sort((a, b) => a.x - b.x || a.y - b.y);
  const cross = (o, a, b) => (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);
  const half = (list) => list.reduce((hull, p) => {
    while (hull.length >= 2 && cross(hull[hull.length - 2], hull[hull.length - 1], p) <= 0) hull.pop();
    hull.push(p);
    return hull;
  }, []);
  const lower = half(sorted);
  const upper = half([...sorted].reverse());
  return [...lower.slice(0, -1), ...upper.slice(0, -1)];
}

const BARCODE_STATUS = { partial: ' (search stopped early)', failed: ' (could not be analysed)', skipped: ' (not analysed)' };

function renderBarcodes(barcodes, firstNumber) {
  const rows = $('barcode-rows');
  for (const code of barcodes) {
    const row = rows.insertRow();
    row.insertCell().textContent = String(firstNumber + code.page);
    row.insertCell().textContent = code.format;
    row.insertCell().textContent = code.text;
    row.insertCell().textContent = code.orientation === undefined ? `found at ${code.rotation}°` : `${code.orientation}°`;
  }
  $('barcodes').hidden = rows.rows.length === 0;
}

function renderPages(pages, barcodes) {
  const firstNumber = pagesList.children.length + 1;
  for (const page of pages) {
    const url = URL.createObjectURL(page.blob);
    objectUrls.push(url);
    const number = pagesList.children.length + 1;
    const item = document.createElement('li');
    item.className = 'page';
    item.innerHTML = `
      <a class="thumb" target="_blank" rel="noopener"><img alt="" loading="lazy" /></a>
      <div class="meta">
        <strong>Page ${number}</strong>
        <span>${page.width}×${page.height} · ${(page.size / 1024).toFixed(0)} KB</span>
        <a class="download">Download</a>
      </div>`;
    item.querySelector('img').src = url;
    item.querySelector('img').alt = `Scanned page ${number}`;
    item.querySelector('.thumb').href = url;
    const download = item.querySelector('.download');
    download.href = url;
    download.download = `scan-page-${number}.png`;
    if (page.provenance) item.querySelector('.meta').append(provenanceList(page.provenance));
    if (page.barcodeStatus) {
      item.querySelector('.thumb').append(barcodeOverlay(page));
      const codes = document.createElement('span');
      const count = page.barcodes.length;
      codes.className = count ? 'codes' : 'codes none';
      codes.textContent = `${count ? `${count} barcode${count === 1 ? '' : 's'}` : 'No barcodes'}${BARCODE_STATUS[page.barcodeStatus] ?? ''}`;
      download.before(codes);
    }
    pagesList.append(item);
  }
  if (barcodes) renderBarcodes(barcodes, firstNumber);
  empty.hidden = pagesList.children.length > 0;
  clearButton.hidden = pagesList.children.length === 0;
}

connectButton.addEventListener('click', async () => {
  clearError(errorBox);
  connectButton.disabled = true;
  connectButton.textContent = 'Connecting…';
  try {
    await client.connect();
    await loadScanners();
    connectButton.textContent = 'Refresh';
  } catch (err) {
    showError(errorBox, err);
    connectButton.textContent = 'Connect';
  } finally {
    connectButton.disabled = false;
    refreshStatus();
  }
});

form.addEventListener('submit', async (event) => {
  event.preventDefault();
  clearError(errorBox);
  scanButton.disabled = true;
  scanButton.classList.add('busy');
  progress.textContent = 'Contacting highbridge…';
  try {
    const result = await client.scan(readOptions(), {
      onProgress: (job) => {
        progress.textContent = STATUS_TEXT[job.status] ?? '';
        const scanned = job.pagesScanned ?? job.pages.length;
        if (job.status === 'scanning' && scanned) {
          const reading = job.pages.filter((p) => p.barcodeStatus === 'pending').length;
          progress.textContent = `Scanning… ${scanned} page(s) so far${reading ? ` · reading barcodes on ${reading}` : ''}`;
        }
      },
    });
    renderPages(result.pages, result.barcodes);
    const found = result.barcodes ? ` · ${result.barcodes.length} barcode(s)` : '';
    progress.textContent = `Done · ${result.pages.length} page(s)${found}`;
    if (result.stamp) {
      progress.textContent += ` · stamped (${result.stamp.scanId}${result.stamp.requiredByPolicy ? ', required by policy' : ''})`;
    }
    if (scannerSelect.disabled) loadScanners().catch(() => {});
  } catch (err) {
    progress.textContent = '';
    showError(errorBox, err);
  } finally {
    scanButton.disabled = false;
    scanButton.classList.remove('busy');
    refreshStatus();
  }
});

clearButton.addEventListener('click', () => {
  objectUrls.splice(0).forEach((u) => URL.revokeObjectURL(u));
  pagesList.replaceChildren();
  $('barcode-rows').replaceChildren();
  $('barcodes').hidden = true;
  empty.hidden = false;
  clearButton.hidden = true;
});

const cardForm = $('card-form');
const readButton = $('read-card');
const cardConnect = $('card-connect');
const readerSelect = $('reader');
const cardProgress = $('card-progress');
const cardError = $('card-error');
const cardApps = $('card-apps');
const cardEmpty = $('card-empty');
const cardClear = $('card-clear');
const cardSummary = $('card-summary');
const scanTab = $('tab-scan');
const cardTab = $('tab-card');
const scanView = $('view-scan');
const cardView = $('view-card');

const CARD_STATUS = {
  queued: 'Waiting to read the card…',
  reading: 'Reading the card…',
};

function selectTab(name, { focus = false } = {}) {
  const card = name === 'card';
  scanView.hidden = card;
  cardView.hidden = !card;
  scanTab.setAttribute('aria-selected', String(!card));
  cardTab.setAttribute('aria-selected', String(card));
  scanTab.tabIndex = card ? -1 : 0;
  cardTab.tabIndex = card ? 0 : -1;
  const url = new URL(location.href);
  url.hash = card ? 'card' : '';
  history.replaceState(null, '', url);
  if (focus) (card ? cardTab : scanTab).focus();
  // A click may list readers after Connect already approved the site. Page load must not
  // fetch loopback on its own; the permission check below does that only when it is allowed.
  if (card && focus && readerSelect.disabled && connection.classList.contains('pill-ok')) {
    loadReaders().catch(() => {});
  }
}

function dash(value) {
  return value === undefined || value === null || value === '' ? '—' : String(value);
}

async function loadReaders() {
  const readers = await client.listCardReaders();
  const current = readerSelect.value;
  readerSelect.replaceChildren(new Option('Any slot', ''));
  for (const reader of readers) {
    readerSelect.add(new Option(reader.name, reader.id));
  }
  readerSelect.value = readers.some((reader) => reader.id === current) ? current : '';
  readerSelect.disabled = false;
}

function readCardOptions() {
  const data = new FormData(cardForm);
  const aid = String(data.get('aid') ?? '').trim();
  const wait = Number(data.get('waitForCardMs'));
  return {
    readerId: data.get('readerId') || undefined,
    interface: data.get('interface'),
    ...(aid ? { aid } : {}),
    waitForCardMs: Number.isFinite(wait) ? wait : undefined,
  };
}

function renderCard(result) {
  cardApps.replaceChildren();
  const selectedAid = result.selected.aid.toUpperCase();
  for (const app of result.applications) {
    const selected = app.aid.toUpperCase() === selectedAid;
    const item = document.createElement('li');
    item.className = selected ? 'page card-app selected' : 'page card-app';
    const face = document.createElement('div');
    face.className = 'card-face';
    const chip = document.createElement('span');
    chip.className = 'card-chip';
    chip.textContent = selected ? 'Selected' : 'On card';
    const label = document.createElement('strong');
    label.textContent = app.label || app.preferredName || 'Application';
    face.append(chip, label);
    if (selected) {
      const pan = document.createElement('span');
      pan.className = 'card-pan';
      pan.textContent = result.selected.pan;
      const row = document.createElement('span');
      row.className = 'card-row';
      const name = document.createElement('span');
      name.textContent = dash(result.selected.cardholderName);
      const expiry = document.createElement('span');
      expiry.textContent = result.selected.expiry;
      row.append(name, expiry);
      face.append(pan, row);
    } else {
      const note = document.createElement('span');
      note.textContent = 'Directory entry. Details are returned for the selected application.';
      face.append(note);
    }

    const meta = document.createElement('div');
    meta.className = 'meta';
    const title = document.createElement('strong');
    title.textContent = app.preferredName || app.label || 'Application';
    const aid = document.createElement('span');
    aid.className = 'aid';
    aid.textContent = app.aid;
    const priority = document.createElement('span');
    priority.textContent = app.priority == null ? 'No priority' : `Priority ${app.priority}`;
    meta.append(title, aid, priority);
    item.append(face, meta);
    cardApps.append(item);
  }

  const rows = $('card-rows');
  rows.replaceChildren();
  for (const app of result.applications) {
    const selected = app.aid.toUpperCase() === selectedAid;
    const fields = selected ? result.selected : app;
    const row = rows.insertRow();
    if (selected) row.className = 'selected';
    for (const value of [
      app.aid,
      dash(fields.label),
      dash(fields.preferredName),
      dash(fields.priority),
      selected ? result.selected.pan : '—',
      selected ? result.selected.expiry : '—',
      selected ? dash(result.selected.cardholderName) : '—',
    ]) {
      row.insertCell().textContent = value;
    }
  }
  $('card-details').hidden = rows.rows.length === 0;
  const iface = result.interface === 'contactless' ? 'contactless' : 'contact';
  cardSummary.textContent = `${result.reader.name} · ${iface} · ${result.applications.length} application(s) · ${result.id}`;
  cardSummary.hidden = false;
  cardEmpty.hidden = cardApps.children.length > 0;
  cardClear.hidden = cardApps.children.length === 0;
}

scanTab.addEventListener('click', () => selectTab('scan', { focus: true }));
cardTab.addEventListener('click', () => selectTab('card', { focus: true }));
for (const tab of [scanTab, cardTab]) {
  tab.addEventListener('keydown', (event) => {
    if (event.key !== 'ArrowRight' && event.key !== 'ArrowLeft') return;
    event.preventDefault();
    selectTab(tab === scanTab ? 'card' : 'scan', { focus: true });
  });
}

cardConnect.addEventListener('click', async () => {
  clearError(cardError);
  cardConnect.disabled = true;
  cardConnect.textContent = 'Connecting…';
  try {
    await client.connect();
    await loadReaders();
    cardConnect.textContent = 'Refresh';
  } catch (err) {
    showError(cardError, err);
    cardConnect.textContent = 'Connect';
  } finally {
    cardConnect.disabled = false;
    refreshStatus();
  }
});

cardForm.addEventListener('submit', async (event) => {
  event.preventDefault();
  clearError(cardError);
  readButton.disabled = true;
  readButton.classList.add('busy');
  cardProgress.textContent = 'Contacting highbridge…';
  try {
    const result = await client.readCard(readCardOptions(), {
      onProgress: (job) => {
        cardProgress.textContent = CARD_STATUS[job.status] ?? '';
      },
    });
    renderCard(result);
    const name = result.selected.preferredName || result.selected.label || result.selected.aid;
    cardProgress.textContent = `Done · ${name} · ${result.selected.pan} · ${result.selected.expiry}`;
    if (readerSelect.disabled) loadReaders().catch(() => {});
  } catch (err) {
    cardProgress.textContent = '';
    showError(cardError, err);
  } finally {
    readButton.disabled = false;
    readButton.classList.remove('busy');
    refreshStatus();
  }
});

cardClear.addEventListener('click', () => {
  cardApps.replaceChildren();
  $('card-rows').replaceChildren();
  $('card-details').hidden = true;
  cardSummary.hidden = true;
  cardEmpty.hidden = false;
  cardClear.hidden = true;
});

if (location.hash === '#card') selectTab('card');

// Don't trigger the browser's loopback permission prompt on page load; wait for a click.
const permission = await client.loopbackPermission();
if (permission === 'prompt') {
  setConnection('unknown', 'Click Connect to link highbridge');
} else if (permission === 'denied') {
  setConnection('off', 'Blocked by browser settings');
} else {
  const status = await refreshStatus();
  if (status?.paired) {
    loadScanners().catch(() => {});
    if (!cardView.hidden) loadReaders().catch(() => {});
  }
}
