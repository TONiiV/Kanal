# Online meeting audio — diagnosis and manual acceptance

How to check, diagnose, and accept the **Online meeting** capture profile: the selected microphone and
the selected computer output, mixed into the single 16 kHz mono stream the speech pipeline receives
([ADR 0050](adr/0050-native-online-meeting-audio.md)).

## Before a meeting

1. Wear headphones. Kanal has no echo cancellation: without headphones every remote sentence reaches
   the pipeline twice, once through the computer output and once through the microphone.
2. Turn on Do Not Disturb. Kanal captures the **whole** selected output mix, including notification
   sounds, other browser tabs, and media players.
3. In the toolbar's microphone menu, pick the microphone and the output the meeting app plays to, then
   press **Test audio for 10 seconds**. Speak, and play speech in the meeting app. Both meters must
   move. The test is local: nothing is transcribed, recorded, or sent.
4. Muting yourself in Teams, Slack, Meet, or Tencent Meeting does **not** mute Kanal's microphone.
   Use Pause in Kanal for private moments; pause sends nothing to transcription and records nothing.

## Kanal.Doctor

Run from the repository root. `system` and `online` are local: they write no file and make no network
call. `mic` writes `mic-check.wav` to the current folder; `gladia` sends a WAV to Gladia.

```bash
dotnet run --project tools/Kanal.Doctor -c Release -- devices
dotnet run --project tools/Kanal.Doctor -c Release -- system 10 0
dotnet run --project tools/Kanal.Doctor -c Release -- online 10 0 0
```

- `devices` lists microphones and outputs with an index and an id, then the platform's computer-audio
  support (`IsAvailable`, `Backend`, `Reason`).
- `system <seconds> [outputIndex]` captures only the computer output.
- `online <seconds> [outputIndex] [microphoneIndex]` runs the same mixer the host uses and prints its
  diagnostics: every event, plus one `levels` line per source every 5 seconds.

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
- `capture_fault code=<code> source=<source> mode=<online|in-room|preview>` at Error, with the
  exception attached. An in-room line adds `the room is live with no audio arriving`.
- `capture_fault code=device_unavailable source=<source> reason=device_list_changed` at Warning, when a
  device the meeting is using disappears from the device list.
- `Capture running on …` and `<n> frames captured.` at Debug.

Fields of the JSON lines:

| Field | Meaning |
|---|---|
| `Session` | Random id of one capture run; joins the lines of one Start, Resume, or test. |
| `Event` | `starting`, `first_frame`, `levels`, `fault`, `stopped`. |
| `Source` | `microphone`, `system`, or `mixer` for faults that belong to neither source. |
| `Device` | First 12 hex characters of the SHA-256 of the device id; `default` when none was chosen. Matches across lines without exposing the device name. |
| `ReceivedSamples` | Samples delivered by the source since it opened (16 000 per second when it is healthy). |
| `DroppedSamples` | Samples discarded because the source ran more than 500 ms ahead of the host clock (its device clock is fast) or would have overwritten audio not yet mixed. Late audio is never dropped; it is placed after what was already mixed. |
| `PaddedSamples` | Silence inserted because the source had nothing for a slot. Grows steadily for a loopback that delivers nothing while the output is quiet. |
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

A hint appears in the status bar during a meeting, and under the meters during a test:

- *No microphone sound detected* — check the selected microphone, its hardware mute switch, and the
  microphone permission.
- *No computer sound detected* — the meeting app plays to a different output, its volume is zero, or
  the capture permission is missing. Pick the output the app uses (its own audio settings name it).
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

The microphone may be judged 8 s after it opens. During a meeting the computer output is judged only
after the microphone has heard sound and 30 s have passed, because the far end is legitimately silent
until the room speaks and a loopback delivers nothing while nothing plays. During the 10-second test
both use the 8 s grace.

## Manual acceptance checklist

Everything here needs real hardware and a second person. None of it is covered by the automated suite.

| Check | Windows | macOS |
|---|---|---|
| Teams desktop, two-party headphone call: a local and a remote sentence are both transcribed | NOT YET VERIFIED | NOT YET VERIFIED |
| Slack huddle, same | NOT YET VERIFIED | NOT YET VERIFIED |
| Tencent Meeting, same | NOT YET VERIFIED | NOT YET VERIFIED |
| Google Meet in a browser, same | NOT YET VERIFIED | NOT YET VERIFIED |
| Wrong output selected → *No computer sound detected* within ~10 s in the audio test; in a meeting ~30 s after someone in the room has spoken | NOT YET VERIFIED | NOT YET VERIFIED |
| Unplug the active USB microphone mid-meeting → meeting stops with *An audio device disconnected or changed* | NOT YET VERIFIED | NOT YET VERIFIED |
| Unplug / switch the active output mid-meeting → same | NOT YET VERIFIED | NOT YET VERIFIED |
| Replug and Start again → both meters move | NOT YET VERIFIED | NOT YET VERIFIED |
| Bluetooth headset switching between headphone and hands-free profile mid-call → visible stop, never a silent switch | NOT YET VERIFIED | NOT YET VERIFIED |
| Pause → no new transcript text and no WAV growth from either source; Resume → both meters move | NOT YET VERIFIED | NOT YET VERIFIED |
| Signed bundle: system-audio permission **allow** → capture works | — | NOT YET VERIFIED |
| Signed bundle: permission **deny** → `permission_denied`, actionable message, no crash | — | NOT YET VERIFIED |
| Signed bundle: permission **revoked** while Kanal runs → a visible stop or refusal, no crash | — | NOT YET VERIFIED |
| A notification sound with Do Not Disturb off appears in the transcript input (documents the limitation) | NOT YET VERIFIED | NOT YET VERIFIED |
| macOS 14.2+: the tapped output disappears mid-meeting → a visible stop, not endless padded silence (see known risks) | — | NOT YET VERIFIED |
| macOS 14.2+: a USB/Bluetooth headset that is both output and microphone → its microphone is not mixed into the computer-audio source (see known risks) | — | NOT YET VERIFIED |
| macOS 13–14.1: ScreenCaptureKit path under Screen & System Audio Recording | — | NOT YET VERIFIED |

## Known risks

Both concern the macOS Core Audio tap in `src/Kanal.Audio/Native/MacSystemAudio.swift` and are untested
on hardware; they are follow-ups, not handled in this release.

- **No error after start.** The tap session has no device-alive or tap listener. If the tapped output
  dies or the tap is invalidated after Start, the native callback simply stops; the mixer then pads
  silence and the hint shows *No computer sound detected* instead of a `device_unavailable` stop. The
  host's device-list check still stops the meeting when an explicitly selected output disappears.
- **Headset microphone in the computer-audio source.** The private aggregate device lists the output
  device as a sub-device, and every input buffer of the IOProc is downmixed. For a headset that is both
  output and microphone, the headset microphone may be mixed into the computer-audio source as well as
  captured as the microphone, doubling the local voice.

## Limitations

- The whole output mix is captured: notifications, other tabs and players are heard. Use Do Not Disturb.
- Per-app capture is not offered; the meeting app does not need to know about Kanal, and Kanal cannot
  isolate it.
- Muting yourself in the meeting app does not mute Kanal's microphone.
- Headphones are required; there is no echo cancellation.
- Local and remote voices are mixed into one channel, so transcripts cannot tell which side spoke.
