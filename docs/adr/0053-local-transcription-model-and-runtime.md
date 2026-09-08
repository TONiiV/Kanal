# ADR 0053: Nemotron 3.5 ASR Streaming on sherpa-onnx for local transcription

**Status:** Proposed  
**Date:** 2026-09-08  
**Issues:** [#72](https://github.com/TONiiV/Kanal/issues/72), [#49](https://github.com/TONiiV/Kanal/issues/49)

## Context

`StageKind.Local` transcription is offered in the mode list and always resolves to
`reason.localasr` — "not built yet". Everything downstream already exists: `LocalModelCatalog`,
`ModelDownloadManager` and the Settings *Translation* section are a working pattern for "a model is
data the operator picks and downloads", and the orchestrator's only decision
(`if (!asr.Caps.Translation)` → route finals through `IMtProvider`) needs no change for a local ASR
that does not translate.

What was missing was a model and a way to run it in .NET.

[#49](https://github.com/TONiiV/Kanal/issues/49) proposed Whisper `large-v3-turbo`, on the July 2026
finding that Whisper was the only family covering zh + de + pl. That finding carried a large
consequence: Whisper is a batch model, so #49 committed Kanal to building its own streaming layer —
VAD segmentation plus LocalAgreement-2 over a re-decoded window — because the model could not
stream itself.

The user has since selected **Nemotron 3.5 ASR Streaming 0.6B**. #72 required that preference be
re-verified rather than inherited. It was, against primary sources, on 2026-09-08.

## Findings

All from `nvidia/nemotron-3.5-asr-streaming-0.6b` (model card, `processor_config.json`, HF API) and
the `k2-fsa/sherpa-onnx` repository.

**The three legs are covered.** The model's own prompt dictionary carries `de` → 9, `pl` → 17,
`zh-CN` → 4, and `auto` → 101 for language-ID-free decoding. Note that bare `zh` is *absent*: the
room's language codes are ISO-639-1, so the provider owes a `zh` → `zh-CN` mapping rather than a
lookup that silently falls through to auto.

**The input format is already what the capture layer produces.** `sampling_rate` 16000, mono,
128-bin mel, 400-sample window, 160-sample hop. `PcmConvert.Float32ToMonoPcm16` feeds it directly.

**It streams natively, so Kanal does not have to.** Cache-aware FastConformer-RNNT: encoder
self-attention and convolution caches are carried across chunks, so frames are strictly
non-overlapping and there is no buffered re-decode. Chunk size is a runtime choice among 80, 160,
320, 560 and 1120 ms — a latency/accuracy dial, no retraining.

**Licence is OpenMDW-1.1** (`https://openmdw.ai/license/1-1/`), not Apache-2.0 and not OSI-listed.
It is permissive in intent, but it is the Gemma case, not the Qwen case, and needs a review note in
the catalog beside it.

**A .NET runtime exists and is first-party.** sherpa-onnx added multilingual Nemotron 3.5 streaming
support upstream (`Add multilingual Nemotron-3.5 streaming ASR support`, k2-fsa/sherpa-onnx #3671),
ships the export script in-repo (`scripts/nemo/nemotron-3.5-asr-streaming-0.6b/export_onnx.py`), and
publishes .NET bindings as `org.k2fsa.sherpa.onnx` on NuGet — 144 versions, 1.13.5 current. Language
is set per stream as a string, and `auto` is accepted.

**The alternatives do not reach .NET.** The official repo's other artefacts are `.nemo` (NeMo, i.e.
PyTorch) and a `q8_0.gguf` built for `transcribe.cpp`, the Rust/C++ runtime Handy uses, which has no
.NET binding. `onnx-community`'s int4 export is ONNX Runtime GenAI layout, a different runtime whose
.NET speech story is unproven, and its licence metadata (`mit`, `nvidia-open-model-license`)
disagrees with the base repository's `openmdw-1.1`. CoreML and MLX exports are macOS-only; the
target machine is Windows with an NVIDIA GPU.

**A transcription model is four files, not one.** Each sherpa-onnx package is `encoder`, `decoder`,
`joiner` and `tokens.txt` — for the 560 ms int8 package, 657 MB + 15 MB + 9.5 MB + 131 KB ≈ 682 MB.
The translation catalog's one-GGUF-per-model assumption does not carry over.

## Decision

**1. The local ASR model is Nemotron 3.5 ASR Streaming 0.6B, run through sherpa-onnx.** In-process,
like `LlamaSharpMtProvider`, for the same reason: an operator mid-meeting cannot debug a second
process. No sidecar, no Python, no ollama.

**2. #49's streaming layer is not built.** VAD segmentation and LocalAgreement-2 were scaffolding
for a batch model. This model emits partial and final hypotheses itself; writing an agreement layer
on top of a cache-aware transducer would add latency and a second source of truth about what was
said. Silero VAD may still earn its place later as an endpointing/cost control, but that is a
separate decision with its own evidence, not part of this one.

**3. Transcription models become a catalog in Settings, mirroring translation models.** Same
operator-visible shape: pick, download on demand, run in process, no vendor branch anywhere.

**4. A catalog record owns a list of parts.** `IDownloadableModel` — file name, URL, size, SHA-256 —
describes one file; the record holds the four and readiness is "every part present and verified".
Progress is the sum. `ModelDownloadManager` is shared, not duplicated: it moved to
`Kanal.Core.Models` and retargeted to the interface.

**5. Readiness is a state the UI acts on**, not a guess: not downloaded, downloading, ready, failed.
A model that is not ready never silently changes what the meeting does — the `PipelinePlanner`
"chosen but not downloaded" reason that already exists for MT is reused.

**6. Translation stays post-processing, explicitly.** `Caps.Translation` is `false` and finals flow
to whatever `IMtProvider` the mode resolved. This is the existing capability route; the orchestrator
does not change. The rest of `Caps` is
`Streaming: true`, `Diarization: false`, `AutoLanguageDetect: true`, `Latency: Near`, and a
`Languages` set taken from the model's own prompt dictionary rather than hand-written.

**7. Chunk size is a setting with a default, not a constant.** 560 ms is the starting point — the
accuracy/latency knee on the model card's own FLEURS curve — and it is recorded here so the number
that ships can be argued with rather than discovered in a diff.

## Consequences

- `local · local` and `local · cloud` stop being roadmap rows, and the mode row's privacy line
  ("nothing leaves this machine") becomes true.
- **No local diarization.** `Caps.Diarization: false`; every utterance carries one speaker tag. The
  rename/merge UI stays and has little to work on. This is [#13](https://github.com/TONiiV/Kanal/issues/13)'s
  territory and a separate model.
- **Two models resident at once** — ≈0.7 GB ASR plus ≈2.7 GB MT. Warm-up order and a stated RAM
  floor in Settings matter.
- The OpenMDW-1.1 licence needs a review note in the catalog and a look before any redistribution.
- A second native dependency (`org.k2fsa.sherpa.onnx`) joins LLamaSharp, with the same
  per-platform-runtime packaging question. Windows + NVIDIA is verified first; CPU and Apple Silicon
  are investigated and their status recorded rather than assumed.
- Language conditioning is per stream. A room whose source language is set explicitly should pass
  it; a room that is not should pass `auto`, which the model supports and which matches the
  unconstrained-recognition decision already taken for Gladia.
- **Testability.** Catalog, readiness and part accounting are pure logic over fakes. Real
  transcription quality, latency and resource use are measured on hardware and recorded in
  `docs/PROGRESS.md` beside the existing ASR/MT benchmarks. CI never downloads a model.

## Implementation slices

One PR each, TDD, suite green at every step.

1. `IDownloadableModel` + `ModelDownloadManager` retarget — pure refactor. **Done, PR #88.**
2. `AsrModelCatalog` (multi-part records, verified sizes and hashes) + `ActiveTranscriptionModelId`
   + readiness over the shared downloader.
3. Settings *Transcription* section — download, progress, select, delete — reusing the translation
   section's controls.
4. `NemotronAsrProvider` over `org.k2fsa.sherpa.onnx`: `IAsrSession`, `IWarmupProvider`, per-stream
   language including the `zh` → `zh-CN` mapping, partial and final emission.
5. `PipelinePlanner`: `StageKind.Local` transcription resolves to the provider; `reason.localasr`
   goes; the unavailable reasons are reused.

## Alternatives rejected

| Alternative | Reason |
|---|---|
| Whisper `large-v3-turbo` (#49's proposal) | Batch model; forces Kanal to own VAD + LocalAgreement-2. Superseded by a natively streaming model that covers the same three languages. |
| `transcribe.cpp` GGUF (Handy's runtime) | No .NET binding. A sidecar or a P/Invoke layer against a young Rust project, to reach the same weights sherpa-onnx already serves. |
| ONNX Runtime GenAI int4 export | Different runtime, unproven .NET speech path, and licence metadata that disagrees with the base model. |
| CoreML / MLX exports | macOS only. The target machine is Windows with an NVIDIA GPU. |
| Parakeet v3 (Handy's CPU default) | 25 European languages, **no Chinese**. Fails the actual meeting. |
| SenseVoice / streaming Zipformer | zh/en/ja/ko/yue — no German *and* Polish in one model. |
| Whisper's own `translate` task instead of `IMtProvider` | English-only output, and it re-introduces the vendor branching the capability model removed. |
| A general model-plugin system (`IModelCatalog`, manifests, discovery) | Two catalogs and one shared downloader is the whole requirement. |
