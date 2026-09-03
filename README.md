# Kanal / 频道

<img src="design/kanal-icon.svg" width="96" alt="Kanal icon">

[![CI](https://github.com/TONiiV/Kanal/actions/workflows/ci.yml/badge.svg)](https://github.com/TONiiV/Kanal/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-222222.svg)](LICENSE)

Kanal is a live meeting-translation host for rooms where people do not share a language. One
laptop captures the conversation, produces parallel transcripts and translations, and gives
participants a QR code for a read-only mobile view. It was built around Chinese, German, and
Polish technical meetings, where part numbers, tolerances, and delivery dates matter more than
chat-like presentation.

The desktop host is an Avalonia application on .NET 10. Speech recognition and translation are
provider-based, so cloud and local stages can be combined without changing the room model or the
mobile client.

> Kanal is under active development. Demo mode is keyless and ready to explore; live use currently
> requires cloud transcription. See [Current limitations and roadmap](#current-limitations-and-roadmap)
> before relying on it in a meeting.

## Why Kanal

- **One room, several readable views.** The host shows up to four language columns; each phone
  shows one language selected by its reader.
- **Live corrections stay consistent.** Partial transcripts are replaced in place, final
  translations cannot overwrite newer source text, and speaker rename/merge updates history.
- **Late join and reconnect work.** The host republishes authoritative room snapshots, while the
  mobile client restores its per-room cache immediately after a lock-screen reconnect.
- **The pipeline is explicit.** Every mode says where transcription and translation run and what
  that processing sends off the host machine.
- **Meeting controls respect the recording boundary.** Pause stops audio before it reaches the
  speech provider or local recording; phones show paused, recording, moved, and closed room states.
- **Built for multilingual operation.** The operator interface is available in English, Chinese,
  German, and Polish, and Chinese output is normalized to Simplified Chinese at the host.
- **No app install is needed on participant phones.** The mobile client is a static, read-only web
  page opened from the join QR code.

## Privacy and data boundaries

Kanal has two separate network boundaries: the speech pipeline and the mobile-caption relay.
“Local” in a mode describes the speech stage; it does **not** mean the whole meeting is offline.

| Data | Where it goes |
|---|---|
| Microphone audio | Sent to the cloud speech provider in cloud-transcription modes. It stays on the host in local-transcription modes once a local ASR provider exists. |
| Captions and room state | Sent through the authenticated Kanal gateway (a Cloudflare Worker) to the meeting's private room object, which fans them out to joined phones. Messages include transcript text, translations, speaker labels, language configuration, and pause/recording/lifecycle state. Nothing is stored server-side. |
| Joined-phone cache | The mobile client stores the current room transcript and state in browser `localStorage` so it can render before a reconnect snapshot arrives. |
| Local recording | Live microphone modes record a WAV file by default in the configured audio folder. Recording pauses with the room and can be disabled in Settings. The WAV is not published by Kanal. |
| API keys and preferences | Gladia keys selected in the UI are stored as plain JSON in the platform application-data directory. The relay host token is supplied only through the operator machine's runtime environment. |
| Local translation models | Downloaded from the model catalog to the platform application-data directory and loaded in-process with llama.cpp. Model files and generated translations stay on the host, apart from captions sent to the relay. |

Kanal ships no backing-store URL or API key of any kind: the relay is a self-contained
[Cloudflare Worker](gateway/) and the only address clients ever see is the Worker's own. The join
QR contains the public gateway address plus a receive-only, room-scoped ticket; anyone who obtains
that bearer invitation can read the room until the ticket expires (currently 12 hours), but cannot
create rooms, publish messages, or enumerate other rooms. Room creation itself requires a
per-device credential issued by the gateway operator, so a stolen laptop is revoked alone without
rotating anything else. Network reachability for the relay is required for phones to receive
captions.

## Quick start

### Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Windows or macOS for live microphone capture
- Git, when building from a clone

Linux can build the solution, but Kanal does not currently provide a Linux microphone-capture
backend. There are no packaged desktop releases yet, so installation is from source:

```bash
git clone https://github.com/TONiiV/Kanal.git
cd Kanal
dotnet run --project src/Kanal.Host
```

The first screen selects Chinese, German, and Polish by default. Keep **Demo — scripted** selected
and press **Start**. A repeatable trilingual script will run through the real room orchestrator
without an API key or microphone. Scan the displayed QR code to exercise the mobile view; that part
still uses the configured caption relay.

For a live room:

1. Open **Settings** and add a Gladia API key, or set `GLADIA_API_KEY` before launching Kanal.
2. Use the microphone test to select and verify the input device.
3. Choose **Cloud transcription · cloud translation**, select one to four languages, and start.
4. Tell participants if local audio recording is enabled, then let them scan the join QR code.

## Running modes

A mode is a preset for the transcription and translation stages. Availability is calculated from
the providers, key, and downloaded model on the current machine; unavailable rows remain visible
and explain what is missing.

| Mode | Transcription | Translation | Speech-pipeline data sent off the host | Current availability |
|---|---|---|---|---|
| Demo — scripted | Scripted | Scripted, or the selected downloaded local model | Nothing | Available without keys |
| Cloud transcription · cloud translation | Gladia live | Gladia live | Audio | Available with a Gladia key |
| Cloud transcription · local translation | Gladia live | Selected local GGUF model | Audio | Available with a Gladia key and downloaded model |
| Local transcription · cloud translation | Not implemented | Standalone cloud MT not implemented | Text only | Unavailable |
| Local transcription · local translation | Not implemented | Selected local GGUF model | Nothing | Unavailable until local ASR lands |

All modes publish text and room state through the mobile relay when run from the production UI.
Cloud-to-local mode disables translation inside the cloud ASR session; the capability-based
orchestrator then sends final transcripts to the local `IMtProvider`.

## Configuration

Most configuration is available from the in-app **Settings** window:

- named Gladia API keys and the active key;
- local translation model download, selection, and deletion;
- microphone selection and level/noise check;
- transcript and audio output folders;
- local WAV recording on/off; and
- operator-interface language.

Preferences are written to `Kanal/settings.json` beneath the operating system's application-data
directory. Downloaded models live in the adjacent `Kanal/models` directory. Transcript exports and
recordings default to `Documents/Kanal`.

Environment variables override connection defaults:

| Variable | Purpose |
|---|---|
| `GLADIA_API_KEY` | Fallback speech-provider key when no stored named key is selected |
| `KANAL_RELAY_URL` | Public HTTPS endpoint of the deployed `kanal-relay` Worker |
| `KANAL_RELAY_HOST_TOKEN` | This desktop's device credential, obtained once with an activation code; runtime only, never part of a build or QR |
| `KANAL_WEB_URL` | Base URL of the static mobile client placed in the join QR code |

Relay stays disabled rather than using a public fallback when either relay variable is absent.
`KANAL_RELAY_URL` is an address, not a credential: every gateway route still requires the device
credential or a role-scoped room ticket. Missing configuration or a gateway failure does not block
transcription: the meeting continues without a QR code and the status bar reports the degraded
mobile relay. Deployment and device-activation commands are in
[`gateway/README.md`](gateway/README.md).

The default web URL is `https://toniiv.github.io/Kanal/`. To self-host it, serve
[`web/index.html`](web/index.html) over HTTPS and point `KANAL_WEB_URL` at that URL. The page has no
runtime import, project configuration, external font, or stylesheet dependency. On Start, the
host puts only the gateway address, 12-hour reader ticket, random room capability, and public P-256
verification key in the URL fragment; the fragment is not sent to the web host.

## Architecture

```text
microphone / demo script
          │
          ▼
   IAsrProvider ── partials/finals ──► MeetingSession ──► authoritative RoomState
                                              │                    │
                        finals, when ASR       │                    ├──► Avalonia host columns
                        cannot translate       ▼                    └──► transcript export
                                        IMtProvider
                                              │
                                              ▼
                                      IRelayPublisher
                                              │
                                              ▼
                                    read-only mobile clients
```

The host is the single authority and clients are projections. `MeetingSession` branches on provider
capabilities, not vendor names. Relay transport is isolated behind `IRelayPublisher`, and speaker
merges remain non-destructive: existing utterances retain their original diarization tag while
clients resolve the canonical speaker at render time.

| Project | Responsibility |
|---|---|
| `src/Kanal.Core` | Provider contracts, room/domain model, orchestration, relay protocol and authenticated gateway publisher |
| `src/Kanal.Audio` | 16 kHz mono PCM16 capture, Windows WASAPI, macOS AudioQueue/CoreAudio, resampling and WAV support |
| `src/Kanal.Providers.Gladia` | Gladia live-v2 session setup, WebSocket streaming, reconnect and wire normalization |
| `src/Kanal.Providers.LocalMt` | In-process llama.cpp translation, prompts, model catalog and downloads |
| `src/Kanal.Host` | Avalonia operator UI, pipeline planning, settings, recording, QR generation and export |
| `tests/Kanal.Core.UnitTests` | Unit tests for audio, providers, serialization, room state, orchestration, and non-visual services |
| `tests/Kanal.UI.UnitTests` | Headless unit tests for deterministic host view-model and application-state behavior; rendering and layout are intentionally out of scope |
| `web/index.html` | Static mobile client; `docs/index.html` is its byte-identical GitHub Pages copy |
| `gateway/` | Relay gateway: Cloudflare Worker plus per-room and device-registry Durable Objects, with its own vitest suite |
| `tools/Kanal.Doctor` | Microphone and live-ASR diagnostics |

The original product requirements and design trade-offs are documented in the Chinese
[`docs/PRD-v0.3.md`](docs/PRD-v0.3.md). Implementation decisions and measured findings live in
[`docs/PROGRESS.md`](docs/PROGRESS.md).

## Development and testing

Build and run the complete test suite from the repository root:

```bash
dotnet build Kanal.slnx --configuration Release
dotnet test tests/Kanal.Core.UnitTests/Kanal.Core.UnitTests.csproj --configuration Release --no-build
dotnet test tests/Kanal.UI.UnitTests/Kanal.UI.UnitTests.csproj --configuration Release --no-build
```

The Core suite covers the room model, audio pipeline, providers, wire protocol, orchestration, and
non-visual services. The UI suite uses headless Avalonia only to exercise deterministic view-model
and application-state behavior; pixel, layout, style, and window-rendering assertions are out of
scope. CI also enforces that the deployable web client and its GitHub Pages copy stay byte-identical:

```bash
cmp web/index.html docs/index.html
```

For pipeline diagnosis:

```bash
# Record five seconds, report levels, and write ./mic-check.wav
dotnet run --project tools/Kanal.Doctor -- mic 5

# With GLADIA_API_KEY set, stream a WAV and print raw + normalized events
dotnet run --project tools/Kanal.Doctor -- gladia mic-check.wav
```

## Contributing

Issues and focused pull requests are welcome. Before changing behavior, read
[`CLAUDE.md`](CLAUDE.md) for repository invariants and [`.impeccable.md`](.impeccable.md) for the
interaction and visual constraints.

- Keep one concern per pull request and include tests for behavior changes.
- Update [`docs/PROGRESS.md`](docs/PROGRESS.md) with relevant plans or design decisions.
- Preserve the provider-capability boundary: orchestration must not branch on vendors.
- Keep `web/index.html` and `docs/index.html` byte-identical.
- Do not weaken the text-only relay boundary or the non-destructive speaker-merge model.

Open a [GitHub issue](https://github.com/TONiiV/Kanal/issues) for a bug, proposal, or deployment
question before starting a broad change.

## Current limitations and roadmap

- Live microphone capture is implemented for Windows and macOS, not Linux.
- Local transcription is not implemented, so a fully local live pipeline is not available.
- There is no standalone cloud text-translation provider, which also blocks local-to-cloud mode.
- The host is intentionally limited to four selected languages; each phone displays one at a time.
- Mobile delivery depends on the authenticated gateway being reachable. A different relay can be
  added behind `IRelayPublisher`, but no alternative ships today. The default `workers.dev`
  hostname is blocked in mainland China; participants whose phones roam through a Chinese
  carrier need the gateway on a custom domain.
- Cloud transcription sends room audio to Gladia. Local WAV recording is enabled by default for
  live microphone modes and must be disclosed to participants.
- Chinese↔Polish terminology quality with real meeting material remains the primary go/no-go
  validation gate.
- Packaged installers and signed releases are not available yet.

Detailed status, benchmarks, and the next implementation steps are tracked in
[`docs/PROGRESS.md`](docs/PROGRESS.md), rather than duplicated here.

## License

Kanal is released under the [MIT License](LICENSE). Downloaded translation models and external
services have their own licences and terms; the model catalog displays the relevant licence for
each model, including a warning where it is not OSI-approved.
