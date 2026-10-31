# Third-party notices

Tern's own code is distributed under the [MIT License](LICENSE). Its libraries, fonts and bundled runtime retain their original licenses, copyright notices and conditions. Keep this file and the complete `licenses/` directory with redistributed application packages.

## License texts and inventory

The [license directory](https://github.com/gcfernando/tern-markdown-editor/tree/main/licenses) contains full texts, copied upstream/package notices and a [machine-readable dependency inventory](https://github.com/gcfernando/tern-markdown-editor/blob/main/licenses/dependency-inventory.json). The inventory was produced from the locked desktop project graph and includes build-only and non-Windows entries; the Windows package does not claim support for those other platforms. Upstream license documents are preserved rather than reformatted as project guides.

The graph declares MIT, BSD-2-Clause and Apache-2.0 package licenses. Inter's font data separately uses the SIL Open Font License 1.1. SQLite's underlying database engine is public domain; the Microsoft and SQLitePCLRaw bindings have their own MIT and Apache-2.0 terms. Native Skia, HarfBuzz and ANGLE components include additional upstream notices supplied in their package files.

## Locked desktop packages

| Package | Locked version | Package license | Copyright / attribution from package metadata |
| --- | --- | --- | --- |
| Avalonia.Angle.Windows.Natives | 2.1.27548.20260419 | LICENSE | Copyright 2013-2026 © The AvaloniaUI Project |
| Avalonia.AvaloniaEdit | 12.0.0 | MIT | Copyright 2017-2026 © The AvaloniaUI Project |
| Avalonia.BuildServices | 11.3.2 | MIT | Copyright 2023-2025 © The AvaloniaUI Project |
| Avalonia.Desktop | 12.1.4 | MIT | Copyright 2013-2026 © The AvaloniaUI Project |
| Avalonia.Fonts.Inter | 12.1.4 | MIT | Copyright 2013-2026 © The AvaloniaUI Project |
| Avalonia.FreeDesktop.AtSpi | 12.1.4 | MIT | Copyright 2013-2026 © The AvaloniaUI Project |
| Avalonia.FreeDesktop | 12.1.4 | MIT | Copyright 2013-2026 © The AvaloniaUI Project |
| Avalonia.HarfBuzz | 12.1.4 | MIT | Copyright 2013-2026 © The AvaloniaUI Project |
| Avalonia.Native | 12.1.4 | MIT | Copyright 2013-2026 © The AvaloniaUI Project |
| Avalonia.Remote.Protocol | 12.1.4 | MIT | Copyright 2013-2026 © The AvaloniaUI Project |
| Avalonia.Skia | 12.1.4 | MIT | Copyright 2013-2026 © The AvaloniaUI Project |
| Avalonia.Themes.Fluent | 12.1.4 | MIT | Copyright 2013-2026 © The AvaloniaUI Project |
| Avalonia.Win32 | 12.1.4 | MIT | Copyright 2013-2026 © The AvaloniaUI Project |
| Avalonia.X11 | 12.1.4 | MIT | Copyright 2013-2026 © The AvaloniaUI Project |
| Avalonia | 12.1.4 | MIT | Copyright 2013-2026 © The AvaloniaUI Project |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | (c) .NET Foundation and Contributors. All rights reserved. |
| HarfBuzzSharp.NativeAssets.Linux | 8.3.1.3 | MIT | © Microsoft Corporation. All rights reserved. |
| HarfBuzzSharp.NativeAssets.macOS | 8.3.1.3 | MIT | © Microsoft Corporation. All rights reserved. |
| HarfBuzzSharp.NativeAssets.WebAssembly | 8.3.1.3 | MIT | © Microsoft Corporation. All rights reserved. |
| HarfBuzzSharp.NativeAssets.Win32 | 8.3.1.3 | MIT | © Microsoft Corporation. All rights reserved. |
| HarfBuzzSharp | 8.3.1.3 | MIT | © Microsoft Corporation. All rights reserved. |
| Markdig | 1.5.0 | BSD-2-Clause | Alexandre Mutel |
| MicroCom.Runtime | 0.11.6 | MIT | Copyright 2021 © Nikita Tsukanov |
| Microsoft.Data.Sqlite.Core | 10.0.14 | MIT | © Microsoft Corporation. All rights reserved. |
| Microsoft.Data.Sqlite | 10.0.14 | MIT | © Microsoft Corporation. All rights reserved. |
| Microsoft.Extensions.DependencyInjection.Abstractions | 8.0.2 | MIT | © Microsoft Corporation. All rights reserved. |
| Microsoft.Extensions.Logging.Abstractions | 8.0.3 | MIT | © Microsoft Corporation. All rights reserved. |
| PDFsharp | 6.2.4 | MIT | © 2026 empira |
| SkiaSharp.NativeAssets.Linux | 3.119.4 | MIT | © Microsoft Corporation. All rights reserved. |
| SkiaSharp.NativeAssets.macOS | 3.119.4 | MIT | © Microsoft Corporation. All rights reserved. |
| SkiaSharp.NativeAssets.WebAssembly | 3.119.4 | MIT | © Microsoft Corporation. All rights reserved. |
| SkiaSharp.NativeAssets.Win32 | 3.119.4 | MIT | © Microsoft Corporation. All rights reserved. |
| SkiaSharp | 3.119.4 | MIT | © Microsoft Corporation. All rights reserved. |
| SQLitePCLRaw.bundle_e_sqlite3 | 2.1.13 | Apache-2.0 | Copyright 2014-2024 SourceGear, LLC |
| SQLitePCLRaw.core | 2.1.13 | Apache-2.0 | Copyright 2014-2024 SourceGear, LLC |
| SQLitePCLRaw.lib.e_sqlite3 | 2.1.13 | Apache-2.0 | Copyright 2014-2024 SourceGear, LLC |
| SQLitePCLRaw.provider.e_sqlite3 | 2.1.13 | Apache-2.0 | Copyright 2014-2024 SourceGear, LLC |
| System.Security.Cryptography.Pkcs | 10.0.14 | MIT | © Microsoft Corporation. All rights reserved. |
| Tmds.DBus.Protocol | 0.94.1 | MIT | Tom Deseyn |

## Bundled .NET runtime

The self-contained Windows x64 package uses Microsoft.NETCore.App 10.0.13, selected by the pinned SDK's restore graph. Its MIT license and third-party notices are retained under `licenses/dotnet-runtime/10.0.13/`. A separate .NET installation is not required for that package. Dependency package versions such as Microsoft.Data.Sqlite are separate from the runtime version.

## Upstream sources

- [Avalonia license](https://github.com/AvaloniaUI/Avalonia/blob/2d79ba55fae1b1e71c69f93a668080c9029e6a31/licence.md) and [included-component notices](https://github.com/AvaloniaUI/Avalonia/blob/2d79ba55fae1b1e71c69f93a668080c9029e6a31/NOTICE.md).
- [Markdig license at the package source revision](https://github.com/xoofx/markdig/blob/9900e46eacd9e17ba32f1b441a349bcab61777af/license.txt).
- [Inter font and SIL Open Font License](https://github.com/rsms/inter/blob/master/LICENSE.txt).
- [Official SPDX license texts](https://github.com/spdx/license-list-data/tree/main/text).
- [SQLite copyright and public-domain statement](https://sqlite.org/copyright.html).
- Other upstream repository URLs and available source revisions are retained from each NuGet package's metadata in `licenses/dependency-inventory.json`.

## Redistribution and maintenance

Preserve the original copyright and permission notices in copies or substantial portions of the relevant components. Retain the supplied Apache, BSD, font and native/runtime notices when redistributing the complete package. Do not treat Tern's MIT license as permission to omit third-party terms. Recheck the resolved graph and bundled files when dependencies or the runtime change.
