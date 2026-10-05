# Kontur Code

**Lee esto primero:** Kontur Code es una **herramienta para desarrolladores que puede escribir en
tus archivos y ejecutar programas en tu máquina.** Eso es el producto, no un fallo suyo. Todo lo
que sigue sobre el aislamiento del agente —y cada hueco conocido— está en
[SECURITY.md](SECURITY.md). Léelo antes de apuntar esto a nada que te importe.

[English](README.md) · [Русский](README.ru.md) · [Deutsch](README.de.md) · [Español](README.es.md) ·
[Français](README.fr.md) · [Português (Brasil)](README.pt-BR.md) · [Italiano](README.it.md) ·
[中文（简体）](README.zh-CN.md) · [日本語](README.ja.md) · [한국어](README.ko.md) · [Türkçe](README.tr.md)

---

Un cliente de LLM de escritorio que se ha convertido en un **entorno de desarrollo de IA
espacial**. Una ventana, tus propias claves de API, tus conversaciones en un archivo SQLite local
—y un espacio de trabajo que convierte la carpeta que le indiques en un grafo que se ve de verdad.

Bajo la ventana, dos hosts comparten todas las capas: una aplicación **WPF** y un shell
**Electron + React** sobre el mismo núcleo de .NET, de modo que la aplicación no está limitada por
el techo de ninguno de los dos toolkits.

<p align="center">
  <img src="docs/screenshots/canvas.png" alt="El lienzo espacial: el espacio de trabajo como grafo de dependencias con aristas etiquetadas" width="100%">
</p>

---

## Contenido

- [Capturas de pantalla](#capturas-de-pantalla)
- [Qué hace](#qué-hace)
- [El agente](#el-agente)
- [Privacidad en resumen](#privacidad-en-resumen)
- [Requisitos](#requisitos)
- [Instalación](#instalación)
- [Primera ejecución](#primera-ejecución)
- [Arquitectura](#arquitectura)
- [Desarrollo](#desarrollo)
- [Documentación](#documentación)
- [Cómo contribuir](#cómo-contribuir)
- [Licencia](#licencia)
- [Estado](#estado)

---

## Capturas de pantalla

### Chat

<p align="center">
  <img src="docs/screenshots/chat.png" alt="Una sesión de chat con una respuesta del asistente y el panel de contexto del espacio de trabajo" width="100%">
</p>

Los tokens aparecen según llegan. Si detienes la respuesta a medias, **el texto parcial se
conserva**, no se descarta: sigue sirviendo como contexto para el siguiente turno.

### El lienzo espacial

<p align="center">
  <img src="docs/screenshots/canvas.png" alt="Nodos y aristas de dependencia etiquetadas en un lienzo infinito, con un minimapa" width="100%">
</p>

Tu proyecto como grafo: archivos, carpetas, módulos, servicios, interfaces, datos, pruebas y planes
se convierten en nodos, con aristas de contención y de dependencia entre ellos. Desplaza, haz zoom,
selecciona por recuadro y observa en la esquina un minimapa de todo el grafo. Las aristas llevan
etiqueta: `Login() → CreateTokenAsync` es una arista de llamada; «solo en tiempo de compilación» es
una dependencia que nunca se ejecuta.

### La estructura del grafo

<p align="center">
  <img src="docs/screenshots/graph-outline.png" alt="Un árbol de estructura del grafo filtrable, agrupado por tipo de nodo" width="100%">
</p>

El mismo grafo como una estructura que puedes leer y filtrar por nombre o ruta.

### El editor

<p align="center">
  <img src="docs/screenshots/editor.png" alt="Un archivo de C# abierto en el editor con resaltado de sintaxis y un contador de cambios" width="100%">
</p>

CodeMirror 6 con gramáticas de diez lenguajes, ediciones de IA en línea sobre una selección y
autocompletado con texto fantasma.

### Ajustes

<p align="center">
  <img src="docs/screenshots/settings.png" alt="Ajustes: tema, idioma, interfaz y valores predeterminados del chat" width="100%">
</p>

Tema, idioma de la interfaz, escala de la aplicación, prompt del sistema, parámetros de muestreo:
todo local, todo guardado en tu propia base de datos.

---

## Qué hace

- **Chat en streaming.** Los tokens llegan a medida que se generan. Detener conserva la respuesta
  parcial. Regenerar sustituye la respuesta en el sitio, opcionalmente en otro modelo.
- **Tres modos de trabajo.** *Chat* para conversar, *Cowork* para analizar y *Code*, donde el agente
  recibe un espacio de trabajo y un bucle de herramientas. El modo es una propiedad del mensaje, no
  de la aplicación, así que «planifícalo y luego constrúyelo» son dos mensajes en lugar de dos
  viajes a Ajustes.
- **El grafo espacial.** Tu carpeta se indexa automáticamente en nodos y aristas. Un indexador
  basado en diffs añade los archivos nuevos, retira los borrados y **conserva la distribución que
  organizaste**. Los planes que produce el agente llegan al lienzo como conjuntos de nodos y
  aristas: se pueden deshacer, se guardan y puedes rechazarlos.
- **Superficies unificadas del espacio de trabajo.** El lienzo como mapa, el grafo como estructura,
  un árbol de archivos, el editor, un panel de git, la trayectoria de una ejecución y una vista de
  tareas: todo a un `Ctrl+Shift+P` de distancia.
- **Git.** Status, diffs staged y unstaged, stage, commit, branch, revert, push, pull, fetch. Todo
  a través de `git`, **sin shell** y con argumentos validados.
- **Contabilidad de tokens.** Consumo en vivo, coste estimado y lo que el modelo realmente retiene
  en contexto, con un botón **Compactar sesión** que pliega los turnos más antiguos en un resumen.
- **Renderizado de Markdown.** Encabezados, listas, tablas, citas, listas de tareas y bloques de
  código, con resaltado de sintaxis. Se renderiza como contenido estructurado, **nunca como HTML
  inyectado**.
- **Catálogo de modelos.** Obtenido de cada proveedor y cacheado en SQLite, así que el selector
  funciona sin conexión a partir de entonces. La ventana de contexto, los precios y las
  capacidades vienen del proveedor, no de una lista codificada a mano.
- **Dos proveedores de serie** —OpenRouter y NVIDIA NIM, ambos compatibles con OpenAI. Apunta el
  endpoint de NVIDIA a un Ollama, LM Studio o contenedor NIM autoalojado en local y nada saldrá de
  tu máquina.
- **Paquetes de sesión.** Exporta toda la sesión —chat, lienzo, archivos, objetivos— como `.zip`.
- **Tres idiomas.** Inglés, ruso y alemán, aplicados en vivo a toda la interfaz.
- **Claro y oscuro**, siguiendo al sistema o fijo.

---

## El agente

El agente ejecuta un bucle de herramientas, y su alcance es lo que conviene entender antes de
usarlo.

| | |
| --- | --- |
| **Trabaja en** | Una carpeta que tú designas, y se niega a leer o escribir fuera de ella |
| **También se niega, dentro de esa carpeta** | `.git`, `.env`, `credentials.json`, `*.pem`, `*.key`, `*.pfx` — por nombre, siempre |
| **Pregunta antes de** | Cada escritura, cada archivo externo, cada petición de red, cada programa |
| **Nunca** | Ejecuta un shell. `&&`, `\|`, `>` y `$HOME` son texto que recibe el programa |
| **Programas** | Desactivados por defecto. Después, solo una lista blanca que edita una persona. Después, aprobación en *cada* llamada |
| **Deshacer** | Tu control de versiones. Los cambios se muestran antes de hacerse, no se revierten después |

Una negativa nombra la regla y le dice al modelo qué hacer en su lugar, para que deje de intentar
usar la misma herramienta tres veces.

**Todo lo que está fuera de esa carpeta es opcional y permanece apagado hasta que lo actives.** El
fetcher de red y el acceso a archivos fuera del proyecto son interruptores separados en Ajustes, y
cada llamada sigue pasando por la solicitud de aprobación. **Para ellos no se recuerda ninguna
respuesta**: una pregunta, una lectura o una escritura.

> El modelo completo de aislamiento —y **lo que sigue abierto**, que ahora es una lista corta— está
> en [SECURITY.md](SECURITY.md). Esto es una alpha; léelo antes de confiar en él.

---

## Privacidad en resumen

- **Sin telemetría. Sin analítica. Sin informes de fallos. Sin cuentas.** No hay código en este
  repositorio que abra una conexión a ninguna dirección propiedad de este proyecto.
- **Tus conversaciones nunca tocan un servidor.** Son un archivo SQLite en tu propio perfil de
  usuario.
- **Las claves de API están cifradas** con Windows DPAPI, limitadas a tu cuenta de Windows y nunca
  escritas en un registro.
- **Lo que sale de tu máquina:** exactamente lo que envías a un proveedor de modelos, y solo cuando
  pulsas Enviar. La lista completa de destinos de red está en
  [PRIVACY.md § 5](PRIVACY.md#5-what-leaves-your-machine-and-who-receives-it).
- **Las transcripciones consecutivas se leen sin esta aplicación.** La base de datos no está
  cifrada en reposo: un compromiso deliberado, documentado en lugar de suavizado.
- **Tu proveedor de modelos ve tu prompt**, según *su* política, no según la de este proyecto. Ese
  es el acuerdo que hace un cliente para el modelo de otro.

El detalle completo, escrito contra el RGPD, la ley rusa 152-FZ y CCPA/CPRA, está en
[PRIVACY.md](PRIVACY.md). Allí también se explica cómo exportar y cómo borrar todo.

---

## Requisitos

- Windows 10 versión 1809 o posterior, o Windows 11
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download) —solo para el instalador; una
  compilación publicada lo necesita, las fuentes necesitan el SDK
- Una clave de API de [OpenRouter](https://openrouter.ai) o
  [NVIDIA](https://integrate.api.nvidia.com)
- Unos 500 MB de disco y una carpeta que estés dispuesto a dejar leer a un agente

No hay compilación multiplataforma. DPAPI y WPF son solo de Windows, y el framework de destino lo
dice en lugar de fallar en tiempo de ejecución.

---

## Instalación

Descarga el instalador desde la
[página de versiones](https://github.com/rwarx/kontur-code/releases). Es una instalación NSIS por
usuario: no hacen falta permisos de administrador.

La primera versión es una **alpha**. Se publica porque la forma ya está lo bastante asentada como
para construir encima, no porque esté lista para uso desatendido.

<details>
<summary>Compílalo tú mismo</summary>

```bash
git clone https://github.com/rwarx/kontur-code.git
cd kontur-code

# The sidecar has to be published next to where Electron looks for it
dotnet publish src/AIClient.Server -c Release -r win-x64 --self-contained false -o electron/sidecar

cd electron
npm install
npm run dist      # → electron/release/
```

Compilar solo la solución de .NET te da el host WPF:

```bash
dotnet build AIClient.slnx
dotnet run --project src/AIClient.App
```

</details>

---

## Primera ejecución

1. **Ajustes → Proveedores**, pega una clave de API, pulsa **Actualizar**. El selector de modelos
   sigue vacío hasta que un proveedor tenga éxito; el catálogo se cachea después, así que a partir
   de ahí funciona sin conexión.
2. **Abre una carpeta.** En el modo *Code*, apúntala a un proyecto. Se indexa en el grafo, y a
   partir de entonces el mundo del agente es esa carpeta.
3. **Haz un commit antes de dejarle trabajar.** Si quieres, un commit vacío con `git commit`. El
   agente escribe directamente en tu working tree, sin nada en el índice y sin copia de seguridad;
   tu historial es el deshacer, y el único.
4. **Lee [SECURITY.md](SECURITY.md)** si piensas habilitar la ejecución de comandos o el acceso a
   archivos fuera del proyecto. Ambos están desactivados por defecto, y ambos son las funciones con
   filo afilado.

---

## Arquitectura

Cinco proyectos, una regla: **las dependencias apuntan hacia dentro.** `Domain` y `Application`
apuntan a un `net10.0` sin adornos, lo que convierte alcanzar WPF o DPAPI en un error de compilación
en lugar de un comentario en la revisión.

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

Tres vocabularios de eventos, cada uno más estrecho que el anterior, traducidos en cada frontera.
Un proveedor no puede meter un identificador de base de datos en el tipo que devuelve, porque el
tipo que devuelve no es el tipo que consume la interfaz.

La API local exige un token bearer en cada arranque y se niega a enlazarse a nada que no sea
loopback: estar en `127.0.0.1` no es una frontera de autorización, y el código la trata como una
frontera que no lo es.

El razonamiento completo, incluidas las dos puertas al sistema de archivos y los dos renderizadores
del lienzo, está en [ARCHITECTURE.md](ARCHITECTURE.md).

---

## Desarrollo

```bash
dotnet build AIClient.slnx     # warnings are errors — that is deliberate
dotnet test                    # 896 tests, no network and no API key needed

cd electron
npm install
npm run typecheck
npm run dev                    # renderer against a seeded demo workspace, no backend needed
```

Requiere Windows y el SDK de .NET 10. Node 22 solo hace falta para el renderer.

Las convenciones que importan, y que `.editorconfig` no puede expresar, están en
[CONTRIBUTING.md](CONTRIBUTING.md).

---

## Documentación

| Documento | Qué contiene |
| --- | --- |
| [ARCHITECTURE.md](ARCHITECTURE.md) | Por qué el código tiene esta forma. Léelo antes de cambiar la estructura. |
| [DEVELOPMENT.md](DEVELOPMENT.md) | Compilar, migrar, probar, extender. Léelo antes de cambiar nada. |
| [SECURITY.md](SECURITY.md) | El modelo de amenazas, qué está protegido, **y los huecos conocidos**. |
| [PRIVACY.md](PRIVACY.md) | Qué datos existen, adónde van y tus derechos. RGPD / 152-FZ / CCPA. |
| [CHANGELOG.md](CHANGELOG.md) | Todos los cambios, con los arreglos de seguridad destacados. |
| [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) | Componentes incluidos y sus licencias. |
| [CONTRIBUTING.md](CONTRIBUTING.md) · [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md) · [SUPPORT.md](SUPPORT.md) | Cómo participar. |

---

## Cómo contribuir

Las contribuciones son bienvenidas, y el listón para revisar cambios en el modelo de seguridad del
agente es alto a propósito, porque ese código puede escribir archivos y ejecutar programas en tu
máquina.

Empieza por [CONTRIBUTING.md](CONTRIBUTING.md). La versión corta: un cambio lógico por pull request,
`dotnet test` en verde y, si tocas el alcance del agente, di en la descripción tras qué puerta lo
has puesto.

Por favor, **no abras una incidencia pública para una vulnerabilidad**: mira
[SECURITY.md](SECURITY.md) para el informe privado.

---

## Licencia

**MIT.** Consulta [LICENSE](LICENSE).

Los componentes de terceros conservan sus propias licencias: unos 40 paquetes incluidos más Electron
y Chromium, catalogados en [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

---

## Estado

`0.1.1-alpha`. Publicado como prerelease, a propósito.

**Funciona:** chat en streaming, ambos hosts, el grafo espacial y el lienzo, el bucle de herramientas
del agente con su puerta de aprobación, el editor, git, sesiones y paquetes, tres idiomas.

**Arreglado desde `0.1.0-alpha`** — dos agujeros de seguridad en la puerta de archivos fuera del
proyecto y dos formas de perder tu trabajo:

- Una aprobación en una lectura de archivo externo solía dar acceso de lectura a todo el disco durante
  el resto de la ejecución. Ahora cada operación fuera del proyecto es su propia pregunta.
- Las rutas fuera del proyecto se comprobaban como texto, así que una junction de Windows podía
  rodear las reglas de nombres de archivo con credenciales. Ahora los enlaces se resuelven antes de
  comprobar nada.
- El editor escribía el archivo entero con cada pulsación de tecla. Ahora las escrituras tienen
  rebote, con un indicador **sin guardar** y un volcado automático antes de que cambies de sesión,
  exportes o salgas.
- El renderer duplicaba el texto de cada archivo en el almacenamiento del navegador, con un tope de
  5–10 MB, y dejaba de guardar *en silencio* cuando se llenaba. Esa duplicación ya no está.

**Sigue abierto**, con referencias a ficheros en
[SECURITY.md](SECURITY.md#known-gaps): las conversaciones no están cifradas en reposo (a propósito, y
tu cuenta de Windows puede leerlas de todos modos), el sidecar no tiene tope de tamaño de petición
ningun limitador de peticiones más allá del valor predeterminado de Kestrel, los scripts principal y
preload de Electron no pasan comprobación de tipos, y las herramientas más recientes del agente no
tienen cobertura de pruebas.

Esta es una versión `0.x` de un proyecto pequeño sin financiación detrás. Se construye en abierto,
las incidencias se responden en la medida de lo posible y no hay SLA. Si necesitas uno, eso es una
conversación con un proveedor, no con este repositorio.

---

<p align="center"><sub>Con licencia MIT. Construido en abierto. Capturas tomadas de la aplicación en funcionamiento.</sub></p>
