# highbridge — architecture plan (POC)

highbridge lets an HTTPS web app start a document scan on the user's Windows PC (TWAIN scanner) or a payment-card read (PC/SC) and get the result back in JavaScript. It is proprietary software of Pursuant Solutions (all rights reserved; Pursuant may distribute it free or paid).

## 1. Decisions

| Area | Decision | Why |
|---|---|---|
| Scope | **Windows 10/11 workstations only.** No terminal servers / multi-user RDS hosts, no macOS/Linux, no WIA. | Agreed scope for the POC. The core and API stay cross-platform only so they build and test on Linux CI. |
| Runtime | .NET 10 (LTS), C#. Windows app targets `net10.0-windows`; core + API are plain `net10.0` so they build and test on Linux/CI. | Current LTS (supported to Nov 2028). |
| Process model | **Per-user tray app** (WinForms `NotifyIcon`), auto-start at sign-in through `HKCU\…\Run`. **Not** a Windows service. One instance per signed-in user. | Services run in session 0 with no interactive desktop. TWAIN drivers need a message loop and window, and often show their own UI. The pairing dialog must appear on the user's desktop. Per-user also means no admin rights are needed. |
| TWAIN library | **NTwain 3.7.6 (MIT)**, pinned. Native (DIB) and file transfers are converted to PNG with `System.Drawing`; memory transfers are assembled by `StripImageAssembler` (Core). | NTwain is stable and MIT since 3.6/3.7; 4.0 is still beta (re-evaluate later). NAPS2.Sdk (LGPL) and Saraff.Twain (LGPL) are excluded. |
| TWAIN threading | **One `TwainThread` per backend**: an STA thread running a WinForms message loop with a hidden window. Every TWAIN call (open the manager and source, capability negotiation, enable, transfers, close) runs on it; NTwain is opened with `WindowsFormsMessageLoopHook` on that window, and the window is the `hParent` passed to `MSG_ENABLEDS`. A scan is a `TwainScan` driven by NTwain's events, with a watchdog (2 min without progress, 5 min during a transfer or while the driver's own window is open) that fails it with `scan_timeout`; `ScanJobManager` adds a 15-minute backstop. If the thread is still stuck in a driver call 15 s after a failure, the next job gets a new thread. Capability sets that fail are logged with their TWAIN condition code and skipped; the scan uses the source's values. `--diagnose-scanner` / `--test-scan` run the same backend in a separate process. | The first hardware test hung: capabilities were set from a different thread than the one that opened the source (sources such as the WIA-TWAIN layer reject that, so every set failed), and `hParent` was null, so a TWAIN 1.x source's transfer-ready window message was lost. TWAIN 1.x sources on the old `twain_32.dll` have no callbacks; the app must pump messages and pass them to `MSG_PROCESSEVENT` on the thread that owns the source. NTwain's `StopOnTransferError` is left off because NTwain 3.7 treats a page's `TWRC_XFERDONE` as an error there and drops the remaining pages. |
| Bitness | **win-x86 only** (`RuntimeIdentifier` fixed in `Highbridge.App.csproj`). No x64 build, no out-of-process workers. | A process only sees TWAIN sources of its own bitness. 32-bit drivers are the common case, and many scanners ship nothing else. |
| Publish | **Framework-dependent, single file**: one `Highbridge.exe` of about 0.55 MB. Needs the x86 **.NET Desktop Runtime 10** and **ASP.NET Core Runtime 10**, which the MSI checks for. | The self-contained build was 186 MB in 410 files. The two runtimes are free Microsoft downloads (about 56 MB + 11 MB installers), installed once per machine and serviced by Microsoft Update. |
| DSM | Use the `TWAINDSM.dll` (or legacy `twain_32.dll`) that scanner drivers install. **We do not redistribute the DSM.** | The TWAIN DSM is LGPL-2.1. Calling the copy already installed on the system via P/Invoke does not ship LGPL code. |
| Result transport | **Local HTTP API on `http://127.0.0.1:47116`** (Kestrel minimal API) with JSON and polling. Pages are returned as image downloads. | Browsers treat `127.0.0.1` as potentially trustworthy, so it is not blocked as mixed content. HTTP is simpler than WebSocket and is covered by the Local Network Access permission everywhere. |
| Port | **Always 47116.** Not configurable in settings or policy, and no fallback ports. If the port is taken, the app says so and the client reports `not_running`. | One well-known endpoint keeps the client simple. Without terminal-server support, there is one instance per machine in practice. |
| Output format | **PNG only** for the POC. `ScanOptions.Format` (`OutputFormat` enum, `format` in the API and `highbridge://`) is already plumbed through; unknown values are rejected as `invalid_options`. | Adding PDF later means: add `OutputFormat.Pdf`, accept it in `OutputFormats.TryParse`, and add a branch to the encoders in `MockScannerBackend` and `TwainScan.Encode` (plus memory transfers in `TwainScan.FinishMemoryPage`). A multipage PDF would be assembled once all pages are in (one result instead of N pages), using an MIT/Apache writer such as PdfSharp 6 (MIT). |
| `highbridge://` role | **Launch + start a scan or card read; it never carries results.** `highbridge://launch` starts the app. `highbridge://scan?v=1&origin=…&request=<id>&dpi=…` queues a scan, and `highbridge://read-card?…` queues a card read, each under a client-chosen random id. The page then collects the result over the HTTP API, where the `Origin` header *is* verifiable. | A protocol launch has no trustworthy caller identity, so any page or email could fire it. Results are released only to a page whose real `Origin` matches and which knows the 128-bit request id. A spoofed launch therefore yields nothing. |
| Protocol registration | `HKCU\Software\Classes\highbridge` (`URL Protocol`, `shell\open\command = "…\Highbridge.exe" "%1"`), written by the MSI. `Highbridge.exe --register` / `--unregister` and a first-run self-check write the same keys for portable use. Single instance: a second launch forwards its URL to the running instance over a current-user-only named pipe. | Per-user, no admin rights needed. |
| Installer | **Per-user MSI only, built with WiX Toolset 5.0.2** (staying on 5.x). Sources: `installer/Highbridge.wxs` (package), `installer/Dialogs.wxs` (screens), `installer/License.rtf` (license text); built by `scripts/build-msi.ps1`. Installs to `%LOCALAPPDATA%\Programs\Highbridge` with no elevation prompt; registers the protocol handler and sign-in auto-start in HKCU, adds a Start menu shortcut, and starts the app. Screens: a runtime check that appears only when an x86 runtime is missing (each runtime shown as Missing/Installed, a Download button per missing one that opens the exact x86 installer URL, and **Check again**, which reopens the installer so it re-detects and continues); the license agreement with an accept box; progress; finished. Running it again offers Repair; uninstall is via Settings > Apps. Silent/basic installs skip the screens, and a missing runtime blocks them with a message naming both direct links. Upgrades (also same-version rebuilds) stop the running app first (`Highbridge.exe --exit`) and re-enable sign-in auto-start. Signing: `signtool` signs `Highbridge.exe` and the MSI when a certificate is given (thumbprint or PFX via parameters or `HIGHBRIDGE_SIGN_*` variables). No bootstrapper `.exe`. | WiX 5 is MS-RL with no Open Source Maintenance Fee (v6+ requires the fee from revenue-generating users). MS-RL applies to WiX's own source, not to MSIs built with it. The screens are our own authoring rather than the WiX UI extension, whose license dialogs need a WiX custom-action DLL inside the MSI; so the MSI still contains no WiX code. A dialog button can only run custom actions, not the standard AppSearch, hence "Check again" relaunches `msiexec` (no script or DLL custom action). The runtimes themselves are per-machine Microsoft installers, so they need admin once; highbridge does not. Inno Setup (no MSI, commercial license requested) and Velopack (own `Setup.exe` and updater) don't fit an MSI requirement. |
| Policy | `%ProgramData%\Highbridge\policy.json` only. **No GPO/ADMX.** | Agreed. Admins can push the file with any deployment tool. |
| Provenance stamp | Optional per scan (`stamp: {position}`), or forced or blocked by `policy.json` `provenanceStamp`. Done in Core after the scan, for every backend: decode the PNG (own decoder), hash the original pixels (`highbridge-pixels-v1`), extend the canvas with a black-on-white band (built-in 5x7 bitmap font, dot of about 0.45 mm, quarter-inch side margins), and re-encode with pHYs and `tEXt`/`iTXt` metadata. Pages are held until the whole scan is stamped, because the band says "page N of M". All values come from highbridge; pairing approval v2 discloses that stamps send the user and computer name. See README, *Provenance stamp*. | These scans serve as evidence of signing: nothing must be covered, the text must survive printing and fax, and the page must not be able to write its own "evidence". No dependency; the rendering is the same on the mock and on TWAIN, and testable on Linux. |

## 2. Security model

1. **Origin allowlist ("pairing"), the only user prompt.** Every API request must carry a browser `Origin`. Allowed origins are `https://…`, plus `http://localhost` and `http://127.0.0.1` for development (this can be turned off by policy). `null`, `file://` and requests with no Origin are rejected.
   - The first time a site calls `POST /v1/pair`, the tray app shows "Allow this website to use your scanners and card reader?". The dialog names both devices, the card fields sent to the site (full card number, expiry, cardholder name), and the stamp identity sentence. It says highbridge is a local program and does not send the card number anywhere else. Approval version 4 is that wording. A site still approved at version 3 is asked again before a card read.
   - A yes is saved to `%APPDATA%\Highbridge\settings.json`. Sites are removed in the tray menu's "Approved websites" window. **Devices** (also opened by double-clicking the tray icon) lists the scanners and card readers Windows can see.
   - A no is remembered for 10 minutes so a site can't spam prompts. Only one prompt is shown at a time, and it times out after 2 minutes.
   - Admins can pre-approve origins (no prompt at all), or turn pairing off, in `policy.json`.
2. **No per-scan prompt.** After pairing, `POST /v1/scans` starts scanning straight away. highbridge adds no dialog, and it turns off the driver's progress indicator (`CAP_INDICATORS = false`) unless the page asks for the driver UI (`showDriverUi`). This is sized for about 1,000 scans per user per day.
3. **Protocol-launched jobs need a claim.** A scan started from a `highbridge://scan` link, or a card read started from `highbridge://read-card` (paired origins only; a link never triggers pairing), is queued but does **not** move paper or touch the reader until the named origin polls the job over HTTP, where the browser's `Origin` is verifiable. If no claim arrives within 30 seconds, the job ends as `canceled` with code `not_claimed`. Without a per-job prompt, this is what stops a forged link from a third-party page or email from running the scanner or the card reader for a paired site. The JS client claims automatically because it polls immediately. A card read also requires approval version 3.
4. **Job binding.** A scan or card-read job belongs to the origin that created (or claimed) it. Other origins get 404. Jobs and pages live only in memory and expire after 10 minutes or on `DELETE`.
5. **Transport hardening.** The server binds to loopback only. It checks the `Host` header (`127.0.0.1|localhost|[::1]:port`) to block DNS rebinding. CORS is reflected per request for valid origins (no `*`), with no credentials. It answers legacy PNA preflights harmlessly. One scan runs at a time, and one card read runs at a time, on separate locks.
6. **Out of scope.** Malicious local processes: they can already reach the scanner directly. Fingerprinting: a non-paired *valid* origin can learn that highbridge is installed via `/v1/status`, but only after the browser's Local Network Access prompt is granted.

## 3. Browser constraints (verified Oct 2026)

- **Local Network Access (Chrome/Edge 14x+, Firefox).** A public HTTPS page that fetches `127.0.0.1` gets a one-time *loopback-network* permission prompt. The JS client therefore:
  - passes `targetAddressSpace: 'loopback'` to `fetch` (this also gives the mixed-content exemption);
  - checks `navigator.permissions.query({name:'loopback-network'})` (never the legacy `'local-network-access'` name);
  - makes the first call from a user click.
  - Enterprises can pre-grant via policy (Chrome/Edge LNA allowlist policies; Firefox `LocalNetworkAccess.SkipDomains`).
  - The PNA preflight headers are deprecated; we only echo them.
- **Iframes** need `allow="loopback-network"` and a matching `Permissions-Policy` on the top-level document.
- **Protocol prompts.** Chrome/Edge show "Open highbridge?" (with "always allow *site*"); the name is the file description of `Highbridge.exe`. Firefox shows a handler chooser. These must be triggered from a user gesture. If the handler isn't installed, nothing happens, so the client detects this by timing out. Enterprises can suppress the prompt with `AutoLaunchProtocolsFromOrigins` (Chrome/Edge). With auto-start at sign-in, the app is normally already running, so day-to-day scans go straight to the HTTP API and never show this prompt.
- **Sites must be HTTPS.** LNA is auto-denied on insecure public origins.

Steady state for a paired site on a machine where the LNA permission is granted: one click on the page, scan, no dialogs.

## 4. Local API (v1)

| Method & path | Who | Purpose |
|---|---|---|
| `GET /v1/status` | any valid origin | `{app, version, apiVersion, backend, devices, paired, pairingAllowed}` |
| `POST /v1/pair` | any valid origin | Shows the pairing prompt; returns `{paired}` |
| `GET /v1/scanners` | paired | List sources |
| `POST /v1/scans` | paired | `{scannerId?, dpi, colorMode, duplex, oneSheet?, source, showDriverUi, maxPages, format: "png", stamp?: {position}, barcodes?, barcodeFormats?}` → `202 {id, status, format, stamp?, pagesScanned, usedFeeder?, …}` |
| `GET /v1/scans/{id}` | job owner | Status (`queued`, `scanning`, `completed`, `failed`, `canceled`) plus page list, and barcodes when requested. Also claims a protocol-launched job |
| `GET /v1/scans/{id}/pages/{n}` | job owner | `image/png` |
| `DELETE /v1/scans/{id}` | job owner | Discard |
| `GET /v1/card-readers` | current approval | `{readers: [{id, name, backend}]}` |
| `POST /v1/card-reads` | current approval | `{readerId?, interface, aid?, waitForCardMs?, waitForRemoval?}` → `202` |
| `GET /v1/card-reads/{id}` | job owner | Status, and the allowlisted card fields when completed. Claims a protocol job |
| `DELETE /v1/card-reads/{id}` | job owner | Discard |

JS client (`clients/js/highbridge.js`, an ES module with no dependencies): `status()`, `pair()`, `listScanners()`, `scan(options)`, `listCardReaders()`, and `readCard(options)`. If the app is not reachable, `scan` falls back to `highbridge://scan` and `readCard` falls back to `highbridge://read-card`. Both poll by request id, which claims the job.

`scanSession` and `readCardSession` arm a session from one click. The client repeats the existing POST and `GET` status poll (`pollIntervalMs` is unchanged) until Stop or an error that needs a person. There is no WebSocket and no long-poll. A card session sets `waitForRemoval` after the first card, so the next read starts only once that card has left the slot; contact and contactless stay on the requested interface (`auto` unless the page says otherwise). The card number is still only on the completed job, over `127.0.0.1`. `oneSheet: true` makes one feeder scan one sheet — the front and the back when duplex is on, and nothing after them. A feeder that counts `CAP_XFERCOUNT` in images (the Ambir nScan 690gt) is asked for that many images. A feeder that counts sheets (PaperStream IP on the Ricoh fi-8170) is asked for one sheet; asking it for two images feeds a second sheet, and resetting that transfer crashes the driver. The job waits while `CAP_FEEDERLOADED` says the feeder is empty instead of failing the session. With `oneSheet` off, one job still pulls the whole feeder. A flatbed scan is not repeated. Form options are read again on every cycle when the page passes a function. Pairing, the claim timeout, and the one-job-at-a-time locks are unchanged.

### Barcode recognition

Optional, off unless the request sets `barcodes: true` (the same flag in a `highbridge://scan` link and in `scan()`). `barcodeFormats` restricts the search; the default is QR, Data Matrix, PDF417, Code 128, Code 39, EAN-13, EAN-8, UPC-A, UPC-E and ITF. Aztec, Code 93 and Codabar are available by name.

`ScanJobManager` copies each page's bytes in the scanner callback and queues them to `BarcodeWorker` (at most two below-normal threads), so detection never runs on the TWAIN thread and it sees the page before anything else, such as a stamp, can change it. The job stays `scanning` until the pages are analysed or 30 seconds after the scanner finishes, whichever is first. Each page gets 3 seconds. A page is `done`, `partial` (budget ran out), `failed` (the image could not be read) or `skipped`. Nothing about barcodes fails the scan.

ZXing.Net reads luminance from highbridge's own PNG decoder (no image-binding package). QR, Data Matrix, Aztec and PDF417 are searched at any angle; 1D codes are searched with the page turned 0/90/180/270. `TryHarder` is on, and the upright pass also uses `TryInverted`. Pages larger than 12 million pixels (past 300 dpi on A4) are shrunk first. A UPC-E or EAN-8 read that lies along a longer 1D code is dropped.

On this machine, one page, decode plus detection, median of three runs:

| | 200 dpi (1654×2338) | 300 dpi (2481×3507) |
|---|---|---|
| Blank page | 107 ms | 147 ms |
| Text page, no codes | 122 ms | 215 ms |
| Sheet of 12 codes | 1.0 s | 1.6 s |

A scan of several pages overlaps this with the next sheet, so the wait after the scanner finishes is about one page's worth.

## 5. Repository layout

`src/Highbridge.Core` (models, URI parser, origin policy, jobs, settings, mock scanner and mock card reader) · `src/Highbridge.Server` (Kestrel API) · `src/Highbridge.Twain` (NTwain backend) · `src/Highbridge.Pcsc` (winscard P/Invoke) · `src/Highbridge.App` (Windows tray app, dialogs, registry) · `src/Highbridge.DevHost` (cross-platform headless host with the mock scanner and mock card reader) · `docs/` (static demo page, also the GitHub Pages root) + `samples/Highbridge.SampleSite` (local static host) · `clients/js` · `installer/` (WiX source) · `branding/` (logo SVGs, `highbridge.ico`, icon build script) · `scripts/` (tests, MSI build, `test-twain-wine.sh`) · `tests/*` (unit/API tests; `tests/twain-wine`: fake TWAIN source and probe for Wine).

## 6. Dependencies and licenses

Rule: nothing shipped may be GPL, LGPL, AGPL or "non-commercial", and no build tool may charge commercial users.

| Dependency | Used by | License | Notes |
|---|---|---|---|
| .NET 10 runtime, ASP.NET Core, Windows Desktop (WinForms/WPF, System.Drawing) | all | MIT | Not bundled: the user installs Microsoft's x86 runtimes (framework-dependent publish). |
| NTwain 3.7.6 | Highbridge.Twain | MIT | Pin ≥3.7. Earlier (≤3.5.x) NuGet metadata pointed to an external license file; do not use them. |
| ZXing.Net 0.16.11 | Highbridge.Core | Apache-2.0 | Barcode detection. The core package only: no bindings, no native code. `lib/net9.0/zxing.dll` has no dependencies. |
| Great.EmvTags 2.1.0 | Highbridge.Core | MIT | EMV TLV and PDOL parsing for card reads. highbridge returns the chip card number, expiry, and cardholder name to the paired page, and does not return the raw tags. |
| TWAIN DSM (`TWAINDSM.dll`) | runtime, system | LGPL-2.1 | **Not redistributed**; installed by scanner drivers. |
| WiX Toolset 5.0.2 (`wix` local .NET tool) | MSI build only | MS-RL | Not shipped: no WiX extensions or custom-action DLLs in the MSI. Do not move to v6+ (OSMF fee). |
| xunit 2.9.x | tests only | Apache-2.0 | |
| xunit.runner.visualstudio, Microsoft.NET.Test.Sdk, coverlet.collector | tests only | MIT/Apache-2.0/MIT | |
| Microsoft.AspNetCore.TestHost | tests only | MIT | |
| highbridge's own PNG decoder/encoder and bitmap font | Highbridge.Core (provenance stamp and barcode luminance) | Proprietary (ours) | No third-party code; zlib and SHA-256 come from the .NET runtime. GDI+ text was considered but is Windows-only, and its anti-aliased output fares worse on fax. |
| **Excluded** | | | NAPS2.Sdk (LGPL), Saraff.Twain (LGPL), Dynamsoft (commercial), SixLabors ImageSharp/Fonts (revenue-gated license), WiX v6+ binaries (OSMF fee), Inno Setup (commercial license requested; no MSI), ZXing.Net.Bindings.ImageSharp (pulls SixLabors ImageSharp, whose license charges revenue), ZXing.Net.Bindings.SkiaSharp (large native library), ZBar (LGPL). |

The JS client and sample page have no third-party dependencies.

## 7. Open questions

None blocking the POC. Inputs still needed before a release: the real license text (replace `installer/License.rtf`) and a code-signing certificate (`HIGHBRIDGE_SIGN_*`).

Barcode recognition, still open:

- Is the 10-format default right, or should a page always name formats? ITF in particular false-reads more than the others.
- Should the 3-second page budget be set by the page or by policy?
- 1D codes past about 10° of skew, and Data Matrix past about 15°, are not read. Extra angle passes would catch them and cost about as much again per pass.
- Results are text only. Callers may want the raw bytes of a binary QR or PDF417.
- Aztec is detected, but ZXing's points don't show which way it is turned, so `orientation` is omitted.
