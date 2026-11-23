export declare const DEFAULT_PORT: 47116;

export type ColorMode = 'color' | 'gray' | 'blackWhite';
export type PaperSource = 'auto' | 'flatbed' | 'feeder';
export type ScanStatus = 'queued' | 'scanning' | 'completed' | 'failed' | 'canceled';
/** Only 'png' (one stacked document) today. */
export type OutputFormat = 'png';

/** Embedded in the PNG (iTXt "highbridge:provenance") and returned on every document. The image is unchanged. */
export interface PageProvenance {
  version: 1;
  /** Made by highbridge for the whole scan, e.g. "HB-7K3M-9QX2". */
  scanId: string;
  /** Local time with UTC offset, ISO 8601. */
  scannedAt: string;
  scannedAtUtc: string;
  workstation: string;
  /** DOMAIN\user */
  user: string;
  scanner: string;
  origin: string;
  page: number;
  pageCount: number;
  /** SHA-256 of the document pixels (canonical pixels, see hashMethod). */
  originalSha256: string;
  hashMethod: 'highbridge-pixels-v1';
  software: string;
}

export interface ScanOptions {
  scannerId?: string;
  dpi?: number;
  colorMode?: ColorMode;
  source?: PaperSource;
  duplex?: boolean;
  showDriverUi?: boolean;
  /**
   * 0 means one document holds every page. A positive value closes the current document once it
   * holds that many pages. The front and back of one sheet are two pages.
   */
  maxPagesPerDocument?: number;
  /**
   * 0 keeps every sheet in the current document. A positive value starts another document
   * after that many sheets. Duplex still counts the front and back as one sheet. The feeder
   * always runs until it is empty. A flatbed stays one shot.
   */
  maxSheets?: number;
  /** Clockwise turn given to the scanner before the page is stacked or searched for barcodes. */
  orientation?: 0 | 90 | 180 | 270;
  format?: OutputFormat;
  /** Find barcodes on every finished document. */
  barcodes?: boolean;
  /** Formats to look for (needs `barcodes: true`); default: every format except aztec, code93 and codabar. */
  barcodeFormats?: BarcodeFormat[];
}

export type BarcodeFormat =
  | 'qrCode' | 'dataMatrix' | 'pdf417' | 'aztec'
  | 'code128' | 'code39' | 'code93' | 'codabar'
  | 'ean13' | 'ean8' | 'upcA' | 'upcE' | 'itf';

/** 'pending' until analysed; 'partial' when the time budget ran out; 'skipped' when not analysed. */
export type BarcodeStatus = 'pending' | 'done' | 'partial' | 'failed' | 'skipped';

export interface Barcode {
  /** Index of the document it is on. */
  page: number;
  format: BarcodeFormat;
  text: string;
  /** How the document was turned when this code was read. A scan that set orientation searches that orientation only. */
  rotation: 0 | 90 | 180 | 270;
  /** Reading direction on the page, degrees clockwise (0 upright, 180 upside down); absent when unknown. */
  orientation?: number;
  /**
   * Pixels of the document image: the scan line's ends for 1D codes, corners or
   * finder patterns for 2D codes. The provenance stamp does not move them.
   */
  points: Array<{ x: number; y: number }>;
}

export interface DeviceStatus {
  kind: 'scanner' | 'cardReader' | string;
  backend: string;
}

export interface AppStatus {
  app: 'highbridge';
  version: string;
  apiVersion: number;
  backend: 'twain' | 'mock' | string;
  devices?: DeviceStatus[];
  paired: boolean;
  pairingAllowed: boolean;
}

export interface ScannerInfo {
  id: string;
  name: string;
  manufacturer?: string;
  backend: string;
  isDefault: boolean;
}

export interface DocumentInfo {
  index: number;
  mediaType: string;
  width: number;
  height: number;
  size: number;
  url: string;
  /** How many pages were stacked into this file. */
  pageCount: number;
  /** Present once the document is finished. */
  provenance?: PageProvenance;
  /** Only when the scan asked for barcodes. */
  barcodeStatus?: BarcodeStatus;
  barcodes?: Barcode[];
}

export interface ScanJob {
  id: string;
  status: ScanStatus;
  trigger: 'api' | 'protocolUrl';
  format: OutputFormat;
  createdAt: string;
  finishedAt?: string;
  error?: { error: string; message: string };
  documents: DocumentInfo[];
  /** Pages received from the scanner so far. Documents appear once they are stacked. */
  pagesScanned: number;
  /** Short id embedded in every document of this scan. */
  scanId: string;
  /** False when the scanner did not use the feeder. */
  usedFeeder?: boolean;
}

export interface ScanResult {
  id: string;
  /** Short id embedded in every document of this scan. */
  scanId: string;
  documents: Array<DocumentInfo & { blob: Blob }>;
  /** False when the scanner did not use the feeder. `scanSession` stops when this is false. */
  usedFeeder?: boolean;
}

export type CardInterface = 'auto' | 'contact' | 'contactless';

export interface CardReadOptions {
  readerId?: string;
  interface?: CardInterface;
  /** Hex AID, 5–16 bytes. Default: the highest priority application that selects. */
  aid?: string;
  /** How long to wait for a card, 0–120000. Default 30000. */
  waitForCardMs?: number;
  /**
   * Wait until the slot is empty before looking for a card. `readCardSession` sets this after
   * the first card so the same card is not read again.
   */
  waitForRemoval?: boolean;
}

export interface CardReaderInfo {
  id: string;
  name: string;
  backend: string;
}

export interface CardApplication {
  aid: string;
  label?: string;
  preferredName?: string;
  priority?: number;
}

export interface CardSelection {
  aid: string;
  label?: string;
  preferredName?: string;
  priority?: number;
  /**
   * Full card number from the chip, digits only. highbridge is a program on this computer:
   * the number is returned to this page over the local connection and is not sent anywhere else.
   */
  pan: string;
  /** Last four digits of `pan`. */
  panLast4: string;
  /** YYYY-MM. */
  expiry: string;
  cardholderName?: string;
}

export interface CardReadResult {
  id: string;
  reader: { id: string; name: string };
  interface: 'contact' | 'contactless';
  applications: CardApplication[];
  selected: CardSelection;
}

export interface HighbridgeClientOptions {
  /** Only for testing against a dev host; highbridge itself always uses 47116. */
  port?: number;
  pollIntervalMs?: number;
  launchTimeoutMs?: number;
  scanTimeoutMs?: number;
  launch?: (url: string) => void;
  fetch?: typeof fetch;
  origin?: string;
  storage?: Storage | null;
  /**
   * Download for the Windows installer. When set, a scan that cannot start highbridge fails with
   * `setup_required` and this URL in `details.installerUrl`, instead of `not_running`.
   */
  installerUrl?: string;
}

export declare class HighbridgeError extends Error {
  readonly code: string;
  readonly details?: unknown;
  constructor(code: string, message: string, details?: unknown);
}

export declare class HighbridgeClient {
  readonly port: number;
  readonly baseUrl: string;
  readonly origin: string | undefined;
  constructor(options?: HighbridgeClientOptions);
  loopbackPermission(): Promise<'granted' | 'prompt' | 'denied' | 'unsupported'>;
  status(): Promise<AppStatus | null>;
  pair(): Promise<{ paired: boolean }>;
  connect(options?: { launch?: boolean }): Promise<AppStatus>;
  listScanners(): Promise<ScannerInfo[]>;
  scan(options?: ScanOptions, hooks?: { onProgress?: (job: ScanJob) => void; signal?: AbortSignal }): Promise<ScanResult>;
  /**
   * Repeats {@link scan} until Stop, a flatbed, or an error that needs a person.
   * Pass a function to read form options again on every cycle.
   */
  scanSession(
    options?: ScanOptions | (() => ScanOptions),
    hooks?: { onResult?: (result: ScanResult) => void; onProgress?: (job: ScanJob) => void; signal?: AbortSignal },
  ): Promise<void>;
  listCardReaders(): Promise<CardReaderInfo[]>;
  readCard(options?: CardReadOptions, hooks?: { onProgress?: (job: object) => void; signal?: AbortSignal }): Promise<CardReadResult>;
  /**
   * Repeats {@link readCard} until Stop or an error that needs a person. The next read waits
   * until the card that was just read has left the slot.
   */
  readCardSession(
    options?: CardReadOptions | (() => CardReadOptions),
    hooks?: { onResult?: (result: CardReadResult) => void; onProgress?: (job: object) => void; signal?: AbortSignal },
  ): Promise<void>;
}

export declare function buildScanUrl(origin: string, requestId: string, options?: ScanOptions): string;

export declare function buildReadCardUrl(origin: string, requestId: string, options?: CardReadOptions): string;

/** Options for the fields this element actually set. Omitted attributes stay out of the JSON. */
export declare function optionsFromAttributes(element: Element, fields: Array<[string, string, string?]>): Record<string, unknown>;

/**
 * Barcode points are pixels of the downloaded image. The provenance stamp does not move them.
 */
export declare function barcodePointsOnImage(
  provenance: PageProvenance | null | undefined,
  points: Array<{ x: number; y: number }>,
): Array<{ x: number; y: number }>;
