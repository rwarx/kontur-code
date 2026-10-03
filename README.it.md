# Kontur Code

**Leggi prima questo:** Kontur Code è uno **strumento di sviluppo che può scrivere sui tuoi file ed
eseguire programmi sulla tua macchina.** Questo è il prodotto, non un difetto. Tutto quanto segue sulla
contenzione dell'agente — e ogni lacuna nota — è in
[SECURITY.md](SECURITY.md). Leggilo prima di puntarlo verso qualcosa a cui tieni.

[English](README.md) · [Русский](README.ru.md) · [Deutsch](README.de.md) · [Español](README.es.md) ·
[Français](README.fr.md) · [Português (Brasil)](README.pt-BR.md) · [Italiano](README.it.md) ·
[中文（简体）](README.zh-CN.md) · [日本語](README.ja.md) · [한국어](README.ko.md) · [Türkçe](README.tr.md)

---

Un client LLM desktop cresciuto fino a diventare un **ambiente di sviluppo IA spaziale**. Una
finestra, le tue chiavi API, le tue conversazioni in un file SQLite locale — e un'area di lavoro che
trasforma la cartella che gli punti in un grafo che si vede davvero.

Due host condividono ogni livello sotto la finestra: un'applicazione WPF e una shell Electron + React
sullo stesso core .NET, così l'app non è limitata dal tetto di nessuno dei due toolkit.

<p align="center">
  <img src="docs/screenshots/canvas.png" alt="La tela spaziale: l'area di lavoro come grafo di dipendenze con archi etichettati" width="100%">
</p>

---

## Indice

- [Screenshot](#screenshot)
- [Cosa fa](#cosa-fa)
- [L'agente](#lagente)
- [Privacy, in breve](#privacy-in-breve)
- [Requisiti](#requisiti)
- [Installazione](#installazione)
- [Primo avvio](#primo-avvio)
- [Architettura](#architettura)
- [Sviluppo](#sviluppo)
- [Documentazione](#documentazione)
- [Contributi](#contributi)
- [Licenza](#licenza)
- [Stato](#stato)

---

## Screenshot

### Chat

<p align="center">
  <img src="docs/screenshots/chat.png" alt="Una sessione di chat con una risposta dell'assistente e il pannello di contesto dell'area di lavoro" width="100%">
</p>

I token compaiono man mano che arrivano. Interrompiti a metà risposta e **il testo parziale viene
conservato**, non scartato — resta utilizzabile come contesto per il turno successivo.

### La tela spaziale

<p align="center">
  <img src="docs/screenshots/canvas.png" alt="Nodi e archi di dipendenza etichettati su una tela infinita, con una minimappa" width="100%">
</p>

Il tuo progetto come grafo: file, cartelle, moduli, servizi, interfacce, dati, test e piani come nodi,
con archi di contenimento e di dipendenza tra loro. Trasforma, ingrandisci, trascina un rettangolo per
selezionare e guarda una minimappa dell'intero grafo nell'angolo. Gli archi sono etichettati — `Login() →
CreateTokenAsync` è un arco di chiamata; "solo in fase di compilazione" è una dipendenza che non viene
mai eseguita.

### Lo schema del grafo

<p align="center">
  <img src="docs/screenshots/graph-outline.png" alt="Un albero di schema del grafo, filtrabile e raggruppato per tipo di nodo" width="100%">
</p>

Lo stesso grafo come una struttura che puoi leggere e filtrare per nome o percorso.

### L'editor

<p align="center">
  <img src="docs/screenshots/editor.png" alt="Un file C# aperto nell'editor con evidenziazione della sintassi e un contatore delle modifiche" width="100%">
</p>

CodeMirror 6 con dieci grammatiche di linguaggio, modifiche IA in linea su una selezione e
completamento con testo fantasma.

### Impostazioni

<p align="center">
  <img src="docs/screenshots/settings.png" alt="Impostazioni: tema, lingua, interfaccia e valori predefiniti della chat" width="100%">
</p>

Tema, lingua dell'interfaccia, scala dell'app, prompt di sistema, parametri di campionamento — tutto
locale, tutto salvato nel tuo database.

---

## Cosa fa

- **Chat in streaming.** I token arrivano man mano che vengono prodotti. Ferma conserva la risposta
  parziale. Rigenera la sostituisce sul posto, opzionalmente su un altro modello.
- **Tre modalità di lavoro.** *Chat* per la conversazione, *Cowork* per l'analisi e *Code* — dove
  all'agente arrivano un'area di lavoro e un ciclo di strumenti. La modalità è una proprietà del
  messaggio, non dell'app, quindi "pianifica questo, poi costruiscilo" sono due messaggi invece di due
  passaggi alle Impostazioni.
- **Il grafo spaziale.** La tua cartella viene indicizzata automaticamente in nodi e archi. Un
  indicizzatore basato sui diff aggiunge i file nuovi, toglie quelli eliminati e **preserva la
  disposizione che hai sistemato**. I piani prodotti dall'agente arrivano sulla tela come insiemi di
  nodi e archi — annullabili, salvati e tuoi da rifiutare.
- **Superfici unificate dell'area di lavoro.** La tela come mappa, il grafo come struttura, un albero
  dei file, l'editor, un pannello git, la traiettoria di un'esecuzione e una vista delle attività —
  tutto a un `Ctrl+Shift+P` di distanza.
- **Git.** Stato, diff staged e unstaged, stage, commit, branch, revert, push, pull, fetch. Tutto
  tramite `git`, **senza shell** e con argomenti validati.
- **Contabilità dei token.** Utilizzo in tempo reale, costo stimato e cosa sta davvero tenendo il
  modello in contesto — con un pulsante **Compatta sessione** che riassume i turni più vecchi in un
  riepilogo.
- **Rendering del Markdown.** Titoli, elenchi, tabelle, citazioni, elenchi di attività e blocchi di
  codice delimitati, con evidenziazione della sintassi. Reso come contenuto strutturato, **mai come
  HTML iniettato**.
- **Catalogo dei modelli.** Recuperato da ogni provider e messo in cache in SQLite, così il selettore
  funziona offline in seguito. Finestra di contesto, prezzi e capacità arrivano dal provider, non da un
  elenco scritto a mano.
- **Due provider pronti** — OpenRouter e NVIDIA NIM, entrambi compatibili con OpenAI. Punta
  l'endpoint di NVIDIA verso una Ollama locale, LM Studio o un container NIM self-hosted e nulla
  esce dalla tua macchina.
- **Bundle di sessione.** Esporta l'intera sessione — chat, tela, file, obiettivi — come `.zip`.
- **Tre lingue.** Inglese, russo e tedesco, applicate dal vivo a tutta l'interfaccia.
- **Chiaro e scuro**, seguendo il sistema o fissato.

---

## L'agente

L'agente esegue un ciclo di strumenti, e la sua portata è la cosa da capire prima di usarlo.

| | |
| --- | --- |
| **Opera in** | Una cartella che designi, e si rifiuta di leggere o scrivere fuori da essa |
| **Rifiutati anche, dentro quella cartella** | `.git`, `.env`, `credentials.json`, `*.pem`, `*.key`, `*.pfx` — per nome, sempre |
| **Chiede prima di** | Ogni scrittura, ogni file esterno, ogni richiesta di rete, ogni programma |
| **Mai** | Eseguire una shell. `&&`, `\|`, `>` e `$HOME` sono testo che il programma riceve |
| **Programmi** | Spenti per impostazione predefinita. Poi una allowlist che solo una persona modifica. Poi approvazione a *ogni* chiamata |
| **Annulla** | Il tuo controllo di versione. Le modifiche vengono mostrate prima di essere fatte, non annullate dopo |

Un rifiuto nomina la regola e dice al modello cosa fare invece, così non insiste per tre volte con lo
stesso strumento.

**Tutto ciò che sta fuori da quella cartella è opt-in, e resta spento finché non lo accendi.** Il recupero
dalla rete e l'accesso a file fuori dal progetto sono interruttori separati nelle Impostazioni, e ogni
chiamata passa comunque per la richiesta di approvazione.

> Il modello di contenimento completo — e **otto lacune note**, tra cui una che permette a un junction
> di Windows di aggirare le regole sui nomi dei file di credenziale per i file fuori dal progetto — è in
> [SECURITY.md](SECURITY.md). Questa è un'alpha; leggilo prima di fidarti.

---

## Privacy, in breve

- **Nessuna telemetria. Nessuna analisi. Nessun crash reporting. Nessun account.** In questo repository
  non c'è codice che apra una connessione verso un indirizzo di proprietà di questo progetto.
- **Le tue conversazioni non toccano mai un server.** Sono un file SQLite nel tuo profilo utente.
- **Le chiavi API sono cifrate** con Windows DPAPI, limitate al tuo account Windows e mai scritte in un
  log.
- **Cosa esce dalla tua macchina:** esattamente quello che invii a un provider di modelli, e solo quando
  premi Invia. L'elenco completo delle destinazioni di rete è in
  [PRIVACY.md § 5](PRIVACY.md#5-what-leaves-your-machine-and-who-receives-it).
- **Le trascrizioni consecutive sono leggibili senza questa app.** Il database non è cifrato a riposo —
  un compromesso deliberato, documentato e non messo sotto il tappeto.
- **Il tuo provider di modelli vede il tuo prompt**, secondo la sua policy, non quella di questo
  progetto. È il patto che stipula un client per il modello di qualcun altro.

Tutti i dettagli, scritti secondo il GDPR, la legge russa 152-FZ e il CCPA/CPRA, sono in
[PRIVACY.md](PRIVACY.md). Spiega anche come esportare e come cancellare tutto.

---

## Requisiti

- Windows 10 versione 1809 o successiva, oppure Windows 11
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download) — solo per l'installer; una build
  pubblicata lo richiede, i sorgenti richiedono l'SDK
- Una chiave API da [OpenRouter](https://openrouter.ai) o da [NVIDIA](https://integrate.api.nvidia.com)
- Circa 500 MB di disco e una cartella che sei disposto a far leggere a un agente

Non esiste una build multipiattaforma. DPAPI e WPF sono solo Windows, e il framework di destinazione
lo dichiara invece di fallire a runtime.

---

## Installazione

Scarica l'installer dalla [pagina delle release](https://github.com/rwarx/kontur-code/releases). È
un'installazione NSIS per utente — nessun diritto di amministratore richiesto.

La prima release è un'**alpha**. È pubblicata perché la forma è abbastanza assodata da costruirci
contro, non perché sia pronta per un uso non presidiato.

<details>
<summary>Compilalo da te</summary>

```bash
git clone https://github.com/rwarx/kontur-code.git
cd kontur-code

# Il sidecar deve essere pubblicato dove Electron va a cercarlo
dotnet publish src/AIClient.Server -c Release -r win-x64 --self-contained false -o electron/sidecar

cd electron
npm install
npm run dist      # → electron/release/
```

Costruire da sola la soluzione .NET ti dà l'host WPF:

```bash
dotnet build AIClient.slnx
dotnet run --project src/AIClient.App
```

</details>

---

## Primo avvio

1. **Impostazioni → Provider**, incolla una chiave API e premi **Aggiorna**. Il selettore dei modelli
   resta vuoto finché un provider non riesce — il catalogo viene messo in cache in seguito, quindi da
   lì in poi funziona offline.
2. **Apri una cartella.** In modalità *Code*, puntala a un progetto. Viene indicizzata nel grafo e da
   quel momento il mondo dell'agente è quella cartella.
3. **Committa prima di lasciarlo lavorare.** Un `git commit` vuoto, se vuoi. L'agente scrive
   direttamente nella tua working tree senza nulla in staging e senza alcun backup; la tua cronologia
   è l'annullamento, ed è l'unico.
4. **Leggi [SECURITY.md](SECURITY.md)** se intendi abilitare l'esecuzione di comandi o l'accesso a
   file fuori dal progetto. Entrambe sono disattivate per impostazione predefinita, ed entrambe sono
   le funzioni con i bordi più affilati.

---

## Architettura

Cinque progetti, una regola: **le dipendenze puntano verso l'interno.** `Domain` e `Application`
mirano a `net10.0` puro, il che rende il ricorso a WPF o a DPAPI un errore di compilazione invece di
un commento in review.

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

Tre vocabolari di eventi, ognuno più stretto del precedente, tradotto a ogni confine. Un provider non
può mettere un id del database nel tipo che restituisce, perché il tipo che restituisce non è il tipo
che la UI consuma.

L'API locale richiede un bearer token a ogni avvio e si rifiuta di fare bind su qualsiasi cosa diversa
dal loopback — stare su `127.0.0.1` non è un confine di autorizzazione, e il codice lo tratta come se
non lo fosse.

Il ragionamento completo, incluse le due porte verso il file system e i due renderer della tela, è in
[ARCHITECTURE.md](ARCHITECTURE.md).

---

## Sviluppo

```bash
dotnet build AIClient.slnx     # gli avvisi sono errori — è una scelta deliberata
dotnet test                    # 896 test, senza rete e senza chiave API

cd electron
npm install
npm run typecheck
npm run dev                    # il renderer contro un'area di lavoro demo già popolata, senza backend
```

Richiede Windows e l'SDK .NET 10. Node 22 serve solo per il renderer.

Le convenzioni che contano, e che `.editorconfig` non può esprimere, sono in
[CONTRIBUTING.md](CONTRIBUTING.md).

---

## Documentazione

| Documento | Cosa contiene |
| --- | --- |
| [ARCHITECTURE.md](ARCHITECTURE.md) | Perché il codice è fatto in questo modo. Leggilo prima di cambiare la struttura. |
| [DEVELOPMENT.md](DEVELOPMENT.md) | Compilare, migrare, testare, estendere. Leggilo prima di cambiare qualsiasi cosa. |
| [SECURITY.md](SECURITY.md) | Il modello di minaccia, cosa è protetto, **e le lacune note**. |
| [PRIVACY.md](PRIVACY.md) | Quali dati esistono, dove vanno e i tuoi diritti. GDPR / 152-ФЗ / CCPA. |
| [CHANGELOG.md](CHANGELOG.md) | Ogni modifica, con le correzioni di sicurezza evidenziate. |
| [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) | I componenti inclusi e le loro licenze. |
| [CONTRIBUTING.md](CONTRIBUTING.md) · [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md) · [SUPPORT.md](SUPPORT.md) | Come partecipare. |

---

## Contributi

I contributi sono benvenuti, e l'asticella di revisione per i cambiamenti al modello di sicurezza
dell'agente è alta di proposito — perché quel codice può scrivere file ed eseguire programmi sulla tua
macchina.

Comincia da [CONTRIBUTING.md](CONTRIBUTING.md). In breve: una modifica logica per pull request,
`dotnet test` verde e, se tocchi la portata dell'agente, dì nella descrizione quale gate hai messo
dietro.

Per favore, **non aprire una issue pubblica per una vulnerabilità di sicurezza** — per la segnalazione
privata vedi [SECURITY.md](SECURITY.md).

---

## Licenza

**MIT.** Vedi [LICENSE](LICENSE).

I componenti di terze parti mantengono le proprie licenze — circa 40 pacchetti inclusi, più Electron e
Chromium — catalogati in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

---

## Stato

`0.1.0-alpha`. Pubblicata come prerelease, deliberatamente.

**Funziona:** chat in streaming, entrambi gli host, il grafo e la tela spaziali, il ciclo di strumenti
dell'agente con il suo gate di approvazione, l'editor, git, sessioni e bundle, tre lingue.

**Sappiamo che non funziona bene** — tutto elencato in [CHANGELOG](CHANGELOG.md#known-limitations),
tutto elencato con riferimenti ai file in [SECURITY.md](SECURITY.md#known-gaps):

1. Un junction di Windows può aggirare le regole sui nomi dei file di credenziale per l'accesso a file
   fuori dal progetto.
2. Due approvazioni che arrivano insieme possono bloccare un'esecuzione invece di farla fallire.
3. Lo stream di eventi non ha heartbeat né riconnessione — una connessione persa perde l'esecuzione.
4. L'editor scrive su disco a ogni tasto premuto, senza debounce.
5. Il renderer persiste il contenuto dei file dell'area di lavoro in `localStorage`; i progetti grandi
   possono superare la quota del browser.
6. Le superfici più recenti — gli strumenti per i file esterni, il fetcher, il server, le operazioni
   git — non hanno copertura nei test. Le correzioni fatte per questa release *sono* coperte.

Questa è una versione `0.x` di un progetto piccolo e senza alcun finanziamento dietro. È costruito in
pubblico, le issue vengono risposte secondo le migliori possibilità e non esiste una SLA. Se ti serve
una, quella è una conversazione con un vendor, non con questo repository.

---

<p align="center"><sub>Licenza MIT. Costruito in pubblico. Screenshot presi dall'applicazione in esecuzione.</sub></p>
