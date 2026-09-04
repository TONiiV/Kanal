# Changelog

What changed, newest first. Kanal shows this file inside the application — Settings → Version →
*View changelog* — so an operator can answer "did something change since last week?" on the laptop
that is running the meeting.

Entries are written for the person using Kanal, not for the person who wrote the commit. One
heading per version, `## <version> — <yyyy-MM-dd>`; the version being worked towards carries no
date until it is released. A pull request that adds a feature, fixes a bug or makes something
measurably better adds its own bullet under that heading as it lands — nothing else does. The
newest heading has to match the version the build reports, and a test holds the two together.

## 1.0.1

The first release. Everything below is what Kanal does on the day it ships.

- The application icon presents the multicolour Kanal mark on a clean warm-beige rounded tile.
- A calm vertical startup lockup appears while the meeting host is being prepared, carrying the
  application mark, the lowercase Kanal name and the line “One room. Every language”.
- The host uses one compact, horizontally scrollable control bar, leaving the meeting more room
  while keeping every mode, language, microphone and transport control available in long locales.
- The control bar is arranged in two groups: transport, mode and languages sit together on the
  left, and the microphone, export and settings controls are held against the right edge, so the
  controls used mid-meeting no longer sit next to the ones set up once.
- The microphone on the control bar is one instrument: a microphone mark that mutes the room with
  one click, a level meter beside it that shows the room actually arriving, and a caret that opens
  the list of inputs with a tick against the one in use. Muting sends silence rather than cutting
  the stream, so the connection to the transcription service is never dropped and unmuting takes
  effect at once.
- Live meeting translation: the host captures the room, transcribes what is said and translates
  it, and everyone reads along on their own phone by scanning the join QR code. Only text ever
  reaches the phones.
- Up to four language columns on the host, reorderable by drag or Alt+←/→; each phone chooses one
  language for itself.
- Modes describe the pipeline rather than the vendor: five combinations of transcription and
  translation, each stating what leaves the machine, with the ones that cannot run right now shown
  greyed out and explaining why.
- Local translation runs in-process — a downloadable model catalogue in Settings, and the weights
  load before the room opens rather than during the first sentence.
- Chinese output is Simplified wherever it was produced.
- Transport controls: Start, Pause/Resume, Stop. Pause takes the room off the record — nothing
  transcribed, translated or sent — and keeps the room, the QR code and the transcript.
- The microphone can be tested before the meeting: a level meter, a held peak, and a verdict that
  names the fault (silent, too quiet, clipping, noisy) and where to fix it.
- The device list notices a microphone being plugged in or unplugged mid-session.
- Transcripts export as Markdown to a folder you choose; the room's audio is recorded alongside it
  unless you turn that off.
- Rooms are isolated from each other by a random room-id suffix, with a per-room cache on the
  phone, and clients are told when a room closes or moves — so a restart no longer strands
  everyone until they rescan.
- Captions travel through an authenticated relay gateway of your own rather than a shared public
  backend. Each room hands the phones a receive-only ticket, and the join QR carries no credential
  that can publish. Without a gateway configured the meeting still runs — with no QR code and a
  warning saying so — instead of falling back to a shared credential.
- The host chrome speaks English, Chinese, German and Polish, and switching takes effect on the
  windows that are already open.
- Audio capture on Windows and macOS, chosen by the same backend selection on both.
- Log files. The host keeps a record of what it did — one file a day, rolled over once it passes a
  size you set, kept for two weeks and never sent anywhere. Settings → Diagnostics chooses how much
  detail is kept (debug, info, warning, error) and opens the folder in one click, so a log can be
  found without knowing where an application hides things.
- Room starts and stops, relay failures, capture failures, a translation model that will not load
  and an export that cannot be written all leave a line behind, with the exception attached.
- The list of open-source projects Kanal is built on, with their licences, readable from Settings.
- Open-source acknowledgements now open in their own readable window from Settings, instead of
  making the settings form several screens longer.
- This changelog, readable from Settings.
- The supplied transparent PNG Kanal route mark across the app, browser tab, splash screen and
  platform icons, without an accompanying wordmark.
