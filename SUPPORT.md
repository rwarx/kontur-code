# Support

## Where to ask

| You want to… | Go to |
| --- | --- |
| Report a bug | <https://github.com/rwarx/kontur-code/issues> |
| Ask how something works | <https://github.com/rwarx/kontur-code/discussions> |
| Ask whether something is a bug or intended | Discussions — read [ARCHITECTURE.md](ARCHITECTURE.md) first, it may already answer it |
| Report a vulnerability | **Do not open an issue.** See [SECURITY.md](SECURITY.md) |
| Report a licence or attribution problem | <https://github.com/rwarx/kontur-code/issues> — these are treated as bugs |
| Understand your data | [PRIVACY.md](PRIVACY.md) |

Before opening anything: **[DEVELOPMENT.md § Troubleshooting](DEVELOPMENT.md#troubleshooting)**
answers the failures people hit most often — a model picker that stays empty, an
agent that refuses a file inside the folder, an API key that reads as absent after
a profile restore, a workspace that will not open.

---

## What to include in a bug report

1. **Version** — the bottom-left of the title bar, or Help → About.
2. **How you installed** — the GitHub installer, or `dotnet run` from a clone.
3. **What you expected and what happened**, as separate sentences.
4. **The log.** `%APPDATA%\AIClient\logs\`, newest file. This is the single most
   useful thing in a report.

Logs deliberately contain no prompt text, no file paths and no keys, so attaching
one does not leak your content. Read `PRIVACY.md § Where your data lives` if you
want to know why.

**Please do not paste conversation content into a public issue.** If a transcript
is needed to reproduce a bug, describe its *shape* — which provider, which mode,
roughly how long — and let the maintainer ask for more privately if it turns out
to be necessary.

---

## What this project will and will not do

This is volunteer work with no funding behind it. Being clear about that now is
more useful than being disappointed later.

**Will:**
- Triage and respond to bug reports, best effort
- Review pull requests, best effort
- Fix security issues, prioritised above everything else
- Keep the licence MIT and stay open source
- Publish what is fixed, in `CHANGELOG.md` and on the releases page

**Will not:**
- Provide support in any language other than English and Russian, and even there
  not on any particular schedule
- Guarantee any response time
- Provide an SLA, indemnity or commercial support contract
- Maintain a hosted or managed version
- Accept a bug bounty — there is no budget behind this repository

If you need an SLA, an indemnity, or a response inside a working day, that is a
commercial arrangement with a vendor. This repository is not that vendor.

---

## Before you report a bug, check these

**The model picker is empty.** A provider with no API key contributes nothing. Add
a key in Settings → Providers and press **Refresh**. The catalogue is cached in
SQLite, so it works offline afterwards but never before the first successful
refresh.

**The app starts and exits with an error dialog.** That path is only reached when
the host cannot be built or the database cannot be migrated. A database from a
newer build than the executable is the usual cause; deleting
`%APPDATA%\AIClient\aiclient.db` recreates it empty and costs you your local
conversation history.

**An API key that worked yesterday reads as absent.** DPAPI is scoped to your
Windows account, so a blob written by a different account — a restored backup, a
copied profile, a different user — cannot be decrypted. It deliberately reads as
*missing* rather than throwing. Re-enter the key.

**The agent refuses a file that is plainly inside the folder.** Check the name
against the refusal lists in `WorkspaceService`. `.env`, `credentials.json`,
`*.pem`, `*.key`, `*.pfx` and everything under `.git` are refused **inside** the
root as well as outside it. `.env.example` is allowed. The refusal message names
the rule.

**A planning run keeps refusing its own tool calls.** It is working. A planning
run is offered the read-only tools plus `submit_plan` and nothing else. If the
model is reaching for `write_file`, the run is in Plan mode and you wanted Build.

**Nothing happens when I press Ctrl+,** or another advertised shortcut. The
shortcut list is in the cheatsheet behind the `?` key. Ctrl+, opens Settings.

**The app is blank or shows no data.** Check the console for a `401`. The sidecar
requires a per-launch bearer token; if you started `AIClient.Server.exe` by hand
rather than through the app, it generated a different token and the renderer's
requests will be rejected. Start it through the app. See [SECURITY.md](SECURITY.md).

---

## Contributing a fix

Read [CONTRIBUTING.md](CONTRIBUTING.md). The short version:

```bash
dotnet build AIClient.slnx    # warnings are errors
dotnet test                   # 896 tests, no network or key needed
cd electron && npm run typecheck
```

A pull request that describes the behaviour change and what you verified is far
more likely to get a timely review than one that does not.