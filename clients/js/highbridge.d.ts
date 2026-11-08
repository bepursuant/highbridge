export declare const DEFAULT_PORT: 47116;

export type ColorMode = 'color' | 'gray' | 'blackWhite';
export type PaperSource = 'auto' | 'flatbed' | 'feeder';
export type ScanStatus = 'queued' | 'scanning' | 'completed' | 'failed' | 'canceled';
/** Only 'png' (one image per page) today. */
export type OutputFormat = 'png';

export type StampPosition = 'top' | 'bottom';

/**
 * Asks highbridge to add its provenance band to each page. The page can only choose the
 * position: every value in the band comes from highbridge. Policy may also require the stamp.
 */
export interface StampOptions {
  position?: StampPosition;
}

/** Printed in the band, embedded in the PNG (iTXt "highbridge:provenance") and returned here. */
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
  /** SHA-256 of the page before the band was added (canonical pixels, see hashMethod). */
  originalSha256: string;
  hashMethod: 'highbridge-pixels-v1';
  position: StampPosition;
  bandHeight: number;
  software: string;
}

export interface StampInfo {
  position: StampPosition;
  requiredByPolicy: boolean;
  scanId: string;
}

export interface ScanOptions {
  scannerId?: string;
  dpi?: number;
  colorMode?: ColorMode;
  source?: PaperSource;
  duplex?: boolean;
  showDriverUi?: boolean;
  maxPages?: number;
  format?: OutputFormat;
  stamp?: StampOptions | null;
  /** Find barcodes on every page. */
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
  /** Index of the page it is on. */
  page: number;
  format: BarcodeFormat;
  text: string;
  /** Detection pass that found it: the page turned clockwise by 0, 90, 180 or 270 degrees. */
  rotation: 0 | 90 | 180 | 270;
  /** Reading direction on the page, degrees clockwise (0 upright, 180 upside down); absent when unknown. */
  orientation?: number;
  /**
   * Pixels of the page as the scanner delivered it: the scan line's ends for 1D codes, corners or
   * finder patterns for 2D codes. A provenance band on top is not included; use
   * {@link barcodePointsOnImage} to draw them on the stamped image.
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

export interface PageInfo {
  index: number;
  mediaType: string;
  width: number;
  height: number;
  size: number;
  url: string;
  /** Only on stamped pages. */
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
  pages: PageInfo[];
  /** Pages received so far. Stamped scans list their pages only once all are stamped. */
  pagesScanned: number;
  stamp?: StampInfo;
  /** Every page's barcodes in page order; only when the scan asked for barcodes. */
  barcodes?: Barcode[];
}

export interface ScanResult {
  id: string;
  pages: Array<PageInfo & { blob: Blob }>;
  stamp?: StampInfo;
  /** Every page's barcodes in page order; only when the scan asked for barcodes. */
  barcodes?: Barcode[];
}

export type CardInterface = 'auto' | 'contact' | 'contactless';

export interface CardReadOptions {
  readerId?: string;
  interface?: CardInterface;
  /** Hex AID, 5–16 bytes. Default: the highest priority application that selects. */
  aid?: string;
  /** How long to wait for a card, 0–120000. Default 30000. */
  waitForCardMs?: number;
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
  listCardReaders(): Promise<CardReaderInfo[]>;
  readCard(options?: CardReadOptions, hooks?: { onProgress?: (job: object) => void; signal?: AbortSignal }): Promise<CardReadResult>;
}

export declare function buildScanUrl(origin: string, requestId: string, options?: ScanOptions): string;

export declare function buildReadCardUrl(origin: string, requestId: string, options?: CardReadOptions): string;

/**
 * Maps barcode points onto the image the page downloaded. Detection runs before the stamp, so a
 * band at the top shifts every point down by `provenance.bandHeight`. A bottom band does not.
 */
export declare function barcodePointsOnImage(
  provenance: PageProvenance | null | undefined,
  points: Array<{ x: number; y: number }>,
): Array<{ x: number; y: number }>;
