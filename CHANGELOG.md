# Changelog

What changed, newest first. Kanal shows this file inside the application — Settings → Version →
*View changelog* — so an operator can answer "did something change since last week?" on the laptop
that is running the meeting.

Entries are written for the person using Kanal, not for the person who wrote the commit. One
heading per version, `## <version> — <yyyy-MM-dd>`; the newest heading has to match the version the
build reports, and a test holds the two together.

## 0.4.0 — 2026-08-04

- Captions now travel through an authenticated relay gateway of your own rather than a shared
  public backend. The operator provisions `KANAL_RELAY_URL` and a host token; each room hands the
  phones a receive-only ticket, and the join QR carries no credential that can publish. Without
  those settings the meeting still runs — with no QR code and a warning saying so — instead of
  falling back to a shared credential.
- Log files. The host keeps a record of what it did — one file a day under
  `%APPDATA%/Kanal/logs`, rolled over once it passes a size you set, kept for two weeks. Nothing is
  sent from here; the folder is yours, and goes on only if you send it. Settings → Diagnostics
  chooses how much detail is kept (debug, info, warning, error) and opens the folder in one click,
  so a log can be found without knowing where an application hides things.
- Room starts and stops, relay failures, capture failures, a translation model that will not load
  and an export that cannot be written now all leave a line behind, with the exception attached.
- Settings says what the Debug level records. At that level the file also keeps what the
  transcription service and the gateway sent back word for word, which can include what was said in
  the room — so the panel says so where the level is chosen. The note about the log folder no longer
  claims nothing is ever sent from it, since sending it to whoever asks is the point of the button
  beside it.
- The log size control now says how much disk the setting can cost. The number of files kept
  follows from the size you choose, so the largest setting can fill about 21 GB where the default
  fills 2.1.
- Settings now tells you when it could not save. A locked or read-only settings folder used to
  close the dialog as though the change had been written, and the first sign was the next Start
  refusing a key you had just entered.
- The list of open-source projects Kanal is built on, with their licences, at the bottom of
  Settings.
- This changelog, readable from Settings.

## 0.3.0 — 2026-08-03

- The host chrome speaks English, Chinese, German and Polish, and switching takes effect on the
  windows that are already open.
- Modes describe the pipeline rather than the vendor: five combinations of transcription and
  translation, each stating what leaves the machine, with the ones that cannot run right now shown
  greyed out and explaining why.
- Local translation runs in-process — a downloadable model catalogue in Settings, and the weights
  load before the room opens rather than during the first sentence.
- Chinese output is Simplified wherever it was produced.
- Up to four language columns, reorderable by drag or Alt+←/→.
- Transport controls: Start, Pause/Resume, Stop. Pause takes the room off the record — nothing
  transcribed, translated or sent — and keeps the room, the QR code and the transcript.
- The microphone can be tested before the meeting: a level meter, a held peak, and a verdict that
  names the fault (silent, too quiet, clipping, noisy) and where to fix it.
- The device list notices a microphone being plugged in or unplugged mid-session.
- Transcripts export as Markdown to a folder you choose; the room's audio is recorded alongside it
  unless you turn that off.

## 0.2.0 — 2026-07-31

- Swiss editorial redesign of both the host and the mobile page: the live utterance carries the
  weight, finalised history recedes in contrast, colour identifies people and nothing else.
- Rooms are isolated from each other by a random room-id suffix, with a per-room cache on the
  phone.
- Clients are told when a room closes or moves, so a restart no longer strands everyone until they
  rescan.
- macOS audio capture via AudioQueue, chosen by the same backend selection as Windows WASAPI.

## 0.1.0 — 2026-07-30

- First working host: desktop capture, a pluggable transcription/translation chain behind
  capability-driven orchestration, a streaming cloud transcription client, the broadcast relay, and
  a read-only mobile page reached by scanning the join QR code.
