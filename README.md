# Kanal / 频道

Internal meeting-translation tool: an Avalonia desktop host captures room audio, streams it through a pluggable ASR/MT provider chain, and broadcasts **text only** to read-only mobile clients. Built for one real scenario: a zh/de/pl meeting with no shared language, replacing human double translation (zh→de→pl).

See [docs/PRD-v0.3.md](docs/PRD-v0.3.md) for the full requirements (Chinese).

## Layout

| Project | Purpose |
|---|---|
| `src/Kanal.Core` | Provider contracts (`IAsrProvider`, `IMtProvider`, `AsrCapabilities`), domain records (`Utterance`, `Speaker`), relay wire protocol (`RelayJson`), authoritative `RoomState`, and the `MeetingSession` orchestrator. Also `FakeAsrProvider`/`FakeMtProvider` for keyless demo and tests. |
| `src/Kanal.Audio` | `IAudioCaptureService` (16 kHz mono PCM16 frames), stateful `LinearResampler` (pure C#, cross-platform), WAV read/write, `WavFileAudioSource` replay, `WasapiAudioCapture` (Windows). |
| `src/Kanal.Providers.Gladia` | Gladia live v2 client: REST session init + `ClientWebSocket` streaming, reconnect with backoff, lenient wire parsing in `GladiaWire`. |
| `src/Kanal.Host` | Avalonia desktop app: up to 4 language columns, partial-gray/final-black bubbles, speaker rename & merge, markdown export. Demo mode runs without any API key. |
| `tests/Kanal.Tests` | xUnit: resampler chunk-equivalence, RoomState upsert/merge/stale-translation semantics, orchestrator capability routing, relay JSON round-trips, Gladia wire parsing. |
| `web/` | Read-only mobile client (single static HTML file). `?demo=1` for a scripted preview; Ably transport wired for `?room=<id>&key=<key>` once the host-side publisher lands (M0-D7). |

## Run

```bash
dotnet run --project src/Kanal.Host
```

Start in **Demo (scripted)** mode — no keys needed; a fake trilingual meeting flows through the real orchestrator (ASR without translation → `IMtProvider` routing). Switch to **Gladia (live)** to stream the microphone (Windows capture only for now).

**API keys**: manage multiple named Gladia keys in ⚙ Settings (stored in `%APPDATA%/Kanal/settings.json`, one active at a time); the `GLADIA_API_KEY` env var (any scope) is the fallback when no stored key exists.

**Mobile clients**: on Start, the host publishes every room message to Supabase Realtime (broadcast channel = room id) and shows a **join QR code** encoding the mobile page URL with room + connection params. A snapshot is republished every 15 s, so late joiners and reconnecting phones recover the backlog. Endpoint overrides: `KANAL_SUPABASE_URL`, `KANAL_SUPABASE_ANON_KEY`, `KANAL_WEB_URL`.

```bash
dotnet test
```

**Pipeline diagnostics** (`tools/Kanal.Doctor`): `dotnet run --project tools/Kanal.Doctor -- mic 5` records five seconds and reports levels (writes `mic-check.wav`); `-- gladia <wav>` streams a WAV to a live Gladia session and dumps raw + normalized messages — use it whenever "nothing shows up" and you need to know which leg is broken.

## Architecture invariants

- **The host is the single authority.** Clients are projections; late join and reconnect are served by `room.snapshot`.
- **Capability-driven orchestration, no vendor branching.** The orchestrator's only decision: `if (!asr.Caps.Translation)` route finals through `IMtProvider`.
- **The relay is a replaceable layer** (`IRelayPublisher`). Hosted pub/sub first; a tunnel or domestic service is one implementation swap.
- **Only text crosses the public network.** (M0 caveat: Gladia receives audio; the strict audio boundary arrives with local models in M2.)
- **Merges are non-destructive.** Utterances keep their original diarization tag; clients resolve via `Speaker.MergedFrom` — history rewrites are a render-time concern.

## Status vs. PRD milestones

- [x] Solution skeleton, provider contracts, RoomState + orchestrator (+tests)
- [x] Audio pipeline: resampler, WAV replay, WASAPI capture (Windows), CoreAudio capture (macOS)
- [x] GladiaAsrProvider (wire format **needs live verification during D0-B** — adjust `GladiaWire`/`GladiaOptions.ExtraConfig`, nothing else)
- [x] Host UI: 4 columns (language chips), rename/merge (✓ or Enter; covered by headless UI tests), demo mode, md export, settings dialog for API keys
- [x] M0-D7: relay publisher (`SupabaseRelayPublisher`, REST broadcast — verified end to end) + join QR code in host + periodic snapshot for late join
- [x] Mobile web client: Supabase transport + demo mode (`web/index.html`, copy in `docs/` for GitHub Pages)
- [ ] Hosting for `web/index.html` — pending: enable GitHub Pages (repo Settings → Pages → main `/docs`) or grant the Vercel integration project-create permission
- [x] D0-A: **macOS** audio capture backend (`CoreAudioCapture`, AudioQueue — verified end to end with `doctor mic`)
- [ ] D0-B: zh↔pl terminology quality check with real part numbers — **go/no-go gate**
- [ ] M0-D10: code-switch degradation experiment, rehearsal
- [ ] M2: local model providers — NVIDIA **Nemotron** streaming ASR + Sortformer diarization + Qwen MT via Python sidecar (`NemotronAsrProvider` slot already in the caps table)

## Relay notes

The shared Supabase project `muwffgozlmjafsoykqfr` (eu-central-1) carries broadcast-only channels named `kanal-*`; nothing is written to its database. The anon key is public by design (it ships in every join QR). Moving to a dedicated Supabase project = changing `KANAL_SUPABASE_URL`/`KANAL_SUPABASE_ANON_KEY`.

## Open decisions / risks

Tracked in [PRD §07](docs/PRD-v0.3.md#07--风险). The remaining unverified killer is zh↔pl terminology quality (D0-B). Cross-platform capture (D0-A) is closed: macOS runs on AudioQueue with no third-party native dependency, so the PortAudio / OpenAL / SoundFlow fallbacks the PRD listed are moot.
