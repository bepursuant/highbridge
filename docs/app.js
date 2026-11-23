import { barcodePointsOnImage } from './lib/highbridge.js';
import { installerUrl } from './config.js';

const params = new URLSearchParams(location.search);
const scanEl = document.querySelector('hb-scan');
const readEl = document.querySelector('hb-read');
const port = params.get('port');
const launchTimeoutMs = params.get('launchTimeoutMs');
if (installerUrl) {
  scanEl.setAttribute('installer-url', installerUrl);
  readEl.setAttribute('installer-url', installerUrl);
}
if (port) {
  scanEl.setAttribute('port', port);
  readEl.setAttribute('port', port);
}
if (Number(launchTimeoutMs) > 0) {
  scanEl.setAttribute('launch-timeout-ms', launchTimeoutMs);
  readEl.setAttribute('launch-timeout-ms', launchTimeoutMs);
}

const $ = (id) => document.getElementById(id);
const connection = $('connection');
const pagesList = $('pages');
const empty = $('empty');
const clearButton = $('clear');
const objectUrls = [];

function setConnection(kind, text) {
  connection.className = `pill pill-${kind}`;
  connection.textContent = text;
}

scanEl.addEventListener('hb-status', (event) => setConnection(event.detail.kind, event.detail.text));

/** Shows what highbridge stamped on the document (values come from highbridge, not from this page). */
function provenanceList(p) {
  const list = document.createElement('dl');
  list.className = 'provenance';
  const rows = [
    ['Scan ID', p.scanId],
    ['Scanned', new Date(p.scannedAt).toLocaleString()],
    ['Computer', p.workstation],
    ['User', p.user],
    ['Scanner', p.scanner],
    ['Document', `${p.page} of ${p.pageCount}`],
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

function barcodeOverlay(documentImage) {
  const svg = document.createElementNS(SVG, 'svg');
  svg.setAttribute('viewBox', `0 0 ${documentImage.width} ${documentImage.height}`);
  svg.setAttribute('preserveAspectRatio', 'xMidYMid meet');
  const stroke = Math.max(4, documentImage.width / 150);
  for (const code of documentImage.barcodes ?? []) {
    let points = barcodePointsOnImage(documentImage.provenance, code.points);
    if (points.length === 2) {
      const line = document.createElementNS(SVG, 'line');
      [['x1', points[0].x], ['y1', points[0].y], ['x2', points[1].x], ['y2', points[1].y]].forEach(([k, v]) => line.setAttribute(k, v));
      line.setAttribute('stroke-width', stroke * 2);
      svg.append(line);
      continue;
    }
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

function renderDocuments(documents) {
  const firstNumber = pagesList.children.length + 1;
  for (const documentImage of documents) {
    const url = URL.createObjectURL(documentImage.blob);
    objectUrls.push(url);
    const number = pagesList.children.length + 1;
    const item = document.createElement('li');
    item.className = 'page';
    const stacked = documentImage.pageCount > 1 ? ` · ${documentImage.pageCount} pages` : '';
    item.innerHTML = `
      <a class="thumb" target="_blank" rel="noopener"><img alt="" loading="lazy" /></a>
      <div class="meta">
        <strong>Document ${number}</strong>
        <span>${documentImage.width}×${documentImage.height}${stacked} · ${(documentImage.size / 1024).toFixed(0)} KB</span>
        <a class="download">Download</a>
      </div>`;
    item.querySelector('img').src = url;
    item.querySelector('img').alt = `Scanned document ${number}`;
    item.querySelector('.thumb').href = url;
    const download = item.querySelector('.download');
    download.href = url;
    download.download = `scan-document-${number}.png`;
    if (documentImage.provenance) item.querySelector('.meta').append(provenanceList(documentImage.provenance));
    if (documentImage.barcodeStatus) {
      item.querySelector('.thumb').append(barcodeOverlay(documentImage));
      const codes = document.createElement('span');
      const count = documentImage.barcodes.length;
      codes.className = count ? 'codes' : 'codes none';
      codes.textContent = `${count ? `${count} barcode${count === 1 ? '' : 's'}` : 'No barcodes'}${BARCODE_STATUS[documentImage.barcodeStatus] ?? ''}`;
      download.before(codes);
    }
    pagesList.append(item);
  }
  const found = documents.flatMap((documentImage) => documentImage.barcodes ?? []);
  if (found.length > 0) renderBarcodes(found, firstNumber);
  empty.hidden = pagesList.children.length > 0;
  clearButton.hidden = pagesList.children.length === 0;
}

scanEl.addEventListener('hb-result', (event) => {
  renderDocuments(event.detail.documents);
});

clearButton.addEventListener('click', () => {
  objectUrls.splice(0).forEach((u) => URL.revokeObjectURL(u));
  pagesList.replaceChildren();
  $('barcode-rows').replaceChildren();
  $('barcodes').hidden = true;
  empty.hidden = false;
  clearButton.hidden = true;
});

const cardApps = $('card-apps');
const cardEmpty = $('card-empty');
const cardClear = $('card-clear');
const cardSummary = $('card-summary');
const scanTab = $('tab-scan');
const cardTab = $('tab-card');
const scanView = $('view-scan');
const cardView = $('view-card');

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
}

function dash(value) {
  return value === undefined || value === null || value === '' ? '—' : String(value);
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

readEl.addEventListener('hb-result', (event) => renderCard(event.detail));

scanTab.addEventListener('click', () => selectTab('scan', { focus: true }));
cardTab.addEventListener('click', () => selectTab('card', { focus: true }));
for (const tab of [scanTab, cardTab]) {
  tab.addEventListener('keydown', (event) => {
    if (event.key !== 'ArrowRight' && event.key !== 'ArrowLeft') return;
    event.preventDefault();
    selectTab(tab === scanTab ? 'card' : 'scan', { focus: true });
  });
}

cardClear.addEventListener('click', () => {
  cardApps.replaceChildren();
  $('card-rows').replaceChildren();
  $('card-details').hidden = true;
  cardSummary.hidden = true;
  cardEmpty.hidden = false;
  cardClear.hidden = true;
});

if (location.hash === '#card') selectTab('card');
