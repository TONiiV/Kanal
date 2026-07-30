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

Start in **Demo (scripted)** mode — no keys needed; a fake trilingual meeting flows through the real orchestrator (ASR without translation → `IMtProvider` routing). Switch to **Gladia (live)** with an API key to stream the microphone (Windows capture only for now).

```bash
dotnet test
```

## Architecture invariants

- **The host is the single authority.** Clients are projections; late join and reconnect are served by `room.snapshot`.
- **Capability-driven orchestration, no vendor branching.** The orchestrator's only decision: `if (!asr.Caps.Translation)` route finals through `IMtProvider`.
- **The relay is a replaceable layer** (`IRelayPublisher`). Hosted pub/sub first; a tunnel or domestic service is one implementation swap.
- **Only text crosses the public network.** (M0 caveat: Gladia receives audio; the strict audio boundary arrives with local models in M2.)
- **Merges are non-destructive.** Utterances keep their original diarization tag; clients resolve via `Speaker.MergedFrom` — history rewrites are a render-time concern.

## Status vs. PRD milestones

- [x] Solution skeleton, provider contracts, RoomState + orchestrator (+tests)
- [x] Audio pipeline: resampler, WAV replay, WASAPI capture (Windows)
- [x] GladiaAsrProvider (wire format **needs live verification during D0-B** — adjust `GladiaWire`/`GladiaOptions.ExtraConfig`, nothing else)
- [x] Host UI: 4 columns, rename/merge, demo mode, md export
- [x] Mobile web client skeleton with demo transport
- [ ] D0-A: **macOS** audio capture backend (`IAudioCaptureService` impl)
- [ ] D0-B: zh↔pl terminology quality check with real part numbers — **go/no-go gate**
- [ ] M0-D7: relay publisher implementation (Ably C# SDK) + QR code in host
- [ ] M0-D10: code-switch degradation experiment, rehearsal

## Open decisions / risks

Tracked in [PRD §07](docs/PRD-v0.3.md#07--风险). The two unverified killers: zh↔pl terminology quality (D0-B) and cross-platform audio capture (D0-A, macOS side).
