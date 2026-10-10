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

- You can now download the installers from the GitHub Releases page. The macOS dmg is signed and
  notarised. The Windows installer is an msi. Each file has a `.sha256` file for checking the download.
- A collapsed sidebar no longer leaves a light strip at the window edge on Windows.
- A meeting now travels as one `.kl` file. Export this meeting writes a `.kl` file. Import meeting
  accepts only a `.kl` file and refuses any other file with a message. Any other file that you
  import into a meeting becomes an attachment of that meeting. Before, a PDF or a slide deck
  imported as a meeting record opened as an empty transcript. The meeting menu has no Markdown
  export now: the transcript as Markdown is `transcript.md` inside the `.kl` file. A `.kl` file is
  a zip archive, so rename it to `.zip` to open it without Kanal.
- When you confirm consent and start a meeting, Kanal writes the capture profile and the time of
  the confirmation into the meeting's transcript file. The record travels inside the `.kl` file.
- The Online meeting capture profile now works. Kanal hears your microphone and computer audio
  together, with a separate meter for each. You do not pick an output. The meeting app can play to
  headphones, a Bluetooth headset or the speakers. Kanal tells you when a source stays silent. If
  the microphone is unplugged, a permission is refused or the machine stalls, the meeting stops
  and shows the reason in your language. Wear headphones and turn on Do Not Disturb. Kanal captures
  all computer audio, notifications included. Muting yourself in the meeting app does not mute
  Kanal. On Windows, this needs Windows 10 version 2004 or later. In both profiles, Pause now
  closes the devices. The meters drop to zero while paused. Kanal also tells you when the
  microphone is silent. `docs/online-meeting-audio.md` covers diagnosis.
- The text on every button is now centred vertically. Before, it sat 2 to 6 pixels above the
  centre, most visibly on Save & Close and View open-source acknowledgements.
- On macOS, the red, yellow and green window buttons are now 20% larger. They sit in the
  vertical centre of the header instead of near its top edge. In full screen, the header content
  moves to the left edge, because the buttons are gone.
- In Chinese, the delete, export and import dialogs and the line naming the meeting being recorded
  now put the meeting's name in quotation marks, “支架料号 KX-4402”, instead of the book-title marks
  《》 used for books and films.
- The Files tab on the right now has an Open folder button beside Import file. It opens the folder
  of the meeting the tab is showing in Explorer or Finder, is greyed out until a meeting is chosen,
  and says so under the list if that folder was moved or deleted outside Kanal.
- The mode list no longer offers Demo — scripted on a normal install. Kanal opens on Cloud
  transcription · Cloud translation instead; without an API key, that row says one is needed. To
  test with Demo, start Kanal with the environment variable `KANAL_ENV=development`.
- The window now has one top row across the sidebar, the meeting and the assistant, with the
  recording buttons always centred on the meeting; narrowing the window trims the outer ends of the
  toolbar instead of squeezing buttons.
- Each language column has an enlarge button that gives that language the whole transcript area,
  and a restore button to bring the others back; scroll position and live following are kept.
- The language columns now scroll together. A round button appears when new lines arrive below
  the line you are reading. It takes you to the latest line. The button also appears when you keep
  scrolling down above the latest line.
- The meeting list is grouped under Today, Yesterday and Recent, and each group can be folded.
- Opening an earlier meeting now shows the speaker ruler on the right, as it does for a live one.
- Opening a long earlier meeting no longer freezes the window: the transcript loads in the
  background and a loading note shows until it is ready.
- Search, add-meeting and add-project buttons, the project list and the meeting list were redrawn;
  the application-language choice in Settings now shows a flag beside each language.
- Kanal now uses the conversation-K logo, warm paper surfaces, rounded controls and a coordinated
  indigo/apricot palette across the desktop host, dialogs, startup screen and mobile reader.
- A meeting's three-dot menu in the sidebar now offers Rename, which edits the name right in the
  list, and Generate title, which names the meeting from its transcript with the local model. It
  works on meetings that have ended, not only the one being recorded; the heading of an ended
  meeting can be renamed again too. Generate title stays visible but greyed out, saying why, when no
  local model is set up or while a meeting is running; a meeting with nothing said keeps its name.
- Clicking a mark on the timeline beside the transcript now tints the sentence it takes you to, in
  every language column at once, so you can see what you landed on. The next click moves the tint;
  closing the room clears it.
- The note under the toolbar no longer states the obvious when in-room capture is selected. It
  still appears when there is something worth saying, and can be closed with a close button at its
  right edge; it comes back if you switch capture profiles.
- The Delete item in a meeting's three-dot menu now has a trash icon and shows in red, so it reads
  as the destructive action it is instead of blending in with import and export.
- Renaming a meeting title now shows selected text in inverse instead of a solid black block, keeps
  the text exactly where it was, and sizes the edit box to the title instead of the whole column.
- While a local translation model loads, a thin ring turns around the stop button and the status
  line at the bottom names the model, so the room does not look frozen.
- A meeting's three-dot menu now has an Open folder item that reveals its record folder in Finder
  or Explorer. If the folder was moved or deleted outside Kanal, or cannot be opened, the sidebar
  says so instead of silently recreating an empty folder or doing nothing.
- The sidebar wordmark now reads KANAL, matching the app name everywhere else it appears.
- Pressing record now asks before anything is captured. A dialog says the meeting will be
  transcribed and translated, reminds you that remote participants cannot see Kanal, and offers two
  ticks: whether to save the audio file as well, and that you have informed everyone. Confirm and
  start is dead until the second one is ticked. Cancel, or press Esc, and nothing happens at all —
  no meeting record is left behind. The tick for the audio file counts for this meeting only and
  never changes the setting, so switching it off for one sensitive meeting cannot quietly switch it
  off for the next. The consent checkbox that used to sit in the toolbar is gone.
  A meeting is named after the workspace and the minute it started — `Kanal 2026-09-08 14:30` —
  until Kanal names it from what was said. Two meetings in one workspace cannot be given the same
  name by hand; a name Kanal generates takes the next free number instead of interrupting.

- A `.kl` file holds the transcript in two forms and the attachments. It holds the recording only
  if you tick the box in the export dialog. An hour of audio is about 115 MB, and most `.kl` files
  go by mail. If the workspace already holds the meeting, import offers to skip it or to keep it as
  a second record. Import never overwrites a meeting. The .json transcript export is gone. The
  meeting's folder now holds that data.
- Two controls that had nothing to do in this version are off the screen. The right panel is Points
  alone — the live transcription tags every sentence as one speaker, so there was never a second
  person to rename or merge. Online meeting is still listed in the capture picker but cannot be
  chosen, and the row says why: this version does not capture computer audio. Both come back when
  the features behind them do.
- Click any meeting in the left column and the transcript switches to it, read-only, even while a
  meeting is being recorded. The one being recorded stays marked in the list, and a line across the
  top of the transcript says which meeting that is and takes you back to it. Titles, speaker names
  and exports always belong to the meeting being recorded — until now, a title Kanal generated
  mid-meeting could be written onto whichever old meeting you happened to be reading.

- A meeting record can be deleted from its three-dot menu. Kanal asks a second time before it
  does, and says in that question that the transcript and the recording go with the record, that
  they do not go to the trash and that they cannot be brought back. The meeting that is currently
  being recorded cannot be deleted.

- A long transcript is now navigable. A strip of short marks runs down the right-hand edge of the
  transcript, one for each time the speaker changes, drawn in that speaker's colour and growing as
  the meeting runs. Rest the pointer on a mark to see who spoke, how far into the meeting it was
  and how the turn opened; click it to jump the transcript to that sentence. A meeting with more
  turns than the strip has room for folds neighbouring marks together rather than dropping any.

- The right-hand column now has a Files tab. It lists what is in the selected meeting's folder —
  transcript, recording, and anything already filed under it — and an Import file button copies a
  drawing, quotation or specification from the laptop into that meeting's attachments folder, where
  it stays with the meeting. Nothing is ever overwritten: a second file of the same name is kept
  alongside the first. The tabs slide sideways when the column is narrow instead of stacking.

- Meetings now save themselves. The moment recording starts, the transcript is written into the
  meeting's own record — line by line as people speak, not at the end — and the room recording goes
  in beside it. Nothing has to be exported to keep it, and a host that dies mid-meeting leaves
  everything said up to that moment readable. The audio folder in Settings is gone with it;
  recordings made before this stay where they were.

- Each pipeline mode now has a mark of its own — a cloud, a laptop, or one of the two with an
  arrow showing which way the work is handed over — in the mode box and against every row of the
  list, so the mode is readable at a glance instead of by reading its label. The help button now
  sits against the mode box it explains instead of drifting across the bar, and the record button
  is a full red disc rather than a dot in a pale field, sitting square in the middle of its ring on
  every display scaling. Reaching for a transport button grows it slightly instead of inverting its
  colours.

- The toolbar's marks sit square in the middle of the controls that carry them, and the capture
  picker shows its whole dropdown arrow at every window width — it used to lose it as the row got
  tight. The mode box now draws a cloud and a laptop to say where each half of the pipeline runs,
  in place of the chip that said nothing.

- A meeting can now name itself. With a local translation model in use, Kanal proposes a short
  title once the meeting is a minute or so in, and a control beside the title asks for another one
  at any time. Click the title to rename it by hand — that name is kept, is never overwritten by a
  suggestion, and is saved with the meeting record.

- The meeting title now sits above the transcript, with the room's language flags on the same line.
- Settings is six tabs down the left instead of one long scroll: General, Audio input,
  Transcription, Translation, Summarisation and Workspace. Every setting that was there before is
  still there, in the tab it belongs to. Summarisation says plainly that it is not built yet rather
  than showing controls that would do nothing.
- If somebody speaks a language the room has no column for, Kanal now says which language it was
  instead of picking one of the columns and calling it the original. The words that were actually
  said stay on screen under every translation. When it cannot tell which language was spoken, it
  says so rather than printing a code.
- The left sidebar is now a workspace. Search your meetings, start a new one with the button beside
  the search box, switch between projects, and reach import and export from each meeting's own
  menu. A record that cannot be read is reported instead of quietly shrinking the list.
- The meeting assistant on the right now has two tabs. **Points and decisions** collects what the
  meeting settled, grouped by topic, with the words that support each one printed underneath — and
  nothing is recorded as agreed until you confirm it. **Speakers** is the naming and merging you
  already had. Until a listening model is connected the first tab says so plainly, so an empty
  panel is never mistaken for a meeting that decided nothing.
- Workspaces keep their meeting records in operator-selected local folders, survive restarts,
  and report missing or damaged records without hiding the meetings that remain readable.

- The meeting window is now three columns. A workspace sidebar sits on the left and the meeting
  assistant on the right; either can be collapsed out of the way and dragged wider, and the width
  you pick comes back when you expand it again. The transcript keeps a usable width whatever the
  sidebars do.
- Record, pause and stop are round marks, each sitting on a larger, paler disc of its own colour,
  so the transport is easier to hit and easier to read across the room. The capture picker shows
  a whole, unclipped mark, and a different one per profile — people around a table for in-room,
  a screen for an online meeting — so you can see which is selected without opening it. The
  microphone mark has been redrawn.
- The control bar is now marks rather than labels, and it never scrolls sideways. Record, pause and
  stop sit in the middle of the bar with the microphone beside them and stay put however narrow the
  window gets; the processing and capture pickers hold the left edge and the join QR the right. What
  the room is doing is spelled out in the status line at the bottom, and the two full-width bands
  that used to push the transcript down are gone. Settings moved to the foot of the workspace
  sidebar, and the room's language flags to the top of the
  transcript.
- Before a live room starts, the operator now chooses in-room or online capture and confirms that
  every participant consented. The host and phones keep live transcription visible even when no
  audio file is recorded.
- The application icon presents the multicolour Kanal mark on a clean warm-beige rounded tile.
- A calm vertical startup lockup appears while the meeting host is being prepared, carrying the
  application mark, the lowercase Kanal name and the line “One room. Every language”.
- A phone whose join link has expired now shows the meeting as ended and stops trying to
  reconnect, instead of sitting on "reconnecting" and retrying every 15 seconds for as long as
  the tab stays open. The transcript already on the page is kept.
- Live meeting translation: the host captures the room, transcribes what is said and translates
  it, and everyone reads along on their own phone by scanning the join QR code. Only text ever
  reaches the phones.
- Up to four language columns on the host, reorderable by drag or Alt+←/→; each phone chooses one
  language for itself.
- In the language dialog each tick box sits on the same centre line as the flag and the language
  name beside it, and every row lands on that same line.
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
- Kanal records the room's audio into the meeting's folder unless you turn that off.
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
