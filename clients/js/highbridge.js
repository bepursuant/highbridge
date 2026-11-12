// highbridge browser client. ES module, no dependencies.
//
//   import { HighbridgeClient } from './highbridge.js';
//   const client = new HighbridgeClient();
//   button.onclick = async () => {
//     const { pages } = await client.scan({ dpi: 300, colorMode: 'gray', source: 'feeder' });
//     for (const page of pages) show(URL.createObjectURL(page.blob));
//   };
//
// Call scan(), readCard() and connect() from a click handler: browsers require a user gesture
// for the loopback-network permission prompt and for opening highbridge:// links. After the
// one-time site approval, scans and card reads run without any highbridge prompt.

/** highbridge always listens on this port. Override it only to test against a dev host. */
export const DEFAULT_PORT = 47116;
const TERMINAL = new Set(['completed', 'failed', 'canceled']);
const FEEDER_RETRY = new Set(['no_pages', 'no_paper', 'feeder_empty']);
const PAIRED_HINT_KEY = 'highbridge.paired';

export class HighbridgeError extends Error {
  /**
   * @param {string} code machine-readable reason, e.g. 'not_running', 'pairing_denied', 'paper_jam'
   * @param {string} message human-readable explanation
   * @param {object} [details]
   */
  constructor(code, message, details) {
    super(message);
    this.name = 'HighbridgeError';
    this.code = code;
    this.details = details;
  }
}

export class HighbridgeClient {
  /**
   * @param {object} [options]
   * @param {number} [options.port=47116] only for testing against a dev host on another port
   * @param {number} [options.pollIntervalMs=500]
   * @param {number} [options.launchTimeoutMs=20000] how long to wait for the app after opening a highbridge:// link
   * @param {number} [options.scanTimeoutMs=600000]
   * @param {(url: string) => void} [options.launch] opens a highbridge:// URL (default: location.href)
   * @param {typeof fetch} [options.fetch]
   * @param {string} [options.origin] defaults to location.origin
   * @param {Storage | null} [options.storage] remembers that this site was paired (default: localStorage)
   * @param {string} [options.installerUrl] MSI download shown when highbridge cannot be started
   */
  constructor(options = {}) {
    this.port = options.port ?? DEFAULT_PORT;
    this.baseUrl = `http://127.0.0.1:${this.port}`;
    this.pollIntervalMs = options.pollIntervalMs ?? 500;
    this.launchTimeoutMs = options.launchTimeoutMs ?? 20000;
    this.scanTimeoutMs = options.scanTimeoutMs ?? 600000;
    this.installerUrl = options.installerUrl || undefined;
    this._launch = options.launch ?? ((url) => { globalThis.location.href = url; });
    this._fetch = options.fetch ?? globalThis.fetch.bind(globalThis);
    this.origin = options.origin ?? globalThis.location?.origin;
    this._storage = options.storage !== undefined ? options.storage : safeLocalStorage();
  }

  /**
   * State of the browser's loopback-network (Local Network Access) permission:
   * 'granted' | 'prompt' | 'denied' | 'unsupported'.
   */
  async loopbackPermission() {
    if (!globalThis.navigator?.permissions?.query) return 'unsupported';
    try {
      // Never query the legacy 'local-network-access' name: it can crash older Chrome renderers.
      const status = await navigator.permissions.query({ name: 'loopback-network' });
      return status.state;
    } catch {
      return 'unsupported';
    }
  }

  /** Returns the app status, or null when highbridge is not reachable. */
  async status() {
    try {
      return await this._json('GET', '/v1/status');
    } catch (err) {
      if (err instanceof HighbridgeError && err.code === 'unreachable') return null;
      throw err;
    }
  }

  /** Asks the user (in the highbridge app) to approve this site. Resolves when approved. */
  async pair() {
    const result = await this._json('POST', '/v1/pair');
    this._rememberPaired(true);
    return result;
  }

  /**
   * Makes sure the app is running (launching it through highbridge://launch if needed) and
   * that this site is paired. Returns the app status.
   */
  async connect({ launch = true } = {}) {
    let status = await this.status();
    if (!status && launch) {
      this._launch('highbridge://launch');
      try {
        status = await this._waitFor(() => this.status(), (s) => s !== null, this.launchTimeoutMs);
      } catch (err) {
        if (err.code !== 'timeout') throw err;
      }
    }
    if (!status) throw await this._unreachableError();
    if (!status.paired) {
      await this.pair();
      status = { ...status, paired: true };
    }
    return status;
  }

  /** @returns {Promise<Array<{id: string, name: string, manufacturer?: string, backend: string, isDefault: boolean}>>} */
  async listScanners() {
    await this.connect();
    return (await this._json('GET', '/v1/scanners')).scanners;
  }

  /**
   * Scans and returns the pages as Blobs.
   * @param {object} [options] scannerId, dpi, colorMode ('color'|'gray'|'blackWhite'), source ('auto'|'flatbed'|'feeder'), duplex,
   *   oneSheet (one feeder sheet per job: front and back when duplex; an empty feeder waits),
   *   showDriverUi, maxPages, format ('png'),
   *   stamp ({ position: 'top'|'bottom' } adds highbridge's provenance band; each page then has `provenance`),
   *   barcodes (true to find barcodes: each page then has `barcodeStatus` and `barcodes`, and the result a flat `barcodes`),
   *   barcodeFormats (e.g. ['qrCode', 'code128']; default: all common formats)
   * @param {object} [hooks]
   * @param {(job: object) => void} [hooks.onProgress] called with each status update
   * @param {AbortSignal} [hooks.signal]
   */
  async scan(options = {}, { onProgress, signal } = {}) {
    let job;
    const status = await this.status();

    if (status) {
      if (!status.paired) await this.pair();
      job = await this._startScan(options);
    } else if (this._wasPaired() && this.origin) {
      // App not running but this site was paired before: start it and the scan with one link.
      const requestId = randomId();
      this._launch(buildScanUrl(this.origin, requestId, options));
      job = await this._waitForProtocolJob(requestId, options, signal);
    } else {
      await this.connect();
      job = await this._startScan(options);
    }

    onProgress?.(job);
    job = await this._waitFor(
      async () => {
        const next = await this._json('GET', `/v1/scans/${job.id}`);
        onProgress?.(next);
        return next;
      },
      (j) => TERMINAL.has(j.status),
      this.scanTimeoutMs,
      signal,
    );

    if (job.status !== 'completed') {
      throw new HighbridgeError(job.error?.error ?? job.status, job.error?.message ?? `The scan was ${job.status}.`, job);
    }

    const pages = await Promise.all(
      job.pages.map(async (page) => ({ ...page, blob: await this._blob(page.url) })),
    );
    this._json('DELETE', `/v1/scans/${job.id}`).catch(() => {});
    const result = { id: job.id, pages };
    if (job.stamp) result.stamp = job.stamp;
    if (job.barcodes) result.barcodes = job.barcodes;
    if (typeof job.usedFeeder === 'boolean') result.usedFeeder = job.usedFeeder;
    return result;
  }

  /** @returns {Promise<Array<{id: string, name: string, backend: string}>>} */
  async listCardReaders() {
    await this.connect();
    try {
      return (await this._json('GET', '/v1/card-readers')).readers;
    } catch (err) {
      if (err.code !== 'approval_required') throw err;
      await this.pair();
      return (await this._json('GET', '/v1/card-readers')).readers;
    }
  }

  /**
   * Reads one card. The selected application includes the full card number, expiry, and
   * cardholder name. highbridge runs on this computer and returns the number only to this page.
   * A track, PIN, or raw card data is not included.
   * @param {object} [options]
   * @param {string} [options.readerId]
   * @param {'auto'|'contact'|'contactless'} [options.interface]
   * @param {string} [options.aid] hex AID; default is the highest priority that selects
   * @param {number} [options.waitForCardMs] 0–120000, default 30000
   * @param {object} [hooks]
   * @param {(job: object) => void} [hooks.onProgress]
   * @param {AbortSignal} [hooks.signal]
   */
  async readCard(options = {}, { onProgress, signal } = {}) {
    let job;
    const status = await this.status();

    if (status) {
      if (!status.paired) await this.pair();
      job = await this._startCardRead(options);
    } else if (this._wasPaired() && this.origin) {
      const requestId = randomId();
      this._launch(buildReadCardUrl(this.origin, requestId, options));
      job = await this._waitForProtocolCardRead(requestId, options, signal);
    } else {
      await this.connect();
      job = await this._startCardRead(options);
    }

    onProgress?.(job);
    const waitMs = Number(options.waitForCardMs);
    const timeout = Math.max(this.scanTimeoutMs, (Number.isFinite(waitMs) ? waitMs : 30000) + 15000);
    job = await this._waitFor(
      async () => {
        const next = await this._json('GET', `/v1/card-reads/${job.id}`);
        onProgress?.(next);
        return next;
      },
      (j) => TERMINAL.has(j.status),
      timeout,
      signal,
    );

    if (job.status !== 'completed') {
      throw new HighbridgeError(job.error?.error ?? job.status, job.error?.message ?? `The card read was ${job.status}.`, job);
    }

    this._json('DELETE', `/v1/card-reads/${job.id}`).catch(() => {});
    return {
      id: job.id,
      reader: job.reader,
      interface: job.interface,
      applications: job.applications,
      selected: job.selected,
    };
  }

  /**
   * Arms a scan session: the same POST and status poll as {@link scan}, repeated until Stop,
   * a flatbed (always one shot), or an error that needs a person. With `oneSheet`, each cycle is
   * one feeder sheet and an empty feeder keeps the session armed. Options are read again each
   * cycle when `options` is a function. `pollIntervalMs` is unchanged.
   * @param {object|(() => object)} options
   * @param {object} [hooks]
   * @param {(result: object) => void} [hooks.onResult]
   * @param {(job: object) => void} [hooks.onProgress]
   * @param {AbortSignal} [hooks.signal]
   */
  async scanSession(options, { onResult, onProgress, signal } = {}) {
    for (;;) {
      if (signal?.aborted) return;
      const current = currentOptions(options);
      const loop = current.oneSheet === true && current.source !== 'flatbed';
      let jobId;
      try {
        const result = await this.scan(current, {
          signal,
          onProgress: (job) => {
            jobId = job.id;
            onProgress?.(job);
          },
        });
        onResult?.(result);
        if (!loop || result.usedFeeder === false) return;
      } catch (err) {
        if (signal?.aborted || err?.code === 'aborted' || err?.code === 'canceled') {
          if (jobId) this._json('DELETE', `/v1/scans/${jobId}`).catch(() => {});
          return;
        }
        // The server waits on CAP_FEEDERLOADED. If a driver still reports an empty feeder,
        // that does not end the session.
        if (loop && FEEDER_RETRY.has(err?.code)) {
          if (jobId) await this._json('DELETE', `/v1/scans/${jobId}`).catch(() => {});
          await sleep(this.pollIntervalMs);
          continue;
        }
        throw err;
      }
    }
  }

  /**
   * Arms a card session: the same POST and status poll as {@link readCard}, repeated until Stop
   * or an error that needs a person. After a card is read, the next read waits until that card
   * has left the slot. Contact and contactless stay on the interface in `options` (auto, unless
   * the form says otherwise). The card number is still only the completed job's field.
   * @param {object|(() => object)} options
   * @param {object} [hooks]
   * @param {(result: object) => void} [hooks.onResult]
   * @param {(job: object) => void} [hooks.onProgress]
   * @param {AbortSignal} [hooks.signal]
   */
  async readCardSession(options, { onResult, onProgress, signal } = {}) {
    let waitForRemoval = false;
    for (;;) {
      if (signal?.aborted) return;
      const current = currentOptions(options);
      const request = waitForRemoval ? { ...current, waitForRemoval: true } : current;
      let jobId;
      try {
        const result = await this.readCard(request, {
          signal,
          onProgress: (job) => {
            jobId = job.id;
            onProgress?.(job);
          },
        });
        onResult?.(result);
        waitForRemoval = true;
      } catch (err) {
        if (signal?.aborted || err?.code === 'aborted' || err?.code === 'canceled') {
          if (jobId) this._json('DELETE', `/v1/card-reads/${jobId}`).catch(() => {});
          return;
        }
        // Nobody presented a card in this wait. Stay armed and try again.
        if (err?.code === 'no_card') {
          if (jobId) await this._json('DELETE', `/v1/card-reads/${jobId}`).catch(() => {});
          await sleep(this.pollIntervalMs);
          continue;
        }
        throw err;
      }
    }
  }

  async _startScan(options) {
    try {
      return await this._json('POST', '/v1/scans', options);
    } catch (err) {
      // Stamped scans send the user's name and computer name; sites approved before highbridge
      // said so are asked once more.
      if (err.code !== 'approval_required') throw err;
      await this.pair();
      return this._json('POST', '/v1/scans', options);
    }
  }

  async _startCardRead(options) {
    try {
      return await this._json('POST', '/v1/card-reads', options);
    } catch (err) {
      // A card read sends the full card number to the site over the local connection. Sites
      // approved before highbridge said so are asked once more.
      if (err.code !== 'approval_required') throw err;
      await this.pair();
      return this._json('POST', '/v1/card-reads', options);
    }
  }

  async _waitForProtocolCardRead(requestId, options, signal) {
    try {
      return await this._waitFor(
        async () => {
          try {
            return await this._json('GET', `/v1/card-reads/${requestId}`);
          } catch (err) {
            if (err.code === 'unreachable' || err.code === 'not_found') return null;
            throw err;
          }
        },
        (job) => job !== null,
        this.launchTimeoutMs,
        signal,
      );
    } catch (err) {
      if (err.code === 'origin_not_paired') {
        this._rememberPaired(false);
        await this.pair();
        return this._startCardRead(options);
      }
      if (err.code === 'timeout') throw await this._unreachableError();
      throw err;
    }
  }

  async _waitForProtocolJob(requestId, options, signal) {
    try {
      return await this._waitFor(
        async () => {
          try {
            return await this._json('GET', `/v1/scans/${requestId}`);
          } catch (err) {
            if (err.code === 'unreachable' || err.code === 'not_found') return null;
            throw err;
          }
        },
        (job) => job !== null,
        this.launchTimeoutMs,
        signal,
      );
    } catch (err) {
      if (err.code === 'origin_not_paired') {
        // The pairing was revoked in the app. Re-pair over HTTP, where the Origin is verified.
        this._rememberPaired(false);
        await this.pair();
        return this._startScan(options);
      }
      if (err.code === 'timeout') throw await this._unreachableError();
      throw err;
    }
  }

  async _unreachableError() {
    if ((await this.loopbackPermission()) === 'denied') {
      return new HighbridgeError(
        'loopback_blocked',
        'Your browser is blocking this site from reaching apps on your device. Allow it in the site settings, then retry.',
      );
    }
    if (this.installerUrl) {
      return new HighbridgeError('setup_required', 'Please complete this one-time setup.', { installerUrl: this.installerUrl });
    }
    return new HighbridgeError('not_running', 'highbridge is not running. Install it or start it from the Start menu, then retry.');
  }

  async _waitFor(poll, done, timeoutMs, signal) {
    const deadline = Date.now() + timeoutMs;
    for (;;) {
      if (signal?.aborted) throw new HighbridgeError('aborted', 'The operation was aborted.');
      const value = await poll();
      if (done(value)) return value;
      if (Date.now() >= deadline) throw new HighbridgeError('timeout', 'Timed out waiting for highbridge.');
      await sleep(this.pollIntervalMs);
    }
  }

  async _request(method, path, body) {
    const init = {
      method,
      mode: 'cors',
      credentials: 'omit',
      cache: 'no-store',
      headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
      body: body === undefined ? undefined : JSON.stringify(body),
    };
    let response;
    try {
      // Local Network Access: declares the loopback target (also exempts it from mixed-content blocking).
      response = await this._fetch(this.baseUrl + path, this._sendAddressSpace === false ? init : { ...init, targetAddressSpace: 'loopback' });
    } catch (err) {
      // Browsers that only know the older Private Network Access values reject 'loopback' outright.
      if (this._sendAddressSpace !== false && /targetAddressSpace/i.test(String(err?.message))) {
        this._sendAddressSpace = false;
        return this._request(method, path, body);
      }
      throw new HighbridgeError('unreachable', 'Could not reach highbridge on this computer.', { cause: err });
    }
    if (!response.ok) {
      let payload = null;
      try {
        payload = await response.json();
      } catch {
        // non-JSON error body
      }
      if (payload?.error === 'origin_not_paired') this._rememberPaired(false);
      throw new HighbridgeError(payload?.error ?? `http_${response.status}`, payload?.message ?? `highbridge returned HTTP ${response.status}.`, payload);
    }
    return response;
  }

  async _json(method, path, body) {
    const response = await this._request(method, path, body);
    return response.status === 204 ? null : response.json();
  }

  async _blob(path) {
    return (await this._request('GET', path)).blob();
  }

  _wasPaired() {
    try {
      return this._storage?.getItem(PAIRED_HINT_KEY) === this.baseUrl;
    } catch {
      return false;
    }
  }

  _rememberPaired(paired) {
    try {
      if (paired) this._storage?.setItem(PAIRED_HINT_KEY, this.baseUrl);
      else this._storage?.removeItem(PAIRED_HINT_KEY);
    } catch {
      // storage may be disabled
    }
  }
}

/** Builds a highbridge://scan URL. Parameter names match the POST /v1/scans body. */
export function buildScanUrl(origin, requestId, options = {}) {
  const params = new URLSearchParams({ v: '1', origin, request: requestId });
  for (const key of ['scannerId', 'dpi', 'colorMode', 'source', 'duplex', 'showDriverUi', 'maxPages', 'format']) {
    if (options[key] !== undefined && options[key] !== null) params.set(key, String(options[key]));
  }
  if (options.stamp) params.set('stamp', options.stamp.position ?? 'bottom');
  if (options.oneSheet) params.set('oneSheet', 'true');
  if (options.barcodes) params.set('barcodes', 'true');
  if (Array.isArray(options.barcodeFormats)) params.set('barcodeFormats', options.barcodeFormats.join(','));
  return `highbridge://scan?${params}`;
}

/** Builds a highbridge://read-card URL. Parameter names match the POST /v1/card-reads body. */
export function buildReadCardUrl(origin, requestId, options = {}) {
  const params = new URLSearchParams({ v: '1', origin, request: requestId });
  for (const key of ['readerId', 'interface', 'aid', 'waitForCardMs']) {
    if (options[key] !== undefined && options[key] !== null && options[key] !== '') params.set(key, String(options[key]));
  }
  return `highbridge://read-card?${params}`;
}

/**
 * Maps barcode points onto the image the page downloaded. Detection runs before the stamp, so a
 * band at the top shifts every point down by the band's height. A bottom band leaves the scan
 * where it was.
 */
export function barcodePointsOnImage(provenance, points) {
  const shift = provenance?.position === 'top' ? Number(provenance.bandHeight) || 0 : 0;
  if (!shift) return points;
  return points.map((point) => ({ ...point, y: point.y + shift }));
}

function randomId() {
  const bytes = new Uint8Array(16);
  globalThis.crypto.getRandomValues(bytes);
  return Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('');
}

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

function currentOptions(options) {
  const value = typeof options === 'function' ? options() : options;
  return value && typeof value === 'object' ? value : {};
}

function safeLocalStorage() {
  try {
    return globalThis.localStorage ?? null;
  } catch {
    return null;
  }
}
