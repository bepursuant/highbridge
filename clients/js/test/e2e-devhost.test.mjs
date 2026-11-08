// End-to-end: drives the real dev host (mock scanner) through the JS client, including the
// highbridge://scan path, where "opening the link" runs a second dev host process that forwards
// the URL to the running one over the single-instance pipe (what Windows does via the registry).
//
// Run: dotnet build src/Highbridge.DevHost && HIGHBRIDGE_DEVHOST_DLL=src/Highbridge.DevHost/bin/Debug/net10.0/Highbridge.DevHost.dll node --test "clients/js/test/*.test.mjs"

import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { spawn, execFile } from 'node:child_process';
import { HighbridgeClient, HighbridgeError } from '../highbridge.js';

const DLL = process.env.HIGHBRIDGE_DEVHOST_DLL;
const PORT = 47190 + Math.floor(Math.random() * 9);
const ORIGIN = 'https://app.example.com';
let host;

// Node's fetch has no page origin; add the header a browser would send.
const withOrigin = (origin) => (url, init = {}) => fetch(url, { ...init, headers: { ...(init.headers ?? {}), Origin: origin } });

before(async () => {
  if (!DLL) return;
  host = spawn('dotnet', [DLL, '--port', String(PORT), '--page-delay-ms', '5'], { stdio: ['ignore', 'pipe', 'inherit'] });
  const probe = new HighbridgeClient({ port: PORT, fetch: withOrigin(ORIGIN), origin: ORIGIN, storage: null });
  for (let i = 0; i < 100 && !(await probe.status()); i++) await new Promise((r) => setTimeout(r, 100));
});

after(() => host?.kill());

test('e2e: pair + scan over HTTP returns real PNG pages', { skip: !DLL && 'set HIGHBRIDGE_DEVHOST_DLL' }, async () => {
  const client = new HighbridgeClient({ port: PORT, fetch: withOrigin(ORIGIN), origin: ORIGIN, storage: null, pollIntervalMs: 50 });
  const status = await client.connect({ launch: false });
  assert.equal(status.backend, 'mock');

  const scanners = await client.listScanners();
  assert.ok(scanners.some((s) => s.id === 'mock-feeder'));

  const result = await client.scan({ source: 'feeder', duplex: true, maxPages: 4, colorMode: 'gray' });
  assert.equal(result.pages.length, 4);
  const bytes = new Uint8Array(await result.pages[0].blob.arrayBuffer());
  assert.deepEqual([...bytes.slice(1, 4)], [80, 78, 71]); // "PNG"
});

test('e2e: highbridge://scan forwarded from a second process is collected by request id', { skip: !DLL && 'set HIGHBRIDGE_DEVHOST_DLL' }, async () => {
  const storage = new Map();
  storage.set('highbridge.paired', `http://127.0.0.1:${PORT}`);
  let online = false;
  const launched = [];
  const client = new HighbridgeClient({
    port: PORT,
    origin: ORIGIN,
    pollIntervalMs: 50,
    launchTimeoutMs: 15000,
    storage: { getItem: (k) => storage.get(k) ?? null, setItem: (k, v) => storage.set(k, v), removeItem: (k) => storage.delete(k) },
    // Pretend the app is down for the first status() call so the client takes the protocol path.
    fetch: (url, init) => (online ? withOrigin(ORIGIN)(url, init) : Promise.reject(new TypeError('Failed to fetch'))),
    launch: (url) => {
      launched.push(url);
      online = true;
      execFile('dotnet', [DLL, '--port', String(PORT), url]);
    },
  });

  const result = await client.scan({ source: 'flatbed', dpi: 150 });
  assert.equal(launched.length, 1);
  assert.match(launched[0], /^highbridge:\/\/scan\?/);
  assert.equal(result.pages.length, 1);
});

test('e2e: stamped scan adds the provenance band and returns the record', { skip: !DLL && 'set HIGHBRIDGE_DEVHOST_DLL' }, async () => {
  const client = new HighbridgeClient({ port: PORT, fetch: withOrigin(ORIGIN), origin: ORIGIN, storage: null, pollIntervalMs: 50 });
  await client.connect({ launch: false });
  const plain = await client.scan({ source: 'flatbed', colorMode: 'gray' });

  const result = await client.scan({ source: 'feeder', maxPages: 2, colorMode: 'gray', stamp: { position: 'bottom' } });

  assert.match(result.stamp.scanId, /^HB-[0-9A-Z]{4}-[0-9A-Z]{4}$/);
  assert.equal(result.pages.length, 2);
  for (const [i, page] of result.pages.entries()) {
    const p = page.provenance;
    assert.equal(p.origin, ORIGIN);
    assert.equal(p.scanId, result.stamp.scanId);
    assert.deepEqual([p.page, p.pageCount], [i + 1, 2]);
    assert.match(p.originalSha256, /^[0-9a-f]{64}$/);
    assert.match(p.user, /\\/);
    assert.equal(page.height, plain.pages[0].height + p.bandHeight);
    const text = new TextDecoder('latin1').decode(await page.blob.arrayBuffer());
    assert.ok(text.includes('iTXthighbridge:provenance'));
    assert.ok(text.includes(p.originalSha256));
  }
  assert.equal(plain.pages[0].provenance, undefined);
});

test('e2e: pages cannot put their own text in the stamp', { skip: !DLL && 'set HIGHBRIDGE_DEVHOST_DLL' }, async () => {
  const client = new HighbridgeClient({ port: PORT, fetch: withOrigin(ORIGIN), origin: ORIGIN, storage: null });
  await client.connect({ launch: false });
  await assert.rejects(client._json('POST', '/v1/scans', { stamp: { position: 'top', user: 'CEO' } }), (err) => err.code === 'invalid_options');
});

test('e2e: barcode scan returns rotated codes from the mock barcode sheet', { skip: !DLL && 'set HIGHBRIDGE_DEVHOST_DLL' }, async () => {
  const client = new HighbridgeClient({ port: PORT, fetch: withOrigin(ORIGIN), origin: ORIGIN, storage: null, pollIntervalMs: 50 });
  await client.connect({ launch: false });

  const result = await client.scan({ scannerId: 'mock-barcodes', dpi: 120, maxPages: 1, barcodes: true });

  assert.equal(result.pages.length, 1);
  assert.equal(result.pages[0].barcodeStatus, 'done');
  const byText = Object.fromEntries(result.barcodes.map((b) => [b.text, b]));
  const upright = byText['https://highbridge.example/doc/P1-R0'];
  const turned = byText['PT-C128-P1-R270'];
  assert.equal(upright.format, 'qrCode');
  assert.equal(upright.rotation, 0);
  assert.equal(upright.orientation, 0);
  assert.ok(upright.points.length >= 3);
  assert.equal(turned.format, 'code128');
  assert.equal(turned.rotation, 90);
  assert.equal(turned.orientation, 270);
  assert.equal(result.pages[0].barcodes.length, result.barcodes.length);

  await assert.rejects(
    client._json('POST', '/v1/scans', { barcodeFormats: ['qrCode'] }),
    (err) => err.code === 'invalid_options',
  );
});

test('e2e: readCard returns the mock card and the failure readers', { skip: !DLL && 'set HIGHBRIDGE_DEVHOST_DLL' }, async () => {
  const client = new HighbridgeClient({ port: PORT, fetch: withOrigin(ORIGIN), origin: ORIGIN, storage: null, pollIntervalMs: 50 });
  await client.connect({ launch: false });
  const readers = await client.listCardReaders();
  assert.ok(readers.some((reader) => reader.id === 'mock-reader'));

  const result = await client.readCard();
  assert.equal(result.interface, 'contact');
  assert.equal(result.applications.length, 2);
  assert.equal(result.selected.panLast4, '1234');
  assert.equal(result.selected.expiry, '2028-12');
  assert.equal(result.selected.cardholderName, 'DOE/JOHN');

  const debit = await client.readCard({ aid: 'A0000000041010', interface: 'contactless' });
  assert.equal(debit.selected.panLast4, '5678');
  assert.equal(debit.interface, 'contactless');
  await assert.rejects(client.readCard({ readerId: 'mock-no-card' }), (err) => err.code === 'no_card');
  await assert.rejects(client.readCard({ readerId: 'mock-empty' }), (err) => err.code === 'no_payment_apps');
  await assert.rejects(client._json('POST', '/v1/card-reads', { apdu: '00A40400' }), (err) => err.code === 'invalid_options');
});

test('e2e: formats other than png are rejected', { skip: !DLL && 'set HIGHBRIDGE_DEVHOST_DLL' }, async () => {
  const client = new HighbridgeClient({ port: PORT, fetch: withOrigin(ORIGIN), origin: ORIGIN, storage: null });
  await client.connect({ launch: false });
  await assert.rejects(client._json('POST', '/v1/scans', { format: 'pdf' }), (err) => err.code === 'invalid_options');
});

test('e2e: another origin cannot collect the result', { skip: !DLL && 'set HIGHBRIDGE_DEVHOST_DLL' }, async () => {
  const owner = new HighbridgeClient({ port: PORT, fetch: withOrigin(ORIGIN), origin: ORIGIN, storage: null });
  await owner.connect({ launch: false });
  const job = await owner._json('POST', '/v1/scans', { source: 'flatbed' });

  const thief = new HighbridgeClient({ port: PORT, fetch: withOrigin('https://evil.example'), origin: 'https://evil.example', storage: null });
  await thief.pair(); // dev host auto-approves pairing; ownership must still hold
  await assert.rejects(thief._json('GET', `/v1/scans/${job.id}`), (err) => err instanceof HighbridgeError && err.code === 'not_found');
});
