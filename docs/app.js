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

function showError(err) {
  errorBox.replaceChildren();
  const message = err instanceof HighbridgeError ? err.message : `Unexpected error: ${err?.message ?? err}`;
  const text = document.createElement('p');
  text.textContent = message;
  errorBox.append(text);
  if (err instanceof HighbridgeError && err.code === 'setup_required' && err.details?.installerUrl) {
    const link = document.createElement('a');
    link.href = err.details.installerUrl;
    link.target = '_blank';
    link.rel = 'noopener';
    link.textContent = 'Download the highbridge installer';
    errorBox.append(link);
    const hint = document.createElement('p');
    hint.className = 'hint';
    hint.textContent = 'If highbridge is already installed, start it from the Start menu and try again.';
    errorBox.append(hint);
  }
  errorBox.hidden = false;
}

function clearError() {
  errorBox.hidden = true;
  errorBox.replaceChildren();
}

async function refreshStatus() {
  try {
    const status = await client.status();
    if (!status) setConnection('off', 'highbridge not running');
    else if (!status.paired) setConnection('warn', `Connected (${status.backend}) · not approved`);
    else setConnection('ok', `Ready · ${status.backend} · v${status.version}`);
    return status;
  } catch (err) {
    setConnection('off', 'Unavailable');
    return null;
  }
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
  clearError();
  connectButton.disabled = true;
  connectButton.textContent = 'Connecting…';
  try {
    await client.connect();
    await loadScanners();
    connectButton.textContent = 'Refresh';
  } catch (err) {
    showError(err);
    connectButton.textContent = 'Connect';
  } finally {
    connectButton.disabled = false;
    refreshStatus();
  }
});

form.addEventListener('submit', async (event) => {
  event.preventDefault();
  clearError();
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
    showError(err);
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

// Don't trigger the browser's loopback permission prompt on page load; wait for a click.
const permission = await client.loopbackPermission();
if (permission === 'prompt') {
  setConnection('unknown', 'Click Connect to link highbridge');
} else if (permission === 'denied') {
  setConnection('off', 'Blocked by browser settings');
} else {
  const status = await refreshStatus();
  if (status?.paired) loadScanners().catch(() => {});
}
