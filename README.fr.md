# Kontur Code

**À lire en premier :** Kontur Code est un **outil de développement capable d'écrire dans vos
fichiers et d'exécuter des programmes sur votre machine.** C'est le produit, pas un défaut. Tout ce
qui suit sur l'isolement de l'agent — et chaque lacune connue — se trouve dans
[SECURITY.md](SECURITY.md). Lisez-le avant de diriger cet outil vers quoi que ce soit qui vous
tient à cœur.

[English](README.md) · [Русский](README.ru.md) · [Deutsch](README.de.md) · [Español](README.es.md) ·
[Français](README.fr.md) · [Português (Brasil)](README.pt-BR.md) · [Italiano](README.it.md) ·
[中文（简体）](README.zh-CN.md) · [日本語](README.ja.md) · [한국어](README.ko.md) · [Türkçe](README.tr.md)

---

Un client LLM de bureau devenu un **environnement de développement spatial pour l'IA**. Une
fenêtre, vos propres clés d'API, vos conversations dans un fichier SQLite local — et un espace de
travail qui transforme le dossier que vous lui désignez en un graphe qu'on voit vraiment.

Sous la fenêtre, deux hôtes partagent toutes les couches : une application **WPF** et une coque
**Electron + React** au-dessus du même cœur .NET, si bien que l'application n'est limitée ni par le
plafond de l'un ni par celui de l'autre.

<p align="center">
  <img src="docs/screenshots/canvas.png" alt="Le canevas spatial : l'espace de travail sous forme de graphe de dépendances aux arêtes étiquetées" width="100%">
</p>

---

## Sommaire

- [Captures d'écran](#captures-décran)
- [Ce qu'il fait](#ce-quil-fait)
- [L'agent](#lagent)
- [Confidentialité en bref](#confidentialité-en-bref)
- [Prérequis](#prérequis)
- [Installation](#installation)
- [Premier lancement](#premier-lancement)
- [Architecture](#architecture)
- [Développement](#développement)
- [Documentation](#documentation)
- [Contribuer](#contribuer)
- [Licence](#licence)
- [État](#état)

---

## Captures d'écran

### Chat

<p align="center">
  <img src="docs/screenshots/chat.png" alt="Une session de chat avec une réponse de l'assistant et le panneau de contexte de l'espace de travail" width="100%">
</p>

Les tokens apparaissent au fur et à mesure qu'ils arrivent. Arrêtez au milieu de la réponse et **le
texte partiel est conservé**, non jeté — il reste utilisable comme contexte pour le tour suivant.

### Le canevas spatial

<p align="center">
  <img src="docs/screenshots/canvas.png" alt="Des nœuds et des arêtes de dépendance étiquetées sur un canevas infini, avec une mini-carte" width="100%">
</p>

Votre projet sous forme de graphe : fichiers, dossiers, modules, services, interfaces, données,
tests et plans deviennent des nœuds, avec des arêtes d'inclusion et de dépendance entre eux.
Déplacez, zoomez, sélectionnez au rectangle et surveillez dans le coin une mini-carte de tout le
graphe. Les arêtes sont étiquetées — `Login() → CreateTokenAsync` est une arête d'appel ; «
uniquement à la compilation » est une dépendance qui ne s'exécute jamais.

### La structure du graphe

<p align="center">
  <img src="docs/screenshots/graph-outline.png" alt="Une arborescence filtrable du graphe, regroupée par type de nœud" width="100%">
</p>

Le même graphe sous forme de structure que vous pouvez lire et filtrer par nom ou par chemin.

### L'éditeur

<p align="center">
  <img src="docs/screenshots/editor.png" alt="Un fichier C# ouvert dans l'éditeur avec coloration syntaxique et un compteur de modifications" width="100%">
</p>

CodeMirror 6 avec dix grammaires de langage, des modifications IA en ligne sur une sélection et la
complétion par texte fantôme.

### Paramètres

<p align="center">
  <img src="docs/screenshots/settings.png" alt="Paramètres : thème, langue, interface et valeurs par défaut du chat" width="100%">
</p>

Thème, langue de l'interface, échelle de l'application, prompt système, paramètres
d'échantillonnage — tout est local, tout est conservé dans votre propre base de données.

---

## Ce qu'il fait

- **Chat en streaming.** Les tokens arrivent au fur et à mesure qu'ils sont produits. L'arrêt
  conserve la réponse partielle. Regénérer la remplace sur place, éventuellement sur un autre
  modèle.
- **Trois modes de travail.** *Chat* pour discuter, *Cowork* pour analyser et *Code* — là où l'agent
  reçoit un espace de travail et une boucle d'outils. Le mode est une propriété du message, pas de
  l'application : « planifiez ceci, puis construisez-le » tient donc en deux messages plutôt qu'en
  deux passages dans les paramètres.
- **Le graphe spatial.** Votre dossier est indexé automatiquement en nœuds et arêtes. Un indexeur
  fondé sur les diffs ajoute les nouveaux fichiers, retire les fichiers supprimés et **conserve la
  disposition que vous avez organisée**. Les plans produits par l'agent arrivent sur le canevas
  sous forme d'ensembles de nœuds et d'arêtes — annulables, enregistrés, et refusables par vous.
- **Des surfaces de travail unifiées.** Le canevas comme carte, le graphe comme structure, une
  arborescence de fichiers, l'éditeur, un panneau git, la trajectoire d'une exécution et une vue des
  tâches — tout à un `Ctrl+Shift+P` les uns des autres.
- **Git.** Status, diffs staged et unstaged, stage, commit, branch, revert, push, pull, fetch. Tout
  par le biais de `git`, **sans shell** et avec des arguments validés.
- **Comptage des tokens.** Consommation en direct, coût estimé et ce que le modèle retient
  réellement en contexte — avec un bouton **Compacter la session** qui replie les tours anciens en
  un résumé.
- **Rendu Markdown.** Titres, listes, tableaux, citations, listes de tâches et blocs de code, avec
  coloration syntaxique. Rendu comme contenu structuré, **jamais comme du HTML injecté**.
- **Catalogue de modèles.** Récupéré auprès de chaque fournisseur et mis en cache dans SQLite, de
  sorte que le sélecteur fonctionne hors ligne ensuite. Fenêtre de contexte, tarifs et capacités
  viennent du fournisseur, pas d'une liste codée en dur.
- **Deux fournisseurs prêts à l'emploi** — OpenRouter et NVIDIA NIM, tous deux compatibles
  OpenAI. Pointez l'endpoint de NVIDIA vers un Ollama, LM Studio ou conteneur NIM auto-hébergé en
  local et rien ne quitte votre machine.
- **Paquets de session.** Exportez toute la session — chat, canevas, fichiers, objectifs — dans un
  `.zip`.
- **Trois langues.** Anglais, russe et allemand, appliqués en direct à toute l'interface.
- **Clair et sombre**, en suivant le système ou en le fixant.

---

## L'agent

L'agent exécute une boucle d'outils, et sa portée est précisément ce qu'il faut comprendre avant de
l'utiliser.

| | |
| --- | --- |
| **Travaille dans** | Un dossier que vous désignez, et refuse de lire ou d'écrire en dehors |
| **Également refusé, dans ce dossier même** | `.git`, `.env`, `credentials.json`, `*.pem`, `*.key`, `*.pfx` — par nom, toujours |
| **Demande avant** | Chaque écriture, chaque fichier externe, chaque requête réseau, chaque programme |
| **Jamais** | Exécuter un shell. `&&`, `\|`, `>` et `$HOME` sont du texte que reçoit le programme |
| **Programmes** | Désactivés par défaut. Ensuite, une simple liste blanche qu'un humain édite. Ensuite, l'approbation à *chaque* appel |
| **Annulation** | Votre gestion de versions. Les modifications sont affichées avant d'être faites, pas annulées après |

Un refus nomme la règle et indique au modèle quoi faire à la place, afin qu'il cesse de réclamer le
même outil trois fois.

**Tout ce qui se trouve en dehors de ce dossier est facultatif et reste désactivé tant que vous ne
l'activez pas.** La récupération réseau et l'accès aux fichiers hors projet sont deux interrupteurs
distincts dans les paramètres, et chaque appel passe toujours par la demande d'approbation.
**Aucune réponse n'est mémorisée pour eux** — une question, une lecture ou une écriture.

> Le modèle complet d'isolement — et **ce qui reste ouvert**, ce qui est désormais une courte liste —
> se trouve dans [SECURITY.md](SECURITY.md). Ceci est une alpha ; lisez le document avant de lui
> faire confiance.

---

## Confidentialité en bref

- **Aucune télémétrie. Aucune analyse. Aucun rapport d'incident. Aucun compte.** Dans ce dépôt,
  aucun code n'ouvre de connexion vers une adresse appartenant à ce projet.
- **Vos conversations ne touchent jamais un serveur.** Ce sont un fichier SQLite dans votre propre
  profil utilisateur.
- **Les clés d'API sont chiffrées** avec Windows DPAPI, limitées à votre compte Windows et jamais
  écrites dans un journal.
- **Ce qui quitte votre machine :** exactement ce que vous envoyez à un fournisseur de modèles, et
  uniquement lorsque vous appuyez sur Envoyer. La liste complète des destinations réseau se trouve
  dans [PRIVACY.md § 5](PRIVACY.md#5-what-leaves-your-machine-and-who-receives-it).
- **Les transcriptions consécutives sont lisibles sans cette application.** La base de données
  n'est pas chiffrée au repos — un compromis délibéré, documenté plutôt que passé sous silence.
- **Votre fournisseur de modèles voit votre prompt**, selon *sa* politique, pas celle de ce projet.
  C'est le marché que fait un client destiné au modèle de quelqu'un d'autre.

Le détail complet, rédigé au regard du RGPD, de la loi russe 152-FZ et du CCPA/CPRA, se trouve dans
[PRIVACY.md](PRIVACY.md). Le même document explique comment tout exporter et comment tout supprimer.

---

## Prérequis

- Windows 10 version 1809 ou ultérieure, ou Windows 11
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download) — uniquement pour
  l'installateur ; une version publiée en a besoin, les sources ont besoin du SDK
- Une clé d'API auprès d'[OpenRouter](https://openrouter.ai) ou de
  [NVIDIA](https://integrate.api.nvidia.com)
- Environ 500 Mo de disque, et un dossier que vous acceptez de laisser lire à un agent

Il n'existe aucune compilation multiplateforme. DPAPI et WPF sont propres à Windows, et le framework
cible le dit au lieu d'échouer à l'exécution.

---

## Installation

Téléchargez l'installateur depuis la
[page des versions](https://github.com/rwarx/kontur-code/releases). C'est une installation NSIS par
utilisateur — aucun droit administrateur n'est requis.

La première version est une **alpha**. Elle est publiée parce que la forme est assez stabilisée pour
construire dessus, pas parce qu'elle est prête pour un usage sans surveillance.

<details>
<summary>Compilez-la vous-même</summary>

```bash
git clone https://github.com/rwarx/kontur-code.git
cd kontur-code

# The sidecar has to be published next to where Electron looks for it
dotnet publish src/AIClient.Server -c Release -r win-x64 --self-contained false -o electron/sidecar

cd electron
npm install
npm run dist      # → electron/release/
```

Compiler la solution .NET seule vous donne l'hôte WPF :

```bash
dotnet build AIClient.slnx
dotnet run --project src/AIClient.App
```

</details>

---

## Premier lancement

1. **Paramètres → Fournisseurs**, collez une clé d'API, appuyez sur **Actualiser**. Le sélecteur de
   modèles reste vide tant qu'aucun fournisseur n'a répondu — le catalogue est ensuite mis en
   cache, donc il fonctionne hors ligne dès lors.
2. **Ouvrez un dossier.** En mode *Code*, désignez-lui un projet. Il est indexé dans le graphe, et
   dès lors le monde de l'agent est ce dossier.
3. **Faites un commit avant de le laisser travailler.** Un commit vide avec `git commit` vous
   convient. L'agent écrit directement dans votre working tree, sans rien indexer et sans aucune
   sauvegarde ; votre historique est l'annulation, et la seule.
4. **Lisez [SECURITY.md](SECURITY.md)** si vous comptez activer l'exécution de commandes ou l'accès
   aux fichiers hors projet. Les deux sont désactivés par défaut, et ce sont les deux
   fonctionnalités aux arêtes coupantes.

---

## Architecture

Cinq projets, une règle : **les dépendances pointent vers l'intérieur.** `Domain` et `Application`
visent un simple `net10.0`, ce qui fait de toute tentative d'atteindre WPF ou DPAPI une erreur de
compilation plutôt qu'une remarque de relecture.

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

Trois vocabulaires d'événements, chacun plus étroit que le précédent, traduits à chaque frontière.
Un fournisseur ne peut pas placer un identifiant de base de données dans le type qu'il renvoie,
parce que le type qu'il renvoie n'est pas celui que consomme l'interface.

L'API locale exige un jeton bearer à chaque lancement et refuse de se lier à quoi que ce soit
d'autre que loopback : être sur `127.0.0.1` n'est pas une frontière d'autorisation, et le code la
traite comme une frontière qui n'en est pas une.

Le raisonnement complet, y compris les deux portes vers le système de fichiers et les deux moteurs
de rendu du canevas, se trouve dans [ARCHITECTURE.md](ARCHITECTURE.md).

---

## Développement

```bash
dotnet build AIClient.slnx     # warnings are errors — that is deliberate
dotnet test                    # 896 tests, no network and no API key needed

cd electron
npm install
npm run typecheck
npm run dev                    # renderer against a seeded demo workspace, no backend needed
```

Nécessite Windows et le SDK .NET 10. Node 22 n'est requis que pour le moteur de rendu.

Les conventions qui comptent, et que `.editorconfig` ne peut pas exprimer, se trouvent dans
[CONTRIBUTING.md](CONTRIBUTING.md).

---

## Documentation

| Document | Ce qu'il contient |
| --- | --- |
| [ARCHITECTURE.md](ARCHITECTURE.md) | Pourquoi le code est façonné ainsi. À lire avant de changer la structure. |
| [DEVELOPMENT.md](DEVELOPMENT.md) | Compiler, migrer, tester, étendre. À lire avant de changer quoi que ce soit. |
| [SECURITY.md](SECURITY.md) | Le modèle de menace, ce qui est protégé, **et les lacunes connues**. |
| [PRIVACY.md](PRIVACY.md) | Quelles données existent, où elles vont et quels sont vos droits. RGPD / 152-FZ / CCPA. |
| [CHANGELOG.md](CHANGELOG.md) | Chaque changement, avec les correctifs de sécurité mis en évidence. |
| [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) | Les composants inclus et leurs licences. |
| [CONTRIBUTING.md](CONTRIBUTING.md) · [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md) · [SUPPORT.md](SUPPORT.md) | Comment participer. |

---

## Contribuer

Les contributions sont bienvenues, et la barre de revue pour les modifications du modèle de
sécurité de l'agent est haute à dessein — parce que ce code peut écrire des fichiers et exécuter
des programmes sur votre machine.

Commencez par [CONTRIBUTING.md](CONTRIBUTING.md). En bref : un changement logique par pull request,
`dotnet test` au vert, et si vous touchez à la portée de l'agent, indiquez dans la description
quelle porte vous l'avez placé derrière.

Veuillez **ne pas ouvrir d'issue publique pour une faille de sécurité** — voir
[SECURITY.md](SECURITY.md) pour le signalement privé.

---

## Licence

**MIT.** Voir [LICENSE](LICENSE).

Les composants tiers conservent leurs propres licences — près de 40 paquets embarqués, plus Electron
et Chromium — catalogués dans [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

---

## État

`0.1.1-alpha`. Publiée comme préversion, délibérément.

**Fonctionne :** le chat en streaming, les deux hôtes, le graphe spatial et le canevas, la boucle
d'outils de l'agent avec sa porte d'approbation, l'éditeur, git, les sessions et les paquets, trois
langues.

**Corrigé depuis `0.1.0-alpha`** — deux failles de sécurité dans la porte des fichiers hors projet, et
deux façons de perdre votre travail :

- Une approbation sur une lecture de fichier externe accordait l'accès en lecture à tout le disque
  pour le reste de l'exécution. Chaque opération hors projet est désormais une question distincte.
- Les chemins hors projet étaient vérifiés comme du texte, si bien qu'une jonction Windows pouvait
  contourner les règles de dénomination des fichiers d'identifiants. Les liens sont désormais résolus
  avant toute vérification.
- L'éditeur écrivait le fichier entier à chaque frappe. Les écritures sont désormais différées, avec
  un indicateur **non enregistré** et une écriture automatique avant que vous ne changiez de
  session, n'exportiez ou ne quittiez.
- Le moteur de rendu dupliquait le texte de chaque fichier dans le stockage du navigateur, face à un
  plafond de 5–10 Mo, et cessait d'enregistrer *silencieusement* une fois ce plafond atteint. Ce
  doublon a disparu.

**Toujours ouvert**, avec références aux fichiers dans
[SECURITY.md](SECURITY.md#known-gaps) : les conversations ne sont pas chiffrées au repos
(délibérément, et votre compte Windows peut les lire de toute façon), le sidecar n'a ni limite de
taille de requête ni limiteur de débit au-delà du réglage par défaut de Kestrel, les scripts
principal et preload d'Electron ne bénéficient d'aucune vérification de types, et les outils d'agent
les plus récents ne sont couverts par aucun test.

C'est une version `0.x` issue d'un petit projet sans financement derrière elle. Elle est construite
à vue ouverte, les tickets reçoivent une réponse au mieux, et il n'y a pas de SLA. Si vous en avez
besoin, c'est une conversation avec un fournisseur, pas avec ce dépôt.

---

<p align="center"><sub>Sous licence MIT. Construit à vue ouverte. Captures prises depuis l'application en cours d'exécution.</sub></p>
