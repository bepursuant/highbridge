# highbridge

highbridge lets a web application scan paper documents on the user's Windows PC. It is a small per-user tray app that:

- drives TWAIN scanners, through [NTwain](https://github.com/soukoku/ntwain) (MIT);
- exposes a local API on `http://127.0.0.1:47116` that only approved HTTPS websites can use;
- registers the `highbridge://` URL scheme so a page can start the app, a scan, or a card read with a link.

The user approves each website once. After that, the site's scans start immediately, with no highbridge dialogs.

```js
import { HighbridgeClient } from './highbridge.js';

const client = new HighbridgeClient();
scanButton.onclick = async () => {
  const { pages } = await client.scan({ dpi: 300, colorMode: 'gray', source: 'feeder', duplex: true });
  for (const page of pages) img.src = URL.createObjectURL(page.blob); // image/png
};
```

Status: **proof of concept** for Windows 10/11 workstations (32-bit build, PNG output). Architecture, security model, browser constraints, dependency licenses and open questions are in [docs/architecture.md](docs/architecture.md).

## How it works

```
 HTTPS web app (browser)                         highbridge.exe (per-user tray app)
 ───────────────────────                         ─────────────────────────────────
 highbridge.js ── fetch http://127.0.0.1:47116 ──► Kestrel API ──► TWAIN (NTwain)
      │            (Origin checked, CORS)              ├──► PC/SC (winscard)
      └── highbridge://launch | scan | read-card ──► Windows ──┘
```

1. **Pairing (once per site).** The first `POST /v1/pair` from a site shows *"Allow this website to use your scanners and card reader?"*. The prompt names both devices and says a card read sends the last four digits of the card number, the expiry, and the cardholder name, and that a stamped scan includes the Windows user and computer name. This is the only highbridge prompt. Approved origins are saved in `%APPDATA%\Highbridge\settings.json` and can be removed in the tray menu's **Approved websites** window. The browser supplies the `Origin` header, so pages cannot fake it. Sites approved under older wording are asked again before the first card read.
2. **Scanning.** `POST /v1/scans` from a paired site starts the scan right away. highbridge also asks the driver to hide its progress window (`CAP_INDICATORS`) unless the page asks for the driver UI. The page polls `GET /v1/scans/{id}` and downloads each page from `/v1/scans/{id}/pages/{n}`. Results stay in memory only, are visible only to the origin that requested them, and expire after 10 minutes.
3. **`highbridge://` links** start the app (`highbridge://launch`), queue a scan (`highbridge://scan?…`), or queue a card read (`highbridge://read-card?…`) for an *already paired* origin. The scanner or reader does not start until that origin's page polls the job over HTTP (within 30 seconds), so a forged link cannot run the device. A protocol link never carries data back, and it can never pair a site.
4. **Browser permission.** Chrome, Edge and Firefox ask the user once to let a public site reach "apps on this device" (Local Network Access, loopback). The client sends `targetAddressSpace: 'loopback'` and should be called from a click.

### Local API (v1)

| Request | Purpose |
|---|---|
| `GET /v1/status` | `{app, version, apiVersion, backend, devices: [{kind, backend}], paired, pairingAllowed}`. `backend` stays the scanner backend |
| `POST /v1/pair` | Ask the user to approve this site |
| `GET /v1/scanners` | List scanners (paired sites only) |
| `POST /v1/scans` | Body: `{scannerId?, dpi, colorMode: color\|gray\|blackWhite, source: auto\|flatbed\|feeder, duplex, showDriverUi, maxPages, format: png, stamp?: {position: top\|bottom}, barcodes?, barcodeFormats?}`. Returns `202` with the job |
| `GET /v1/scans/{id}` | Job status: `queued`, `scanning`, `completed`, `failed` or `canceled`, plus pages. With `barcodes: true`, also the barcodes found |
| `GET /v1/scans/{id}/pages/{n}` | Page image (`image/png`) |
| `DELETE /v1/scans/{id}` | Discard or cancel a job |
| `GET /v1/card-readers` | List readers (`{id, name, backend}`). No card-present flag. Current approval required |
| `POST /v1/card-reads` | Body: `{readerId?, interface: auto\|contact\|contactless, aid?, waitForCardMs?}`. Returns `202` with the job. No APDU fields |
| `GET /v1/card-reads/{id}` | Job status: `queued`, `reading`, `completed`, `failed` or `canceled`. The card fields are inline when completed. Also claims a `highbridge://read-card` job |
| `DELETE /v1/card-reads/{id}` | Discard or cancel a card read |

`format` is optional and only `png` is accepted for now; the option exists so PDF can be added later without changing the API shape.

`barcodes: true` finds barcodes on each page (QR, Data Matrix, PDF417, Code 128, Code 39, EAN-13, EAN-8, UPC-A, UPC-E and ITF, unless `barcodeFormats` names a subset, for example `["qrCode", "code128"]`). It is off unless asked. Detection runs in the background, on a copy of the page as the scanner delivered it, and the job stays `scanning` until it finishes. Each barcode is `{page, format, text, rotation, orientation, points}`: `rotation` is which way the page was turned to read it (0, 90, 180 or 270 degrees clockwise), `orientation` is the code's own angle on the page when it can be measured, and `points` are page-pixel coordinates. Pages also carry `barcodeStatus`: `done`, `partial` (the time budget ran out), `failed` or `skipped`. The same list is repeated once per page and once for the whole job. A `highbridge://scan` link takes the same options as `barcodes=true&barcodeFormats=qrCode,code128`. A stamp, when one is requested, is applied after that copy is taken, so detection sees the unstamped page.

### Provenance stamp

With `stamp: { position: 'top' | 'bottom' }` (or `stamp=top|bottom` in a `highbridge://scan` link), highbridge makes each page taller by a white band with black text. The band is never drawn over the scan. Names are left-aligned and padded so the colons share a column:

```
HIGHBRIDGE ID : HB-7K3M-9QX2                     PAGE 1 OF 3
TIMESTAMP     : 2026-10-29 16:36:05 UTC-04:00
WORKSTATION   : PC-0042  USER : CONTOSO\JOSE
SCANNER       : CANON DR-C225
SHA-256 HASH  : 4f1c0a9e2b7d88a14e90c3b6f25d17a8...
```

The scan id is on the left of the first line and "PAGE N OF M" is flush right. The time is local, with its UTC offset. The bitmap font is upper case, so the printed user name drops accents. The band shows the first 32 digits of the SHA-256; the full hash, the origin, and the UTC instant stay in the embedded record and in `page.provenance`.

highbridge fills in every value. The page can only choose the position, and any other member of `stamp` is rejected as `invalid_options`.

The same record is embedded in the PNG: an `iTXt` chunk named `highbridge:provenance` (JSON), plus `Software`, `Creation Time`, `Source` and `Description`. It is also returned to the page as `page.provenance`. `originalSha256` is computed over the page's pixels before the band was added (method `highbridge-pixels-v1`), so it can be rechecked from the stamped PNG alone with `ProvenanceVerifier` in `Highbridge.Core`.

A stamped job publishes its pages only once all of them are stamped; until then, `pagesScanned` shows progress. The band uses a built-in bitmap font, so it shows only upper-case ASCII; the metadata keeps the exact values.

Stamped scans send the user's Windows name and computer name to the site, so the pairing prompt says so. Sites approved before that wording are asked again the first time they request a stamp (`approval_required`), and the JS client handles this automatically. Sites listed in policy, and stamps required by policy, are covered by the admin.

A completed card read returns `reader`, `interface` (`contact` or `contactless`), `applications` (every directory entry) and `selected` (one application: `aid`, `label`, `preferredName`, `priority`, `panLast4`, `expiry`, `cardholderName`). Absent optional fields are omitted. The full card number, tracks, PIN, cryptogram and raw card data never leave the machine.

Errors are returned as `{ "error": "<code>", "message": "…" }`. Common codes are `origin_not_paired`, `pairing_denied`, `invalid_options`, `approval_required` and `stamp_disabled` (provenance stamp), `stamp_failed`, `paper_jam`, `no_scanners`, `twain_unavailable`, `scan_timeout` (the scanner stopped responding; the message says at which step), `not_claimed` (a `highbridge://` job that no page picked up), `card_reader_disabled`, `no_card`, `no_payment_apps`, `app_not_found`, `read_failed`, `pcsc_unavailable` and `read_timeout`.

## Repository layout

| Path | What |
|---|---|
| `src/Highbridge.Core` | Scan and card-read options, `highbridge://` parser, origin rules and pairing, job managers, barcode detection, settings and policy, mock scanner and mock card reader (cross-platform) |
| `src/Highbridge.Server` | Loopback HTTP API: Host and Origin checks, CORS, endpoints (cross-platform) |
| `src/Highbridge.Twain` | TWAIN backend on NTwain (Windows) |
| `src/Highbridge.Pcsc` | PC/SC card reader via P/Invoke to `winscard.dll` (Windows). Not referenced by Server, DevHost, or Core |
| `src/Highbridge.App` | `Highbridge.exe`: tray app, pairing dialog, approved-sites window, protocol and auto-start registration (Windows, x86) |
| `src/Highbridge.DevHost` | Headless host with the mock scanner and mock card reader, for Linux, macOS, CI and demos |
| `clients/js` | Browser client (`highbridge.js` plus `.d.ts`), with unit and end-to-end tests |
| `docs/`, `samples/Highbridge.SampleSite` | Static demo page (GitHub Pages root) and a local host for it on `http://127.0.0.1:47200` |
| `installer/` | WiX source for the MSI: `Highbridge.wxs` (package), `Dialogs.wxs` (installer screens), `License.rtf` (license text) |
| `branding/` | Logo: `logo.svg` (master), `logo-16.svg` (pixel-aligned for 16-40 px), `highbridge.ico` (used by the exe, tray, MSI). `node branding/build-icons.mjs` regenerates the `.ico` and `docs/logo.svg` with headless Chrome |
| `tests/` | xUnit tests for Core and the API |
| `scripts/` | `test.sh` (everything except Windows-only checks), `build-msi.ps1` (Windows: build, sign, validate), `build-msi.sh` (Linux via Wine: unsigned dev build) |

## Build and test (any OS)

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) and Node.js 20 or later (for the JS tests only).

```bash
./scripts/test.sh          # builds everything (Windows projects cross-compile) and runs all tests
```

Or step by step:

```bash
dotnet build highbridge.slnx
dotnet test highbridge.slnx
HIGHBRIDGE_DEVHOST_DLL=$PWD/src/Highbridge.DevHost/bin/Debug/net10.0/Highbridge.DevHost.dll \
  node --test "clients/js/test/*.test.mjs"
```

## Try it without a scanner (mock)

```bash
dotnet run --project src/Highbridge.DevHost            # API on http://127.0.0.1:47116, mock scanners, auto-approves pairing
dotnet run --project samples/Highbridge.SampleSite     # demo page on http://127.0.0.1:47200
```

Open http://127.0.0.1:47200, click **Connect**, then **Scan**. The mock scanner offers a duplex feeder, a flatbed, a "jammed" device for testing errors, and **highbridge mock feeder with barcode test sheets**: A4 pages with every supported barcode, turned and skewed. Tick **Find barcodes** and scan that device to see them listed.

The **Card reader** tab lists readers and reads a card with the same options as `readCard`: reader, interface (`auto`, `contact`, or `contactless`), an optional application id, and how long to wait. `auto` uses whichever slot has a card. `contact` prefers the chip slot and the contact directory (PSE); `contactless` prefers the tap slot and the contactless directory (PPSE). If that directory is not on the card, the other is tried. Windows shows the two slots as two readers; a card in either slot of the same reader is accepted. The mock offers a reader with a Visa and a Mastercard (last four `1234`, expiry `2028-12`, name `DOE/JOHN`; `A0000000041010` selects the Mastercard), a reader with no card, and a reader with no payment applications.

To print those sheets and try them on a real scanner: `dotnet run --project src/Highbridge.DevHost -- --write-barcode-sheets ./sheets` writes three PNGs at 300 dpi. Print each on A4, fit to the page.

To simulate a `highbridge://` launch (after the sample page has been paired), pass the URL to a second dev host process. It forwards the URL to the running one, which is what Windows does when a link is clicked. The scan waits until something polls `GET /v1/scans/0123456789abcdef0123` with the sample page's `Origin`, and is dropped after 30 seconds otherwise:

```bash
dotnet run --project src/Highbridge.DevHost -- "highbridge://scan?v=1&origin=http%3A%2F%2F127.0.0.1%3A47200&request=0123456789abcdef0123&source=flatbed"
```

### From Visual Studio (F5)

The demo needs two projects running at once: a scanner host and the sample site. Open `highbridge.slnx` in Visual Studio 2022 17.11 or later, pick a multi-project launch profile from the startup dropdown (they are stored in `highbridge.slnLaunch`), and press F5:

- **Demo: dev host + sample site** runs `Highbridge.DevHost` (mock scanners, pairing auto-approved) and `Highbridge.SampleSite`, which opens the demo page in the browser.
- **Demo: tray app (mock scanner) + sample site** runs the real tray app with `--mock` instead, so you see the actual pairing dialog and tray menu. The app is 32-bit, so this needs the x86 .NET Desktop Runtime and ASP.NET Core Runtime 10 (see [Install on Windows](#install-on-windows)). Exit any installed highbridge first, because both use port 47116.

On older Visual Studio versions, right-click the solution, choose **Configure Startup Projects... > Multiple startup projects**, and set `Highbridge.DevHost` (or `Highbridge.App`, whose launch profile passes `--mock`) and `Highbridge.SampleSite` to **Start**.

## Install on Windows

Requirements:

- Windows 10 or 11 (workstations; terminal servers are not supported).
- The **32-bit (x86)** [.NET Desktop Runtime 10](https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x86.exe) and [ASP.NET Core Runtime 10](https://aka.ms/dotnet/10.0/aspnetcore-runtime-win-x86.exe). Both are free Microsoft installers (these links download them directly), and the x86 versions are needed even on 64-bit Windows. They install machine-wide, so Windows asks for administrator permission once; IT can push them ahead of highbridge.
- A scanner with a **32-bit TWAIN** driver. Vendor drivers usually install `TWAINDSM.dll`; highbridge does not ship it. Without one, Windows' own `twain_32.dll` and its WIA-TWAIN layer (sources named "WIA-…") are used; that works, but with fewer settings. Without a scanner, you can install the TWAIN Working Group's *TWAIN2 FreeImage Software Scanner* sample source for testing.

The MSI is per-user: no administrator rights and no UAC prompt for highbridge itself. Double-clicking `highbridge-<version>-x86.msi` shows:

1. **Only if a runtime is missing:** an "Install Microsoft .NET first" screen that lists both runtimes as *Missing* or *Installed*, with a **Download** button for each missing one (it opens the exact x86 installer download above). After running Microsoft's installer, the user clicks **Check again**: the highbridge installer reopens, finds the runtime and carries on. Nothing has to be redone.
2. **License agreement** (from `installer/License.rtf`) with an "I accept" box. Install stays disabled until it is ticked.
3. A progress bar, then **highbridge is installed** with a Finish button. The app is already running in the notification area.

Running the MSI again on a machine where it's installed offers **Repair** (puts back the files, URL handler and shortcut). To uninstall, use **Settings > Apps > Installed apps > highbridge > Uninstall**.

Silent and scripted installs skip all screens, including the license:

```powershell
msiexec /i highbridge-0.1.0-x86.msi /qn                 # AUTOSTART=0 / LAUNCHAPP=0 to opt out of those
msiexec /x highbridge-0.1.0-x86.msi /qn                 # uninstall
```

If a runtime is missing, a silent install fails (exit code 1603) without changing anything. A basic-UI install (`/qb`) shows a message naming both runtimes with their download links, and asks the user to run the installer again.

The MSI:

- installs `Highbridge.exe` (about 0.55 MB) to `%LOCALAPPDATA%\Programs\Highbridge`;
- registers the URL handler: `HKCU\Software\Classes\highbridge\shell\open\command` = `"…\Highbridge.exe" "%1"`;
- starts highbridge at sign-in: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Highbridge` = `"…\Highbridge.exe" --background` (also toggled from the tray menu; an upgrade turns it back on unless run with `AUTOSTART=0`);
- adds a Start menu shortcut and starts the app.

Upgrades install over the old version, including rebuilds with the same version number: the MSI asks the running app to exit (`Highbridge.exe --exit`), replaces it and starts the new one. Uninstalling removes the files and registry entries, but keeps the approved sites and logs in `%APPDATA%\Highbridge` and `%LOCALAPPDATA%\Highbridge`.

**License text.** The installer shows `installer/License.rtf`. It is currently a placeholder ("You agree to be cool."). To change it, replace that one file with your own RTF, for example saved from WordPad or Word, and rebuild the MSI.

### Build the MSI

On Windows, with the .NET 10 SDK (WiX is restored as a local .NET tool, see `.config/dotnet-tools.json`):

```powershell
powershell -ExecutionPolicy Bypass -File scripts/build-msi.ps1                     # unsigned
$env:HIGHBRIDGE_SIGN_CERT_THUMBPRINT = '<sha1>'                                     # or HIGHBRIDGE_SIGN_PFX + HIGHBRIDGE_SIGN_PFX_PASSWORD
powershell -ExecutionPolicy Bypass -File scripts/build-msi.ps1 -RequireSigning     # release
```

The script publishes `Highbridge.exe`, signs it with `signtool` (SHA-256, RFC 3161 timestamp from `HIGHBRIDGE_SIGN_TIMESTAMP_URL`, default DigiCert), builds `artifacts/msi/highbridge-<version>-x86.msi`, runs the ICE validation (`wix msi validate`) and signs the MSI. `signtool` is found in the Windows SDK, or set `SIGNTOOL`. `-RequireSigning` fails the build when no certificate is configured; use it in release CI.

On Linux, `scripts/build-msi.sh` builds an unsigned MSI by running WiX under Wine. It is for development checks only: it skips ICE validation (which does not work under Wine), and Wine's cabinet library doesn't compress, so the file is larger.

For CI, run `scripts/build-msi.ps1 -RequireSigning` on a `windows-latest` runner, with the certificate supplied through the `HIGHBRIDGE_SIGN_*` secrets.

### GitHub repository

Create an empty private repository in the enterprise org (no generated README or license, so the histories do not diverge). Push this `main` branch to it. Pull requests from Cursor open on whichever forge is the project's source of truth. Pointing that at the GitHub repository is what makes new pull requests land there; adding a second git remote does not, by itself.

`docs/config.js` points at `bepursuant/highbridge` on github.com. If the org, name, or host differs, change `installerUrl` there.

### One-time setup link

The demo page cannot tell "not installed" from "installed, but the browser never handed off the `highbridge://` link". When a scan cannot start the app, and `docs/config.js` has an `installerUrl`, the page says **Please complete this one-time setup** and links to that URL. Pass the same option to `HighbridgeClient` from your own page (`installerUrl`); the error code is `setup_required` and the URL is `error.details.installerUrl`. Without that option the client still reports `not_running`.

The URL in `docs/config.js` is a GitHub release asset with a fixed name:

`https://github.com/bepursuant/highbridge/releases/latest/download/highbridge-x86.msi`

After `scripts/build-msi.ps1`, publish it under that name (the file the script writes includes the version; the release asset should not):

```powershell
Copy-Item artifacts\msi\highbridge-0.1.0-x86.msi highbridge-x86.msi
gh release create v0.1.0 highbridge-x86.msi --title "highbridge 0.1.0"
```

`releases/latest` always points at the newest release, so the demo does not change when the version does. On a private repository, GitHub only serves that file to someone who is signed in and allowed to read the repo. A public demo needs a public repository, or a public release.

### Demo site on GitHub Pages

`docs/index.html` is a static page. It talks to highbridge on the visitor's own computer (`http://127.0.0.1:47116`); GitHub only hosts the page. `docs/lib/highbridge.js` is a copy of `clients/js/highbridge.js`. The test `clients/js/test/demo-site.test.mjs` fails if the copy drifts. `samples/Highbridge.SampleSite` serves the same `docs/` folder at `http://127.0.0.1:47200` for local checks.

In the GitHub repository: **Settings → Pages → Deploy from a branch → main → /docs**. `docs/.nojekyll` keeps GitHub from running Jekyll on the folder. `docs/architecture.md` stays at `/architecture.md` and is not the homepage.

### Run without installing

`dotnet publish src/Highbridge.App -c Release -o artifacts/publish/win-x86` produces a single `Highbridge.exe` (the x86 runtimes above are still needed). Started from any folder, it registers the URL handler for itself on first run.

Command line: `Highbridge.exe [--mock] [--background]`, `--register [--autostart] [--quiet]`, `--unregister`, `--exit` (ask the running instance to quit; used by the MSI), and the scanner tools `--diagnose-scanner` and `--test-scan [--dpi=200] [--color=bw|gray|color] [--source=auto|flatbed|feeder] [--scanner=<name>]` (see [Scanner troubleshooting](#scanner-troubleshooting); add `--quiet` to skip the message box). When Windows passes a `highbridge:` URL, all switches are ignored.

Files:

| Path | Contents |
|---|---|
| `%APPDATA%\Highbridge\settings.json` | Approved sites |
| `%LOCALAPPDATA%\Highbridge\logs\highbridge.log` | Log (rolls at 5 MB and keeps `highbridge.log.1` through `highbridge.log.3`) |
| `%LOCALAPPDATA%\Highbridge\logs\scanner-diagnostics.txt` | Last **Diagnose scanner** report |
| `%LOCALAPPDATA%\Highbridge\logs\twaindsm.log` | TWAIN manager trace from the scanner tools (only with `TWAINDSM.dll`) |
| `%LOCALAPPDATA%\Highbridge\logs\test-scan-<n>.png` | Pages from the last **Test scan** |
| `%ProgramData%\Highbridge\policy.json` | Optional admin policy (see below) |

### Scanner troubleshooting

All logs are in `%LOCALAPPDATA%\Highbridge\logs` (for example `C:\Users\<you>\AppData\Local\Highbridge\logs`); the tray menu's **Open log folder** opens it. **Devices** lists connected scanners and card readers, and **Copy summary** puts that list on the clipboard. The log starts with the app version, machine, user, and port, then the card readers. Opening Devices records the scanners and card readers. Each scan or card read has a started line and a finished line (status and error code; reader name, interface, and AID for a card). A card number or a long hex blob is stripped before the line is written. Every scan logs each TWAIN step: the TWAIN manager and source in use (name, manufacturer, `.ds` file, whether it is the WIA-TWAIN layer), each setting with its old value, the requested value and the result (with the TWAIN condition code when the scanner rejects it), every state change and transfer. A rejected setting is never fatal: the scanner's own value is used. A scan that makes no progress for 2 minutes (5 while a page transfers or the driver's own window is open) fails with `scan_timeout` instead of hanging the page.

When a scanner misbehaves:

1. Tray menu > **Diagnose scanner...** (or `Highbridge.exe --diagnose-scanner`). It lists the TWAIN managers and sources, opens each source and writes every capability with its allowed and current values to `scanner-diagnostics.txt`. It does not scan.
2. Tray menu > **Test scan...** (or `Highbridge.exe --test-scan`, with the options above). It scans once without the browser and saves the pages as `test-scan-<n>.png`.
3. Send `highbridge.log`, `scanner-diagnostics.txt` and, if present, `twaindsm.log` from the log folder.

Both tools run in a separate process, so they work whether or not highbridge is running in the tray; don't start a scan from a website at the same time.

To rerun the TWAIN tests without a scanner on Linux, `scripts/test-twain-wine.sh` builds a fake WIA-style TWAIN source (`tests/twain-wine/fake_wia_source.c`) and scans through it under Wine.

### Admin policy (optional)

```json
{
  "allowedOrigins": ["https://app.example.com"],
  "allowPairing": false,
  "allowInsecureLoopbackOrigins": false,
  "provenanceStamp": { "mode": "required", "position": "bottom" },
  "cardReader": { "mode": "enabled" }
}
```

`allowedOrigins` are approved without a prompt. A listed origin may scan and read cards. `allowPairing: false` stops users from approving other sites. `provenanceStamp.mode` is `optional` (the default: the page decides), `required` (every scan is stamped) or `disabled` (stamp requests fail with `stamp_disabled`); `position`, when set, overrides the page's choice. `cardReader.mode` is `enabled` (the default) or `disabled`. `disabled` makes every card route return `card_reader_disabled` and leaves scanning alone. The policy is read from this file only (no Group Policy templates); push it with your usual deployment tool. The port is always 47116.

To skip browser prompts on managed machines, use the browsers' own policies: the Local Network Access allowlist policies in Chrome and Edge, `LocalNetworkAccess.SkipDomains` in Firefox, and `AutoLaunchProtocolsFromOrigins` for the `highbridge://` prompt.

## Using the JS client

Copy `clients/js/highbridge.js` (and `highbridge.d.ts` for TypeScript) into your app. Then:

- `await client.connect()` makes sure the app is running (launching it via `highbridge://launch` if needed) and that the site is paired.
- `await client.listScanners()` lists scanners.
- `await client.scan(options, { onProgress, signal })` returns `{ id, pages: [{ index, width, height, mediaType, blob, provenance? }], stamp? }`. Pass `stamp: { position: 'bottom' }` for the provenance stamp. With `barcodes: true` the result also has `barcodes`, and each page has `barcodeStatus` and `barcodes`. If the app isn't running, it starts it via a `highbridge://` link.
- `await client.readCard({ readerId, interface, aid, waitForCardMs })` returns `{ id, reader, interface, applications, selected }`. `selected` has `panLast4`, `expiry` and `cardholderName`. If the app isn't running, it starts it via `highbridge://read-card`.
- Errors are `HighbridgeError` objects with a `code`, such as `not_running`, `setup_required` (when `installerUrl` is set and the app never starts), `loopback_blocked`, `pairing_denied` or `paper_jam`.
- Call these from a click handler, and serve your site over HTTPS. Browsers deny loopback access to insecure public pages.
- If your page runs in an iframe, the embedding page must delegate `allow="loopback-network"`.

## Known limitations (POC)

- **Barely tested with real scanners.** The MSI and tray app work on Windows; the first real scan (a Samsung all-in-one) hung, which led to the current TWAIN threading. The TWAIN backend is tested under Wine against a fake source (`scripts/test-twain-wine.sh`), not yet against real drivers.
- **Card reads are covered by the mock reader in CI.** A real contact reader has been read through PC/SC. A tap uses the same read: both slots of a dual reader are watched, PPSE is tried and then PSE, and the Windows call is retried in the shapes contactless drivers accept. Restart the tray app from this build before trying a tap. If Windows still rejects it, the page message includes the PC/SC code.
- PNG output only (PDF is planned). Large color scans at 600 dpi produce big PNGs, and everything is held in memory.
- 32-bit only, so only 32-bit TWAIN drivers are visible.
- Canceling a scan stops at the next page boundary. Scans waiting in the driver's own dialog cannot be aborted from the web page.
- One instance per Windows session on the fixed port 47116; terminal servers are not supported.
- Provenance stamps are not signed or trusted-timestamped yet: the hash shows that the page content matches the record, but anyone could rewrite both. The band shows upper-case ASCII only. A `highbridge://` stamped scan from a site that still needs re-approval is rejected in the app (logged), so the page times out; the HTTP path asks for re-approval instead.
- Barcode detection (ZXing.Net) reads 2D codes at any angle and 1D codes turned by 0/90/180/270, plus a modest skew (about 10° for a 1D code, about 15° for Data Matrix). It has not been tried on a real scanner. Aztec is found, but its angle is not reported. A page of codes adds about a second after the scan, on a background thread.

## License

Proprietary. Copyright (c) 2026 Pursuant Solutions, all rights reserved; see [LICENSE](LICENSE). Third-party components are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md); everything shipped is MIT or Apache-2.0, and the MSI is built with WiX Toolset 5 (MS-RL, no fee).
