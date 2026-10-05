# Kontur Code

**Leia isto primeiro:** o Kontur Code é uma **ferramenta de desenvolvimento que pode escrever nos seus
arquivos e executar programas na sua máquina.** Isso é o produto, não um defeito dele. Tudo o que
aparece abaixo sobre a contenção do agente — e todas as falhas conhecidas — está em
[SECURITY.md](SECURITY.md). Leia antes de apontar isto para qualquer coisa que importe para você.

[English](README.md) · [Русский](README.ru.md) · [Deutsch](README.de.md) · [Español](README.es.md) ·
[Français](README.fr.md) · [Português (Brasil)](README.pt-BR.md) · [Italiano](README.it.md) ·
[中文（简体）](README.zh-CN.md) · [日本語](README.ja.md) · [한국어](README.ko.md) · [Türkçe](README.tr.md)

---

Um cliente LLM de desktop que virou um **ambiente de desenvolvimento de IA espacial**. Uma janela, as
suas próprias chaves de API, suas conversas em um arquivo SQLite local — e um espaço de trabalho que
transforma a pasta que você indicar em um grafo que dá para ver de verdade.

Dois hosts compartilham todas as camadas abaixo da janela: um **aplicativo WPF** e um shell
**Electron + React** sobre o mesmo núcleo .NET, de modo que o app não fica preso ao teto de nenhuma
das duas ferramentas.

<p align="center">
  <img src="docs/screenshots/canvas.png" alt="O canvas espacial: o espaço de trabalho como um grafo de dependências com arestas rotuladas" width="100%">
</p>

---

## Conteúdo

- [Capturas de tela](#capturas-de-tela)
- [O que ele faz](#o-que-ele-faz)
- [O agente](#o-agente)
- [Privacidade, em resumo](#privacidade-em-resumo)
- [Requisitos](#requisitos)
- [Instalação](#instalação)
- [Primeira execução](#primeira-execução)
- [Arquitetura](#arquitetura)
- [Desenvolvimento](#desenvolvimento)
- [Documentação](#documentação)
- [Contribuição](#contribuição)
- [Licença](#licença)
- [Status](#status)

---

## Capturas de tela

### Conversa

<p align="center">
  <img src="docs/screenshots/chat.png" alt="Uma sessão de conversa com uma resposta do assistente e o painel de contexto do espaço de trabalho" width="100%">
</p>

Os tokens aparecem conforme chegam. Interrompa no meio da resposta e **o texto parcial é mantido**,
não descartado — ele continua utilizável como contexto para o próximo turno.

### O canvas espacial

<p align="center">
  <img src="docs/screenshots/canvas.png" alt="Nós e arestas de dependência rotuladas em um canvas infinito, com um minimapa" width="100%">
</p>

O seu projeto como um grafo: arquivos, pastas, módulos, serviços, interfaces, dados, testes e planos
como nós, com arestas de contenção e de dependência entre eles. Desloque, amplie, faça uma seleção por
laço e acompanhe um minimapa do grafo inteiro no canto. As arestas têm rótulo — `Login() →
CreateTokenAsync` é uma aresta de chamada; "somente em tempo de compilação" é uma dependência que
nunca é executada.

### A estrutura do grafo

<p align="center">
  <img src="docs/screenshots/graph-outline.png" alt="Uma árvore de contorno do grafo, filtrável e agrupada por tipo de nó" width="100%">
</p>

O mesmo grafo como uma estrutura que dá para ler e filtrar por nome ou caminho.

### O editor

<p align="center">
  <img src="docs/screenshots/editor.png" alt="Um arquivo C# aberto no editor com realce de sintaxe e um contador de alterações" width="100%">
</p>

CodeMirror 6 com dez gramáticas de linguagem, edições de IA embutidas sobre uma seleção e
completamento com texto fantasma.

### Configurações

<p align="center">
  <img src="docs/screenshots/settings.png" alt="Configurações: tema, idioma, interface e padrões de conversa" width="100%">
</p>

Tema, idioma da interface, escala do aplicativo, prompt de sistema, parâmetros de amostragem — tudo
local, tudo persistido no seu próprio banco de dados.

---

## O que ele faz

- **Chat em streaming.** Os tokens chegam conforme são produzidos. Parar mantém a resposta parcial.
  Regenerar a substitui no lugar, opcionalmente em outro modelo.
- **Três modos de trabalho.** *Conversa* para conversar, *Cowork* para análise e *Código* — onde o
  agente ganha um espaço de trabalho e um ciclo de ferramentas. O modo é uma propriedade da mensagem,
  não do aplicativo, então "planeje isto e depois construa" são duas mensagens em vez de duas viagens
  às Configurações.
- **O grafo espacial.** Sua pasta é indexada em nós e arestas automaticamente. Um indexador por diff
  adiciona arquivos novos, remove os excluídos e **preserva o layout que você organizou**. Os planos
  que o agente produz chegam ao canvas como conjuntos de nós e arestas — desfazíveis, persistidos e
  seus, para você rejeitar.
- **Superfícies unificadas do espaço de trabalho.** O canvas como mapa, o grafo como estrutura, uma
  árvore de arquivos, o editor, um painel de git, a trajetória de uma execução e uma visualização de
  tarefas — todos a um `Ctrl+Shift+P` de distância.
- **Git.** Status, diffs staged e unstaged, stage, commit, branch, revert, push, pull, fetch. Tudo
  por `git`, **sem shell** e com argumentos validados.
- **Contabilidade de tokens.** Uso ao vivo, custo estimado e o que o modelo está realmente segurando
  no contexto — com um botão **Compactar sessão** que dobra os turnos mais antigos em um resumo.
- **Renderização de Markdown.** Títulos, listas, tabelas, citações, listas de tarefas e blocos de
  código cercados, com realce de sintaxe. Renderizado como conteúdo estruturado, **nunca como HTML
  injetado**.
- **Catálogo de modelos.** Buscado em cada provedor e mantido em cache no SQLite, então o seletor
  funciona offline depois. Janela de contexto, preços e capacidades vêm do provedor, não de uma lista
  fixa.
- **Dois provedores prontos** — OpenRouter e NVIDIA NIM, ambos compatíveis com OpenAI. Aponte o
  endpoint da NVIDIA para um Ollama local, LM Studio ou contêiner NIM auto-hospedado e nada sai da sua
  máquina.
- **Pacotes de sessão.** Exporte a sessão inteira — conversa, canvas, arquivos, objetivos — como um
  `.zip`.
- **Três idiomas.** Inglês, russo e alemão, aplicados ao vivo em toda a interface.
- **Claro e escuro**, seguindo o sistema ou fixado.

---

## O agente

O agente executa um ciclo de ferramentas, e o seu alcance é justamente o que você precisa entender
antes de usá-lo.

| | |
| --- | --- |
| **Trabalha em** | Uma pasta que você indicar, e recusa ler ou escrever fora dela |
| **Também recusado, dentro dessa pasta** | `.git`, `.env`, `credentials.json`, `*.pem`, `*.key`, `*.pfx` — por nome, sempre |
| **Pergunta antes de** | Toda escrita, todo arquivo externo, toda requisição de rede, todo programa |
| **Nunca** | Executar um shell. `&&`, `\|`, `>` e `$HOME` são texto que o programa recebe |
| **Programas** | Desligados por padrão. Depois, uma lista de permissões que só uma pessoa edita. Depois, aprovação em *cada* chamada |
| **Desfazer** | Seu controle de versão. As mudanças são mostradas antes de serem feitas, não revertidas depois |

Uma recusa nomeia a regra e diz ao modelo o que fazer em vez disso, para que ele pare de recorrer à
mesma ferramenta três vezes.

**Tudo fora dessa pasta é opt-in e fica desligado até você ligá-lo.** O acesso à rede e o acesso a
arquivos fora do projeto são interruptores separados nas Configurações, e cada chamada ainda passa pelo
pedido de aprovação. **Nenhuma resposta é lembrada para eles** — uma pergunta, uma leitura ou uma
escrita.

> O modelo completo de contenção — e **o que continua aberto**, que agora é uma lista curta — está
> em [SECURITY.md](SECURITY.md). Isto é uma alpha; leia antes de confiar nele.

---

## Privacidade, em resumo

- **Sem telemetria. Sem análise. Sem relatórios de falha. Sem contas.** Não há neste repositório
  nenhum código que abra conexão com qualquer endereço pertencente a este projeto.
- **Suas conversas nunca tocam um servidor.** Elas são um arquivo SQLite dentro do seu próprio perfil
  de usuário.
- **As chaves de API são criptografadas** com Windows DPAPI, limitadas à sua conta do Windows e nunca
  gravadas em log.
- **O que sai da sua máquina:** exatamente o que você envia a um provedor de modelos, e somente quando
  você pressiona Enviar. A lista completa de destinos de rede está em
  [PRIVACY.md § 5](PRIVACY.md#5-what-leaves-your-machine-and-who-receives-it).
- **Transcrições consecutivas são legíveis sem este aplicativo.** O banco de dados não é criptografado
  em repouso — uma escolha deliberada, documentada em vez de suavizada.
- **Seu provedor de modelos vê o seu prompt**, sob a política *dele*, não a deste projeto. Esse é o
  acordo que um cliente para o modelo de outra pessoa faz.

Os detalhes completos, escritos em conformidade com o GDPR, a lei russa 152-FZ e a CCPA/CPRA, estão em
[PRIVACY.md](PRIVACY.md). O documento também explica como exportar e como apagar tudo.

---

## Requisitos

- Windows 10 versão 1809 ou posterior, ou Windows 11
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download) — apenas para o instalador; uma
  build publicada precisa dele, os fontes precisam do SDK
- Uma chave de API do [OpenRouter](https://openrouter.ai) ou da [NVIDIA](https://integrate.api.nvidia.com)
- Cerca de 500 MB de disco e uma pasta que você esteja disposto a deixar um agente ler

Não existe build multiplataforma. DPAPI e WPF são exclusivos do Windows, e o framework de destino diz
isso em vez de falhar em tempo de execução.

---

## Instalação

Baixe o instalador na [página de releases](https://github.com/rwarx/kontur-code/releases). É uma
instalação NSIS por usuário — sem necessidade de direitos de administrador.

O primeiro release é uma **alpha**. Ele é publicado porque o formato já está assente o bastante para
se construir em cima dele, não porque esteja pronto para uso não supervisionado.

<details>
<summary>Compilar você mesmo</summary>

```bash
git clone https://github.com/rwarx/kontur-code.git
cd kontur-code

# O sidecar precisa ser publicado ao lado de onde o Electron procura
dotnet publish src/AIClient.Server -c Release -r win-x64 --self-contained false -o electron/sidecar

cd electron
npm install
npm run dist      # → electron/release/
```

Compilar a solução .NET sozinha entrega o host WPF:

```bash
dotnet build AIClient.slnx
dotnet run --project src/AIClient.App
```

</details>

---

## Primeira execução

1. **Configurações → Provedores**, cole uma chave de API e pressione **Atualizar**. O seletor de
   modelos fica vazio até que um provedor tenha sucesso — o catálogo é mantido em cache depois, então
   funciona offline a partir daí.
2. **Abra uma pasta.** No modo *Código*, aponte-a para um projeto. Ela é indexada no grafo e, a
   partir daí, o mundo do agente é essa pasta.
3. **Faça commit antes de deixá-lo trabalhar.** Um `git commit` vazio, se preferir. O agente escreve
   direto na sua árvore de trabalho sem nada staged e sem nenhum backup; o seu histórico é o desfazer,
   e é o único.
4. **Leia [SECURITY.md](SECURITY.md)** se você pretende habilitar a execução de comandos ou o acesso
   a arquivos fora do projeto. Ambos vêm desligados por padrão, e ambos são os recursos com arestas
   mais afiadas.

---

## Arquitetura

Cinco projetos, uma regra: **as dependências apontam para dentro.** `Domain` e `Application` têm como
alvo `net10.0` puro, o que faz buscar WPF ou DPAPI ser um erro de compilação em vez de um comentário
de revisão.

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

Três vocabulários de eventos, cada um mais estreito que o anterior, traduzidos em cada fronteira. Um
provedor não consegue colocar um id de banco no tipo que retorna, porque o tipo que retorna não é o
tipo que a interface consome.

A API local exige um token bearer por inicialização e se recusa a se vincular a qualquer coisa além do
loopback — estar em `127.0.0.1` não é uma fronteira de autorização, e o código trata isso como uma.

O raciocínio completo, incluindo as duas portas de entrada para o sistema de arquivos e os dois
renderizadores de canvas, está em [ARCHITECTURE.md](ARCHITECTURE.md).

---

## Desenvolvimento

```bash
dotnet build AIClient.slnx     # avisos contam como erros — isso é deliberado
dotnet test                    # 896 testes, sem rede e sem chave de API

cd electron
npm install
npm run typecheck
npm run dev                    # renderer contra um espaço de trabalho demo pré-populado, sem backend
```

Requer Windows e o SDK do .NET 10. O Node 22 é necessário apenas para o renderer.

As convenções que importam, e que o `.editorconfig` não consegue expressar, estão em
[CONTRIBUTING.md](CONTRIBUTING.md).

---

## Documentação

| Documento | O que há nele |
| --- | --- |
| [ARCHITECTURE.md](ARCHITECTURE.md) | Por que o código tem esta forma. Leia antes de mudar a estrutura. |
| [DEVELOPMENT.md](DEVELOPMENT.md) | Compilar, migrar, testar, estender. Leia antes de mudar qualquer coisa. |
| [SECURITY.md](SECURITY.md) | O modelo de ameaça, o que é protegido, **e as falhas conhecidas**. |
| [PRIVACY.md](PRIVACY.md) | Que dados existem, para onde vão e os seus direitos. GDPR / 152-ФЗ / CCPA. |
| [CHANGELOG.md](CHANGELOG.md) | Todas as mudanças, com as correções de segurança destacadas. |
| [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) | Componentes empacotados e suas licenças. |
| [CONTRIBUTING.md](CONTRIBUTING.md) · [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md) · [SUPPORT.md](SUPPORT.md) | Como participar. |

---

## Contribuição

Contribuições são bem-vindas, e a régua de revisão para mudanças no modelo de segurança do agente é
alta de propósito — porque esse código pode escrever arquivos e executar programas na sua máquina.

Comece pelo [CONTRIBUTING.md](CONTRIBUTING.md). A versão curta: uma mudança lógica por pull request,
`dotnet test` verde e, se você mexer no alcance do agente, diga na descrição qual portão você colocou
atrás dele.

Por favor, **não abra uma issue pública para uma vulnerabilidade de segurança** — veja
[SECURITY.md](SECURITY.md) para denúncia privada.

---

## Licença

**MIT.** Veja [LICENSE](LICENSE).

Componentes de terceiros mantêm as suas próprias licenças — cerca de 40 pacotes empacotados, além de
Electron e Chromium — catalogados em [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

---

## Status

`0.1.1-alpha`. Publicado como prerelease, deliberadamente.

**Funciona:** chat em streaming, os dois hosts, o grafo e o canvas espaciais, o ciclo de ferramentas do
agente com o seu portão de aprovação, o editor, o git, sessões e pacotes, três idiomas.

**Corrigido desde `0.1.0-alpha`** — duas falhas de segurança na porta de arquivos fora do projeto e
duas maneiras de perder o seu trabalho:

- Uma aprovação em uma leitura de arquivo externo dava acesso de leitura a todo o disco pelo resto da
  execução. Agora cada operação fora do projeto é uma pergunta própria.
- Caminhos fora do projeto eram verificados como texto, então uma junction do Windows podia contornar
  as regras de nome de credencial para arquivos. Agora os links são resolvidos antes de qualquer
  verificação.
- O editor gravava o arquivo inteiro a cada tecla. Agora as gravações são adiadas, com um indicador
  **não salvo** e um despejo automático antes de trocar de sessão, exportar ou sair.
- O renderer duplicava o texto de cada arquivo no armazenamento do navegador, com um limite de 5–10 MB,
  e parava de gravar *silenciosamente* quando ele enchia. Essa duplicação acabou.

**Continua aberto**, com referências a arquivos em
[SECURITY.md](SECURITY.md#known-gaps): as conversas não são criptografadas em repouso (de propósito, e a
sua conta do Windows consegue lê-las de qualquer forma), o sidecar não tem limite de tamanho de
requisição nem limitador de taxa além do padrão do Kestrel, os scripts principal e preload do Electron
não passam por verificação de tipos, e as ferramentas mais novas do agente não têm cobertura de testes.

Esta é uma versão `0.x` de um projeto pequeno sem nenhum financiamento por trás. Ela é construída ao
vivo, as issues são respondidas no melhor esforço possível e não há SLA. Se você precisar de um, isso é
uma conversa com um fornecedor, não com este repositório.

---

<p align="center"><sub>Licenciado sob MIT. Construído ao vivo. Capturas de tela tiradas do aplicativo em execução.</sub></p>
