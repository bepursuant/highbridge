# Third-party notices

highbridge is proprietary software of Pursuant Solutions (see `LICENSE`), which may distribute
it free or for a fee. Every component shipped with it must therefore be under a permissive
license (MIT, Apache-2.0, BSD or similar). GPL, LGPL, AGPL and "non-commercial" components are not
allowed, and neither are build tools whose license charges commercial users. Check these tables
before adding a package to `Directory.Packages.props` or a tool to `.config/dotnet-tools.json`.

## Shipped with the Windows app

| Component | Version | License | Source |
|---|---|---|---|
| .NET runtime, ASP.NET Core (Kestrel), Windows Desktop (WinForms, WPF, System.Drawing) | 10.0 | MIT | https://github.com/dotnet |
| Microsoft.Extensions.Logging.Abstractions | 10.0.14 | MIT | https://github.com/dotnet/runtime |
| NTwain | 3.7.6 | MIT | https://github.com/soukoku/ntwain |
| ZXing.Net | 0.16.11 | Apache-2.0 | https://github.com/micjahn/ZXing.Net |

The provenance stamp (PNG decoding and encoding, the band font, SHA-256) adds no third-party
component: it is highbridge's own code on top of the .NET runtime's zlib and cryptography.
SixLabors ImageSharp and SixLabors Fonts must not be used (their license charges larger
commercial users).

`Highbridge.exe` is a framework-dependent build: the .NET runtimes are not inside the MSI. Users
install Microsoft's own x86 .NET Desktop Runtime and ASP.NET Core Runtime (MIT), which the MSI
checks for.

## Used at runtime but not distributed

| Component | License | Notes |
|---|---|---|
| TWAIN Data Source Manager (`TWAINDSM.dll` / `twain_32.dll`) | LGPL-2.1 | Installed by scanner drivers into the Windows system directory and loaded from there. highbridge must **not** bundle it. |
| .NET Desktop Runtime 10 and ASP.NET Core Runtime 10 (x86) | MIT | Installed by the user or an admin from Microsoft; see the README. |

## Build and test only (not shipped)

| Component | Version | License |
|---|---|---|
| WiX Toolset (`wix` .NET tool, builds the MSI) | 5.0.2 | MS-RL |
| xunit | 2.9.3 | Apache-2.0 |
| xunit.runner.visualstudio | 3.1.4 | Apache-2.0 |
| Microsoft.NET.Test.Sdk | 17.14.1 | MIT |
| Microsoft.AspNetCore.TestHost | 10.0.14 | MIT |
| coverlet.collector | 6.0.4 | MIT |

WiX 5.0.2 is the last release that is free for commercial use: WiX v6 and later require
revenue-generating users to pay the Open Source Maintenance Fee. Its license, MS-RL, covers the
WiX toolset's own source files. It places no terms on MSI packages built with it. The highbridge
MSI uses no WiX extensions or custom-action DLLs, so it contains no WiX code at all: only
`Highbridge.exe` and Windows Installer tables. Do not upgrade the tool past 5.x without settling
the fee.

ZXing.Net is a hand port of the ZXing barcode library. Both are Apache-2.0. The package carries no NOTICE file; the license text is at <https://www.apache.org/licenses/LICENSE-2.0.txt> and in the ZXing.Net repository (`COPYING`). highbridge uses the core assembly only and does not ship any ZXing image binding.

The JS client (`clients/js`) and the sample page have no third-party dependencies.

## Logo and icons

The highbridge logo (`branding/logo.svg`, `branding/logo-16.svg`) is original artwork made for
highbridge and owned by Pursuant Solutions: hand-written SVG with no fonts, clip art, icon sets or
generated images. `branding/highbridge.ico` and `docs/logo.svg` are produced from it by
`branding/build-icons.mjs`, our own script with no npm dependencies. The script rasterizes the
SVGs with a locally installed Google Chrome or Chromium, used only as a build tool. The output
images carry no third-party terms. The sample page's header text uses the visitor's system font
(Segoe UI on Windows); no font is shipped.

## Considered and rejected

| Component | Reason |
|---|---|
| NAPS2.Sdk | LGPL |
| Saraff.Twain.NET | LGPL-3.0 |
| Dynamic Web TWAIN / Dynamsoft .NET TWAIN | Commercial license |
| NTwain 3.5.x and earlier | NuGet metadata points to an external license file rather than an SPDX MIT expression. Pin 3.7+ |
| WiX Toolset v6+ (prebuilt binaries) | Revenue-generating users must pay the Open Source Maintenance Fee |
| WiX Toolset v4 | Same license as v5 (MS-RL) but older. v5 is the newer fee-free release |
| Inno Setup | Produces EXE setups, not MSI, and commercial users are asked to buy a license |
| Velopack | MIT, but built around its own `Setup.exe` and updater; the chosen format is a plain MSI |
| ZXing.Net.Bindings.ImageSharp | Pulls SixLabors ImageSharp, whose license charges revenue-generating users. Not needed: highbridge decodes PNG itself |
| ZXing.Net.Bindings.SkiaSharp and the other ZXing.Net bindings | A native library, or a binding we don't use. The core ZXing.Net package takes a luminance buffer directly |
| ZBar / libzbar | LGPL |
| Dynamsoft Barcode Reader, Aspose.BarCode, IronBarcode, Leadtools | Commercial |

---

## NTwain license

```
The MIT License (MIT)
Copyright (c) 2012 Yin-Chun Wang

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies
of the Software, and to permit persons to whom the Software is furnished
to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included
in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES
OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE
OR OTHER DEALINGS IN THE SOFTWARE.
```
