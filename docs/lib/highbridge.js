// highbridge browser client. ES module, no dependencies.
//
//   import { HighbridgeClient } from './highbridge.js';
//   const client = new HighbridgeClient();
//   button.onclick = async () => {
//     const { documents } = await client.scan({ dpi: 300, colorMode: 'gray', source: 'feeder' });
//     for (const document of documents) show(URL.createObjectURL(document.blob));
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
      return await this._json('GET', '/status');
    } catch (err) {
      if (err instanceof HighbridgeError && err.code === 'unreachable') return null;
      throw err;
    }
  }

  /** Asks the user (in the highbridge app) to approve this site. Resolves when approved. */
  async pair() {
    const result = await this._json('POST', '/pair');
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
    return (await this._json('GET', '/scanner')).scanners;
  }

  /**
   * Scans and returns one PNG per document. A document stacks that scan's pages top to bottom.
   * A field left out uses the default saved for that scanner.
   * @param {object} [options]
   * @param {string} [options.scannerId] id from listScanners(); omit for the default scanner
   * @param {number} [options.dpi] 50–1200
   * @param {'color'|'gray'|'blackWhite'} [options.colorMode]
   * @param {'auto'|'flatbed'|'feeder'} [options.source]
   * @param {boolean} [options.duplex] feeder returns both sides; front and back are two pages
   * @param {number} [options.maxPagesPerDocument] 0 keeps every page in one PNG
   * @param {number} [options.maxSheets] 0 keeps the whole feeder in one document; a positive value starts another after that many sheets
   * @param {0|90|180|270} [options.orientation] clockwise sheet orientation, given to the scanner. highbridge does not rotate the image
   * @param {boolean} [options.showDriverUi]
   * @param {'png'} [options.format]
   * @param {boolean} [options.barcodes] find barcodes on each finished document (`barcodeStatus` and `barcodes`)
   * @param {string[]} [options.barcodeFormats] e.g. ['qrCode', 'code128']; default is the common set
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
        const next = await this._json('GET', `/scanner/${job.id}`);
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

    const documents = await Promise.all(
      job.documents.map(async (document) => ({ ...document, blob: await this._blob(document.url) })),
    );
    this._json('DELETE', `/scanner/${job.id}`).catch(() => {});
    const result = { id: job.id, scanId: job.scanId, documents };
    if (typeof job.usedFeeder === 'boolean') result.usedFeeder = job.usedFeeder;
    return result;
  }

  /** @returns {Promise<Array<{id: string, name: string, backend: string}>>} */
  async listCardReaders() {
    await this.connect();
    try {
      return (await this._json('GET', '/pcsc')).readers;
    } catch (err) {
      if (err.code !== 'approval_required') throw err;
      await this.pair();
      return (await this._json('GET', '/pcsc')).readers;
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
        const next = await this._json('GET', `/pcsc/${job.id}`);
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

    this._json('DELETE', `/pcsc/${job.id}`).catch(() => {});
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
   * a flatbed (always one shot), or an error that needs a person. With `maxSheets` above zero
   * on a feeder, each cycle scans the loaded stack and splits it into documents of that many
   * sheets. An empty feeder keeps the session armed. Options are read again each cycle when
   * `options` is a function. `pollIntervalMs` is unchanged.
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
      const loop = Number(current.maxSheets) > 0 && current.source !== 'flatbed';
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
          if (jobId) this._json('DELETE', `/scanner/${jobId}`).catch(() => {});
          return;
        }
        // The server waits on CAP_FEEDERLOADED. If a driver still reports an empty feeder,
        // that does not end the session.
        if (loop && FEEDER_RETRY.has(err?.code)) {
          if (jobId) await this._json('DELETE', `/scanner/${jobId}`).catch(() => {});
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
          if (jobId) this._json('DELETE', `/pcsc/${jobId}`).catch(() => {});
          return;
        }
        // Nobody presented a card in this wait. Stay armed and try again.
        if (err?.code === 'no_card') {
          if (jobId) await this._json('DELETE', `/pcsc/${jobId}`).catch(() => {});
          await sleep(this.pollIntervalMs);
          continue;
        }
        throw err;
      }
    }
  }

  async _startScan(options) {
    try {
      return await this._json('POST', '/scanner', options);
    } catch (err) {
      // Every scan sends the user's name and computer name; sites approved before highbridge
      // said so are asked once more.
      if (err.code !== 'approval_required') throw err;
      await this.pair();
      return this._json('POST', '/scanner', options);
    }
  }

  async _startCardRead(options) {
    try {
      return await this._json('POST', '/pcsc', options);
    } catch (err) {
      // A card read sends the full card number to the site over the local connection. Sites
      // approved before highbridge said so are asked once more.
      if (err.code !== 'approval_required') throw err;
      await this.pair();
      return this._json('POST', '/pcsc', options);
    }
  }

  async _waitForProtocolCardRead(requestId, options, signal) {
    try {
      return await this._waitFor(
        async () => {
          try {
            return await this._json('GET', `/pcsc/${requestId}`);
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
            return await this._json('GET', `/scanner/${requestId}`);
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

/** Builds a highbridge://scan URL. Parameter names match the POST /scanner body. */
export function buildScanUrl(origin, requestId, options = {}) {
  const params = new URLSearchParams({ v: '1', origin, request: requestId });
  for (const key of ['scannerId', 'dpi', 'colorMode', 'source', 'duplex', 'showDriverUi', 'maxPagesPerDocument', 'maxSheets', 'orientation', 'format']) {
    if (options[key] !== undefined && options[key] !== null) params.set(key, String(options[key]));
  }
  if (options.barcodes) params.set('barcodes', 'true');
  if (Array.isArray(options.barcodeFormats)) params.set('barcodeFormats', options.barcodeFormats.join(','));
  return `highbridge://scan?${params}`;
}

/** Builds a highbridge://read-card URL. Parameter names match the POST /pcsc body. */
export function buildReadCardUrl(origin, requestId, options = {}) {
  const params = new URLSearchParams({ v: '1', origin, request: requestId });
  for (const key of ['readerId', 'interface', 'aid', 'waitForCardMs']) {
    if (options[key] !== undefined && options[key] !== null && options[key] !== '') params.set(key, String(options[key]));
  }
  return `highbridge://read-card?${params}`;
}

/**
 * Barcode points are pixels of the downloaded image. The provenance stamp does not move them.
 */
export function barcodePointsOnImage(_provenance, points) {
  return points;
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

const SCAN_FIELDS = [
  ['scanner-id', 'scannerId'],
  ['dpi', 'dpi', 'number'],
  ['color-mode', 'colorMode'],
  ['source', 'source'],
  ['duplex', 'duplex', 'bool'],
  ['show-driver-ui', 'showDriverUi', 'bool'],
  ['max-pages-per-document', 'maxPagesPerDocument', 'number'],
  ['max-sheets', 'maxSheets', 'number'],
  ['orientation', 'orientation', 'number'],
  ['format', 'format'],
  ['barcodes', 'barcodes', 'bool'],
  ['barcode-formats', 'barcodeFormats', 'list'],
];

const CARD_FIELDS = [
  ['reader-id', 'readerId'],
  ['interface', 'interface'],
  ['aid', 'aid'],
  ['wait-for-card-ms', 'waitForCardMs', 'number'],
];

/** Options for the fields this element actually set. Omitted attributes stay out of the JSON. */
export function optionsFromAttributes(element, fields) {
  const options = {};
  for (const [attr, key, kind] of fields) {
    if (!element.hasAttribute(attr)) continue;
    const raw = element.getAttribute(attr) ?? '';
    if (kind === 'bool') {
      if (raw === 'false') continue;
      options[key] = true;
    } else if (raw === '') {
      continue;
    } else if (kind === 'number') {
      const value = Number(raw);
      if (Number.isFinite(value)) options[key] = value;
    } else if (kind === 'list') {
      options[key] = raw.split(',').map((part) => part.trim()).filter(Boolean);
    } else {
      options[key] = raw;
    }
  }
  return options;
}

function bindAttribute(element, attr, read) {
  const apply = () => {
    const value = read();
    if (value === undefined || value === null || value === '' || value === false) element.removeAttribute(attr);
    else element.setAttribute(attr, value === true ? '' : String(value));
  };
  return apply;
}

function defineDeviceElements() {
  if (!globalThis.customElements) return;

  class HbScan extends HTMLElement {
    connectedCallback() {
      if (this._built) return;
      this._built = true;
      this.innerHTML = `
        <p class="status-line" data-status>Checking…</p>
        <form novalidate>
          <div class="form-row">
            <label class="field"><span>Scanner</span>
              <div class="row">
                <select data-scanner disabled><option value="">Device default</option></select>
                <button type="button" class="secondary" data-connect>Connect</button>
              </div>
            </label>
          </div>
          <div class="form-row">
            <label class="field"><span>Resolution</span>
              <select data-dpi>
                <option value="">Device default</option>
                <option value="150">150 dpi</option>
                <option value="200">200 dpi</option>
                <option value="300">300 dpi</option>
                <option value="600">600 dpi</option>
              </select>
            </label>
          </div>
          <div class="form-row">
            <label class="field"><span>Paper source</span>
              <select data-source>
                <option value="">Device default</option>
                <option value="auto">Automatic</option>
                <option value="feeder">Document feeder</option>
                <option value="flatbed">Flatbed glass</option>
              </select>
            </label>
          </div>
          <div class="form-row">
            <label class="field"><span>Color</span>
              <select data-color>
                <option value="">Device default</option>
                <option value="color">Color</option>
                <option value="gray">Grayscale</option>
                <option value="blackWhite">Black &amp; white</option>
              </select>
            </label>
            <p class="hint">A field left on device default uses the value saved for this scanner. A value chosen here is sent and wins.</p>
          </div>
          <div class="form-row">
            <label class="check"><input type="checkbox" data-duplex /> Scan both sides (duplex)</label>
          </div>
          <div class="form-row">
            <label class="field"><span>Pages per document</span>
              <input type="number" data-per-document min="0" max="500" step="1" placeholder="Device default (0 = no limit)" />
            </label>
          </div>
          <div class="form-row">
            <label class="field"><span>Sheets</span>
              <input type="number" data-sheets min="0" max="500" step="1" placeholder="Device default (0 = whole feeder)" />
            </label>
            <p class="hint">A positive sheet count splits the stack into documents of that many sheets. Duplex front and back are one sheet. 0 keeps the whole feeder in one document. Start scans the next stack until Stop. An empty feeder waits.</p>
          </div>
          <div class="form-row">
            <label class="field"><span>Orientation</span>
              <select data-orientation>
                <option value="">Device default</option>
                <option value="0">0°</option>
                <option value="90">90° clockwise</option>
                <option value="180">180°</option>
                <option value="270">270° clockwise</option>
              </select>
            </label>
          </div>
          <div class="form-row">
            <label class="check"><input type="checkbox" data-driver-ui /> Use the scanner's own settings dialog</label>
          </div>
          <div class="form-row">
            <label class="check"><input type="checkbox" data-barcodes /> Find barcodes</label>
          </div>
          <div class="row actions">
            <button type="submit" class="primary" data-scan>Scan</button>
            <button type="button" class="secondary" data-start>Start</button>
            <button type="button" class="secondary" data-stop disabled>Stop</button>
          </div>
          <p class="progress" data-progress aria-live="polite"></p>
        </form>
        <div class="notice error" data-error role="alert" hidden></div>`;
      this._applyAttributes();
      this._wire();
      setTimeout(() => this._boot(), 0);
    }

    _client() {
      if (!this._hb) {
        const port = Number(this.getAttribute('port'));
        const launchTimeoutMs = Number(this.getAttribute('launch-timeout-ms'));
        this._hb = new HighbridgeClient({
          port: port > 0 ? port : undefined,
          installerUrl: this.getAttribute('installer-url') || undefined,
          ...(launchTimeoutMs > 0 ? { launchTimeoutMs } : {}),
        });
      }
      return this._hb;
    }

    _applyAttributes() {
      const select = (selector, attr) => {
        const node = this.querySelector(selector);
        if (this.hasAttribute(attr)) node.value = this.getAttribute(attr);
        return node;
      };
      select('[data-dpi]', 'dpi');
      select('[data-source]', 'source');
      select('[data-orientation]', 'orientation');
      select('[data-color]', 'color-mode');
      this.querySelector('[data-duplex]').checked = this.hasAttribute('duplex') && this.getAttribute('duplex') !== 'false';
      this.querySelector('[data-driver-ui]').checked = this.hasAttribute('show-driver-ui') && this.getAttribute('show-driver-ui') !== 'false';
      this.querySelector('[data-barcodes]').checked = this.hasAttribute('barcodes') && this.getAttribute('barcodes') !== 'false';
      if (this.hasAttribute('max-pages-per-document')) this.querySelector('[data-per-document]').value = this.getAttribute('max-pages-per-document');
      if (this.hasAttribute('max-sheets')) this.querySelector('[data-sheets]').value = this.getAttribute('max-sheets');
    }

    _readControls() {
      const set = (attr, value) => bindAttribute(this, attr, () => value)();
      set('dpi', this.querySelector('[data-dpi]').value);
      set('source', this.querySelector('[data-source]').value);
      set('orientation', this.querySelector('[data-orientation]').value);
      set('color-mode', this.querySelector('[data-color]').value);
      set('duplex', this.querySelector('[data-duplex]').checked);
      set('show-driver-ui', this.querySelector('[data-driver-ui]').checked);
      set('barcodes', this.querySelector('[data-barcodes]').checked);
      set('max-pages-per-document', this.querySelector('[data-per-document]').value);
      set('max-sheets', this.querySelector('[data-sheets]').value);
      const scanner = this.querySelector('[data-scanner]').value;
      if (scanner) this.setAttribute('scanner-id', scanner);
      else this.removeAttribute('scanner-id');
      return optionsFromAttributes(this, SCAN_FIELDS);
    }

    _wire() {
      const connect = this.querySelector('[data-connect]');
      const scan = this.querySelector('[data-scan]');
      const start = this.querySelector('[data-start]');
      const stop = this.querySelector('[data-stop]');
      connect.addEventListener('click', () => this._connect(connect));
      this.querySelector('form').addEventListener('submit', (event) => {
        event.preventDefault();
        this._once();
      });
      start.addEventListener('click', () => this._session());
      stop.addEventListener('click', () => this._control?.abort());
      this._buttons = { scan, start, stop };
    }

    _setBusy(busy, session = false) {
      this._buttons.scan.disabled = busy;
      this._buttons.start.disabled = busy;
      this._buttons.stop.disabled = !session;
    }

    _progress(text) {
      this.querySelector('[data-progress]').textContent = text;
      this.dispatchEvent(new CustomEvent('hb-progress', { detail: { text }, bubbles: true }));
    }

    _fail(err) {
      const box = this.querySelector('[data-error]');
      box.hidden = false;
      box.textContent = err?.message ?? String(err);
      this.dispatchEvent(new CustomEvent('hb-error', { detail: err, bubbles: true }));
    }

    _clearError() {
      const box = this.querySelector('[data-error]');
      box.hidden = true;
      box.textContent = '';
    }

    async _boot() {
      const permission = await this._client().loopbackPermission();
      if (permission === 'prompt') this._setStatus('unknown', 'Click Connect to link highbridge');
      else if (permission === 'denied') this._setStatus('off', 'Blocked by browser settings');
      else await this._refreshStatus();
    }

    _setStatus(kind, text) {
      const node = this.querySelector('[data-status]');
      node.textContent = text;
      this.dispatchEvent(new CustomEvent('hb-status', { detail: { kind, text }, bubbles: true }));
    }

    async _refreshStatus() {
      try {
        const status = await this._client().status();
        if (!status) this._setStatus('off', 'highbridge not running');
        else if (!status.paired) this._setStatus('warn', 'Connected · not approved');
        else this._setStatus('ok', `Ready · v${status.version}`);
        return status;
      } catch {
        this._setStatus('off', 'Unavailable');
        return null;
      }
    }

    async _connect(button) {
      this._clearError();
      button.disabled = true;
      try {
        await this._client().connect();
        const scanners = await this._client().listScanners();
        const select = this.querySelector('[data-scanner]');
        const current = this.getAttribute('scanner-id') ?? '';
        select.replaceChildren(new Option('Device default', ''));
        for (const scanner of scanners) select.add(new Option(`${scanner.name}${scanner.isDefault ? ' (default)' : ''}`, scanner.id));
        select.value = scanners.some((scanner) => scanner.id === current) ? current : '';
        select.disabled = false;
        button.textContent = 'Refresh';
      } catch (err) {
        this._fail(err);
      } finally {
        button.disabled = false;
        this._refreshStatus();
      }
    }

    async _once() {
      if (this._control) return;
      this._clearError();
      this._setBusy(true);
      this._progress('Contacting highbridge…');
      try {
        const result = await this._client().scan(this._readControls(), {
          onProgress: (job) => this._progress(job.status === 'scanning' ? `Scanning… ${job.pagesScanned ?? 0} page(s)` : 'Waiting for the scanner…'),
        });
        this.dispatchEvent(new CustomEvent('hb-result', { detail: result, bubbles: true }));
        this._progress(`Done · ${result.documents.length} document(s)`);
      } catch (err) {
        this._progress('');
        this._fail(err);
      } finally {
        this._setBusy(false);
        this._refreshStatus();
      }
    }

    async _session() {
      if (this._control) return;
      this._clearError();
      this._control = new AbortController();
      this._setBusy(true, true);
      this._progress('Session armed…');
      let passes = 0;
      try {
        await this._client().scanSession(() => this._readControls(), {
          signal: this._control.signal,
          onProgress: (job) => this._progress(`Session · ${job.pagesScanned ?? 0} page(s)`),
          onResult: (result) => {
            passes += 1;
            this.dispatchEvent(new CustomEvent('hb-result', { detail: result, bubbles: true }));
            this._progress(`Session · pass ${passes} · ${result.documents.length} document(s)`);
          },
        });
        if (this._control.signal.aborted && !passes) this._progress('Stopped');
      } catch (err) {
        this._progress('');
        this._fail(err);
      } finally {
        this._control = null;
        this._setBusy(false);
        this._refreshStatus();
      }
    }
  }

  class HbRead extends HTMLElement {
    connectedCallback() {
      if (this._built) return;
      this._built = true;
      this.innerHTML = `
        <p class="status-line" data-status>Checking…</p>
        <form novalidate>
          <div class="form-row">
            <label class="field"><span>Reader</span>
              <div class="row">
                <select data-reader disabled><option value="">Device default</option></select>
                <button type="button" class="secondary" data-connect>Connect</button>
              </div>
            </label>
          </div>
          <div class="form-row">
            <label class="field"><span>Interface</span>
              <select data-interface>
                <option value="">Device default</option>
                <option value="auto">Auto</option>
                <option value="contact">Contact</option>
                <option value="contactless">Contactless</option>
              </select>
            </label>
          </div>
          <div class="form-row">
            <label class="field"><span>Application (AID)</span>
              <input type="text" data-aid spellcheck="false" autocomplete="off" placeholder="Device default" />
            </label>
          </div>
          <div class="form-row">
            <label class="field"><span>Wait for a card (ms)</span>
              <input type="number" data-wait min="0" max="120000" step="1000" placeholder="Device default" />
            </label>
            <p class="hint">A field left blank uses the value saved for this reader. Start keeps reading until Stop, and waits for the previous card to leave the slot.</p>
          </div>
          <div class="row actions">
            <button type="submit" class="primary" data-read>Read card</button>
            <button type="button" class="secondary" data-start>Start</button>
            <button type="button" class="secondary" data-stop disabled>Stop</button>
          </div>
          <p class="progress" data-progress aria-live="polite"></p>
        </form>
        <div class="notice error" data-error role="alert" hidden></div>`;
      const iface = this.querySelector('[data-interface]');
      if (this.hasAttribute('interface')) iface.value = this.getAttribute('interface');
      if (this.hasAttribute('aid')) this.querySelector('[data-aid]').value = this.getAttribute('aid');
      if (this.hasAttribute('wait-for-card-ms')) this.querySelector('[data-wait]').value = this.getAttribute('wait-for-card-ms');
      const connect = this.querySelector('[data-connect]');
      const read = this.querySelector('[data-read]');
      const start = this.querySelector('[data-start]');
      const stop = this.querySelector('[data-stop]');
      this._buttons = { read, start, stop };
      connect.addEventListener('click', () => this._connect(connect));
      this.querySelector('form').addEventListener('submit', (event) => {
        event.preventDefault();
        this._once();
      });
      start.addEventListener('click', () => this._session());
      stop.addEventListener('click', () => this._control?.abort());
      setTimeout(() => this._boot(), 0);
    }

    _client() {
      if (!this._hb) {
        const port = Number(this.getAttribute('port'));
        const launchTimeoutMs = Number(this.getAttribute('launch-timeout-ms'));
        this._hb = new HighbridgeClient({
          port: port > 0 ? port : undefined,
          installerUrl: this.getAttribute('installer-url') || undefined,
          ...(launchTimeoutMs > 0 ? { launchTimeoutMs } : {}),
        });
      }
      return this._hb;
    }

    _readControls() {
      const iface = this.querySelector('[data-interface]').value;
      if (iface) this.setAttribute('interface', iface);
      else this.removeAttribute('interface');
      const aid = this.querySelector('[data-aid]').value.trim();
      if (aid) this.setAttribute('aid', aid);
      else this.removeAttribute('aid');
      const wait = this.querySelector('[data-wait]').value;
      if (wait !== '') this.setAttribute('wait-for-card-ms', wait);
      else this.removeAttribute('wait-for-card-ms');
      const reader = this.querySelector('[data-reader]').value;
      if (reader) this.setAttribute('reader-id', reader);
      else this.removeAttribute('reader-id');
      return optionsFromAttributes(this, CARD_FIELDS);
    }

    _setBusy(busy, session = false) {
      this._buttons.read.disabled = busy;
      this._buttons.start.disabled = busy;
      this._buttons.stop.disabled = !session;
    }

    _progress(text) {
      this.querySelector('[data-progress]').textContent = text;
      this.dispatchEvent(new CustomEvent('hb-progress', { detail: { text }, bubbles: true }));
    }

    _fail(err) {
      const box = this.querySelector('[data-error]');
      box.hidden = false;
      box.textContent = err?.message ?? String(err);
      this.dispatchEvent(new CustomEvent('hb-error', { detail: err, bubbles: true }));
    }

    async _boot() {
      const permission = await this._client().loopbackPermission();
      if (permission === 'prompt') this.querySelector('[data-status]').textContent = 'Click Connect to link highbridge';
      else if (permission === 'denied') this.querySelector('[data-status]').textContent = 'Blocked by browser settings';
      else {
        const status = await this._client().status().catch(() => null);
        this.querySelector('[data-status]').textContent = status?.paired ? `Ready · v${status.version}` : status ? 'Connected · not approved' : 'highbridge not running';
      }
    }

    async _connect(button) {
      button.disabled = true;
      try {
        await this._client().connect();
        const readers = await this._client().listCardReaders();
        const select = this.querySelector('[data-reader]');
        const current = this.getAttribute('reader-id') ?? '';
        select.replaceChildren(new Option('Device default', ''));
        for (const reader of readers) select.add(new Option(reader.name, reader.id));
        select.value = readers.some((reader) => reader.id === current) ? current : '';
        select.disabled = false;
        button.textContent = 'Refresh';
      } catch (err) {
        this._fail(err);
      } finally {
        button.disabled = false;
      }
    }

    async _once() {
      if (this._control) return;
      this._setBusy(true);
      this._progress('Contacting highbridge…');
      try {
        const result = await this._client().readCard(this._readControls(), {
          onProgress: (job) => this._progress(job.status === 'reading' ? 'Reading the card…' : 'Waiting to read the card…'),
        });
        this.dispatchEvent(new CustomEvent('hb-result', { detail: result, bubbles: true }));
        this._progress('Done');
      } catch (err) {
        this._progress('');
        this._fail(err);
      } finally {
        this._setBusy(false);
      }
    }

    async _session() {
      if (this._control) return;
      this._control = new AbortController();
      this._setBusy(true, true);
      this._progress('Session armed…');
      let passes = 0;
      try {
        await this._client().readCardSession(() => this._readControls(), {
          signal: this._control.signal,
          onResult: (result) => {
            passes += 1;
            this.dispatchEvent(new CustomEvent('hb-result', { detail: result, bubbles: true }));
            this._progress(`Session · ${passes} card(s)`);
          },
        });
        if (this._control.signal.aborted && !passes) this._progress('Stopped');
      } catch (err) {
        this._progress('');
        this._fail(err);
      } finally {
        this._control = null;
        this._setBusy(false);
      }
    }
  }

  customElements.define('hb-scan', HbScan);
  customElements.define('hb-read', HbRead);
}

defineDeviceElements();
