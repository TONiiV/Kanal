# Online meeting audio — diagnosis and manual acceptance

How to check, diagnose, and accept the **Online meeting** capture profile: the selected microphone and
all computer audio, mixed into the single 16 kHz mono stream the speech pipeline receives
([ADR 0050](adr/0050-native-online-meeting-audio.md)).

## Before a meeting

1. Wear headphones. Kanal has no echo cancellation: without headphones every remote sentence reaches
   the pipeline twice, once through the computer audio and once through the microphone.
2. Turn on Do Not Disturb. Kanal captures **all** computer audio, whichever speaker, headset or
   Bluetooth device it plays on, including notification sounds, other browser tabs, and media
   players. There is no output to choose: the meeting app can play to headphones, the built-in
   speakers or any other device and Kanal hears it. Kanal itself plays nothing.
3. In the toolbar's microphone menu, pick the microphone. Start the meeting. Speak, and let the
   meeting app play speech. Both meters in the microphone menu must move.
4. Muting yourself in Teams, Slack, Meet, or Tencent Meeting does **not** mute Kanal's microphone.
   Use Pause in Kanal for private moments; pause sends nothing to transcription and records nothing.

## Kanal.Doctor

Run from the repository root. `system` and `online` are local: they write no file and make no network
call. `mic` writes `mic-check.wav` to the current folder; `gladia` sends a WAV to Gladia.

```bash
dotnet run --project tools/Kanal.Doctor -c Release -- devices
dotnet run --project tools/Kanal.Doctor -c Release -- system 10
dotnet run --project tools/Kanal.Doctor -c Release -- online 10 0
```

- `devices` lists microphones with an index and an id, then the platform's computer-audio support
  (`IsAvailable`, `Backend`, `Reason`).
- `system <seconds>` captures only the computer audio.
- `online <seconds> [microphoneIndex]` runs the same mixer the host uses and prints its diagnostics:
  every event, plus one `levels` line per source every 5 seconds.

Each run ends with one `summary` line per source and a verdict.

| Exit | Meaning |
|---|---|
| 0 | Every source carried sound (`OK:`). |
| 2 | A fault, a bad argument (`code: usage`), or a device index that is not listed (`device_unavailable`). The `fault` line on stderr names the code and source. |
| 3 | A source was silent. A `SILENT microphone:` or `SILENT system:` line names it. |

## Host log

Logs are local and are never sent anywhere. **Open log folder** in the **Log files** section of
Settings opens the right place; the folder is `Kanal/logs` under the application-data directory:

| OS | Folder |
|---|---|
| Windows | `%APPDATA%\Kanal\logs\kanal-<yyyy-MM-dd>.log` |
| macOS | `~/.config/Kanal/logs/kanal-<yyyy-MM-dd>.log` (.NET's `ApplicationData` folder) |

Capture lines use the `audio` category. Audio samples, transcript text, and API keys are never logged.

- `Room <id> open: mode …, capture online|in-room, …` — the profile the meeting actually used.
- A JSON line per mixer event: `starting`, `first_frame`, `fault`, `stopped` at Info, and `levels` at
  Debug, at most once per source every 5 seconds.
- `signal source=<microphone|system> state=<state>` at Info, once per change.
- `capture_fault code=<code> source=<source> mode=<online|in-room>` at Error, with the
  exception attached. An in-room line adds `the room is live with no audio arriving`.
- `capture_fault code=device_unavailable source=microphone reason=device_list_changed` at Warning, when
  the microphone the meeting is using disappears from the device list. Computer audio is not tied to a
  device, so its loss only ever shows up as a capture fault.
- `Capture running on …` and `<n> frames captured.` at Debug.

Fields of the JSON lines:

| Field | Meaning |
|---|---|
| `Session` | Random id of one capture run; joins the lines of one Start or Resume. |
| `Event` | `starting`, `first_frame`, `levels`, `fault`, `stopped`. |
| `Source` | `microphone`, `system`, or `mixer` for faults that belong to neither source. |
| `Device` | Microphone lines: first 12 hex characters of the SHA-256 of the device id; `default` when none was chosen. Matches across lines without exposing the device name. Empty for `system` and `mixer`. |
| `ReceivedSamples` | Samples delivered by the source since it opened (16 000 per second when it is healthy). |
| `DroppedSamples` | Samples discarded because the source ran more than 500 ms ahead of the host clock (its device clock is fast) or would have overwritten audio not yet mixed. Late audio is never dropped; it is placed after what was already mixed. |
| `PaddedSamples` | Silence inserted because the source had nothing for a slot. Grows steadily for a loopback that delivers nothing while nothing plays. |
| `Peak`, `Rms` | Level since the previous `levels` line, 0–1 of full scale. |
| `Error` | The fault code on a `fault` line. |

## Fault codes

The host stops an online meeting on any fault and shows the localized reason in the status bar; an
in-room meeting stays live and says capture stopped. The same code reaches the log and Doctor.

| Code | Source | What happened | What to do |
|---|---|---|---|
| `permission_denied` | microphone / system | The OS refused access. | Windows: Settings → Privacy & security → Microphone → allow desktop apps. macOS: System Settings → Privacy & Security → Microphone, and Screen & System Audio Recording (the system-audio list where the OS version shows one), for Kanal; quit and reopen Kanal after changing it. |
| `device_unavailable` | microphone / system | The device was unplugged, disabled, switched, or is held exclusively by another app; or a Doctor index is not listed. | Re-plug or re-enable it, run `devices`, pick it again in the microphone menu. Turn off exclusive mode in the device's Windows sound properties. |
| `source_ended` | microphone / system | The source stopped delivering without an error (driver reset, Bluetooth profile switch). | Pick the device again; for Bluetooth, see below. |
| `source_failed` | microphone / system / mixer | Any other platform error; the message carries the platform's text. | Read the attached exception in the log; retry with `system` or `online` in Doctor. |
| `clock_stalled` | mixer | The mixer fell more than 2 s behind the clock: the computer slept or was saturated. | Keep the laptop awake and on power; close heavy apps; start again. |
| `consumer_stalled` | mixer | The transcription connection accepted no audio for 60 s. Shorter stalls are buffered and delivered late, never dropped. | Check the network and the transcription provider; start again. |

A failed push to transcription is not a capture fault. It is logged under the `asr` category as
`transcription_push_failed source=asr mode=<mode>` (once per streak, exception attached) and
`transcription_push_recovered dropped_frames=<n>`; the status bar says *Audio is not reaching
transcription* until a push succeeds again, the room stays live, and frames are dropped until a push succeeds.

## Silent source hints

A hint appears in the status bar and under the meters during a meeting:

- *No microphone sound detected* — check the selected microphone, its hardware mute switch, and the
  microphone permission.
- *No computer sound detected* — the meeting app is muted or its volume is zero, or the capture
  permission is missing.
- *No microphone sound for a while* / *No computer sound for a while* — a source that was heard has
  been quiet for 2 minutes. Informational; often one side is simply presenting.

The state behind the hint, also logged as `signal … state=`:

| State | Meaning |
|---|---|
| `pending` | Not judged yet. No hint. |
| `sound` | Something above the noise floor in the last 2 minutes. |
| `no_signal` | Nothing delivered at all once the source may be judged. |
| `silent_signal` | Samples delivered, but never above the noise floor (a muted or wrong device). |
| `went_quiet` | Was heard, then 2 minutes of nothing. Ordinary pauses do not trigger it. |

The microphone may be judged 8 s after it opens. During a meeting the computer audio is judged only
after the microphone has heard sound and 30 s have passed, because the far end is legitimately silent
until the room speaks and a loopback delivers nothing while nothing plays.

## Manual acceptance checklist

Everything here needs real hardware and a second person. None of it is covered by the automated suite.

| Check | Windows | macOS |
|---|---|---|
| Teams desktop, two-party headphone call: a local and a remote sentence are both transcribed | NOT YET VERIFIED | NOT YET VERIFIED |
| Slack huddle, same | NOT YET VERIFIED | NOT YET VERIFIED |
| Tencent Meeting, same | NOT YET VERIFIED | NOT YET VERIFIED |
| Google Meet in a browser, same | NOT YET VERIFIED | NOT YET VERIFIED |
| Meeting app playing to wired headphones, a Bluetooth headset, and the built-in speakers in turn → the remote sentence is transcribed in each case | NOT YET VERIFIED | NOT YET VERIFIED |
| Meeting app muted → *No computer sound detected* ~30 s after someone in the room has spoken | NOT YET VERIFIED | NOT YET VERIFIED |
| Unplug the active USB microphone mid-meeting → meeting stops with *The microphone disconnected or changed* | NOT YET VERIFIED | NOT YET VERIFIED |
| Switch the meeting app's output (speakers ↔ headphones) mid-meeting → computer audio keeps arriving | NOT YET VERIFIED | NOT YET VERIFIED |
| Replug and Start again → both meters move | NOT YET VERIFIED | NOT YET VERIFIED |
| Bluetooth headset switching between headphone and hands-free profile mid-call → visible stop, never a silent switch | NOT YET VERIFIED | NOT YET VERIFIED |
| Pause → no new transcript text and no WAV growth from either source; Resume → both meters move | NOT YET VERIFIED | NOT YET VERIFIED |
| Signed bundle: system-audio permission **allow** → capture works | — | NOT YET VERIFIED |
| Signed bundle: permission **deny** → `permission_denied`, actionable message, no crash | — | NOT YET VERIFIED |
| Signed bundle: permission **revoked** while Kanal runs → a visible stop or refusal, no crash | — | NOT YET VERIFIED |
| A notification sound with Do Not Disturb off appears in the transcript input (documents the limitation) | NOT YET VERIFIED | NOT YET VERIFIED |
| macOS 14.2+: the output that was the default at Start disappears mid-meeting → computer audio keeps arriving, or a visible stop, not endless padded silence (see known risks) | — | NOT YET VERIFIED |
| Windows 10 2004+: process loopback captures every app's audio but not Kanal's own (see known risks) | NOT YET VERIFIED | — |
| Windows 10 before 2004: Online meeting shows as unavailable with the required version | NOT YET VERIFIED | — |
| macOS 14.2+: a USB/Bluetooth headset that is both output and microphone → its microphone is not mixed into the computer-audio source (see known risks) | — | NOT YET VERIFIED |
| macOS 13–14.1: ScreenCaptureKit path under Screen & System Audio Recording | — | NOT YET VERIFIED |

## Known risks

The first two concern the macOS Core Audio tap in `src/Kanal.Audio/Native/MacSystemAudio.swift`, the
third the Windows adapter in `src/Kanal.Audio/WasapiLoopbackAudioCapture.cs`. All are untested on
hardware; they are follow-ups, not handled in this release.

- **No error after start (macOS).** The tap is global — every process on every output device — but the
  private aggregate device that carries it uses the default output at Start as its clock. The session
  has no device-alive or tap listener: if that device dies or the tap is invalidated after Start, the
  native callback simply stops; the mixer then pads silence and the hint shows *No computer sound
  detected* instead of a `device_unavailable` stop.
- **Headset microphone in the computer-audio source (macOS).** The aggregate lists the default output
  as its clock sub-device, and every input buffer of the IOProc is downmixed. When that default output
  is a headset that is also a microphone, the headset microphone may be mixed into the computer-audio
  source as well as captured as the microphone, doubling the local voice. An aggregate with the tap
  alone and no sub-device ran its IOProc at the same rate on a development machine, but without the
  capture permission the samples were silent, so it was not adopted unproven.
- **Process loopback (Windows).** Computer audio is captured with WASAPI process loopback excluding
  Kanal's own process tree (Windows 10 2004, build 19041, or later), at 44.1 kHz 16-bit stereo, then
  downmixed and resampled. It compiles but has not been run on Windows.

## Limitations

- All computer audio is captured, from every app on every output device: notifications, other tabs
  and players are heard. Use Do Not Disturb.
- Per-app capture is not offered; the meeting app does not need to know about Kanal, and Kanal cannot
  isolate it.
- Muting yourself in the meeting app does not mute Kanal's microphone.
- Headphones are required; there is no echo cancellation.
- Local and remote voices are mixed into one channel, so transcripts cannot tell which side spoke.
