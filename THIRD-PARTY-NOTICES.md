# Third-Party Notices

Kontur Code is MIT-licensed (see [LICENSE](LICENSE)). That licence covers **this
repository's own source only**. It does not extend to the third-party components
the built application bundles, several of which are under their own terms. This
file lists them so the obligations travel with the source.

**This is a summary, not the licence texts.** Each component's authoritative
licence is the one in its own distribution. The versions below are those pinned at
the time of writing; run `dotnet list package` and `npm ls --depth=0` for the
resolved tree in your checkout.

---

## Compiled into the .NET application

| Component | Version | Licence | Notes |
| --- | --- | --- | --- |
| [WPF-UI](https://github.com/wpf-ui/WPF-UI) | 4.3.0 | MIT | WPF control library. Used by the App project only. |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | 8.4.0 | MIT | ObservableObject and source generators. |
| [Microsoft.Extensions.Hosting](https://learn.microsoft.com/dotnet/core/extensions/) | 10.0.11 | MIT | |
| [Microsoft.Extensions.Http](https://learn.microsoft.com/dotnet/core/extensions/) | 10.0.11 | MIT | `IHttpClientFactory` for provider clients. |
| [Microsoft.Extensions.Logging.Abstractions](https://learn.microsoft.com/dotnet/core/extensions/) | 10.0.11 | MIT | |
| [Microsoft.Extensions.Options](https://learn.microsoft.com/dotnet/core/extensions/) | 10.0.11 | MIT | |
| [Microsoft.Extensions.DependencyInjection.Abstractions](https://learn.microsoft.com/dotnet/core/extensions/) | 10.0.11 | MIT | |
| [Microsoft.EntityFrameworkCore](https://learn.microsoft.com/ef/core/) | 10.0.11 | MIT | |
| [Microsoft.EntityFrameworkCore.Sqlite](https://learn.microsoft.com/ef/core/providers/sqlite) | 10.0.11 | MIT | SQLite itself is public domain. |
| [Microsoft.EntityFrameworkCore.Design](https://learn.microsoft.com/ef/core/) | 10.0.11 | MIT | Build-time only. |
| [Markdig](https://github.com/xoofx/markdig) | 1.3.2 | BSD-3-Clause | Markdown parsing, used in chat rendering. |
| [System.Security.Cryptography.ProtectedData](https://learn.microsoft.com/dotnet/api/system.security.cryptography.protecteddata) | 10.0.11 | MIT | DPAPI wrapper for API-key storage. |
| [System.Speech](https://learn.microsoft.com/dotnet/api/system.speech) | 10.0.11 | MIT | Windows dictation, WPF host only. |

## Test-only

| Component | Version | Licence |
| --- | --- | --- |
| [Microsoft.NET.Test.Sdk](https://learn.microsoft.com/dotnet/core/testing/) | 18.8.1 | MIT |
| [xunit.v3](https://github.com/xunit/xunit) | 3.2.2 | Apache-2.0 |
| [xunit.runner.visualstudio](https://github.com/xunit/xunit) | 3.1.5 | Apache-2.0 |

> **xunit is Apache-2.0, not MIT.** It is test-only and never ships in a build, but
> Apache-2.0 carries a patent grant and a NOTICE obligation, which is why it is
> called out separately rather than merged into the MIT list above.

## Runtime and shell

| Component | Licence | Notes |
| --- | --- | --- |
| [.NET 10 runtime](https://github.com/dotnet/runtime) | MIT | Windows desktop runtime. |
| [ASP.NET Core](https://github.com/dotnet/aspnetcore) | MIT | The local sidecar's framework. |
| [Electron](https://github.com/electron/electron) | MIT | Includes Chromium and Node.js. |
| [Chromium](https://chromium.googlesource.com/chromium/src/) | BSD-3-Clause and others | Bundled with Electron; see `LICENSES.chromium.html` in a packaged build. |
| [Node.js](https://github.com/nodejs/node) | MIT and others | Bundled with Electron. |

## Renderer bundle

Runtime dependencies that are actually reachable from the shipped renderer:

| Package | Licence |
| --- | --- |
| [react](https://github.com/facebook/react), [react-dom](https://github.com/facebook/react) | MIT |
| [zustand](https://github.com/pmndrs/zustand) | MIT |
| [clsx](https://github.com/lukeed/clsx) | MIT |
| [tailwind-merge](https://github.com/dcastil/tailwind-merge) | MIT |
| [class-variance-authority](https://github.com/joe-bell/cva) | Apache-2.0 |
| [lucide-react](https://github.com/lucide-icons/lucide) | ISC |
| [sonner](https://github.com/emilkowalski/sonner) | MIT |
| [framer-motion](https://github.com/motiondivision/motion) | MIT |
| [jszip](https://github.com/Stuk/jszip) | MIT or GPL-3.0 (dual) |
| [date-fns](https://github.com/date-fns/date-fns) | MIT |
| [CodeMirror 6](https://github.com/codemirror) — `state`, `view`, `commands`, `language`, `search`, `autocomplete`, `legacy-modes`, `highlight` | MIT |
| [Lezer](https://github.com/lezer) — `@lezer/highlight` | MIT |
| [CodeMirror language grammars](https://github.com/codemirror/lang) — cpp, css, html, java, javascript, json, markdown, python, rust, xml | MIT |
| `@radix-ui/react-switch`, `@radix-ui/react-slider` | MIT |

### Unused packages present in `package.json`

`electron/package.json` declares roughly forty dependencies that no shipped code
imports: the remaining `@radix-ui/*` packages, `recharts`, `cmdk`,
`embla-carousel-react`, `react-day-picker`, `react-hook-form`,
`react-resizable-panels`, `vaul`, `input-otp` and friends. They came in with a UI
kit and are not used by `components/kontur/`.

They are **not** bundled into a build, so they do not ship. They are still a
dependency review burden and a supply-chain surface for anyone who runs
`npm install`, so removing them is tracked in `CHANGELOG.md`.

---

## Fonts and assets

No third-party fonts are bundled. The renderer uses the system UI font stack.
The application icon (`electron/resources/logo.svg`) and the WPF assets under
`src/AIClient.App/Assets/` are the project's own.

---

## How to verify

```bash
dotnet list AIClient.slnx package --include-transitive   # .NET, with licences
cd electron && npm ls --all                              # npm tree
npx license-checker --production --summary               # npm licences
```

Full licence texts ship inside the packaged application:
`resources/LICENSE.electron.txt` and `resources/LICENSES.chromium.html` under the
Electron resources directory.

---

## Reporting an attribution problem

If you believe a component is used here without the attribution its licence
requires, open an issue on the repository. Attribution bugs are treated as bugs,
not as nitpicks.