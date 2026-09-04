# ADR 0050: Native microphone and system-audio capture for online meetings

**Status:** Accepted  
**Date:** 2026-09-04  
**Issue:** [#50](https://github.com/TONiiV/Kanal/issues/50)

## Context

Kanal currently opens one recording endpoint and sends one 16 kHz mono PCM stream to the speech
pipeline. That is sufficient when everybody is in the room, but it misses anybody heard through
Teams, Slack, Zoom, a browser, or another calling application.

Issue #50 proposed treating BlackHole on macOS and VB-Cable on Windows as ordinary recording
devices. That gets computer playback into the existing device list, but it does not produce a
complete online-meeting stream:

- selecting the virtual device replaces the microphone, so the local speaker disappears;
- the operator must route the calling application into the virtual device and separately monitor
  that device to hear the call;
- installation requires a third-party driver and, on Windows, administrator access and a reboot;
- the routing survives outside Kanal and can leave the machine silent or sending audio to the
  wrong destination after the meeting.

Notion's desktop behaviour establishes the product shape, not an implementation dependency: the
desktop app captures the microphone and computer audio together, while its browser surface can
only capture the microphone. Kanal can provide the same app-independent behaviour through the
operating systems' native audio facilities. Teams and Slack do not need to know that Kanal exists.

## Decision

### Capture profiles are independent of the speech pipeline

The host has two capture profiles:

- **In the room** opens the selected microphone, as today.
- **Online meeting** opens the selected microphone and the selected computer-output source.

Capture profile is not a new `PipelineMode`: cloud/local transcription and translation remain
orthogonal. `MeetingSession` continues to receive one contiguous 16 kHz mono PCM16 stream and does
not learn about operating systems, devices, or calling applications.

The online profile requires headphones. Acoustic echo cancellation remains out of scope: without
headphones, remote speech reaches the pipeline once through computer audio and again through the
microphone.

### Native system-audio adapters are the primary path

- **Windows:** WASAPI shared-mode loopback on the selected render endpoint. The existing NAudio
  dependency already contains `WasapiLoopbackCapture`. The adapter normalizes the endpoint's mix
  format to 16 kHz mono PCM16 using the same conversion path as microphone capture.
- **macOS 14.2 and later:** a Core Audio process tap configured as a private global stereo tap,
  carried by a private aggregate device and consumed through an IOProc. The adapter requests
  system-audio recording permission with `NSAudioCaptureUsageDescription`, downmixes and resamples
  the tap format, and destroys the IOProc, aggregate device, and tap in dependency order.
- **macOS 13 through 14.1:** ScreenCaptureKit captures computer audio under the Screen & System
  Audio Recording permission. It is an explicit compatibility adapter behind the same seam; the
  host never captures or retains video frames. macOS before 13 retains in-room microphone capture
  and shows online capture as unavailable with the required version.

BlackHole and VB-Cable remain troubleshooting fallbacks that can be selected as microphone-like
inputs; Kanal does not install, configure, or depend on them.

The first implementation captures the complete selected output mix. Per-process capture is
deferred: it would require tracking the process trees of Teams, Slack, and browsers, and it would
still need a global-output fallback. The UI warns the operator to enable Do Not Disturb because
notification sounds are part of the selected output mix.

### One deep capture module owns synchronisation and mixing

The public capture interface stays small. Callers select a profile and stable device identifiers;
the platform adapter owns device opening, format conversion, buffering, clock alignment, mixing,
and teardown.

For an online meeting the module:

1. normalizes microphone and system audio independently to 16 kHz mono PCM16;
2. places both sources into bounded buffers;
3. emits fixed-duration frames on one timeline, padding a late or silent source with silence;
4. mixes with headroom and saturation so two loud sources cannot wrap or clip catastrophically;
5. fails the online stream if either selected device disappears instead of silently switching to
   another source.

Pause and Stop act on the combined stream, preserving the existing invariant that paused audio
reaches neither ASR nor the optional WAV recorder.

### Disclosure is a prerequisite, not a later polish pass

Online capture does not create a Teams or Slack recording indicator. Before Start, the operator
must explicitly confirm that every participant has consented to the meeting being transcribed and
translated. The gate reminds the operator that remote participants cannot see Kanal and that the
operator must announce the processing verbally or in the meeting chat. The confirmation time and
capture profile are included in exports. This is an operator attestation, not proof that every
participant actually consented.

The permanent room notice is keyed to a live transcription session, not to WAV recording, and is
shown both in the host masthead and on the phone page. When audio recording is also enabled, the
stronger recording notice replaces it. The two copies of the phone page remain byte-identical.
Online meetings default WAV recording to off.

## Implementation plan

Each slice is a separate pull request and leaves the full test suite green.

### 1. Capture profile and safe host flow

- Add the in-room/online profile to host state without coupling it to `PipelineMode`.
- Let the operator select a microphone and, for online meetings, a computer output.
- Show the headphone and Do Not Disturb warnings.
- Gate Start on the all-participant consent confirmation, including the remote-participant
  verbal/chat reminder; publish a live-transcription notice in the host masthead and phone page;
  and carry the attestation into Markdown/JSON exports.
- Default WAV recording off for online meetings while retaining the current in-room default.

### 2. Native system-audio adapters

- Add Windows render-endpoint enumeration and WASAPI loopback capture.
- Add the macOS 14.2 Core Audio process-tap lifecycle, permission description, and output-device
  change notifications, plus the macOS 13 ScreenCaptureKit compatibility adapter.
- Keep platform interop behind the existing audio seam; headless tests use fakes and never request
  real permissions.

### 3. Dual-source synchronisation and mixing

- Start the microphone and system source as one operation.
- Align fixed-duration frames, pad silence, apply headroom plus saturation, and bound both queues.
- Expose separate microphone/computer-audio meters before and during Start.
- Cancel and dispose both sources after a partial-start failure, Pause, Stop, or device loss.
- Add deterministic tests for one-sided silence, unequal callback sizes, clipping, cancellation,
  partial-start cleanup, and a disappearing source.

### Later: preserve source channels

Once the mono path has been rehearsed, carry channel count through `AsrSessionOptions`, WAV
recording, and providers. Gladia can then receive microphone and computer audio as two interleaved
channels and label utterances as local or remote. This is deliberately outside slices 1–3 because
it changes the provider contract, recording format, billing, and local-ASR fallback together.

## Verification

Slices 1–3 are complete when all of the following hold:

- a headphone call in Teams desktop, Slack desktop, and one browser call transcribes both a local
  sentence and a remote sentence;
- selecting the wrong computer output produces an actionable silent-source state;
- changing or unplugging either active device stops capture visibly instead of switching devices;
- denying or revoking macOS system-audio permission produces an actionable error and no crash;
- the host masthead and both byte-identical phone pages disclose live transcription even when WAV
  recording is off;
- Start remains disabled until the operator attests that all participants consented and has been
  reminded to announce processing verbally or in the remote meeting chat;
- Pause sends and records no microphone or computer audio;
- stopping during partial startup releases every acquired native object;
- `dotnet test` and the repository CI checks pass on every pull request.

## Consequences

- Online capture works below the conferencing-application layer and therefore needs no bot,
  extension, tenant registration, or conferencing vendor branch.
- Windows implementation is small because the dependency and PCM conversion already exist.
- macOS implementation is the highest-risk slice: permission state and three dependent HAL objects
  need explicit lifecycle tests and a manual signed-bundle rehearsal.
- The mono first release cannot distinguish individual remote participants and may also merge the
  local and remote sides. The later channel-preservation slice addresses side attribution, not
  per-person identity.
- Capturing the whole output mix includes notification sounds. Per-process capture can be added
  behind the same interface if rehearsal shows Do Not Disturb is insufficient.

## Alternatives rejected

| Alternative | Reason |
|---|---|
| BlackHole/VB-Cable as the primary path | Driver installation and fragile routing still fail to capture the local microphone without another input/mix path. |
| ScreenCaptureKit for every supported macOS version | Core Audio taps request the narrower system-audio permission and avoid screen capture on 14.2+; ScreenCaptureKit remains only for macOS 13 compatibility. |
| Teams or Slack bot | Adds vendor registration, tenant approval, and separate integrations for a problem the operating system can solve once. |
| Per-process capture in the first release | Process discovery and restart handling add application-specific failure modes before the basic dual-source path is proven. |
| Acoustic echo cancellation | Headphones solve the known internal-tool setup; AEC is a separate signal-processing project. |
