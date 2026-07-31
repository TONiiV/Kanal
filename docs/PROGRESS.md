# Kanal — progress, plan & design changes

Living log. Update in the same PR as the work it describes. Newest section on top.

---

## 2026-07-31

### Findings

**Gladia capability boundary.** One live-v2 WebSocket session transcribes *and* translates to
multiple target languages simultaneously (`realtime_processing.translation_config.target_languages`);
zh/de/pl are all supported for both. Kanal already uses this. Boundaries: translations for multiple
targets are processed **sequentially** (latency accumulates per language; keep the set small);
translations arrive only for **finals** — partials never carry them; code-switching needs the
expected-language list, never an empty one. Transcription latency ≈300 ms partial / ≈600 ms final.

**Local-model direction.** The only local ASR family covering zh+de+pl is **Whisper**
(whisper.cpp / faster-whisper, MIT). Every 2025/26 true-streaming model misses a leg: Voxtral
Realtime has no Polish, kyutai only en/fr, NVIDIA Canary/Parakeet no Chinese. MT licence
minefield: NLLB-200, SeamlessM4T, Tower+ are CC-BY-NC (no commercial use, internal tools
included); Hunyuan-MT excludes the EU — unusable with a German participant. Clean path:
**Qwen3.5-4B** (Apache 2.0).

**Measured on Apple M4 (24 GB):**

| What | Result |
|---|---|
| whisper.cpp large-v3-turbo, 84 s Polish audio | 6.5 s (≈13× real-time); de/pl near-perfect, zh readable with homophone slips + sparse punctuation |
| Qwen3-4B translate, per target language (ollama) | avg 0.85 s |
| Qwen3.5-4B, same cases (ollama, think off) | avg 1.41 s — slower but visibly better terminology (支架→wsporników correct) |
| Qwen3.5-4B on MLX (same 4-bit quant) | avg 1.10 s — ≈22 % faster than ollama, macOS-only |
| Qwen3-1.7B | avg 0.59 s but **unusable**: leaks Chinese characters into Polish output |
| One final → two target languages, sequential | avg 1.7 s (1.1–2.6 s) |

Perceived cross-language delay ≈2–3.5 s after the speaker stops (endpointing + final + MT).
The structural bottleneck is "translate only on final", not MT speed.

No audio ever touches disk locally: capture backends stream PCM in memory (`PushAudioAsync`);
the only deliberate file write is `Kanal.Doctor mic`'s `mic-check.wav` diagnostic.

### Fixes

- **Multi-room isolation.** Two hosts starting in the same second used to land on the same
  broadcast channel (room id was `kanal-HHmmss`); ids now carry a random 4-char suffix
  (`RoomIds.New`, e.g. `kanal-093005-x7kq`). The mobile page's localStorage cache is now keyed
  per room, so a phone joining meeting B no longer opens on meeting A's history; other rooms'
  caches are pruned on load. Concurrent meetings were otherwise already independent — one
  Supabase channel per room, stateless static page.

- **Room lifecycle is visible to clients.** Stop and restart were silent on the wire: a phone
  held the channel it scanned into, so after Stop it sat on a dead room still looking connected,
  and after a restart (new room id → new channel) it was stranded until someone rescanned the QR.
  Two new wire messages close that: `room.closed` (transcript stays readable, page stops
  presenting itself as live, survives reload via the cache) and `room.moved` carrying the new
  room id, published on the **old** channel so already-joined phones re-subscribe themselves,
  rewrite their URL and cache key, and drop the previous meeting's records. A fresh room id per
  Start stays deliberate — ASR utterance ids restart at zero, so reusing a channel would let a
  new meeting overwrite the old one's records by id.

### Design changes

1. **Column rendering rule** (PR #2): each language column carries *only* its own language.
   Source column shows the transcript tagged **· ORIGINAL** (mobile localises: 原文 / Original /
   oryginał); other columns show a muted ellipsis until their translation lands — never the raw
   source text. `FakeMtProvider` now ships real translations for the demo script.
2. **Flag-disc language picker** (PR #3): toggle chips + free-text extras replaced by an
   overlapping stack of circular vector flags (custom `FlagIcon`, no emoji/bitmaps) that opens a
   modal catalog with an add-by-ISO-code row. *Deliberate deviation* from ".impeccable.md — the
   only colour on screen is people": flags are confined to the masthead tool area and the ISO
   codes are always printed beside them, so colour never carries meaning alone.
3. **TDD + PR discipline** written into `CLAUDE.md` (this PR).

### Plan

- [x] Gladia capability research
- [x] Local ASR/MT feasibility research + benchmarks
- [x] Column rendering fix — PR #2
- [x] Flag language picker — PR #3
- [ ] **Local translation LLM support** — `LLamaSharpMtProvider` (in-process llama.cpp, no
      ollama/Python dependency) + Settings section to download/select a translation model
      (catalog: Qwen3.5-4B first, plus alternatives of similar size, A/B-tested); TDD; own PR.
- [ ] Local ASR (`WhisperCppAsrProvider` via Whisper.net, VAD + LocalAgreement streaming) — after MT.
- [ ] Measure Gladia translation latency precisely once an API key is configured
      (`Kanal.Doctor -- gladia <wav>` dumps timestamped raw JSON).
- [ ] Streaming/segmented translation to cut the 2–3.5 s cross-language delay.

### In flight (other sessions)

- macOS audio capture (`feat/macos-audio-capture`): CoreAudio backend, `AudioCaptureFactory`,
  cross-platform mic list.
