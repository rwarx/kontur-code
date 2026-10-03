# Privacy Policy

**Applies to Kontur Code `0.1.0-alpha` and every later build.**
**Effective date: 3 October 2026.**

Kontur Code is free and open-source software. It runs on your own computer. It
has no account system, no sign-up, and — this is the part that matters most —
**no server operated by the project that your data reaches.** This document
explains what happens to your data anyway, because "it's all local" is a claim
that is only worth something if it is specific.

> **This document is not legal advice.** It was written by the project's
> contributors, not by a lawyer, and it describes the software as the code
> actually behaves. Before you rely on it for a compliance decision — inside a
> company, for a procurement form, for a regulator — have a qualified lawyer in
> the relevant jurisdiction review it. Every section below says which legal
> instrument it is written against so that review has something to work from.

---

## 1. The short version

| | |
| --- | --- |
| Who is responsible for your data | You. Kontur Code's authors run no service that receives your data. |
| Is there an account? | No. No registration, no login, no user identifier. |
| Is there telemetry or analytics? | **No.** See [§4](#4-what-is-never-collected). |
| Is there a server run by this project? | **No.** Your conversation data never touches one. |
| What leaves your machine? | Only what you send to a model provider, and only when you press Send. |
| Where is your data stored? | In your own user profile directory. See [§3](#3-where-data-lives). |
| How do I delete everything? | Delete one file and one folder. See [§8](#8-deleting-your-data). |
| How do I get my data out? | Export from the app, or copy it. See [§7](#7-exporting-your-data). |

---

## 2. Who this is about, and who it is for

### 2.1 The roles

Data-protection law asks who is doing what with your information. For Kontur
Code the honest answer is split in two, and conflating them is the usual way
software like this gets its policies wrong:

- **You are the controller.** You decide what to type into Kontur Code, which
  folder to open in it, and which provider to point it at. Nobody but you can
  make those decisions, and the data is not processed on our behalf.
- **Your chosen model provider is an independent controller** for the content
  you send it. When you press Send, OpenRouter or NVIDIA receives your prompt as
  its own controller, under **its** privacy policy, not this one. See
  [§5](#5-what-leaves-your-machine-and-who-receives-it).

### 2.2 This is written against three legal instruments

Because the software is distributed globally, three regimes plausibly apply and
the policy tries to satisfy all three rather than picking the easiest:

| Instrument | Where it applies | What it requires of this document |
| --- | --- | --- |
| **GDPR** (Regulation (EU) 2016/679) | EU/EEA data subjects | Articles 13–14 transparency: who you are, what is processed, on what basis, who receives it, how long it is kept, what rights you have, whether transfers occur. [§9](#9-gdpr-rights-eu-eea-and-uk). |
| **Russian Federal Law No. 152-FZ** ("On Personal Data") | Russian data subjects | Consent, purpose limitation, security, and the right to withdraw consent. See [§10](#10-russian-federation--152-fz). |
| **COPPA** (US) and **CCPA/CPRA** (California) | US users | A posted policy; category disclosures; access and deletion rights. See [§11](#11-united-states). |

Nothing here establishes that a given instrument applies to you. Whether it does
depends on who you are, where you are, and what your employer has agreed — which
is precisely the question a lawyer answers.

### 2.3 Age

Kontur Code is a developer tool. It is **not directed at children**, and the
project does not knowingly collect information from anyone under 13. Because it
is distributed as free software and operates no service, COPPA's commercial
operator trigger does not apply to the project itself — but if you are under 13,
do not put personal information into it, and ask an adult before sending
anything to a model provider.

---

## 3. Where your data lives

Everything Kontur Code writes is under your user profile, in a folder named
`AIClient`. Nothing is written beside the executable and nothing is written into
the source tree.

| Path (under `%APPDATA%\AIClient` on Windows) | Contents | Format |
| --- | --- | --- |
| `aiclient.db` | Conversations, messages, attachments, the model cache, all settings | SQLite, unencrypted |
| `secrets\<provider>.dat` | One provider API key per file | **Encrypted** — Windows DPAPI, `CurrentUser` scope |
| `logs\aiclient-<date>.log` | One file per day, older ones deleted at startup | Plain text |
| `attachments\` | Copies of files you attached | Plain text, as the source was |
| `graphs\`, `checkpoints\` | Spatial-graph state and session snapshots | JSON |

Three consequences worth stating plainly:

1. **Your conversations are not encrypted at rest.** They are an ordinary SQLite
   file. Anyone with access to your Windows account can read them. This is a
   deliberate trade-off — an encrypted database would put the key somewhere the
   same account can reach — but it is a trade-off, not an oversight.
2. **Your API keys are encrypted**, with a key only your Windows account can
   derive. They are never written to disk in the clear, never sent to any
   Kontur Code address, and never written to a log.
3. **Logs deliberately contain almost nothing.** A log line carries a category, a
   timestamp and an error kind. It does not carry your prompt, a file path, a tool
   argument or a key. This is enforced by tests that capture log output and search
   it, not by convention.

---

## 4. What is never collected

This is the section that distinguishes local-first software from software with a
business model, so it is worth being exact:

- **No telemetry, no analytics, no crash reporting, no usage metrics.** There is
  no code in this repository that opens a connection to any address owned by the
  project. If you want to verify this rather than trust it: search the tree for
  the domains it talks to (§5), and read `Program.cs` in the sidecar, which is
  where every outbound address in the product is named.
- **No account, no identifier, no persistent user ID.** There is nothing to
  associate one install with another.
- **No advertising, no profiling, no cross-site tracking, no cookies.** The app
  has no cookie store and no web analytics tag.
- **No data sold or shared for anyone else's benefit.** The project receives no
  data to share, because it receives no data.

---

## 5. What leaves your machine, and who receives it

This is the entire list. If something is not here, the application does not do it.

### 5.1 Model provider requests — the only outbound data flow

When you press **Send**, the following goes to the provider you configured:

- your message text, the conversation history Kontur Code has assembled for this
  turn, and any attachment text;
- for an agent run, the file contents the agent read, and the arguments of the
  tool calls it made;
- a system prompt, and sampling settings (model, temperature, token limits);
- your provider API key, in the `Authorization` header.

The provider is chosen by you in Settings. This build ships two, both speaking
the OpenAI-compatible protocol:

| Provider | Endpoint | Privacy policy |
| --- | --- | --- |
| OpenRouter | `https://openrouter.ai/api/v1` | <https://openrouter.ai/privacy> |
| NVIDIA | `https://integrate.api.nvidia.com/v1` | <https://www.nvidia.com/en-us/privacy-policy/> |

NVIDIA's base URL is overridable, so you can point Kontur Code at a local
OpenAI-compatible server (Ollama, LM Studio, a self-hosted NIM container) and
keep the request on your machine. See [DEVELOPMENT.md](DEVELOPMENT.md#configuration).

**Everything a provider does with your prompt is governed by that provider's
policy, not by this document.** If that matters to you, that is the trade this
tool makes: it is a client for someone else's model, and the model sees what you
send. Read the provider's terms before you send anything you would not want read.

### 5.2 Optional agent capabilities

Two agent features send data beyond the model call, and both are **off until you
turn them on**, and both put every call in front of an approval prompt:

- **Network fetch** — lets the agent retrieve a URL and read the response. Off by
  default. The URL you or the agent names is fetched from your machine, and the
  text is then sent to the provider as part of the next step.
- **External files** — lets the agent read and write files outside the folder you
  opened. Off by default. Requires approval per call.

### 5.3 Git

The Git panel runs `git` on your machine to read status and diffs, and — only
when you click — to stage, commit, push, pull and fetch. Remote names and branch
names are validated before they reach the `git` argument list. Nothing about your
repository is sent anywhere by Kontur Code.

### 5.4 Update checks

There are none. The application does not phone home to discover that a newer
version exists. Releases are announced on the GitHub releases page, which you
can visit or not.

### 5.5 Complete list of network destinations

For verification, these are the only hosts this codebase contacts:

| Host | Purpose | When |
| --- | --- | --- |
| `openrouter.ai` | model catalogue, chat completions | you press Send, or refresh models |
| `integrate.api.nvidia.com` | model catalogue, chat completions | as above |
| `github.com` | sends `HTTP-Referer: https://github.com/rwarx/kontur-code` on OpenRouter requests | you press Send (this is OpenRouter's convention for app attribution) |
| `127.0.0.1:45631` | the local sidecar, spoken to by the app itself | always, on your own machine |

Plus any host **you** explicitly configure as a provider, and any host the agent
fetches when you enable and approve network access.

---

## 6. Legal bases for processing (GDPR Art. 6)

Where GDPR applies, the processing Kontur Code performs locally rests on:

- **Art. 6(1)(b) — performance of a contract**, for storing and displaying your
  conversations and settings: it is what makes the application do the thing you
  opened it for.
- **Art. 6(1)(f) — legitimate interests**, for the minimal operational logging
  described in [§3](#3-where-data-lives), which exists so the application can
  tell you why something failed. The interest is diagnosing faults in software you
  chose to run; the data is a category and a timestamp, not your content.
- **Art. 6(1)(a) — consent**, for anything that leaves your machine, because we
  cannot ask for it on your behalf: pressing **Send** is the act of consent, and
  it is specific to the provider you chose and the content you chose to send.

Consent is withdrawable at any time and does not affect the lawfulness of
processing before withdrawal. Withdrawing it is simple: stop sending messages,
or delete the provider's key in Settings. Once the key is deleted, Kontur Code
cannot authenticate to that provider at all.

**No automated decision-making with legal or similarly significant effects**
occurs within Kontur Code. The project itself makes no decision about you. Your
provider may — a model does produce output — which is a further reason the
provider's own policy, not this one, governs what happens to your prompt.

---

## 7. Exporting your data

GDPR Art. 20 gives a right to data portability, and it should not cost anything
when the data never left your machine.

- **Conversations** — the header's **Export / Import** button writes a session
  bundle (`.zip`) containing chat, canvas, files and goals.
- **Settings** — each section is one JSON row in the `Settings` table and is
  readable with any SQLite browser.
- **Everything else** — it is in the files listed in [§3](#3-where-data-lives),
  which you can open directly. There is no export format to learn, because there
  is no server to export from.

Format note: exported bundles are a project format, not a documented public
standard. The conversation text itself is plain text or Markdown, so it is
readable without this application.

---

## 8. Deleting your data

There is no server, so there is nothing to request and no one to process the
request. Deleting is a file operation you control:

1. **Individual conversations** — delete in the app; the rows cascade.
2. **Everything** — quit the application, then delete `%APPDATA%\AIClient`. This
   removes the database, the encrypted keys, the logs and the attachments folder.
   The application recreates an empty database on next launch; you will need to
   re-enter your API keys.
3. **To be certain a provider no longer holds it** — deleting your local copy
   cannot delete your provider's copy. Deleting the key stops future sending.
   Provider-side retention is governed by that provider's terms; where a provider
   offers account or request deletion, that is the mechanism to use.

If you want a key to be unrecoverable, delete `secrets\` — DPAPI's scope means it
is already unreadable to any other Windows account, but deletion removes it
entirely.

---

## 9. GDPR rights (EU/EEA and UK)

Where GDPR applies to you, and where you are the controller of the data in your
own install, the rights are exercised **against the provider that holds the
copy**, not against this project — because this project holds no copy that
leaves your machine.

| Right | How you exercise it here |
| --- | --- |
| **Access** (Art. 15) | Read your install directly, or export. |
| **Rectification** (Art. 16) | Edit in the app, or edit the database. |
| **Erasure** (Art. 17) | [§8](#8-deleting-your-data). |
| **Restriction** (Art. 18) | Stop using the application; nothing is transmitted. |
| **Portability** (Art. 20) | [§7](#7-exporting-your-data). |
| **Object** (Art. 21) | [§6](#6-legal-bases-for-processing-gdpr-art-6). |
| **Withdraw consent** (Art. 7(3)) | Delete the provider key, or stop sending. |
| **Complaint** (Art. 77) | You may complain to the supervisory authority in your country of residence or place of work. A list is at <https://www.edpb.europa.eu/about-edpb/about-edpb-members_en>. |

**Transfers outside the EEA.** When you press Send, the request goes to a provider
outside the EEA, and for OpenRouter (US) and NVIDIA (US) there is no EU adequacy
decision in place for the transfer. The relevant safeguard is the provider's own
published policy and standard contractual terms. If you need to know what those
are, they are at <https://openrouter.ai/privacy> and
<https://www.nvidia.com/en-us/privacy-policy/>. If transfer governance is a
requirement for your organisation, configure a provider endpoint you control
(§5.1) so the data does not leave your jurisdiction at all.

---

## 10. Russian Federation — 152-FZ

If you are in Russia, Federal Law No. 152-FZ applies to the processing of your
personal data. Kontur Code is structured so that the project's authors hold none.

**Localisation.** Article 18(5) requires that the collection, storage,
processing and transfer of Russian citizens' personal data use databases located
in Russia. This obligation attaches to an operator or a data holder that
**processes** such data. Kontur Code processes your data on your own machine, and
the project's authors operate no server anywhere, so there is no database this
project could be required to localise — and none is operated.

**If your employer asks for a compliant deployment,** the supported path is the
one this document already describes: configure a provider endpoint inside your own
jurisdiction ([§5.1](#51-model-provider-requests--the-only-outbound-data-flow)),
so that neither the model provider nor this project holds a copy outside it.
That is a deployment decision, and this document does not make it for you.

**Your rights under 152-FZ**, and how each is exercised:

| Right | Article | How |
| --- | --- | --- |
| To be informed | 14 | This document, and the Settings screen. |
| Consent | 18(1) | Pressing Send, for that message, for that provider. |
| Withdraw consent | 18(1) | Stop sending; delete the provider key. |
| Access, rectification, deletion | 14–14.1 | [§7](#7-exporting-your-data), [§8](#8-deleting-your-data). |
| Stop processing | 14(1) | Delete `%APPDATA%\AIClient`. |
| Object to automated processing | 21 | Kontur Code performs none itself; see [§6](#6-legal-bases-for-processing-gdpr-art-6). |

**Security (Art. 19).** Keys are encrypted with DPAPI. The local sidecar requires
a per-launch bearer token and refuses to bind to anything but loopback. The agent's
file access is confined to the folder you opened, with credential-shaped filenames
refused by name. These are described in [SECURITY.md](SECURITY.md), which also
lists what is *not* protected.

**Cross-border transfer (Art. 12).** Sending to OpenRouter or NVIDIA is a
transfer to a third country. It is a transfer **you initiate**, to a provider
**you chose**, over a connection from **your** machine — the project is not the
exporter and does not hold the data. If you need processing to stay in Russia,
use the local-endpoint option in [§5.1](#51-model-provider-requests--the-only-outbound-data-flow).

**Localisation caveat worth being honest about:** the phrase above is an argument,
not a certification. If a Russian regulator disagreed, the argument would be tested
by them and not by this document. Get advice before relying on it.

---

## 11. United States

**California (CCPA/CPRA).** Kontur Code does not sell personal information, does
not share it for cross-context behavioural advertising, and has no knowledge of it
in a form it could link to a consumer — there is no consumer record, because there
is no account. "Sharing" for cross-context behavioural advertising does not occur.
If CCPA/CPRA applies to you, the categories of personal information involved are
those you choose to place in a prompt, and exercising access or deletion is
[§7](#7-exporting-your-data) and [§8](#8-deleting-your-data).

**COPPA.** COPPA is not engaged: this project is not a commercial operator of a
website or online service directed to children, and does not run one. Note that
the FTC **amended the COPPA Rule on 22 April 2025**; the amended rule governs any
future hosted offering, so re-read this section before shipping one. See
<https://www.ftc.gov/legal-library/browse/rules/childrens-online-privacy-protection-rule-coppa>.

**Other US states.** State breach-notification statutes are written around
operators holding unencrypted personal data. Kontur Code holds none of yours
off-machine, and the one credential it does hold is DPAPI-encrypted. Consult
<https://www.ftc.gov/privacy-and-security/privacy-policy>.

---

## 12. Security

Summarised here; the detail, including the known gaps, is in
[SECURITY.md](SECURITY.md). In outline: keys are DPAPI-encrypted per Windows
account; the local API requires a per-launch bearer token and refuses non-loopback
binds; the renderer's process is context-isolated, sandboxed and node-free;
Content-Security-Policy is applied and a web page cannot read your token; the
agent's file access is confined to the folder you opened; and every write above
`AgentToolRisk.Read` goes through an approval prompt that defaults to refusing.

The gaps are stated in that document rather than here, because a privacy policy
that lists only strengths is not a privacy policy.

---

## 13. Children

Not directed at children under 13. Do not enter children's personal information.
See [§2.3](#23-age).

---

## 14. Changes to this policy

Material changes are announced in [`CHANGELOG.md`](CHANGELOG.md) with the version
that introduced them, and listed on the GitHub release page. Because this is a
file in a repository, you can always read the version that shipped with the build
you are running — the policy travels with the code rather than sitting on a server
that can change after you have read it.

---

## 15. Contact

**Project and repository:** <https://github.com/rwarx/kontur-code>
**Security reports:** see [SECURITY.md](SECURITY.md) — private vulnerability
reporting is enabled on the repository.
**Everything else:** <https://github.com/rwarx/kontur-code/issues>

> **Before distributing a build of this yourself, fill this in.** GDPR Art. 13(1)(a)
> and 152-FZ Art. 18(1) both require the identity and contact details of the party
> responsible for processing. A GitHub URL identifies the repository but is not a
> contact point for a data subject's request. Add a monitored email address, and
> — if your organisation has one — a registered address, before you rely on this
> document to satisfy either article. We have left the placeholder rather than
> inventing a contact that does not exist, because a privacy policy pointing at an
> unmonitored inbox is worse than one that says it is incomplete.

---

## 16. Scope of the software licence

Kontur Code is MIT-licensed; see [LICENSE](LICENSE). The licence covers the
software. **It does not cover your data, and it does not make the project a party
to any contract with you.** The MIT terms include a warranty disclaimer and a
limitation of liability. If you are deploying this in an organisation and need
indemnity, support or an SLA, that is a commercial conversation, and this
repository is not the place for it.

---

*This document describes Kontur Code `0.1.0-alpha`. It was written by the project's
contributors and reviewed by nobody with a law degree. Treat it as an accurate and
good-faith description of what the code does, and get it reviewed before relying
on it.*