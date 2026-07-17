# Audio Transcriber — Desktop

App de escritorio (**WPF / .NET 8**) para transcribir audio a texto, grabar reuniones y organizar
notas — con transcripción **local** (sin conexión) o **en la nube**, separación de hablantes y
sincronización con una versión web.

Es la mitad de escritorio de un producto híbrido: esta app + una **web companion**
([audio-transcriber-web](https://github.com/ianhominal/audio-transcriber-web), Next.js) que comparten
los mismos datos vía Supabase.

## Qué hace

- **Transcribe** audios (español y más) con dos motores intercambiables:
  - **Local** — [Whisper.net](https://github.com/sandrohanea/whisper.net) (whisper.cpp): corre 100 %
    en tu PC, sin conexión ni costo.
  - **Nube** — Groq (server-side): rápido, para equipos modestos.
- **Graba reuniones** capturando el audio del sistema + micrófono, con selección de **qué aplicación**
  capturar (WASAPI loopback + process loopback interop).
- **Separa hablantes** (diarización con sherpa-onnx) y los muestra en una vista de lectura tipo
  documento, cada voz con su color.
- **Organiza** los audios en proyectos (carpetas) con metadata, drag & drop y búsqueda por contenido.
- **IA sobre la transcripción**: resumen, formatos reutilizables ("armame un acta"), chat, y
  "mejorar texto" (puntuación + vocabulario).
- **Sincroniza** en dos direcciones con la web (Supabase), login con Google (**PKCE**, sin client
  secret), y sube el audio comprimido a Opus directo a Storage.
- **Exporta** a Markdown (Obsidian/Drive) y PDF. **Auto-updates** con Velopack.

## Arquitectura

Separación estricta para que la lógica sea testeable sin arrastrar la UI:

```
src/
  AudioTranscriber.Core/   Toda la lógica: workspace/proyectos, motor de sync (reconciliación
                           de 3 vías contra una baseline), diarización, audio, transcripción,
                           export. SIN dependencia de WPF → 100 % testeable.
  AudioTranscriber.App/    UI WPF (MVVM con CommunityToolkit.Mvvm). Solo presentación.
tests/
  AudioTranscriber.Core.Tests/   ~960 tests (xUnit, TDD)
```

Algunas decisiones de diseño que vale la pena mirar:

- El **motor de sync** reconcilia local ↔ nube contra una baseline persistida (3-way merge), con un
  freno anti-borrado-masivo y clasificación de fallas permanentes vs. transitorias (para no
  reintentar para siempre algo que nunca va a andar).
- El **contraste de la paleta** se valida con tests que leen los XAML de tema desde disco y miden AA
  (WCAG), en vez de confiar en constantes duplicadas que se desincronizan.
- Tokens de sesión cifrados localmente con **DPAPI**; la key de Groq nunca vive en el cliente (la
  transcripción en la nube corre server-side).

## Stack

WPF · .NET 8 · CommunityToolkit.Mvvm · Whisper.net · Groq · NAudio + WASAPI · sherpa-onnx · Concentus
(Opus) · Velopack · Supabase · Sentry · xUnit

## Correr

```bash
dotnet run --project src/AudioTranscriber.App
```

La primera transcripción local descarga el modelo Whisper (una única vez) a
`%LOCALAPPDATA%\AudioTranscriber\models`.

## Tests

```bash
dotnet test AudioTranscriber.slnx
```
