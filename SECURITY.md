# Security Policy

**Applies to Kontur Code `0.1.0-alpha` and every later build.**

Kontur Code runs a language-model agent with the ability to read and write files
and to run programs. That is the product. A security document for this project is
therefore not a list of hardening achievements — it is mostly a description of what
the agent can reach, what stops it, and **what does not**.

---

## Reporting a vulnerability

**Do not open a public issue for a security problem.**

Use GitHub's private reporting on this repository:
**<https://github.com/rwarx/kontur-code/security/advisories/new>**

If that is unavailable to you, open a regular issue that says only *"security
report, please open a private channel"* — with **no technical detail** — and wait.

**What to include:** what you can do that you should not be able to do, the
version, your OS, and the steps. A proof of concept is far more useful than a
description.

**What to expect:** an acknowledgement within a few days, and a fix or an
assessment. This is a volunteer project with no SLA, and pretending otherwise would
be dishonest. If your situation has a deadline that we cannot meet, treat that as
information about your choice of vendor, not as a complaint.

**What we ask in return:** give us a reasonable window to ship a fix before
disclosing, and do not access data that is not yours. There is no bug bounty.

---

## Threat model

The following are **in** scope for this document, and the rest are **out**.

| In scope | Out of scope |
| --- | --- |
| A web page the user has open, attacking the local sidecar | Another program running as the same Windows user |
| The agent escaping the folder the user opened | A user deliberately granting the agent broad access |
| The agent executing something it was not meant to | The contents of a file the user opened in the editor |
| An API key reaching a log, a transcript or the network in the clear | Malicious content in a model response, read by a human |
| A remote or branch name becoming code execution in `git` | Denial of service by the user against their own machine |
| A remote provider endpoint redirecting traffic to an attacker's host | Provider-side breaches or retention |
| Secrets in the source tree, a bundle or a release artefact | Weaknesses in Electron, Chromium, .NET or a NuGet dependency |

The out-of-scope column is a decision, not an oversight. **Another process running
as your Windows account can read your conversation database and decrypt your API
keys directly.** DPAPI's `CurrentUser` scope means only your account can, and any
process running as your account *is* your account. No amount of work inside this
codebase changes that, and pretending otherwise would misrepresent the product.

---

## What is protected, and by what

### 1. API keys

- Stored one per provider, **DPAPI `CurrentUser`**-encrypted, outside the source
  tree in `%APPDATA%\AIClient\secrets\`.
- Written to `<key>.dat.tmp` and moved into place, so a concurrent read cannot see
  a half-written blob.
- Never attached to a shared `HttpClient` — set per request, so it cannot leak into
  a later call's headers.
- Never rendered in the visual tree: the key field is a `PasswordBox` whose
  `Password` is deliberately not bound.
- Never logged. Log messages carry the **key name** only. A test captures log output
  at `Trace` and `Debug` and searches it, because careless logging hides there.
- A blob that will not decrypt reads as *absent*, not as an error, so a restored
  profile from another account leads you to re-enter it.

### 2. The local sidecar

The .NET backend listens on `127.0.0.1:45631`. **Loopback is not an authorisation
boundary** — any web page can send a same-machine request and, under permissive
CORS, read the response. So:

- **A per-launch bearer token is required on every route** except `/api/health`.
  The token is generated in the Electron main process from 32 bytes of CSPRNG
  output, passed to the sidecar over an inherited environment variable, and handed
  to the renderer over the context bridge. It is not bundled, not persisted, and
  not in a URL.
- **Comparison is constant-time**, on SHA-256 digests rather than on the strings,
  so neither length nor a prefix leaks.
- **There is no default token.** Started by hand with none configured, the sidecar
  generates one; a hardcoded fallback would be a published constant.
- **A non-loopback bind is refused at startup**, loudly, rather than quietly
  opening a port.
- **CORS is restricted** to loopback origins, the `null` origin that a `file://`
  renderer reports, and requests with no `Origin` at all.
- **Settings that reduce containment cannot be written over HTTP.** The command
  allowlist, the command/network/external-file switches and the workspace root are
  refused by name on the settings route — otherwise a caller with the token could
  turn off the guard rails and then answer its own approval prompts.
- **`/api/ai/complete` is clamped**: prompt length, system length, temperature and
  output tokens are bounded, so even a caller legitimately inside the app cannot
  spend unbounded credit through it.
- **`git` arguments are validated.** A remote name containing `:` is refused,
  because `ext::` is a documented git transport that runs a local program;
  `--`-prefixed names are refused; branch and revision names carrying refspec
  characters are refused; and `--` ends git's options before a file path.

### 3. The renderer

- `contextIsolation: true`, `nodeIntegration: false`, `sandbox: true`.
- **`window.open` and in-page navigation are blocked.** A child window inherits the
  preload bridge, so an unhandled `window.open` would be a second privileged
  surface. Outbound links go through a protocol-gated `openExternal`.
- **All permission requests are refused** — camera, microphone, geolocation,
  notifications. Nothing in the app needs them.
- **Content-Security-Policy** is applied, with `object-src 'none'`,
  `base-uri 'none'`, `frame-src 'none'`. `script-src` has no `unsafe-inline` in a
  packaged build. The policy is injected at runtime from the loopback address
  actually in use, so pointing the app at a different port cannot silently produce
  an app that loads no data.
- **`kontur:readFileLocal` is confined** to folders the user picked through the OS
  dialog in this session, refuses symlinks, and is capped at 8 MB. It was an
  arbitrary-path read with no cap before this release.

### 4. The agent's reach

The agent is the part with teeth, so this is the part worth reading twice.

- **One root.** Every path the agent names is resolved against the folder the user
  opened and refused if it lands outside — through `..`, through an absolute path,
  or through a symbolic link or junction, which is why enumeration skips reparse
  points and links are resolved to their final target before comparison.
- **Refusal by name as well as by path.** Version-control internals and
  credential-shaped filenames (`.env`, `credentials.json`, `*.pem`, `*.key`, …) are
  refused even *inside* the folder. `.env.example` is allowed, because it exists to
  be committed.
- **Risk decides the gate.** A tool declares `Read`, `Write` or `Execute`, and
  everything above `Read` goes to the approval gate. There is no second allowlist
  to keep in step, and the **default approval implementation refuses** — a host that
  forgets to install one gets an agent that can only read.
- **A standing "yes" covers file writes, never programs.** `Execute` is excluded,
  so ten commands is ten questions — and `git status` and `git clean -xfd` are not
  the same question.
- **No shell.** Programs are started directly with an argument list. `&&`, `|`,
  `>` and `$HOME` are text the program receives, not syntax anything interprets.
  The program must be on an allowlist a person edits, which is off by default.
- **A child process cannot read the environment.** Variables this application owns,
  and anything whose name looks like a credential, are stripped before a program is
  started — because a build script that prints its environment is a log.
- **Fetch is guarded against SSRF.** `AllowAutoRedirect` is off and each redirect
  hop is re-checked; the address guard runs while resolving the name and connects
  to the addresses it validated, which closes the DNS-rebinding window; private,
  loopback, link-local and carrier-grade ranges are refused; responses are capped.

---

## Known gaps

These are real, known, and not fixed in `0.1.0-alpha`. They are listed because a
security page that lists only strengths is marketing.

| # | Gap | Impact | Status |
| --- | --- | --- | --- |
| 1 | `ExternalFileService` checks sensitive *names* textually but does **not** resolve reparse points, unlike `WorkspaceService`. | A pre-existing junction inside an allowed path can redirect an external read or write to somewhere the name-based denial list would have refused. External access is off by default and per-call approved, which is what contains it today. | Open. High. |
| 2 | `RunRegistry.Start` accepts a caller-supplied run id and overwrites an existing entry. | A repeated id orphans the earlier run's `CancellationTokenSource` and approval waiter. Requires the bearer token, so it is reachable only from inside the app. | Open. Medium. |
| 3 | The approval-waiter map has one slot, so two approvals arriving together can leave the first waiter's promise unresolved. | A run can hang instead of failing. Availability, not confidentiality. | Open. Medium. |
| 4 | The NDJSON stream has no heartbeat, so a long provider stall or an open approval writes nothing. | Kestrel's minimum data rate can drop the connection; a dropped stream loses the run. No reconnect logic. | Open. Medium. |
| 5 | No request-size limit or rate limiter on the sidecar beyond Kestrel's 30 MB default. | A caller with the token can ask for expensive work. Contained by the token and by the `/api/ai/complete` clamps. | Open. Low. |
| 6 | Conversations are **not encrypted at rest**. | Anyone with your Windows account can read the database. Deliberate: an encrypted store would put the key where that account can reach it. | By design. Documented in [PRIVACY.md](PRIVACY.md). |
| 7 | The renderer's persisted state serialises workspace file contents into `localStorage`. | Larger working sets can exceed the browser storage quota; the failure is a silent loss of draft state, not a disclosure. | Open. Medium. |
| 8 | `main.js` and `preload.js` are plain JavaScript outside the TypeScript project, so they get no type checking. | The most security-critical surface in the shell is unchecked by the compiler. | Open. Low. |

If you find one of these exploited, report it privately as above — do not open a
public issue.

---

## Hardening your own install

1. **Review the command allowlist** before enabling command execution. It is off
   by default; turn it on only if you need it, and keep it short.
2. **Open the narrowest folder you can.** Every guard above is relative to the
   workspace root. Root at a project, not at a home directory. (A root that
   contains your user profile is refused.)
3. **Leave external file access and network fetch off** unless a task needs them.
   Each is per-call approved, which is the containment.
4. **Use a provider key you can rotate.** The app stores it encrypted, but the key
   is spent the moment you send a prompt.
5. **Run it as a normal user**, not an administrator. The agent can run programs
   from an allowlist, and those programs inherit whatever the account can reach.
6. **Keep version control as your undo.** Agent edits go directly into your working
   tree, with no backup taken first. A clean commit before a run is the cheapest
   safety net available.

---

*This document describes Kontur Code `0.1.0-alpha`. It is written by the project's
contributors; treat the "protected" sections as claims about code that has tests
behind them, and the "known gaps" section as the honest remainder.*