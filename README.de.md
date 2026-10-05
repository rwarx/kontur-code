# Kontur Code

**Lies das zuerst:** Kontur Code ist ein **Tool für Entwickler, das in deine Dateien schreiben und
Programme auf deinem Rechner ausführen kann.** Das ist das Produkt, kein Fehler darin. Alles
Weiteres zur Eingrenzung des Agenten — und jede bekannte Lücke — steht in
[SECURITY.md](SECURITY.md). Lies es, bevor du dieses Tool auf etwas richtest, das dir wichtig ist.

[English](README.md) · [Русский](README.ru.md) · [Deutsch](README.de.md) · [Español](README.es.md) ·
[Français](README.fr.md) · [Português (Brasil)](README.pt-BR.md) · [Italiano](README.it.md) ·
[中文（简体）](README.zh-CN.md) · [日本語](README.ja.md) · [한국어](README.ko.md) · [Türkçe](README.tr.md)

---

Ein LLM-Client für den Desktop, der zu einer **räumlichen KI-Entwicklungsumgebung** geworden ist.
Ein Fenster, deine eigenen API-Schlüssel, deine Unterhaltungen in einer lokalen SQLite-Datei — und
ein Arbeitsbereich, der den Ordner, den du ihm gibst, in einen Graphen verwandelt, den man wirklich
sehen kann.

Unter dem Fenster teilen sich zwei Hosts jede Schicht: eine **WPF-Anwendung** und eine
**Electron + React**-Hülle über demselben .NET-Kern, sodass die Anwendung durch die Obergrenze
keines der beiden Toolkits begrenzt ist.

<p align="center">
  <img src="docs/screenshots/canvas.png" alt="Das räumliche Canvas: der Arbeitsbereich als Abhängigkeitsgraph mit beschrifteten Kanten" width="100%">
</p>

---

## Inhalt

- [Screenshots](#screenshots)
- [Was es tut](#was-es-tut)
- [Der Agent](#der-agent)
- [Datenschutz in Kürze](#datenschutz-in-kürze)
- [Voraussetzungen](#voraussetzungen)
- [Installation](#installation)
- [Erster Start](#erster-start)
- [Architektur](#architektur)
- [Entwicklung](#entwicklung)
- [Dokumentation](#dokumentation)
- [Mitwirken](#mitwirken)
- [Lizenz](#lizenz)
- [Status](#status)

---

## Screenshots

### Chat

<p align="center">
  <img src="docs/screenshots/chat.png" alt="Eine Chat-Sitzung mit einer Antwort des Assistenten und dem Kontextbereich des Arbeitsbereichs" width="100%">
</p>

Tokens erscheinen, sobald sie eintreffen. Stoppst du mitten in der Antwort, **bleibt der Teiltext
erhalten**, statt verworfen zu werden — er bleibt als Kontext für den nächsten Zug verwendbar.

### Das räumliche Canvas

<p align="center">
  <img src="docs/screenshots/canvas.png" alt="Knoten und beschriftete Abhängigkeitskanten auf einem endlosen Canvas, mit Minikarte" width="100%">
</p>

Dein Projekt als Graph: Dateien, Ordner, Module, Services, Interfaces, Daten, Tests und Pläne
werden zu Knoten, dazwischen verlaufen Kanten für Enthalten und Abhängigkeit. Verschiebe, zoome,
wähle mit einem Rahmen aus und beobachte in der Ecke eine Minikarte des ganzen Graphen. Die Kanten
sind beschriftet — `Login() → CreateTokenAsync` ist eine Aufrufkante; „nur zur Kompilierzeit“ ist
eine Abhängigkeit, die nie ausgeführt wird.

### Die Gliederung des Graphen

<p align="center">
  <img src="docs/screenshots/graph-outline.png" alt="Ein filterbarer Gliederungsbaum des Graphen, gruppiert nach Knotenart" width="100%">
</p>

Derselbe Graph als Struktur, die du lesen und nach Name oder Pfad filtern kannst.

### Der Editor

<p align="center">
  <img src="docs/screenshots/editor.png" alt="Eine C#-Datei im Editor mit Syntaxhervorhebung und einem Änderungszähler" width="100%">
</p>

CodeMirror 6 mit zehn Sprachgrammatiken, Inline-KI-Änderungen an einer Auswahl und
Ghost-Text-Vervollständigung.

### Einstellungen

<p align="center">
  <img src="docs/screenshots/settings.png" alt="Einstellungen: Thema, Sprache, Oberfläche und Chat-Standardwerte" width="100%">
</p>

Thema, Oberflächensprache, App-Skalierung, System-Prompt, Sampling-Parameter — alles lokal, alles
in deiner eigenen Datenbank gespeichert.

---

## Was es tut

- **Chat im Streaming.** Tokens treffen ein, während sie erzeugt werden. Stopp behält die
  Teilantwort. Neu generieren ersetzt sie an Ort und Stelle, optional auf einem anderen Modell.
- **Drei Arbeitsmodi.** *Chat* für Unterhaltung, *Cowork* für Analyse und *Code* — dort bekommt der
  Agent einen Arbeitsbereich und eine Tool-Schleife. Der Modus ist eine Eigenschaft der Nachricht,
  nicht der Anwendung, „plane das, dann bau es“ sind also zwei Nachrichten statt zweier Wege in
  die Einstellungen.
- **Der räumliche Graph.** Dein Ordner wird automatisch in Knoten und Kanten indiziert. Ein
  Diff-basierter Indexer fügt neue Dateien hinzu, entfernt gelöschte und **erhält die Anordnung,
  die du gesetzt hast**. Die Pläne, die der Agent erstellt, landen als Knoten- und Kantensätze auf
  dem Canvas — rückgängig machbar, gespeichert und von dir ablehnbar.
- **Einheitliche Arbeitsbereichs-Flächen.** Das Canvas als Karte, der Graph als Struktur, ein
  Dateibaum, der Editor, eine git-Leiste, der Verlauf eines Laufs und eine Aufgabenansicht — alles
  nur ein `Ctrl+Shift+P` voneinander entfernt.
- **Git.** Status, gestagte und ungestagte Diffs, stage, commit, branch, revert, push, pull, fetch.
  Alles über `git`, **ohne Shell** und mit geprüften Argumenten.
- **Token-Bilanz.** Laufender Verbrauch, geschätzte Kosten und was das Modell tatsächlich im Kontext
  hält — dazu eine Schaltfläche **Sitzung verdichten**, die ältere Züge in eine Zusammenfassung
  faltet.
- **Markdown-Darstellung.** Überschriften, Listen, Tabellen, Zitate, Aufgabenlisten und Codeblöcke,
  mit Syntaxhervorhebung. Dargestellt als strukturierter Inhalt, **niemals als eingeschleustes
  HTML**.
- **Modellkatalog.** Von jedem Anbieter abgerufen und in SQLite zwischengespeichert, sodass die
  Auswahl danach offline funktioniert. Kontextfenster, Preise und Fähigkeiten kommen vom Anbieter,
  nicht aus einer fest verdrahteten Liste.
- **Zwei Anbieter ab Werk** — OpenRouter und NVIDIA NIM, beide OpenAI-kompatibel. Richte den
  Endpunkt von NVIDIA auf ein lokales Ollama, LM Studio oder einen selbst gehosteten NIM-Container
  aus, und nichts verlässt deinen Rechner.
- **Sitzungs-Pakete.** Exportiere die ganze Sitzung — Chat, Canvas, Dateien, Ziele — als `.zip`.
- **Drei Sprachen.** Englisch, Russisch und Deutsch, live über die ganze Oberfläche angewendet.
- **Hell und dunkel**, dem System folgend oder fest eingestellt.

---

## Der Agent

Der Agent führt eine Tool-Schleife aus, und seine Reichweite ist das, was du verstehen solltest,
bevor du ihn benutzt.

| | |
| --- | --- |
| **Arbeitet in** | Einem Ordner, den du benennst, und verweigert Lesen oder Schreiben außerhalb davon |
| **Ebenfalls verweigert, innerhalb dieses Ordners** | `.git`, `.env`, `credentials.json`, `*.pem`, `*.key`, `*.pfx` — nach Namen, immer |
| **Fragt vorher** | Jeden Schreibvorgang, jede externe Datei, jede Netzwerkanfrage, jedes Programm |
| **Niemals** | Eine Shell auszuführen. `&&`, `\|`, `>` und `$HOME` sind Text, den das Programm erhält |
| **Programme** | Standardmäßig aus. Dann nur eine Positivliste, die ein Mensch pflegt. Dann eine Freigabe bei *jedem* Aufruf |
| **Rückgängig** | Deine Versionsverwaltung. Änderungen werden gezeigt, bevor sie entstehen, nicht danach zurückgenommen |

Eine Verweigerung nennt die Regel und sagt dem Modell, was es stattdessen tun soll, damit es nicht
dreimal nach demselben Tool greift.

**Alles außerhalb dieses Ordners ist Opt-in und bleibt aus, bis du es einschaltest.** Netzwerk-
Abrufe und Dateizugriff außerhalb des Projekts sind getrennte Schalter in den Einstellungen, und
jeder Aufruf geht weiterhin durch die Freigabeabfrage. **Für sie wird keine Antwort gemerkt** — eine
Frage, ein Lesen oder ein Schreiben.

> Das vollständige Eingrenzungsmodell — und **was noch offen ist**, und das ist jetzt eine kurze
> Liste — steht in [SECURITY.md](SECURITY.md). Das ist eine Alpha; lies es, bevor du ihm
> vertraust.

---

## Datenschutz in Kürze

- **Keine Telemetrie. Keine Analyse. Kein Absturzbericht. Keine Konten.** In diesem Repository gibt
  es keinen Code, der eine Verbindung zu einer Adresse öffnet, die diesem Projekt gehört.
- **Deine Unterhaltungen erreichen nie einen Server.** Sie sind eine SQLite-Datei in deinem eigenen
  Benutzerprofil.
- **API-Schlüssel sind verschlüsselt** mit Windows DPAPI, an dein Windows-Konto gebunden und
  niemals in ein Log geschrieben.
- **Was deinen Rechner verlässt:** genau das, was du an einen Modellanbieter sendest, und nur wenn du
  auf Senden klickst. Die vollständige Liste der Netzwerkziele steht in
  [PRIVACY.md § 5](PRIVACY.md#5-what-leaves-your-machine-and-who-receives-it).
- **Aufeinanderfolgende Transkripte sind ohne diese App lesbar.** Die Datenbank ist im Ruhezustand
  nicht verschlüsselt — ein bewusster Kompromiss, dokumentiert statt weggeredet.
- **Dein Modellanbieter sieht deinen Prompt**, nach *seiner* Richtlinie, nicht nach der dieses
  Projekts. Das ist die Abmachung, die ein Client für das Modell eines anderen macht.

Ausführliche Details, gegen GDPR, russisches 152-FZ und CCPA/CPRA geschrieben, stehen in
[PRIVACY.md](PRIVACY.md). Dort steht auch, wie du alles exportierst und wie du alles löschst.

---

## Voraussetzungen

- Windows 10 Version 1809 oder neuer, oder Windows 11
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download) — nur für das
  Installationsprogramm; eine veröffentlichte Version braucht es, für die Quellen braucht es das SDK
- Ein API-Schlüssel von [OpenRouter](https://openrouter.ai) oder
  [NVIDIA](https://integrate.api.nvidia.com)
- Etwa 500 MB Speicher auf der Platte und ein Ordner, den du einem Agenten zum Lesen geben
  bereit bist

Es gibt keinen plattformübergreifenden Build. DPAPI und WPF gibt es nur unter Windows, und das
Zielframework sagt das, statt zur Laufzeit zu scheitern.

---

## Installation

Lade das Installationsprogramm von der
[Releases-Seite](https://github.com/rwarx/kontur-code/releases). Es ist eine
NSIS-Installation pro Benutzer — Administratorrechte sind nicht nötig.

Die erste Veröffentlichung ist eine **Alpha**. Sie erscheint, weil die Form stabil genug ist, um
darauf aufzubauen, nicht weil sie für den unbeaufsichtigten Betrieb bereit ist.

<details>
<summary>Selbst bauen</summary>

```bash
git clone https://github.com/rwarx/kontur-code.git
cd kontur-code

# The sidecar has to be published next to where Electron looks for it
dotnet publish src/AIClient.Server -c Release -r win-x64 --self-contained false -o electron/sidecar

cd electron
npm install
npm run dist      # → electron/release/
```

Der Build der .NET-Lösung allein ergibt den WPF-Host:

```bash
dotnet build AIClient.slnx
dotnet run --project src/AIClient.App
```

</details>

---

## Erster Start

1. **Einstellungen → Anbieter**, einen API-Schlüssel einfügen, **Aktualisieren** drücken. Die
   Modellauswahl bleibt leer, bis ein Anbieter erfolgreich ist — der Katalog wird danach
   zwischengespeichert, also funktioniert es von da an offline.
2. **Einen Ordner öffnen.** Richte ihn im Modus *Code* auf ein Projekt. Er wird in den Graphen
   indiziert, und von da an ist die Welt des Agenten dieser Ordner.
3. **Committe, bevor du ihn arbeiten lässt.** Ein leerer Commit über `git commit` ist in Ordnung.
   Der Agent schreibt direkt in dein Working Tree, ohne dass etwas gestaged und nichts gesichert
   wäre; deine Historie ist das Rückgängig, und zwar das einzige.
4. **Lies [SECURITY.md](SECURITY.md)**, wenn du Befehlsausführung oder Dateizugriff außerhalb des
   Projekts aktivieren willst. Beides ist standardmäßig aus, und beides sind die Funktionen mit
   scharfen Kanten.

---

## Architektur

Fünf Projekte, eine Regel: **Abhängigkeiten zeigen nach innen.** `Domain` und `Application` zielen
auf schlichtes `net10.0`, wodurch der Griff nach WPF oder DPAPI ein Compilerfehler wird und nicht
eine Anmerkung im Review.

```text
AIClient.Domain ◄──── AIClient.Application ◄──── AIClient.Infrastructure
                          ▲                          ▲            ▲
                          └──────── AIClient.App ────┘            │
                          └──────── AIClient.Server ─────────────┘
```

```text
provider bytes ──► AIStreamEvent ──► ChatTurnEvent ──► the UI
   (SSE frames)       (Domain)          (Application)    (WPF or React)
```

Drei Ereignisvokabulare, jedes engere als das vorige, werden an jeder Grenze übersetzt. Ein Anbieter
kann keine Datenbank-ID in den Typ stecken, den er zurückgibt, denn der Typ, den er zurückgibt, ist
nicht der Typ, den die UI konsumiert.

Die lokale API verlangt ein Bearer-Token pro Start und verweigert die Bindung an alles außer
Loopback — auf `127.0.0.1` zu sein ist keine Autorisierungsgrenze, und der Code behandelt sie als
eine, die es nicht ist.

Die vollständige Begründung, einschließlich der beiden Türen zum Dateisystem und der beiden
Canvas-Renderer, steht in [ARCHITECTURE.md](ARCHITECTURE.md).

---

## Entwicklung

```bash
dotnet build AIClient.slnx     # warnings are errors — that is deliberate
dotnet test                    # 896 tests, no network and no API key needed

cd electron
npm install
npm run typecheck
npm run dev                    # renderer against a seeded demo workspace, no backend needed
```

Erfordert Windows und das .NET 10 SDK. Node 22 wird nur für den Renderer gebraucht.

Konventionen, auf die es ankommt und die `.editorconfig` nicht ausdrücken kann, stehen in
[CONTRIBUTING.md](CONTRIBUTING.md).

---

## Dokumentation

| Dokument | Was drinsteht |
| --- | --- |
| [ARCHITECTURE.md](ARCHITECTURE.md) | Warum der Code so gebaut ist. Lies es, bevor du die Struktur änderst. |
| [DEVELOPMENT.md](DEVELOPMENT.md) | Bauen, migrieren, testen, erweitern. Lies es, bevor du irgendetwas änderst. |
| [SECURITY.md](SECURITY.md) | Das Bedrohungsmodell, was geschützt ist, **und die bekannten Lücken**. |
| [PRIVACY.md](PRIVACY.md) | Welche Daten es gibt, wohin sie gehen und welche Rechte du hast. GDPR / 152-FZ / CCPA. |
| [CHANGELOG.md](CHANGELOG.md) | Jede Änderung, mit hervorgehobenen Sicherheitskorrekturen. |
| [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) | Mitgelieferte Komponenten und ihre Lizenzen. |
| [CONTRIBUTING.md](CONTRIBUTING.md) · [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md) · [SUPPORT.md](SUPPORT.md) | Wie du mitmachst. |

---

## Mitwirken

Beiträge sind willkommen, und die Messlatte für Änderungen am Sicherheitsmodell des Agenten ist
absichtlich hoch — weil dieser Code in deine Dateien schreiben und Programme auf deinem Rechner
ausführen kann.

Beginne mit [CONTRIBUTING.md](CONTRIBUTING.md). Die Kurzfassung: eine logische Änderung pro Pull
Request, `dotnet test` grün, und wenn du die Reichweite des Agenten anfasst, sage in der
Beschreibung, hinter welches Tor du sie gestellt hast.

Bitte **eröffne keine öffentliche Issue für eine Sicherheitslücke** — siehe
[SECURITY.md](SECURITY.md) für die private Meldung.

---

## Lizenz

**MIT.** Siehe [LICENSE](LICENSE).

Komponenten von Dritten behalten ihre eigenen Lizenzen — rund 40 mitgelieferte Pakete plus Electron
und Chromium —, katalogisiert in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

---

## Status

`0.1.1-alpha`. Bewusst als Vorabversion veröffentlicht.

**Funktioniert:** Chat im Streaming, beide Hosts, der räumliche Graph und das Canvas, die
Tool-Schleife des Agenten mit ihrem Freigabe-Tor, der Editor, git, Sitzungen und Pakete, drei
Sprachen.

**Behoben seit `0.1.0-alpha`** — zwei Sicherheitslücken in der Tür zu Dateien außerhalb des
Projekts und zwei Wege, deine Arbeit zu verlieren:

- Eine Freigabe für das Lesen einer externen Datei gewährte bisher für den Rest des Laufs Lesezugriff
  auf die ganze Festplatte. Jeder Vorgang außerhalb des Projekts ist jetzt eine eigene Frage.
- Pfade außerhalb des Projekts wurden als Text geprüft, sodass eine Windows-Junction die Regeln für
  Dateinamen mit Anmeldedaten umgehen konnte. Links werden jetzt aufgelöst, bevor irgendetwas geprüft
  wird.
- Der Editor schrieb bei jedem Tastendruck die ganze Datei. Schreibvorgänge sind jetzt entprellt, mit
  einer Anzeige **Nicht gespeichert** und einem automatischen Flush, bevor du die Sitzung wechselst,
  exportierst oder beendest.
- Der Renderer duplizierte den Text jeder Datei im Browserspeicher, gegen eine Grenze von 5–10 MB,
  und hörte dann *stillschweigend* auf zu speichern, wenn der voll war. Diese Duplizierung ist weg.

**Noch offen**, mit Dateiverweisen aufgeführt in
[SECURITY.md](SECURITY.md#known-gaps): Unterhaltungen sind im Ruhezustand nicht verschlüsselt
(absichtlich, und dein Windows-Konto kann sie ohnehin lesen), der Sidecar hat über Kestrels Standard
hinaus weder eine Begrenzung der Anfragegröße noch einen Ratenbegrenzer, die Electron-Haupt- und
-Preload-Skripte werden nicht typgeprüft, und die neuesten Werkzeuge des Agenten haben keine
Testabdeckung.

Das ist eine `0.x`-Version aus einem kleinen Projekt ohne Geld im Hintergrund. Sie wird offen
gebaut, Issues werden nach bestem Wissen beantwortet, und es gibt keine SLA. Wenn du eine
brauchst, ist das ein Gespräch mit einem Anbieter, nicht mit diesem Repository.

---

<p align="center"><sub>MIT-lizenziert. Offen gebaut. Screenshots aus der laufenden Anwendung.</sub></p>
