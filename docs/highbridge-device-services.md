---
cursor:
  subagentId: "bc-22800741-f661-5b8c-9b00-d1168c902e9b"
---

# Highbridge device services

Investigation of [bepursuant/papertrail](https://github.com/bepursuant/papertrail) at `main` (`d650b0aa7998ad5118ef5cf0007569c1003f4849`). No papertrail files were changed, no pull request was opened, and no GitHub repository was created.

## Recommendation

Leave papertrail as the scanner product. Import its history once into a new private repository named **highbridge** (the oldest standing bridge in New York), then change identity so the two apps can be installed on the same PC. Keep the tray-app bridge: loopback HTTP, origin pairing, and protocol-launch claim. Generalize the host so a device is a registered service with its own options, job, and result. Keep the scanner as the first service, unchanged in behavior. Add a PC/SC card reader as the second.

The page calls `readCard(options)` and gets a result, the same shape as `scan(options)`. The bridge selects PSE (`1PAY.SYS.DDF01`) or PPSE (`2PAY.SYS.DDF01`), reads the application directory, selects one application, and reads the AFL records. The result is a list of installed apps plus an allowlist of fields from those records. The browser never sends or receives an APDU. Full PC/SC passthrough is a later product, not this one.

There is no card, PC/SC, winscard, or APDU code in papertrail today. Every card type below is proposed for the fork.

## What the scanner bridge is

```
HTTPS page
  clients/js/papertrail.js
    fetch http://127.0.0.1:47115     papertrail://launch | papertrail://scan
              │                                    │
              ▼                                    ▼
  PapertrailServer (Kestrel, loopback)     second process forwards the URL
              │                            on a per-user named pipe
              ▼
  PapertrailRuntime
     IScannerBackend  +  ScanJobManager  +  OriginRegistry  +  IConsentService
              │
              ├── TwainScannerBackend   (Windows tray app)
              └── MockScannerBackend    (DevHost, tests, --mock)
```

A scan from a page that can already reach the app:

1. `PapertrailClient.scan` calls `GET /v1/status`, then `POST /v1/pair` if needed, then `POST /v1/scans` with `ScanOptions`.
2. `PapertrailServer.GuardAsync` checks `Host` (loopback only) and `Origin` (`OriginRules`). CORS echoes the request origin. No cookies.
3. Paired origins skip the dialog. `ScanJobManager.Start` validates options, stores an in-memory job bound to that origin, and returns `202` with the job id.
4. One scan runs at a time (`ScanJobManager`’s lock, plus `TwainScannerBackend`’s own gate). The TWAIN work runs on one STA thread with a hidden window (`TwainThread`).
5. The page polls `GET /v1/scans/{id}` until `completed`, `failed`, or `canceled`, then downloads each page from `GET /v1/scans/{id}/pages/{n}` and `DELETE`s the job. Jobs expire after 10 minutes. Another origin gets 404.

If the app is not running and this site was paired before, `scan` opens `papertrail://scan?v=1&origin=…&request=<128-bit id>&…`. `PapertrailUri` queues the job but does not move paper until that origin polls `GET /v1/scans/{id}` (`ScanJobManager.Claim`). No claim within 30 seconds cancels the job as `not_claimed`. A protocol URL never carries image bytes and cannot pair a site.

Composition today is one backend, hard-wired:

| Piece | Where | What is fixed to “scanner” |
|---|---|---|
| Backend interface | `IScannerBackend` in `src/Papertrail.Core/Scanning/ScanModels.cs` | `ListScannersAsync` / `ScanAsync` only |
| Host | `PapertrailRuntime` in `src/Papertrail.Core/PapertrailRuntime.cs` | One `IScannerBackend`, one `ScanJobManager`, port `47115` |
| HTTP | `MapEndpoints` in `src/Papertrail.Server/PapertrailServer.cs` | `/v1/status`, `/v1/pair`, `/v1/scanners`, `/v1/scans` |
| Protocol | `PapertrailUri` in `src/Papertrail.Core/Protocol/PapertrailUri.cs` | `launch` and `scan` only. Scheme `papertrail` |
| Tray | `TrayApplicationContext` in `src/Papertrail.App/TrayApplicationContext.cs` | Constructs either `TwainScannerBackend` or `MockScannerBackend` |
| Headless | `src/Papertrail.DevHost/Program.cs` | Mock scanner only. Does not reference `Papertrail.Twain` |
| Windows driver project | `src/Papertrail.Twain` | Referenced by `Papertrail.App` only |
| Client | `clients/js/papertrail.js` | `scan(options)` and `buildScanUrl`. `docs/lib/papertrail.js` is a copy; `clients/js/test/demo-site.test.mjs` fails if they drift |
| Consent | `ConsentForm.ForPairing` | “Allow this website to use your scanners?” `OriginRegistry.CurrentApprovalVersion` is 2, for the stamp identity sentence |
| Pairing store | `PapertrailPaths` in `src/Papertrail.Core/Configuration/Settings.cs` | `%APPDATA%\Papertrail\settings.json`, `%ProgramData%\Papertrail\policy.json` |
| Single instance | Mutex `Local\Papertrail.SingleInstance.v1` in `src/Papertrail.App/Program.cs`; pipe `papertrail-{user}-v1` in `src/Papertrail.Core/Hosting/SingleInstance.cs` | |
| Protocol registration | `WindowsRegistration` in `src/Papertrail.App/WindowsRegistration.cs` | `HKCU\Software\Classes\papertrail`, Run value `Papertrail` |
| Installer | `installer/Papertrail.wxs` | Per-user MSI, `%LOCALAPPDATA%\Programs\Papertrail`, `UpgradeCode` `7235DF8D-063A-4C39-9ADB-A57FF9B2B533` |
| Bitness | `src/Papertrail.App/Papertrail.App.csproj` | `win-x86` only, because a process sees TWAIN sources of its own bitness |

`ScanJobManager` is not a generic job runner. It also owns provenance stamping and barcode detection. Those stay with the scanner.

## What to keep

Keep these behaviors. Rename types and strings with the product; do not redesign them.

- Per-user tray app (`NotifyIcon`), not a Windows service. The pairing dialog has to be on the interactive desktop. PC/SC itself does not need TWAIN’s STA message loop; the tray app stays for consent, the single instance, and the scanner.
- Windows 10/11 workstations only. No terminal servers. That decision is in `docs/architecture.md` and still fits: one fixed port, one instance per session.
- Loopback Kestrel, fixed port, `Host` check, `Origin` allowlist, reflected CORS, no credentials, 64 KB request-body cap, quiet ASP.NET logging (`PapertrailServerLogging.UseQuietLogging`).
- Pair once (`POST /v1/pair` → `IConsentService`), then no per-operation dialog. Denial cooldown 10 minutes, one prompt at a time, prompt timeout 2 minutes. Policy file can pre-approve origins or turn pairing off.
- In-memory jobs, origin-scoped, 10-minute retention, 32-job cap, 128-bit ids. Protocol launch queues work and waits for an HTTP claim (30 seconds) before touching hardware.
- Errors as `{ "error", "message" }`.
- `win-x86`, framework-dependent single-file exe, per-user WiX 5.0.2 MSI, x86 .NET Desktop Runtime and ASP.NET Core Runtime 10. Stay on x86 while the scanner stays: PC/SC readers are listed through the Windows Smart Card service to both bitnesses, so x86 does not hide readers. Do not add a 64-bit helper for cards.
- License rule in `Directory.Packages.props`: shipped packages are MIT, Apache-2.0, or BSD-style. NTwain 3.7.6 and ZXing.Net 0.16.11 stay. Do not add a PC/SC NuGet package in this step. Call `winscard.dll`, which Windows already has, the same way papertrail calls the installed `TWAINDSM.dll` and does not ship it.
- DevHost plus a mock backend in Core, so Linux CI never loads winscard. `Papertrail.DevHost.csproj` references Core and Server only; the new Windows card project should be referenced the way `Papertrail.Twain` is: by the tray app, not by DevHost or Server.
- JS client shape: ESM, no dependencies, `targetAddressSpace: 'loopback'`, call from a click, protocol fallback only when the app is down and the site was paired before.
- Scanner behavior: `IScannerBackend`, `TwainScannerBackend`, `MockScannerBackend`, `ScanJobManager`, provenance, barcodes, `/v1/scans` and `/v1/scanners`. Highbridge still scans. Papertrail does not gain cards.

## What stays behind

Papertrail keeps its name, port `47115`, `papertrail://`, mutex, pipe, `%APPDATA%\Papertrail`, `%LOCALAPPDATA%\Papertrail`, `%ProgramData%\Papertrail`, Run value, WiX `UpgradeCode`, and the GitHub Pages installer URL in `docs/config.js` (`bepursuant/papertrail`). Do not migrate pairing files. A PC with both products has two approval lists, because the grant is different: scanners only, versus scanners and card reads.

Highbridge must not reuse any of these, or its MSI will upgrade or unregister papertrail:

| papertrail (found) | highbridge (new; not in the repo) |
|---|---|
| Port `47115` (`PapertrailRuntime.FixedPort`) | `47116`. Nothing in this repo uses it. Sample site stays a different port (`47200` today); move the highbridge demo if both demos must run at once |
| Scheme `papertrail`, key `HKCU\Software\Classes\papertrail` | `highbridge` |
| Mutex `Local\Papertrail.SingleInstance.v1` | `Local\Highbridge.SingleInstance.v1` |
| Pipe `papertrail-{user}-v1` | `highbridge-{user}-v1` |
| `%APPDATA%\Papertrail`, `%LOCALAPPDATA%\Papertrail\logs`, `%ProgramData%\Papertrail\policy.json` | `Highbridge` in those three roots |
| Run value `Papertrail` | `Highbridge` |
| Install dir `%LOCALAPPDATA%\Programs\Papertrail`, exe `Papertrail.exe`, `AssemblyTitle` `papertrail` (this is the name in the browser’s “Open …?” prompt) | `Highbridge.exe`, title `highbridge` |
| WiX `UpgradeCode` `7235DF8D-063A-4C39-9ADB-A57FF9B2B533` and the component `Guid`s in `installer/Papertrail.wxs` | New upgrade code and new component GUIDs |
| `localStorage` key `papertrail.paired` | `highbridge.paired` |
| Namespaces and project names `Papertrail.*` | `Highbridge.*` |

`docs/config.js` in the fork must not keep the papertrail release URL.

## How to generalize device services

Do not collapse scan and card-read into one `Execute(json)` method. That is the passthrough this step is avoiding. Each device has a typed options record, a typed result, and a backend interface in Core. The host shares only the job lifecycle.

Proposed layout after the rename (none of these card files exist in papertrail):

```
src/Highbridge.Core
  Scanning/          IScannerBackend, ScanOptions, MockScannerBackend   (moved as-is)
  Cards/             ICardReaderBackend, CardReadOptions, CardReadResult, MockCardReaderBackend
  Jobs/              ScanJobManager (stays scanner-specific)
                     CardReadManager (claim, retention, one-at-a-time, no pages)
                     DeviceJobRules  (shared id check, retention, claim timeout — extract from ScanJobManager, do not fork the rules)
  Protocol/          HighbridgeUri   actions: launch | scan | read-card

src/Highbridge.Server
  scanner routes stay /v1/scans and /v1/scanners
  card routes are new; Server still does not reference the Windows driver projects

src/Highbridge.Pcsc          Windows only, like Papertrail.Twain
  PcscCardReaderBackend : ICardReaderBackend

src/Highbridge.App           references Twain and Pcsc
src/Highbridge.DevHost       mock scanner and mock card reader; no Twain, no Pcsc
```

`HighbridgeRuntime` holds a scanner backend and a card backend, two managers, one origin registry, one consent service, one port. `GET /v1/status` grows a device list. Proposed body, scanner fields kept so the moved client tests still have `backend`:

```json
{
  "app": "highbridge",
  "version": "0.1.0",
  "apiVersion": 1,
  "backend": "twain",
  "devices": [
    { "kind": "scanner", "backend": "twain" },
    { "kind": "cardReader", "backend": "pcsc" }
  ],
  "paired": true,
  "pairingAllowed": true
}
```

`backend` remains the scanner backend name so existing status checks keep a meaning. `devices` is the extension point.

Locks are per device, not process-global. A card read must not wait on a scan, and a scan must not wait on a card. `ScanJobManager`’s single scan lock stays. `CardReadManager` has its own. v1 runs one card read at a time even if two readers are plugged in.

`HighbridgeUri` dispatches on the action. `launch` only starts the app. `scan` still builds `ScanOptions`. `read-card` builds `CardReadOptions`. Unknown actions fail the parse, as `PapertrailUri` does today. The URL still cannot return the result. Query names match the JSON body, plus `v`, `origin`, and `request`. The existing 2048-character cap is enough: card options are a reader id, an interface, an AID, and a timeout.

Consent copy has to change, in `ConsentForm.ForPairing` and in the policy description. The current sentence is scanner-only. The new prompt names both devices and the card fields that leave the machine (last four of the PAN, expiry, cardholder name). Because settings live in a new directory, nobody arrives pre-approved from papertrail. Policy `allowedOrigins` still skips the prompt, so the policy comment must say that a listed origin may scan and read cards. Add a policy switch, same style as `provenanceStamp`:

```json
{
  "allowedOrigins": ["https://app.example.com"],
  "allowPairing": false,
  "cardReader": { "mode": "enabled" }
}
```

`mode` is `enabled` (default) or `disabled`. `disabled` makes card routes return `card_reader_disabled` and leaves scanning alone. There is no Group Policy template today (`docs/architecture.md`); do not add one.

A future device does not get a new prompt. Pairing is still once per origin. If a later device reveals a new category of data, bump `CurrentApprovalVersion` and re-ask, which is what version 2 already does for the stamp identity sentence.

## ReadCard contract

Client, same hiding of jobs that `scan` does:

```js
const { id, reader, interface: iface, applications, selected } = await client.readCard({
  readerId,                  // optional; default reader
  interface: 'auto',         // 'auto' | 'contact' | 'contactless'
  aid,                       // optional hex AID; default: highest priority that selects
  waitForCardMs: 30000,
});
```

No `apdu`, `cla`, `ins`, `data`, or command array on the options. `CardReadOptions.Validate` rejects unknown members the same way `ScanOptions.Validate` rejects a bad `stamp`. An AID, if present, is 5–16 bytes of hex. `waitForCardMs` is capped (proposed 0–120000, default 30000).

HTTP, paired origin required, same guard as scans:

| Method | Body / response |
|---|---|
| `GET /v1/card-readers` | `{ readers: [{ id, name, backend }] }`. No card-present flag |
| `POST /v1/card-reads` | `CardReadOptions` → `202` and a job (`queued`) |
| `GET /v1/card-reads/{id}` | Owner only. Claims a `highbridge://read-card` job. Result JSON is inline when `completed` |
| `DELETE /v1/card-reads/{id}` | Cancel or discard |

Statuses: `queued`, `reading`, `completed`, `failed`, `canceled`. The result is small JSON, so there is no page-download subresource. Do not put card fields in the URL.

`highbridge://read-card?v=1&origin=…&request=…&readerId=…&interface=…&aid=…&waitForCardMs=…` follows the scan claim rules. The reader does not connect until the poll.

Completed body:

```json
{
  "id": "…",
  "status": "completed",
  "reader": { "id": "…", "name": "…" },
  "interface": "contact",
  "applications": [
    { "aid": "A0000000031010", "label": "VISA CREDIT", "preferredName": "Visa", "priority": 1 }
  ],
  "selected": {
    "aid": "A0000000031010",
    "label": "VISA CREDIT",
    "preferredName": "Visa",
    "priority": 1,
    "panLast4": "1234",
    "expiry": "2028-12",
    "cardholderName": "DOE/JOHN"
  }
}
```

`applications` is every directory entry. `selected` is one application: the `aid` option, or the highest priority that selects. AFL details are read for that application only.

Allowlist for `selected`. Anything else is dropped inside the bridge and is not logged:

| Field | EMV source | Notes |
|---|---|---|
| `aid` | directory tag `4F`, confirmed by SELECT | hex, uppercase |
| `label` | tag `50` | |
| `preferredName` | tag `9F12` | omit if absent |
| `priority` | tag `87` | omit if absent |
| `panLast4` | PAN tag `5A`, or the PAN inside track-2 equivalent tag `57` | four digits only |
| `expiry` | tag `5F24` | `YYYY-MM` |
| `cardholderName` | tag `5F20` | omit if absent |

Never return, and never write to `%LOCALAPPDATA%\Highbridge\logs`: full PAN, track 1 or 2 (`56`, `57`, `9F6B`), PIN try counter, cryptogram (`9F26`), ATC (`9F36`), issuer application data, signed records, the ATR, raw TLV, raw APDU, or status words. Status words stay in the bridge and become the error codes below. Local logs may contain reader name, interface, AID, and the mapped error code.

The bridge’s command sequence, not visible to the page:

1. `SCardEstablishContext`, `SCardListReaders`, `SCardGetStatusChange` until a card is present or `waitForCardMs` elapses, `SCardConnect`, then an exclusive transaction for the read.
2. Interface `contact` selects PSE. `contactless` selects PPSE. `auto` uses the reader when the name or the ATR makes the interface obvious, otherwise tries PPSE and then PSE, and stops at the first directory. The page does not choose the APDU.
3. Read the directory. PPSE often returns entries in the SELECT response (tag `61` under `BF0C`). PSE usually returns an SFI (tag `88`) and the entries are READ RECORD on that SFI until the card says the record is missing. Handle both. This step does not issue GET PROCESSING OPTIONS.
4. SELECT the chosen AID. If the directory is missing, fail `no_payment_apps`. Do not walk a browser-supplied AID list, and do not ship a private fallback list in v1.
5. GET PROCESSING OPTIONS for that AID only. The bridge builds the PDOL (`9F38`) itself, with EMV missing-data rules. The page cannot supply PDOL values.
6. Walk the AFL (tag `94` in the GPO response): each entry is SFI, first record, last record, and how many of those records participate in offline auth. READ RECORD those records and map the allowlist. Stop at the first failure of that application and return `read_failed` rather than a partial PAN.

Error codes for the page (no status words): `invalid_options`, `origin_not_paired`, `card_reader_disabled`, `no_readers`, `reader_not_found`, `no_card`, `card_removed`, `card_unresponsive`, `no_payment_apps`, `app_not_found`, `read_failed`, `pcsc_unavailable`, `read_timeout`, `busy`, `not_claimed`, `too_many_jobs`.

`MockCardReaderBackend` lives in Core, next to `MockScannerBackend`. It lists a fake reader and returns a fixed two-app directory with allowlisted `selected` fields, plus a reader id that fails with `no_card` and one that fails with `no_payment_apps`. DevHost and the server tests use it. No Linux PC/SC.

Tray menu, beside the existing scanner tools: the disabled status line names both backends. A “Test card read…” item can wait for a later pass; scanner diagnose/test scan stay as they are (`--diagnose-scanner`, `--test-scan` in `src/Papertrail.App/Program.cs`). Do not add a menu item that prints PAN.

## How the next device plugs in

One checklist. A signature pad, a fingerprint reader, or a later PC/SC passthrough all follow it. Passthrough is not this checklist with the filter removed; it would be a new service with its own consent bump, because it is a different grant.

1. Options and result records in Core, with `Validate()`. Public properties are domain fields. No transport buffers, handles, or vendor command bytes.
2. `I<Device>Backend` in Core: list devices, run one operation, throw a small exception type with a stable `Code` and a message safe for the page (`ScannerException` is the pattern).
3. A mock backend in Core, constructed by DevHost. Linux tests never reference the Windows project.
4. A Windows project only when a driver SDK or P/Invoke is required. The tray app references it. Server, DevHost, and Core do not.
5. A job manager that uses `DeviceJobRules` for id, claim, retention, and origin checks, and its own one-at-a-time lock. Scanner-only work (pages, barcodes, stamps) is not pulled into the base.
6. Routes under `/v1/<plural>`, registered from `Highbridge.Server`, plus a `devices[]` entry on `/v1/status`.
7. A protocol action only if the operation must start while the app is down. Same claim rule. If the options do not fit in 2048 characters, the operation is HTTP-only.
8. One method on the JS client, with the protocol fallback copied from `scan`. Update `docs/lib` in the same change so the drift test stays meaningful.
9. If the result contains a new kind of personal data, change the pairing sentence and bump `CurrentApprovalVersion`.
10. A policy `mode` of `enabled` or `disabled` when an admin must ship the app without that device.

## Fork steps

Do these in the new repo. Do not commit them to papertrail.

1. Create an empty private `highbridge` repository (no generated README or license). Push papertrail `main` at `d650b0aa7998ad5118ef5cf0007569c1003f4849` so the history comes along. Do not make it a GitHub fork of papertrail, and do not set papertrail as a daily upstream. Later scanner fixes are cherry-picks.
2. First commits are the rename only: namespaces, assembly, exe title, port `47116`, scheme, mutex, pipe, paths, WiX upgrade code and component GUIDs, branding, JS client and `docs/lib` copy, `docs/config.js`. Scanner tests should pass before any card code exists. `scripts/test.sh` is the check (`docs/architecture.md` and the README).
3. Extract `DeviceJobRules` from `ScanJobManager` without changing scan results. Re-run the Core and Server tests.
4. Add `ICardReaderBackend`, `CardReadManager`, mock, server routes, protocol action, and `readCard` on the client. Server tests drive the mock through the HTTP API, the way `tests/Papertrail.Server.Tests` does for scans.
5. Add `Highbridge.Pcsc` with P/Invoke to `winscard.dll`. Wire it in the tray app next to the TWAIN backend. DevHost stays on the mock.
6. Change the pairing sentence. Point the installer and the demo config at highbridge. New MSI name `highbridge-<version>-x86.msi`. Signing can reuse the same certificate; the display name and the env-var prefix should be highbridge’s, not `PAPERTRAIL_SIGN_*`.
7. Confirm on one Windows PC that papertrail and highbridge run together: both tray icons, ports `47115` and `47116`, both URL schemes, papertrail’s approved sites unchanged.

## Gaps and risks

- GET PROCESSING OPTIONS increments the application transaction counter on most EMV cards, and a contactless card treats GPO as the start of a transaction. The requested AFL details require that command. Limit it to one application per `readCard`, and do not retry GPO in a loop. Directory-only discovery (steps 1–3) does not have this side effect; it is not enough for the AFL fields, so it is not the default.
- Last four, expiry, and cardholder name are cardholder data in the browser origin that asked for the read. Highbridge should send nothing else. The site that calls `readCard` owns whatever compliance follows from storing those fields. v1 does not return the full PAN. If a later release does, it needs a new approval version and an explicit option, not a silent widening of `selected`.
- Some cards have no PSE/PPSE and answer only to a known AID list. v1 reports `no_payment_apps`. Adding a built-in AID list is a product decision for later; accepting the list from the page is a step toward passthrough.
- PDOL data the bridge fills with zeros can make GPO fail on cards that require a real amount or country. Map that to `read_failed`. Do not let the page fill the PDOL to “make it work.”
- Dual-interface readers do not always tell contact from contactless cleanly. `auto` can try both directories; it must not run GPO twice.
- Card removed mid-read, shared readers, and exclusive transactions (`SCardBeginTransaction`) need a timeout that always disconnects. Mirror the scanner rule: a stuck read fails the job (`read_timeout`) and the next job can open a new context. There is no TWAIN-style message loop to recover.
- Logs. `FileLogger` writes under the local app-data logs directory and rolls at 5 MB. A transmit trace would capture PAN. Do not add one in v1.
- Coexistence. Reusing port `47115`, the `papertrail` scheme, the mutex, the pipe, or the WiX upgrade code will fight the installed scanner app. The rename commit is the risky one; the card work should not start until both apps launch side by side.
- Bitness stays x86 for TWAIN. That is fine for PC/SC. It is a problem only if a future device ships a 64-bit-only driver. That device needs its own decision, not a silent change of `RuntimeIdentifier`.
- No hardware in this plan. The mock covers CI. The first real reader (contact and contactless, with and without PSE) is still an open test, the same kind of gap `README.md` records for real TWAIN scanners.
- Cherry-picks from papertrail will get harder after the namespace rename. Take scanner fixes deliberately. Do not merge the two repos.
- Passthrough later (`SCardTransmit` of page-supplied bytes, or returning raw responses) is a different consent grant and a different API. Do not leave an unused `apdu` field on `CardReadOptions` “for later.”

## Out of scope for this step

- Editing papertrail, opening a papertrail pull request, or creating the GitHub repository.
- PC/SC passthrough, a browser-supplied AID list, PIN, contactless payments, and issuer scripts.
- Dropping the scanner from highbridge.
- Terminal servers, macOS, Linux hosts, or a 64-bit build.
- A second fixed port, fallback ports, or making `47116` configurable. Same rule as papertrail’s `47115`: one well-known endpoint.
