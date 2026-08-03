# Kanal — progress, plan & design changes

Living log. Update in the same PR as the work it describes. Newest section on top.

---

## 2026-08-03

### Chinese comes out Simplified, wherever it was produced

**Finding.** Chinese transcripts (and translations into Chinese) reached the room in Traditional
characters, but the primary Chinese participant is a mainland supplier who reads Simplified.
Gladia offers no knob for this: both `TranscriptionLanguageCodeEnum` and
`TranslationLanguageCodeEnum` know a single `zh` — no `zh-Hans`/`zh-Hant`, nothing in
`language_config` or `translation_config` selects a script. So the fix cannot live in the request
body; it has to live on the host.

**Fix.** `SimplifiedChinese` (`Kanal.Core/Text`): Traditional→Simplified normalization applied by
`MeetingSession` — the host is the single authority, so text is normalized once, before it enters
`RoomState` or the relay, and clients never convert. It covers all three ways Chinese text is
produced: transcript partials/finals with `SrcLang: zh`, translations arriving inside Gladia
transcript events, and `IMtProvider` results. The local-MT prompt now also asks for "Simplified
Chinese" outright — steering word choice at the source (信息 not 資訊), which character mapping
cannot fix after the fact.

**Trade-offs.** No dependency: OpenCC's `TSCharacters.txt` (Apache-2.0, ~5 000 single-character
mappings) is embedded as a resource instead of pulling in an OpenCC binding (OpenCCSharp is
prerelease and its trie/data packages are more moving parts than this needs). Conversion is
character-level and pure dictionary lookups; text below the CJK range skips the lookup entirely and
unchanged strings return the same instance, so the Latin/Polish path and the already-Simplified
common case allocate nothing — safe at partial frequency. **Limitation:** one-to-many characters
(乾/幹/干, 髮/发…) take OpenCC's first, most common mapping, and there is no phrase-level
disambiguation — acceptable here because the input is overwhelmingly machine-emitted Traditional
forms of Simplified-intended speech, not literary text.



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

- **Local translation produced nothing at all, and Stop took twenty seconds.** Both were the same
  cause. Qwen3.5 reasons by default; given the 512-token budget a translation needs, the whole
  budget went to `<think>` and the block never closed, so `MtOutputCleaner` correctly found no
  translation in it and every column sat on `…` for the whole meeting with nothing printed
  anywhere. Measured against the 2B on this machine: **40 s per call, empty string out.**
  Prefilling an already-closed think block skips the reasoning turn: **1 s per call, and a usable
  sentence.** (Qwen's documented `/no_think` marker was tried first and did not work — the model
  reasoned anyway.) The prefill is data on the catalog entry (`LocalModelInfo.AssistantPrefill`),
  not a switch, so a new model family declares its own convention and nothing branches on a
  vendor. End-to-end through the shipping path afterwards: **2.0–5.3 s per utterance for two
  target languages**, part numbers (`KX-4402`) and standards (`ISO 7599`) preserved.

  Stop was slow because those 40-second decodes were exactly what shutdown waited for:
  `MeetingSession.DisposeAsync` awaited every pending translation with no cancellation at all, so
  the operator's Stop button belonged to the translator. There is now a bounded grace
  (`DefaultTranslationGrace`, 2 s) for a translation that is nearly done, after which the token
  is cancelled and the decode unwinds — measured cancel-and-dispose: **0.7 s**. The masthead says
  `Stopping…` for the duration and both transport buttons are refused, since a second press used
  to race the first.

  Two further defects surfaced while fixing this. Translations were registered as pending *after*
  the call had already entered the provider, so a shutdown landing in that window saw no pending
  work and abandoned a translation that had in fact begun; registration now happens before the
  work starts. And a translator returning nothing for *every* target was silent — indistinguishable
  on screen from a slow one — which is what made this a rehearsal-length mystery rather than a
  warning line; total failure is now reported through the existing non-fatal error path. Partial
  failure stays quiet on purpose: the languages that worked are worth more than a warning about
  the one that did not.

  Review of the fix found a third window of the same shape: the pending snapshot is taken while
  the pump may still be draining finals buffered before Stop, so a translation tracked during the
  grace was cancelled with the rest but awaited by nobody — disposal could return, and the caller
  free the native weights, while that decode was still unwinding, with the freshly disposed
  cancellation source firing a spurious "Relay publish failed" behind it. Once the pump has
  exited nothing can register any more, so disposal now takes the pending list a second time at
  that point and waits for the stragglers; they are already cancelled, so Stop stays bounded.

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

- **Transport: Start · Pause/Resume · Stop, with icons.** The host had two buttons and no way to
  take the room off the record without ending the meeting. Pause is designed as a **privacy
  control** first — in a negotiation the operator steps out to talk to their own side — so it
  stops the audio at the door (`MeetingSession.PushAudioAsync` returns early while paused) rather
  than hiding the transcript afterwards. Dropping the transcript while still streaming the room to
  a cloud transcriber would mean the private conversation left the building and only the record of
  it was hidden, which is worse than offering no pause at all. A provider that generates its own
  audio (the scripted one) is handled at the other end too: nothing it says while paused is
  recorded.

  Pausing is announced to the room (`room.paused`) and carried in `room.snapshot`, so a phone
  joining mid-pause lands in the same state as everyone else. A column that simply stops is
  indistinguishable from a broken connection, and "is my next sentence being recorded" is not a
  question to answer by inference. On the host the same state is an inverted ink band across the
  full width — the heaviest statement available without spending colour, which belongs to
  speakers. The bottom status line alone was not enough: at a metre it is easy to miss, on exactly
  the state where being wrong is expensive.

  Icons are drawn as geometry rather than set as characters (▶ ❚❚ ■). The font stack here is
  chosen to carry three scripts at once, and which face ends up supplying a symbol out of it is
  not worth leaving to chance on the one row of controls used mid-meeting. Settings is three
  sliders rather than a gear, drawn from the same rules-and-blocks vocabulary as the rest of the
  screen. A glyph is not text and does not inherit `TextElement.Foreground`, so every button state
  states what its icon is painted with — an icon left ink-on-ink during a hover fill disappears.

  Review follow-up: while paused, a sentence that **began on the record may still finish on it**.
  The pump originally dropped every transcript during a pause, including the final of a sentence
  whose partial was already on every phone — and the audio gate means a real transcriber can only
  be flushing pre-pause, on-record audio at that point, so the last sentence before the pause was
  left a muted partial forever and its translation never requested. Nothing new may begin while
  paused; that unchanged rule is what still keeps the scripted provider off the record.

- **Mode availability was invisible.** Whether a mode could run was carried only by the row's
  contrast — the same signal the grey second line already uses — so five unequal choices read as
  five equal ones and the operator found out at Start. Each row now carries a marker (filled
  square = runs now, hollow = blocked) and states its status in words, and a **help flyout** next
  to the dropdown lays all five out side by side with what each one does, what it sends off this
  machine, and what is blocking it. The flyout is generated from the same `Modes` collection the
  dropdown binds to, so the help cannot drift from the list. Three of five modes cannot run yet;
  the list is as much roadmap as control, which is why a row nobody can pick still explains itself
  — and, like every other string here, without naming a company.

  Rendering it caught a defect the assertions could not: `FlyoutPresenter`'s default `MaxWidth` is
  narrower than a readable measure of body text, and content wider than it is **clipped, not
  wrapped** — the first version lost the right-hand third of every line, and ran past the bottom
  of the window. Both are now set explicitly, as with every other Fluent default here, and the
  flyout content sits in a `ScrollViewer` so growth past `MaxHeight` scrolls instead of silently
  clipping.

  Review then caught the help **overstating privacy**: Demo promised "no network" while the demo's
  stated purpose — checking the join QR and the phones — runs over the relay, and local · local
  promised "nothing is sent anywhere" while the captions themselves cross the network in every
  mode. The relay fact now lives once in the flyout's introduction, each mode's help claims only
  what its *pipeline* sends out, and a test bans the false absolutes outright. The same review
  closed a hermeticity hole the PR itself had documented: the mode list read the ambient
  `GLADIA_API_KEY`, so "unavailable without a key" was untestable on a machine that has one —
  the key resolver is now injected like the other two test seams.

- **A meeting now produces both artefacts, where the operator chose.** Export wrote to
  `Documents\<roomid>.md` and printed the path in a status line nobody was looking at. It now
  opens a save dialog on the configured transcript folder with the room id as the name — both
  only suggestions. A cancelled dialog writes nothing; a failed write (read-only folder, full
  disk) is reported rather than thrown out of a command nothing awaits, because losing the
  transcript at the last step is the worst possible moment for that.

  The room's audio is written to disk as the meeting runs (`WavWriter`, one file per meeting
  named after the room, ~115 MB an hour). Streamed rather than assembled at the end — an hour in
  memory means a crash costs all of it — and the RIFF lengths are patched every ~2 s, so a host
  that dies mid-meeting still leaves a file that plays. A WAV with zero lengths is not a
  truncated recording; it is one most players refuse to open.

  **Recording hangs off `MeetingSession.AudioAccepted`, a tap that only fires for audio the
  session actually took.** Reading `IsPaused` a second time in the capture loop would have worked
  today and given the pause promise a second place to quietly stop being true. Pause says nothing
  said in that minute is kept; that is now structural. The status bar states `RECORDING` while it
  runs and `RECORDING HELD` while paused — the file outlives the meeting, and nobody should find
  out about it afterwards. Settings carries both folders and an off switch.

  Review then asked the question the host-side indicator could not answer: the operator knows,
  but the people whose voices are in the file read a phone, and two of the three languages in
  the room are spoken where recording a private conversation without the other side knowing is
  a criminal matter, not an etiquette one. Recording is now a room state like pause —
  `room.recording` on the wire, carried in `room.snapshot` because a phone that scans the QR ten
  minutes in never saw the announcement, and cached, because the notice has to survive a
  lock-screen reconnect. The mobile page states it in all four languages, and says where the
  audio stays.

  Rendering it caught the defect the assertions could not: the notice was in the flow, and the
  feed follows the newest utterance, so a participant spends the meeting scrolled to the bottom
  with the notice a few thousand pixels above them. It lives inside the sticky masthead now.
  During a pause it is held rather than hidden — the file exists and resumes, and a notice that
  vanished would read as "it stopped". It is ink on paper with a hairline, not the alarm wash:
  a standing fact about the room, not an error.

- **Microphone test in Settings, and an honest answer about noise suppression.** There was no way
  to find out whether the room's microphone worked until the meeting had started and the columns
  were filling with nothing. Settings now opens with an `INPUT` section: pick a device, press
  Test, speak from where people will sit, and get a verdict — *nothing is arriving* / *too quiet*
  / *clipping* / *the room is nearly as loud as the speaker* / *good* — each with what to do about
  it. Level logic lives in `LevelMeter` and is tested against generated audio rather than a room.

  The measurement that earns its place is the **margin**: how far speech sits above the room's own
  noise floor, taken as the 10th percentile of recent frames (between sentences a meeting room is
  at its floor). A loud microphone in a loud room passes every single-number check and still
  transcribes badly; only the distance between the two predicts that.

  On noise suppression the answer is **Kanal has none**. `WasapiAudioCapture` opens a plain shared
  -mode stream, so whatever the device and Windows do — suppression, echo cancellation, automatic
  gain — happens before Kanal sees a sample and is configured per device in Windows. A level
  slider here would have controlled nothing, so the panel states this and measures the result
  instead.

  Rendering it caught a misleading number: with digitally silent gaps the panel reported *"speech
  sits 81 dB above the room"*, a margin measured against the dB clamp rather than against
  anything real. Digital silence between sentences means a device delivering zeros or gating
  hard, not a very quiet room, and it is now reported as such.

  Review fixes, after the fact. Every piece of advice named Windows, on a tool whose development
  machine is a Mac — and macOS answers a denied microphone permission with exactly what a dead
  device answers, zeros, so the one actionable cause was the one cause never mentioned. The
  wording now follows the platform and names Privacy & Security where it applies. A second fix:
  the capture loop wrote into the meter *field*, so a frame the old device still had in flight
  when the operator pressed Stop landed in the next test's meter — one full-scale straggler and
  a perfectly good second microphone was condemned as clipping until yet another restart. The
  loop now writes only into the meter it was started with, and every update back to the UI
  checks it still speaks for the current session.

- **The host speaks four languages.** Chrome, messages and mode descriptions in English, 简体中文,
  Deutsch and Polski, chosen in Settings and remembered. Separate from the room's languages by
  design: the person driving the laptop is often not one of the people the meeting is being
  translated for, and a German buyer running a session between a Chinese supplier and a Polish
  contractor should not have to read English labels to do it.

  A `Localizer` singleton with an indexer, reached from XAML through an `{l:T key}` markup
  extension that produces a *binding* rather than a value. Switching therefore reaches windows
  that are already open — mid-meeting, without restarting a room. Modes carry keys rather than
  text for the same reason: built once at construction, they would otherwise have stayed in
  whatever language the application started in. Missing keys fall back to English and then to the
  key itself, so a gap shows up as a visible identifier rather than as a blank control.

  Three tests keep it honest: the other three languages must carry **exactly** the English key
  set, no string may still be the English one (bar a handful that genuinely are the same word —
  "Start" and "Pause" are ordinary German), and `{0}` placeholders must survive translation, since
  a format string that loses one drops the path or the decibel figure it was carrying and
  `string.Format` says nothing. The unbranded rule is now checked in all four languages.

  Two defects this turned up. A `Strings.Tables` map declared **above** the dictionaries it
  indexes was built out of four nulls — static initialisers run in declaration order — so every
  lookup threw instead of falling back. And a test that switched the language never put it back:
  the language is a global singleton, as it must be for a desktop application, so a leak changed
  what every other test's window said, and xunit's parallel classes turned that into failures that
  moved between runs. Parallelisation is now off for the assembly, with the reason recorded.

  Rendering all four caught the layout defect i18n always produces: `Merge` is one short word in
  English and `Zusammenführen` in German, and on one row the German ran off the edge of the
  speakers panel. The button now sits under the two tags, which fits any language rather than the
  four that exist today.

  Review fixes, after the fact. The German and Polish had promoted "the mode that sends audio
  out" to "the *only* mode that sends audio out" — false, CloudLocal sends it too, and exactly
  the fact this tool exists to keep straight; a test now refuses the claim. The Settings window,
  where the switch happens, half-stayed in the old language: the env-var note, the processing
  note, the folder note, the untested verdict and the model rows were all built at construction,
  and the model rows were still hard-coded English besides. All of it now follows the change,
  the two file dialogs use the keys that already existed for them, and the "same word in the
  target language" exemptions are per language, so a Chinese 开始 reverted to "Start" fails.

  Merging the two brought out a conflict worth naming: the microphone panel had just been made
  platform-aware in English while this branch was turning the same strings into keys, so taking
  either side alone would have silently reverted the macOS permission advice. The platform
  difference lives in the language tables now — `settings.sound.mac` / `settings.sound.win` fill
  a placeholder in the three sentences that name a settings panel, and the silent verdict has a
  macOS detail of its own, because a denied permission there sounds exactly like a dead device.

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
4. **App icon** (PR #10): a level meter over three lines of translation — sound in, three languages
   out. The host shipped Avalonia's template logo and the mobile page had no favicon, so every phone
   that scanned the QR also fired a 404 at `/favicon.ico`. `design/kanal-icon.py` is the single
   source of truth: SVG, `.icns`, `.ico`, the 1024 px PNG and the inlined favicon are all generated
   from one geometry table, and the script now rewrites the `<link rel="icon">` line in both
   `web/index.html` and `docs/index.html` itself rather than printing a "paste this" instruction —
   CI's byte-identity check compares the two pages to each other, so it cannot see them go stale
   together. *Deliberate deviations* from `.impeccable.md`: rust/ochre/pine are spent on the three
   languages (in a Dock there is no speaker to confuse them with, and there are exactly three), and
   the tile is warm paper `#F5F0E6` rather than the interface's `#FCFCFD`, which reads cold among a
   row of colourful icons. Below 64 px the five meter bars smear into one grey block, so small sizes
   switch to a three-bar geometry — the 3-against-3 reading is what has to survive, not the bar count.
   `<ApplicationIcon>` carries the mark into Explorer, pinned shortcuts and Alt-Tab; the window icon
   alone does not. The ICO container is hand-written, so a test parses its directory table
   byte-for-byte: headless Avalonia has no image codec, and its `Icon` property is a
   `HeadlessBitmapStub` that a truncated file would satisfy.
5. **The active translation engine is named on the main screen** (PR #7): a persistent
   `Translation: Gladia (cloud)` / `Translation: Qwen3.5 4B (local)` label sits beside `KeyStatus`
   in the masthead, separated by a hairline rule, refreshed by `RefreshKeyStatus()` and after the
   Settings dialog closes. Nothing previously distinguished the two paths before Start — the
   engine was inferable only from latency. It also names the two failure shapes
   (`— not downloaded`, `unknown model "…"`), which closes a quieter hole: demo mode discarded
   `plan.Error` and substituted `FakeMtProvider`, so an operator who selected a model they had
   never downloaded read plausible scripted translations with no hint their choice was inactive.
   Demo mode now says so in the status line as well. The mode dropdown deliberately still offers
   only Demo and Gladia: mode is the *audio* source, Settings is the translation engine, and a
   "Local" mode entry would reintroduce the vendor branching the capability model exists to
   avoid — plus there is no fully-local path to select until `WhisperCppAsrProvider` exists.
   *Superseded by 6.*
6. **The mode names the pipeline, not the vendor** (PR for #14). `Demo (scripted)` /
   `Gladia (live)` became five modes spanning both stages — demo; cloud·cloud; cloud·local;
   local·cloud; local·local — each stating in the row what it sends off the machine (nothing /
   audio / only text). Two things were wrong with the old pair: it named a company, which
   `.impeccable.md` rules out ("Precise. Calm. Unbranded."), and it hid half the pipeline —
   `Gladia (live)` meant cloud or local translation depending on a setting several clicks away,
   so the one question that has to be answered before a meeting with a Chinese supplier ("does
   audio leave this machine, does text leave this machine") was the one the UI would not answer.
   `TranslationPlanner` generalised into `PipelinePlanner`: one resolver mapping mode + settings
   to a provider *pair*, an availability reason, and both stage labels. **No new branching
   reached `MeetingSession`** — the mode is a preset, and cloud·local still works by the #7
   mechanism (`GladiaOptions.EnableTranslation = false` drops `Caps.Translation`, which is what
   makes the orchestrator route finals through `IMtProvider`). `MainViewModel` no longer holds a
   vendor-typed field at all: it keeps `IAsrProvider`/`IMtProvider` and disposes whichever pair
   the planner returned. The masthead's `Translation: …` label grew its missing half —
   `Transcription: … | Translation: …` on the same hairline rule and the same `Ink3` chrome ink —
   and the vendor-named `Gladia key: …` folded into the transcription label as
   `key “meeting-room”` / `key from the environment`, since the env var's own name is a brand.
   Settings is now grouped by stage (Transcription: the named key list, plus "Local transcription
   — not built yet"; Translation: the local-model catalog, plus a line saying there is no
   standalone cloud MT provider yet). The former `Gladia cloud` radio in the model list became
   `None`: cloud-vs-local is the *mode's* choice now, and that row only picks which local model
   the local-translation modes load.

   *Deliberate deviations.* (a) The issue's table has demo translating with a fake; demo instead
   keeps #7's behaviour — a downloaded model translates the scripted transcript, since with no
   local ASR that is the only way to rehearse a model without a key, and a model that was chosen
   but never downloaded still falls back loudly rather than silently. "Nothing leaves this
   machine" holds either way, which is what the table's column is actually about. (b) Unavailable
   rows recede in contrast (`Ink2`) but keep their reason at full legibility, and `ComboBoxItem`
   selection moved off FluentTheme's system accent onto a `Rule`-grey block — the mode list was
   the largest patch of non-speaker colour on the screen. (c) The mode combo is a two-line row
   (name over consequence) in *both* the popup and the closed box: `.impeccable.md` says nobody
   will hover for a tooltip, and Avalonia has no separate selection-box template, so the
   consequence is either always shown or effectively hidden.

   *Not built, still blocking `local · cloud`*: a standalone cloud `IMtProvider`. Gladia is a
   speech API with no text-only translation endpoint, so that row is unavailable for two reasons
   at once and says both.

7. **The four-column limit is enforced where it is chosen** (PR for #15). `.impeccable.md` freezes
   the host at four columns and `StartAsync` truncated with `Take(4)`, but the *selection* was
   unbounded: six ticked languages silently became four columns while all six were still requested
   as translation targets — and Gladia processes targets **sequentially**, so the two invisible
   ones cost latency on every final. The cap now lives in one place, `MainViewModel.MaxLanguages`,
   read by both the selection and the column loop, so `Take(…)` cannot drift from the picker.
   At the cap the remaining catalog rows are disabled and recede in contrast, the add-by-ISO-code
   row refuses and keeps what was typed, and the reason — *four columns maximum — deselect one to
   add another* — is printed between the two, because a click that does nothing and says nothing
   is exactly the failure this replaces. The refusal is enforced on `LanguageOption.IsSelected`
   itself rather than in the view, so a fifth cannot arrive by any other route; nothing persists a
   language list today, and a future restore path hits the same rule.

   *Consequence worth naming*: capping the selection also caps what phones can choose. The mobile
   page renders one column from a dropdown and could until now offer a fifth language that the host
   never displayed. That is a real reduction in reach, taken deliberately — a language the operator
   cannot see is a language nobody can correct — and it buys latency back on every final.

   *Fixed in passing*: a language typed as an ISO code reached the catalog but never
   `SelectedLanguages`, because the option arrived already selected and its `PropertyChanged`
   handler was attached afterwards. The flag stack, the summary and the room config all missed it
   until some other checkbox was toggled.

8. **Columns can be moved, mid-meeting** (PR for #15). The operator drags a column head to put the
   language they are actually reading where they are looking; the head is the grab handle, so the
   transcript under it stays scrollable. `MoveColumn` moves the `ColumnViewModel` itself, so every
   utterance already rendered travels with it — nothing is rebuilt, nothing re-resolved, and
   `ApplyUtterance` addresses columns by language, never by index, so a move during a live
   utterance cannot misroute it. One order is authoritative: a private list of codes that both the
   columns and the flag stack read, so the two can never disagree; a language selected after a
   reorder joins at the end, and the order survives Stop/Start.

   **Nothing goes on the wire.** `RoomConfig` carries the language *set*, phones render a single
   column chosen from a dropdown, and column order is host-local presentation — no `room.config`
   republish, no snapshot change, no client-visible effect at all.

   *Design.* The drop target is a 3 px ink rule standing in the gutter the column would be
   inserted into — the same rule vocabulary as the live record, not a coloured highlight, and no
   drag ghost. It overlays rather than occupying layout, so marking a target never reflows text
   under the operator's eye. Keyboard focus on a head is marked the same way (a rule down its
   left), after a first attempt using a `Paper` fill turned out to be invisible against `Sheet` in
   a headless render — a 4 % lightness step is not a signal at a metre.

9. **Chrome accents are ink, not the OS accent.** FluentTheme paints checked boxes, radio dots and
   list selection from `SystemAccentColor`, which on this machine is `#0078D7`. Reviewing the
   renders for changes 6-8, that blue was the most saturated thing on screen in both dialogs —
   four bright checkboxes in the language catalog out-shouting the speaker hues they sit beside,
   directly against ".impeccable.md — the only colour on screen is people". The accent ramp is now
   redirected onto the ink palette in `App.axaml` (`SystemAccentColor` → `Ink`, the Light steps →
   `Ink2`/`Ink3`/`Rule`), so hover and pressed states still differ, but by weight rather than hue.
   A test asserts every accent resource resolves to a palette colour, so the next control that
   reaches for the system accent fails the suite instead of the design review.

   *Deliberate addition.* Alt+← / Alt+→ on a focused head performs the same move. Drag stays the
   primary gesture, but a modal OLE drag on a trackpad mid-meeting is a poor single route, and it
   is unverifiable here: Avalonia's headless platform registers no `IPlatformDragSource`, so
   `DoDragDropAsync` returns `None` and a real drag cannot be simulated. The keyboard route is the
   one path a headless test drives end to end (real key event → handler → view model → order); the
   pointer handler is covered by a smoke test proving the gesture is harmless without a drag
   source, and the drop geometry is tested through `BeginColumnDrag`/`UpdateColumnDropTarget`/
   `DropColumn`, which is all the handler computes.

### Fixes in review (PR #7)

- **Native use-after-free on Stop.** `LlamaSharpTextGenerator.Dispose()` freed the llama.cpp
  weights without synchronising against an in-flight `GenerateAsync`. A translation tracked after
  `MeetingSession.DisposeAsync` snapshots `_pendingTranslations` can still be decoding when
  `MainViewModel.StopAsync` disposes the provider — freeing native memory under a live decode is
  an AccessViolationException and process death mid-meeting, with the transcript unexported.
  Disposal now acquires the same gate the decode holds and sets `_disposed` under it, and a
  `DisposeAsync` path keeps that wait off the UI thread. The `SemaphoreSlim` is deliberately never
  disposed: a caller parked in `WaitAsync` has to resume into a clean `ObjectDisposedException`,
  not a disposed-semaphore failure inside `Release()` that reaches the operator as "Translation
  failed". To make the lifetime rules testable at all, llama.cpp moved behind `ILlamaBackend`
  (`LlamaCppBackend`), so the generator's load-once/one-at-a-time/never-free-under-a-decode
  discipline is exercised by a fake instead of requiring a multi-gigabyte model.

- **A second download deleted the first one's file.** `ModelDownloadManager` derived its `.part`
  path from the model id alone and deleted it unconditionally in `finally`, so a second
  `DownloadAsync` for the same model destroyed the part file a still-running first download owned
  — the first then died at `File.Move` after however many gigabytes had transferred (on Windows
  the delete itself failed with a sharing violation and masked the original error). Each call now
  streams into its own `<file>.<guid>.part` and only removes the one it created; `Delete` sweeps
  any leftovers. The reachable trigger is fixed too: `SettingsWindow.OnClosed` cancels outstanding
  downloads, since MainWindow builds a fresh window and view model each time Settings opens, and a
  download left running behind a closed dialog was invisible, uncancellable, and collided with the
  Download button the next dialog offered.

- **`MtOutputCleaner` mangled two quoted spans.** A quote at each end is not the same as a quoted
  line: `"ISO 7599" gilt auch für "KX-4402"` came out as `ISO 7599" gilt auch für "KX-4402`,
  rewriting exactly the standard and part numbers the class promises to leave alone. Stripping now
  requires that nothing between the ends closes the span first.

- **Hermetic UI tests.** Demo-mode tests reached the developer's real
  `%APPDATA%\Kanal\settings.json` through `TranslationPlanner.Plan`, so a developer with a model
  downloaded had headless tests load a multi-gigabyte LLM. `MainViewModel` now takes settings and
  a `ModelDownloadManager` as constructor seams, in the shape of `RelayPublisherFactory`.
  `ModelDownloadManagerTests.CancelRemovesPartialFile` was also vacuous — it cancelled before the
  response existed, so `GetAsync` threw and the mid-stream cleanup path it claimed to cover never
  ran; it now cancels from inside the response body. `MainViewModel` also no longer leaks
  `plan.Mt` when Start returns early on a missing Gladia key.

### Plan

- [x] Gladia capability research
- [x] Local ASR/MT feasibility research + benchmarks
- [x] Column rendering fix — PR #2
- [x] Flag language picker — PR #3
- [x] App icon + mobile favicon — PR #10
- [x] **Local translation LLM support** — `LlamaSharpMtProvider` (in-process llama.cpp, no
      ollama/Python dependency) + Settings section to download/select a translation model
      (catalog: Qwen3.5-4B default, Qwen3.5-2B, Gemma 3 4B with licence note; A/B-tested); TDD.
- [x] **Modes describe the pipeline, not the vendor** — five modes over both stages,
      `PipelinePlanner` resolving mode → provider pair, unavailable modes shown/disabled with the
      reason and the privacy consequence in place, Settings grouped by stage (#14).
- [x] **Operator control over the language columns** — selection capped at four with the reason
      stated where it bites, and columns reorderable by drag (Alt+←/→ as the keyboard route),
      order host-local (#15).
- [ ] Standalone cloud `IMtProvider` (DeepL / Google / an LLM API reusing `MtPrompt`) — the
      second blocker on `local · cloud`, buildable independently of the local ASR work.
- [ ] Local ASR (`WhisperCppAsrProvider` via Whisper.net, VAD + LocalAgreement streaming) — after MT.
- [ ] Measure Gladia translation latency precisely once an API key is configured
      (`Kanal.Doctor -- gladia <wav>` dumps timestamped raw JSON).
- [ ] Streaming/segmented translation to cut the 2–3.5 s cross-language delay.

### In flight (other sessions)

- macOS audio capture (`feat/macos-audio-capture`): CoreAudio backend, `AudioCaptureFactory`,
  cross-platform mic list.
