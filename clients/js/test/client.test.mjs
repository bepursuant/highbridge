import { test } from 'node:test';
import assert from 'node:assert/strict';
import { HighbridgeClient, HighbridgeError, barcodePointsOnImage, buildReadCardUrl, buildScanUrl } from '../highbridge.js';

const ORIGIN = 'https://app.example.com';

/** In-memory stand-in for the highbridge API. */
class FakeApp {
  constructor({ running = true, paired = false, reapprove = false } = {}) {
    this.running = running;
    this.paired = paired;
    this.reapprove = reapprove;
    this.jobs = new Map();
    this.cardJobs = new Map();
    this.calls = [];
    this.deleted = [];
  }

  fetch = async (url, init = {}) => {
    const { pathname } = new URL(url);
    const method = init.method ?? 'GET';
    this.calls.push(`${method} ${pathname}`);
    assert.equal(init.targetAddressSpace, 'loopback');
    assert.equal(init.credentials, 'omit');
    if (!this.running) throw new TypeError('Failed to fetch');

    const json = (status, body) => new Response(body === null ? null : JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
    if (method === 'GET' && pathname === '/v1/status') return json(200, { app: 'highbridge', version: '0.1.0', apiVersion: 1, backend: 'mock', paired: this.paired, pairingAllowed: true });
    if (method === 'POST' && pathname === '/v1/pair') { this.paired = true; this.reapprove = false; return json(200, { paired: true }); }
    if (!this.paired) return json(403, { error: 'origin_not_paired', message: 'Pair first.' });
    if (method === 'GET' && pathname === '/v1/scanners') return json(200, { scanners: [{ id: 'mock-feeder', name: 'Mock', backend: 'mock', isDefault: true }] });
    if (method === 'POST' && pathname === '/v1/scans' && this.reapprove && JSON.parse(init.body ?? '{}').stamp) {
      return json(403, { error: 'approval_required', message: 'Approve this site again.' });
    }
    if (method === 'POST' && pathname === '/v1/scans') return json(202, this.addJob('job123456789abcdef', JSON.parse(init.body ?? '{}')));
    if (method === 'POST' && pathname === '/v1/card-reads' && this.reapprove) {
      return json(403, { error: 'approval_required', message: 'Approve this site again.' });
    }
    if (method === 'POST' && pathname === '/v1/card-reads') return json(202, this.addCard(JSON.parse(init.body ?? '{}').requestId ?? 'card123456789abcdef', JSON.parse(init.body ?? '{}')));

    const cardMatch = pathname.match(/^\/v1\/card-reads\/([^/]+)$/);
    if (cardMatch) {
      const card = this.cardJobs.get(cardMatch[1]);
      if (!card) return json(404, { error: 'not_found', message: 'No such card read.' });
      if (method === 'DELETE') { this.deleted.push(card.id); return json(204, null); }
      card.polls += 1;
      if (card.polls >= 2) card.status = 'completed';
      return json(200, this.cardView(card));
    }

    const match = pathname.match(/^\/v1\/scans\/([^/]+)(?:\/pages\/(\d+))?$/);
    const job = match && this.jobs.get(match[1]);
    if (!job) return json(404, { error: 'not_found', message: 'No such scan.' });
    if (method === 'DELETE') { this.deleted.push(job.id); return json(204, null); }
    if (match[2] !== undefined) return new Response(new Uint8Array([137, 80, 78, 71]), { status: 200, headers: { 'Content-Type': 'image/png' } });
    job.polls += 1;
    if (job.polls >= 2) job.status = job.outcome;
    return json(200, this.view(job));
  };

  addCard(id, options) {
    const job = { id, options, polls: 0, status: 'queued' };
    this.cardJobs.set(id, job);
    return this.cardView(job);
  }

  cardView(job) {
    const done = job.status === 'completed';
    return {
      id: job.id,
      status: job.status,
      reader: done ? { id: 'mock-reader', name: 'Mock' } : undefined,
      interface: done ? 'contact' : undefined,
      applications: done ? [{ aid: 'A0000000031010', label: 'VISA CREDIT', preferredName: 'Visa', priority: 1 }] : undefined,
      selected: done ? {
        aid: 'A0000000031010', label: 'VISA CREDIT', preferredName: 'Visa', priority: 1,
        panLast4: '1234', expiry: '2028-12', cardholderName: 'DOE/JOHN',
      } : undefined,
    };
  }

  addJob(id, options, outcome = 'completed') {
    const job = { id, options, polls: 0, status: 'queued', outcome };
    this.jobs.set(id, job);
    return this.view(job);
  }

  view(job) {
    const done = job.status === 'completed';
    return {
      id: job.id,
      status: job.status,
      trigger: 'api',
      format: 'png',
      createdAt: new Date().toISOString(),
      error: job.status === 'failed' ? { error: 'paper_jam', message: 'The scanner reported a paper jam.' } : undefined,
      pages: done ? [{
        index: 0,
        mediaType: 'image/png',
        width: 10,
        height: 14,
        size: 4,
        url: `/v1/scans/${job.id}/pages/0`,
        ...(job.options.stamp ? { provenance: { scanId: 'PT-AAAA-BBBB', page: 1, pageCount: 1 } } : {}),
        ...(job.options.barcodes ? { barcodeStatus: 'done', barcodes: found } : {}),
      }] : [],
      pagesScanned: done ? 1 : 0,
      ...(job.options.stamp ? { stamp: { position: job.options.stamp.position ?? 'bottom', requiredByPolicy: false, scanId: 'PT-AAAA-BBBB' } } : {}),
      ...(job.options.barcodes && done ? { barcodes: found } : {}),
    };
  }
}

const found = [{ page: 0, format: 'qrCode', text: 'PT', rotation: 180, orientation: 180, points: [{ x: 1, y: 2 }, { x: 9, y: 2 }] }];

function memoryStorage() {
  const map = new Map();
  return { getItem: (k) => map.get(k) ?? null, setItem: (k, v) => map.set(k, v), removeItem: (k) => map.delete(k) };
}

function client(app, extra = {}) {
  return new HighbridgeClient({ fetch: app.fetch, origin: ORIGIN, pollIntervalMs: 1, launchTimeoutMs: 50, storage: memoryStorage(), ...extra });
}

test('buildScanUrl encodes origin, request id and options', () => {
  const url = buildScanUrl(ORIGIN, 'abc123abc123abc123', { dpi: 300, colorMode: 'gray', duplex: true, scannerId: 'A & B', format: 'png' });
  const parsed = new URL(url);
  assert.equal(parsed.protocol, 'highbridge:');
  assert.equal(parsed.searchParams.get('origin'), ORIGIN);
  assert.equal(parsed.searchParams.get('request'), 'abc123abc123abc123');
  assert.equal(parsed.searchParams.get('scannerId'), 'A & B');
  assert.equal(parsed.searchParams.get('duplex'), 'true');
  assert.equal(parsed.searchParams.get('format'), 'png');
  assert.equal(parsed.searchParams.get('v'), '1');
  assert.equal(parsed.searchParams.get('barcodes'), null);

  const withCodes = new URL(buildScanUrl(ORIGIN, 'abc123abc123abc123', { barcodes: true, barcodeFormats: ['qrCode', 'code128'] }));
  assert.equal(withCodes.searchParams.get('barcodes'), 'true');
  assert.equal(withCodes.searchParams.get('barcodeFormats'), 'qrCode,code128');
});

test('scan returns the barcodes the app found', async () => {
  const app = new FakeApp({ paired: true });
  const result = await client(app).scan({ barcodes: true, barcodeFormats: ['qrCode'] });

  assert.deepEqual(app.jobs.get('job123456789abcdef').options.barcodeFormats, ['qrCode']);
  assert.equal(result.barcodes[0].text, 'PT');
  assert.equal(result.barcodes[0].rotation, 180);
  assert.equal(result.pages[0].barcodeStatus, 'done');
  assert.equal(result.pages[0].barcodes[0].points.length, 2);
});

test('scan pairs an unpaired site, polls to completion, downloads pages and cleans up', async () => {
  const app = new FakeApp({ paired: false });
  const seen = [];
  const result = await client(app).scan({ dpi: 200 }, { onProgress: (job) => seen.push(job.status) });

  assert.equal(result.pages.length, 1);
  assert.equal(result.pages[0].blob.type, 'image/png');
  assert.ok(app.calls.includes('POST /v1/pair'));
  assert.deepEqual(app.jobs.get('job123456789abcdef').options, { dpi: 200 });
  assert.equal(seen.at(-1), 'completed');
  await new Promise((r) => setTimeout(r, 5));
  assert.deepEqual(app.deleted, ['job123456789abcdef']);
});

test('failed scans reject with the server error code', async () => {
  const app = new FakeApp({ paired: true });
  app.fetch = ((inner) => async (url, init) => {
    if (init?.method === 'POST' && url.endsWith('/v1/scans')) {
      return new Response(JSON.stringify(app.addJob('jammedjob1234567890', {}, 'failed')), { status: 202 });
    }
    return inner(url, init);
  })(app.fetch);

  await assert.rejects(client(app).scan(), (err) => err instanceof HighbridgeError && err.code === 'paper_jam');
});

test('when the app is not running and the site was paired before, scan launches highbridge://scan', async () => {
  const app = new FakeApp({ running: false, paired: true });
  const storage = memoryStorage();
  storage.setItem('highbridge.paired', 'http://127.0.0.1:47116');
  const launched = [];
  const c = client(app, {
    storage,
    launch: (url) => {
      launched.push(url);
      const request = new URL(url).searchParams.get('request');
      setTimeout(() => { app.running = true; app.addJob(request, {}); }, 5);
    },
  });

  const result = await c.scan({ colorMode: 'blackWhite' });

  assert.equal(launched.length, 1);
  const url = new URL(launched[0]);
  assert.equal(url.host, 'scan');
  assert.equal(url.searchParams.get('origin'), ORIGIN);
  assert.equal(url.searchParams.get('colorMode'), 'blackWhite');
  assert.match(url.searchParams.get('request'), /^[0-9a-f]{32}$/);
  assert.equal(result.pages.length, 1);
});

test('when the app is not running and the site was never paired, scan launches the app then pairs over HTTP', async () => {
  const app = new FakeApp({ running: false, paired: false });
  const launched = [];
  const c = client(app, { launch: (url) => { launched.push(url); app.running = true; } });

  const result = await c.scan();

  assert.deepEqual(launched, ['highbridge://launch']);
  assert.ok(app.calls.includes('POST /v1/pair'));
  assert.equal(result.pages.length, 1);
});

test('reports not_running when the app never comes up', async () => {
  const app = new FakeApp({ running: false });
  await assert.rejects(client(app, { launch: () => {} }).connect(), (err) => err.code === 'not_running');
});

test('offers the one-time installer when a download URL is configured and the app never starts', async () => {
  const app = new FakeApp({ running: false });
  const installerUrl = 'https://github.com/bepursuant/highbridge/releases/latest/download/highbridge-x86.msi';
  const launched = [];
  await assert.rejects(
    client(app, { launch: (url) => launched.push(url), installerUrl }).scan(),
    (err) => err instanceof HighbridgeError
      && err.code === 'setup_required'
      && err.message === 'Please complete this one-time setup.'
      && err.details.installerUrl === installerUrl,
  );
  assert.deepEqual(launched, ['highbridge://launch']);
});

test('retries without targetAddressSpace when the browser rejects the value', async () => {
  const app = new FakeApp({ paired: true });
  const seen = [];
  const fetchImpl = async (url, init) => {
    seen.push(init.targetAddressSpace);
    if (init.targetAddressSpace === 'loopback') {
      throw new TypeError("Failed to read the 'targetAddressSpace' property from 'RequestInit': The provided value 'loopback' is not a valid enum value");
    }
    return app.fetch(url, { ...init, targetAddressSpace: 'loopback' });
  };
  const c = client(app, { fetch: fetchImpl });

  assert.equal((await c.status()).paired, true);
  await c.status();
  assert.deepEqual(seen, ['loopback', undefined, undefined]);
});

test('status() returns null when unreachable', async () => {
  assert.equal(await client(new FakeApp({ running: false })).status(), null);
});

test('a top stamp shifts barcode outlines down by the band; a bottom stamp does not', () => {
  const points = [{ x: 10, y: 20 }, { x: 30, y: 40 }];
  assert.deepEqual(barcodePointsOnImage({ position: 'top', bandHeight: 80 }, points), [{ x: 10, y: 100 }, { x: 30, y: 120 }]);
  assert.equal(barcodePointsOnImage({ position: 'bottom', bandHeight: 80 }, points), points);
  assert.equal(barcodePointsOnImage(undefined, points), points);
});

test('buildScanUrl carries only the stamp position', () => {
  assert.equal(new URL(buildScanUrl(ORIGIN, 'abc123abc123abc123', { stamp: { position: 'top' } })).searchParams.get('stamp'), 'top');
  assert.equal(new URL(buildScanUrl(ORIGIN, 'abc123abc123abc123', { stamp: {} })).searchParams.get('stamp'), 'bottom');
  assert.equal(new URL(buildScanUrl(ORIGIN, 'abc123abc123abc123', { stamp: null })).searchParams.has('stamp'), false);
});

test('readCard returns the allowlisted fields and re-approves an old approval once', async () => {
  const app = new FakeApp({ paired: true, reapprove: true });
  const result = await client(app).readCard({ interface: 'contact', waitForCardMs: 1000 });
  assert.deepEqual(app.calls.filter((c) => c.startsWith('POST')), ['POST /v1/card-reads', 'POST /v1/pair', 'POST /v1/card-reads']);
  assert.equal(result.selected.panLast4, '1234');
  assert.equal(result.selected.expiry, '2028-12');
  assert.equal(result.interface, 'contact');
  assert.equal(app.deleted[0], result.id);
});

test('when the app is not running and the site was paired before, readCard launches highbridge://read-card', async () => {
  const app = new FakeApp({ running: false, paired: true });
  const storage = memoryStorage();
  storage.setItem('highbridge.paired', 'http://127.0.0.1:47116');
  const launched = [];
  const c = client(app, {
    storage,
    launch: (url) => {
      launched.push(url);
      const request = new URL(url).searchParams.get('request');
      setTimeout(() => { app.running = true; app.addCard(request, {}); }, 5);
    },
  });

  const result = await c.readCard({ aid: 'A0000000031010' });
  const url = new URL(launched[0]);
  assert.equal(url.host, 'read-card');
  assert.equal(url.searchParams.get('aid'), 'A0000000031010');
  assert.equal(result.selected.cardholderName, 'DOE/JOHN');
});

test('buildReadCardUrl carries the card options and nothing else', () => {
  const url = new URL(buildReadCardUrl(ORIGIN, 'abc123abc123abc123', { readerId: 'A & B', interface: 'contactless', aid: 'A0000000031010', waitForCardMs: 5000 }));
  assert.equal(url.protocol, 'highbridge:');
  assert.equal(url.host, 'read-card');
  assert.equal(url.searchParams.get('readerId'), 'A & B');
  assert.equal(url.searchParams.get('interface'), 'contactless');
  assert.equal(url.searchParams.get('aid'), 'A0000000031010');
  assert.equal(url.searchParams.get('waitForCardMs'), '5000');
  assert.equal(url.searchParams.get('apdu'), null);
});

test('stamped scan returns provenance and re-approves an old approval once', async () => {
  const app = new FakeApp({ paired: true, reapprove: true });

  const result = await client(app).scan({ stamp: { position: 'top' } });

  assert.deepEqual(app.calls.filter((c) => c.startsWith('POST')), ['POST /v1/scans', 'POST /v1/pair', 'POST /v1/scans']);
  assert.deepEqual(result.stamp, { position: 'top', requiredByPolicy: false, scanId: 'PT-AAAA-BBBB' });
  assert.equal(result.pages[0].provenance.scanId, 'PT-AAAA-BBBB');
});
