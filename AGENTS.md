# Audio Transcriber — Desktop (contexto técnico para agentes)

App de escritorio (WPF / .NET 8) para transcribir audio a texto, grabar reuniones (con separación de
hablantes) y organizar notas — con transcripción **local** (Whisper.net, sin conexión) o **en la nube**
(Groq), y sincronización bidireccional con una web companion. Ver también `README.md` para la
descripción orientada a usuario.

## Los dos repos

| | |
|---|---|
| **Desktop** (este repo) | WPF / .NET 8. Se distribuye con **Velopack** (auto-update). |
| **Web / backend** | [`audio-transcriber-web`](https://github.com/ianhominal/audio-transcriber-web) — Next.js 16, deploy automático a Vercel al pushear a `main`. |
| **Backend compartido** | Supabase (Postgres + Storage + Auth + RLS). Sync bidireccional desktop ↔ web. |

## Convenciones

- **Español rioplatense neutro** en UI y comentarios, código incluido. Evitar modismos marcados
  ("tranqui", "compu", "posta", "al toque", "dale").
- **Sin emojis** en el chrome de la app (pedido explícito de producto).
- **Nada de PowerShell**: usar Bash/POSIX o la CLI de `dotnet`.
- **Conventional commits**, sin "Co-Authored-By" ni atribución de IA.
- Trunk-based: los commits van directo a `main`, sin ramas de feature ni PRs de por medio.
- Los changelogs de cada cambio van a `.claude/resources/changelog/YYYY-MM-DD.md` — ese directorio
  está gitignored (LOCAL, no se pushea), así que no vas a encontrarlos clonando el repo.
- La clave de Groq nunca viaja en texto plano: se cifra con DPAPI y nunca se expone en el cliente.

## Build / test

```bash
dotnet build src/AudioTranscriber.App/AudioTranscriber.App.csproj -c Release
dotnet test tests/AudioTranscriber.Core.Tests/AudioTranscriber.Core.Tests.csproj
dotnet test tests/AudioTranscriber.App.UiTests/AudioTranscriber.App.UiTests.csproj
```

Todo junto: `dotnet build AudioTranscriber.slnx` / `dotnet test AudioTranscriber.slnx`. Para correr la
app: `dotnet run --project src/AudioTranscriber.App` (la primera transcripción local descarga el
modelo Whisper una única vez a `%LOCALAPPDATA%\AudioTranscriber\models`).

## Publicar (el baile de Velopack — LEER ANTES DE PUBLICAR)

El instalador es **Velopack** (no Inno Setup), con auto-update. Pasos:

1. Bump de `<Version>` en `src/AudioTranscriber.App/AudioTranscriber.App.csproj`.
2. `dotnet publish src/AudioTranscriber.App/... -c Release -r win-x64 --self-contained -o publish/win-x64`
3. **Bajar el `.nupkg` full de la versión ANTERIOR** a `publish/velopack` (para que `vpk` pueda generar
   el delta), después:
   `vpk pack --packId AudioTranscriber --packVersion X --packDir publish/win-x64 --mainExe AudioTranscriber.exe --outputDir publish/velopack --icon src/AudioTranscriber.App/appicon.ico --channel win`.
   Borrar el full previo del directorio antes de subir.
4. `vpk upload github --repoUrl https://github.com/ianhominal/audio-transcriber-web --outputDir publish/velopack --channel win --publish --releaseName "Audio Transcriber X" --tag desktop-vX --token "$(gh auth token)"`
5. **Gotcha del feed truncado** (crítico, no obvio): `vpk upload github` PISA el `releases.win.json`
   del release nuevo con SOLO la versión actual. Si no se corrige, el auto-update de los clientes ya
   instalados se rompe (dejan de ver versiones intermedias del feed). Hay que **restaurar el historial
   completo**: bajar el `releases.win.json` + `RELEASES` del release anterior, mergear las entradas
   nuevas (dedupe por `FileName`), regenerar `RELEASES` (solo fulls, con BOM), y
   `gh release upload desktop-vX releases.win.json RELEASES --clobber`. Conviene un script de merge
   (Python u otro) para no rehacer esto a mano cada vez. **Verificar el feed publicado después**
   (cantidad de assets: tienen que estar el full + delta nuevos, y el full base).

- **Los releases viven en el repo WEB** (`audio-transcriber-web`), tag `desktop-vX` — porque
  `UpdateService.cs` (`RepoUrl`) apunta ahí. El código del desktop vive en este repo.
- Los clientes instalados se auto-actualizan contra ese feed → un feed mal armado rompe el update para
  todo el mundo. No dejarlo a medio hacer.

## Arquitectura

- **`AudioTranscriber.Core`** (`net8.0`, sin WPF/WinForms): TODA la lógica testeable — sync, workspaces,
  cola de transcripción, detector de reuniones, parsing de diarización, export. Tests en
  `tests/AudioTranscriber.Core.Tests` (xUnit, TDD).
- **`AudioTranscriber.App`** (`net8.0-windows`, WPF): MVVM con **CommunityToolkit.Mvvm**
  (`[ObservableProperty]`, `[RelayCommand]`) + UI + plomería de plataforma. `AppSettings` guarda la
  clave de Groq cifrada con DPAPI. UI tests en `tests/AudioTranscriber.App.UiTests`.
- Solución: **`.slnx`** (no `.sln`) → `dotnet build AudioTranscriber.slnx`.
- Deps clave: Velopack 1.2.0 · NAudio + WASAPI (grabación: mic + loopback del sistema) ·
  **Whisper.net** (transcripción LOCAL) · Groq (server-side, vía `/api/transcribe` del web) ·
  **sherpa-onnx** (diarización) · Concentus + Concentus.Oggfile (Opus — codificación lenta, por eso
  Groq no lo usa para subir) · Sentry · `System.Windows.Forms` (SOLO para el `NotifyIcon` de la
  bandeja del sistema — ver el `<Using Remove>` en el csproj).
- Entry point custom para Velopack: `App.xaml` es `Page` (no `ApplicationDefinition`); el `Main` real
  está en `App.xaml.cs`; `StartupObject = AudioTranscriber.App.App`.
- DPI Per-Monitor V2 vía `app.manifest` (warning WFAC010 suprimido a propósito: falso positivo de
  WinForms).

## Subsistemas

- **Sync** (`Core/Sync/`): merge de 3 vías contra un baseline persistido en `.synccache/index.db`
  (SQLite: tablas `SyncBaseline` + `SyncIdMap` + `SyncLocalTombstone`).
  - `HashId` = id determinístico calculado desde la ruta (SHA-256 → UUID). **Ojo**: por el layout
    mixed-endian de `.NET Guid(byte[])`, los nibbles de versión/variante quedan mal ubicados → NO son
    UUIDs RFC-estrictos. Cualquier validación server-side de estos ids tiene que ser un **regex de
    forma** (`8-4-4-4-12` hex), nunca `z.uuid()` estricto (rompía el "Asistente del proyecto" del
    lado web).
  - Freno anti-borrado-masivo (umbral 40%, se saltea si el baseline está vacía).
  - Los borrados locales propagan vía **tombstones explícitos** (`SyncLocalTombstone`, los registra
    `SyncCoordinator.MarkAudioDeletedForSync` cuando el usuario borra). La AUSENCIA sola nunca borra
    nada (protección contra un bug viejo de vaciado accidental de la nube).
  - Los audios sin transcripción solo se auto-suben para Groq si el motor activo es **Groq** (si es
    Local, se respeta: se espera a que el usuario transcriba).
  - El audio bajado de la nube nunca pisa el original local (solo se baja si falta un local usable).
- **Cola de transcripción** (`Core/Workspaces/TranscriptionQueue.cs`): FIFO con dedupe, worker de a
  uno (dos Whisper local en paralelo = el doble de RAM). Grabar es independiente (corre durante una
  transcripción). Estados separados: `IsTranscribing` vs. `IsBusy` (descarga de modelo) vs.
  `IsRecording`.
- **Detección de reuniones** (`Core/Meetings/MeetingDetector.cs` puro + `App/MicrophoneUsageReader.cs`
  + `App/MeetingDetectionService.cs`): detecta Meet/Zoom/Teams/Discord por "hay una app usando el
  micrófono" (registro `ConsentStore` de Windows, `LastUsedTimeStop==0`). Configurable en
  `AppSettings.MeetingDetection`: `Preguntar` / `Automatico` / `Desactivado`. Reusa la grabación de
  reunión; nunca pisa una grabación manual (flag `_recordingStartedByDetection`). Debounce ~8s. Caso
  flojo conocido: push-to-talk / apps que sueltan el micrófono al mutear.
- **Asistente de IA** (compartido con la web): chat por-nota (`/api/chat`, con historial), "Segundo
  cerebro" sobre todas las notas (`/api/brain`, RAG por FTS `search_vector`), scope de **proyecto**
  (`/api/brain` con `projectId`), y "Combinar en documento"/merge (`/api/notes/merge`). Del lado
  desktop: `ChatScopeRouter`, `AiChatClient` / `AiBrainClient` / `AiMergeClient`, `BrainWindow`. El
  botón "Asistente IA" es **contextual**: proyecto seleccionado → asistente de ese proyecto; nota
  abierta → chat de esa nota.
- **Grabación**: solo-mic ("Grabar solo mi voz") + reunión (audio del sistema + mic,
  `ToggleMeetingRecording`, con selector de aplicación + diarización).
- **Ventana**: recuerda estado/tamaño/posición (`WindowBoundsPersistence` +
  `Core/Ui/WindowBoundsValidator` / `InitialWindowSizer`), con validación on-screen en setups
  multi-monitor.
- **Export**: a Obsidian/Drive (`.md`) y PDF.

## Gotchas (WPF y del repo)

- Los comentarios XAML **no pueden tener `--`** (error MC3000).
- **No insertar miembros entre `[RelayCommand]` y su método** (rompe el generador de código).
  `[RelayCommand]` sobre `void Foo` → genera `FooCommand`; sobre `Task FooAsync` → genera `FooCommand`
  (pela el sufijo "Async"); un método SÍNCRONO llamado `FooAsync` → genera `FooAsyncCommand` y rompe
  los bindings que esperan `FooCommand`.
- `DynamicResource` con una clave que no existe falla **en silencio** (hereda el valor);
  `StaticResource` con clave inexistente sí es error de build.
- `Icon=` en XAML rompe el single-file publish (se usa `EmbeddedResource` con `LogicalName`, ver el
  csproj).
- `AudioProject.Name` (carpeta en disco, sanitizada) vs. `Title` (metadata editable) — el sync usa
  **`Title`** como identidad, no `Name`.
- Los `.env` de este repo están bloqueados por permisos de archivo (no se pueden leer directamente).

## Repo web (`audio-transcriber-web`)

- Next.js 16 (App Router), Tailwind v4 (CSS-first, `@theme` en `globals.css`), Supabase con RLS,
  `getApiUser` (Bearer JWT para el desktop + cookies para la web, cliente siempre user-scoped).
  Auto-deploy al pushear a `main`.
- APIs clave para el desktop: `/api/transcribe` (Groq, único lugar que CREA transcripciones vía el
  motor Groq), `/api/sync/pull` + `/api/sync/push` (el push hace `.upsert()` de transcripciones
  cuando llega `audio_name` — antes era `.update()`-solo y perdía en silencio las transcripciones
  100% locales), `/api/chat`, `/api/brain` (`projectId` validado con un regex de forma, no
  `z.uuid()` estricto — ver el gotcha de `HashId` más arriba), `/api/notes/merge`.
- Caps de costo/abuso: triggers de Postgres por `kind` sobre `ai_usage_log` (`chat` 60/día, `brain`
  30/día, `merge` 20/día). Los números están **duplicados** entre el código TypeScript del web
  (`src/lib/aiUsage.ts`) y las migraciones SQL — si cambian, hay que mantenerlos en sync en los dos
  lugares.
- Tests del lado web: **Vitest** (solo lógica pura; la UI se testea con Playwright, no con Vitest).

## Stack completo

WPF · .NET 8 · CommunityToolkit.Mvvm · Whisper.net · Groq · NAudio + WASAPI · sherpa-onnx · Concentus
(Opus) · Velopack · Supabase · Sentry · xUnit.
