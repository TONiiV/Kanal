# Kanal — progress, plan & design changes

Living log. Update in the same PR as the work it describes. Newest section on top.

---

## 2026-09-07

### The meeting title takes the top of the transcript ([#69](https://github.com/TONiiV/Kanal/issues/69))

#66 had already put the room's overlapping circular language flags at the top of the transcript.
This gives that row its left half: the meeting title, set at 22 px semi-bold, sharing one grid row
with the flags. The starred column is the title, so a long German compound truncates and the flags
keep every pixel they asked for.

There is no project-name header above it and there never was — #64's story 31 asks for its absence,
and nothing in the host had built one. The transcript/summary/decision tabs of story 38 are the
same: never built, so there was nothing to remove. Both are recorded here rather than silently
counted as delivered.

The title names whatever transcript is on screen: `SelectedMeeting.Title` when the operator is
browsing a record, otherwise the id of the room that is loaded, otherwise a placeholder — never
blank. `LoadedRoomId` is cleared when the *next* room opens rather than when this one stops,
because `Stop` deliberately leaves the session in place for rename, merge and export; a row reading
"New meeting" above a finished transcript would name the wrong thing. Generating a title with a
local model and renaming it by hand are [#73](https://github.com/TONiiV/Kanal/issues/73); this is
the row that displays whatever those produce.

One existing guard moved rather than weakened. `SettingsTheJoinCodeAndTheFlagsMovedOutOfTheirOldHomes`
asserted the flags button was docked to the top of the transcript; the flags are now inside the
title's row, so the assertion is on that row instead. What it defends is unchanged — the flags
belong at the top of the transcript, not in the toolbar or the side panel.

### The left sidebar becomes a workspace ([#69](https://github.com/TONiiV/Kanal/issues/69))

#65 left the left sidebar as a header and a settings button. It now reads top to bottom: the brand
lockup and collapse control, a search box with the new-meeting button beside it, the workspace
picker with its add menu, the meeting list, and settings at the foot. `WorkspaceSidebarViewModel`
sits on #68's `WorkspaceStore`, so nothing here invents a second idea of where records live.

The new-meeting button is the one solid control in the sidebar. It began as a ghost `+` like the
add menu beside it and the two were indistinguishable at a glance, which is the failure the
criterion names — creating a meeting has to be one clear action, not something the operator hunts
for. Weight, not colour, separates them.

Selection is styled off Fluent's accent onto ink and paper: a selected meeting takes a paper fill
and a two-pixel ink bar down its left edge. An accent-blue row would be the only chrome on screen
carrying colour, and colour here belongs to people.

Import and export are file moves, not parsers. The add menu's "import meeting record" makes a new
meeting named after the file and copies it into the meeting's folder; a meeting's own ellipsis menu
does the same into that record, and exports by copying the transcript back out. Reading a foreign
transcript into `RoomState` is a different job, and neither #69 nor the record model asks for it
yet — a parser written now would have no consumer.

Search filters the list held in memory and never touches disk; `Refresh` is the only path that
re-reads. A meeting folder that cannot be read is counted in a note above settings rather than
swallowing the meetings that still read — the store already separates the two, and the sidebar
keeps that separation instead of collapsing it into an empty list.

**What this does not do yet.** Selecting a meeting sets `SelectedMeeting` and nothing else. The
criterion "selecting a meeting shows its transcript and summary" needs an answer to a question the
design has not settled — what happens to a running meeting when the operator browses a past one —
and summaries do not exist until [#34](https://github.com/TONiiV/Kanal/issues/34). The meeting
title row and the language flags, the other half of #69, are their own change.

### The assistant sidebar becomes points and speakers ([#71](https://github.com/TONiiV/Kanal/issues/71))

The right sidebar held one list — speakers, plus the merge box. It now carries two tabs, "Points and
decisions" and "Speakers", the second being what was already there. The tab header names the list,
so the pane's own `SPEAKERS` heading and its rule went with the move.

`MeetingInsights` (`Kanal.Core/Insights`) is a plain aggregate over `RoomState`, not a provider
interface. #34 will bring a model; inventing an `IMeetingAnalyst` now would be an abstraction with
one imaginary implementer, and the panel would still have nothing to show. What the panel does
instead is state the absence: with no model connected it prints that sentence where the items would
go, rather than an empty list an operator would read as "no decisions were made". `Nothing raised
yet` is a *different* message, shown only once something is connected — the two states must not
collapse into one blank pane.

Two rules hold the record honest:

- **An item that leads back to nothing said is refused, not shown.** `Record` filters the source
  utterance ids against the room and drops the item entirely if none survive. The candidate state
  exists to stop a guess reading as a commitment; an item with no source behind it is that same
  failure with nothing left to check it against.
- **A decision arrives as a candidate and only a person makes it a commitment.** Every item renders
  its state beside its kind, and the confirm control is present only while it is a candidate. The
  supporting utterances are printed under the item, so "leads back to what was said" is something
  the operator reads rather than something they have to trust.

Two Avalonia notes, both cheap to re-break:

- The selected pane is a logical child of *both* its `TabItem` and the `TabControl`'s presenter, so
  an unfiltered walk of the logical tree meets the same control twice. The tests de-duplicate.
- Fluent's selection marker sits flush under the header content, and `TabItem.Padding` moves the
  marker with the text rather than away from it. The clearance comes from a bottom margin on each
  header instead; without it the rule strikes through the descenders of "Points".

Dismissing takes an item off the panel without deleting it — `MeetingInsights` keeps it in the
dismissed state, and a dismissed item can never be confirmed afterwards. The third tab the design
sketches, the listening agent itself, is not built here; `docs/design/meeting-intelligence.md` is a
discussion document and its open branches are not decided.

### Settings becomes six tabs ([#67](https://github.com/TONiiV/Kanal/issues/67))

The settings window was one 640 px column of eight stacked sections, so finding the log level meant
scrolling past every API key and both folder pickers. It is now a `TabControl` with
`TabStripPlacement="Left"`: General, Audio input, Transcription, Translation, Summarisation,
Workspace. Nothing was dropped — a test walks every tab and asserts each of the eighteen named
controls has exactly one home, so a setting cannot be lost or accidentally duplicated into two panes.

**The tab list departs from the ticket in one word.** #67 names the third tab "Local transcription".
The only transcription settings that exist today are the Gladia cloud keys, and the ticket gives them
no other tab; putting them under "General" would file the most-used setting in the drawer for
odds and ends. The tab is therefore **Transcription**, holding the cloud keys now and a `LOCAL MODELS`
section that says local transcription is not built yet — which is the home #72 will fill. The
alternative, keeping the ticket's name and moving the keys, buys literal compliance at the cost of
the arrangement the rest of the tabs follow: one pipeline stage per tab.

Summarisation is a pane with no controls at all, only a sentence saying it is not built and what will
be chosen there when it is. A test finds every pane that offers no interactive control and requires
it to say something instead, so an empty tab cannot ship by accident.

Two Avalonia notes worth keeping. A `TabItem`'s content stays a **logical** child of the tab whether
or not the tab is selected, so `GetLogicalDescendants` sees all six panes at once - which is why the
existing `SettingsWindowBindingTests` still find their controls without selecting anything, and why
the test that asserts selection actually swaps the pane has to read the **visual** tree instead.
Fluent draws the selected tab's marker in the system accent colour; the host is ink and paper, so
`Border#PART_SelectedPipe` is restyled.

Settings opening from the foot of the workspace sidebar was already done in #66; this ticket's first
acceptance criterion was met there.

### The centre toolbar becomes marks, and the two status bands go ([#66](https://github.com/TONiiV/Kanal/issues/66))

The bar #65 left behind was the old labelled row moved into a 766 px column, and it scrolled
sideways at the default window size to reach export and settings. [The approved
design](design/meeting-workspace.md) forbids both the scrolling and the wrapping, and its
prototype settles the arrangement: processing and capture pickers at the left, transport with the
microphone beside it in the middle, join QR at the right, and nothing else.

**The narrowing rule is three column definitions.** The bar is `*,Auto,*`. A starred column gives up
width when there is not enough; an `Auto` one does not. So the transport keeps every pixel it asked
for at any window size, and the two side clusters — which are `ClipToBounds` and aligned outward —
lose their innermost controls instead. There is no `ScrollViewer` left in the bar. The two expand
chevrons stay docked outside the three columns, because a collapsed sidebar is reachable only
through them and they may not be what gets clipped.

**One control per choice, not three.** The mode picker was a 300 px combo box plus a `?` flyout. It
is now a 160 px combo whose `SelectionBoxItemTemplate` shows a chip mark and two words
(`Local · local`), while its dropdown carries the full name and the privacy consequence per row.
Capture mode is the same trick at 62 px — a screen mark, with the two profiles and their guidance in
the dropdown. The `?` flyout stays: `modes.intro` is where the operator learns that captions always
reach the phones as text through the relay, and that is a paragraph, not a tooltip.

**Colour enters the chrome, once.** `Record` (`#C42B22`) and `Hold` (`#C08A12`) are the only
non-ink brushes on a control. The design fixes them — a red record dot idle, a yellow pause and a
red stop while running — and the worktree's `CLAUDE.md` says the approved design supersedes the
older chrome-colour guidance where they conflict. Both sit off the speaker palette so a transport
mark is never mistaken for a person, and both marks carry a shape, a tooltip and an accessible name,
so colour is never the only signal. They keep their brush through hover and press: which mark is
record and which is stop is the last thing that may change under the pointer mid-meeting.

**`CompactState` replaces `LiveNoticeText` and `ShowProcessingNotice`.** The two full-width Ink bands
are gone. Their strings were written to be shouted across a band — `RECORDING, TRANSCRIPTION AND
TRANSLATION ARE LIVE` — and none of them fits a line beside the transport, so the seven
`*.notice` keys and `paused.band` were replaced by six short `state.*` ones. Every branch is spelled
out rather than collapsed: a line that says `Live · saving audio` while nothing is being written is
the one failure the property exists to prevent.

**Three controls left the bar, and each had to land somewhere real rather than nowhere.**

- Settings is at the foot of the workspace sidebar, where story 19 and the design put it. #67 still
  owns what the dialog looks like inside; this is the button and its new home.
- The room's language flags are at the head of the transcript, where the design puts them next to
  the meeting title. #69 adds the title beside them. Leaving them in the bar was the alternative,
  and it did not fit: at 1320 px with both sidebars open the left cluster has about 310 px, and mode
  plus capture plus flags is closer to 340 — the flags would have been clipped in the default state.
- Export is behind an ellipsis in the right cluster. It belongs on the meeting record's own menu
  (#69), and this is the same affordance parked one place early rather than two labelled buttons.

The join QR moved out of the assistant sidebar into the bar, which #71 needs anyway — that sidebar
becomes points, decisions and speakers.

**What this deliberately does not do.** The capture picker and the `?` flyout are the first things
clipped when the window narrows or the status line grows; both are set before a meeting rather than
during one.

The three paused variants the bands used to distinguish - held while recording, held while recording
only, held while transcribing - collapse into one `state.paused`. The line describes what is
happening now, and while paused nothing is being written whichever of the three preceded it; naming
the suspended activity would put "saving audio" on screen at the one moment it is false. The cost is
that the line no longer says whether a WAV was open before the pause.

The marks also shift sideways when the status line changes length, because the line shares the
transport's `Auto` column: pressing pause swaps "Live" for "Paused - nothing captured" and the marks
slide about 68 px left. Reserving a fixed width for the line would hold them still, but at the
default 1320 px window the bar has only about 50 px of slack per side, and reserving the widest
state's 135 px would clip the capture picker permanently. Holding the marks still is worth less than
keeping a control reachable, so this stays. Should the marks need to be fixed, the answer is a
narrower bar budget - a sidebar collapsed by default, or the state line moved off the bar
entirely - not a spacer paid for out of the left cluster.

The recording state machine is untouched — start, pause/resume, stop, load cancellation
and the stop-in-progress guard are the same code, and `MeetingSessionTests` still holds the line
that a paused session reaches neither the ASR provider nor the WAV writer.

### An expired room says so once, instead of refusing 2000 times

- Reader tickets last 12 h. Past that the phone's backoff loop retried forever at its 15 s cap,
  collecting a 401 each time and showing "reconnecting" — a tab left open overnight made roughly
  2000 pointless requests and never told the participant the room was simply over. Item 3 of #40,
  now #70.
- The room object closes an expired socket with **4001** rather than 1000. A tidy normal closure is
  indistinguishable from every other tidy closure, so the phone had nothing to reason about and
  reasonably guessed "reconnect". 4001 is in the application-private range and is terminal.
- The phone acts only on that authenticated gateway decision. A failed upgrade still appears as
  1006 and keeps the existing retry behavior; it is never reclassified from an unverified ticket
  timestamp or the phone clock.
- Every other close code keeps the existing backoff. A locked phone, a lost cell and a roamed
  network all still have a live room to come back to, and must still come back to it. The
  transcript stays on the page either way.
- Tests: the gateway side is driven through the room object directly, because a 12 h ticket cannot
  be aged out through `?action=stream` inside a test — the Worker refuses it long before the room
  sees it. Both paths that can close a socket are covered: the expiry **alarm**, which is the only
  one that fires in the real overnight case where nobody is publishing, and `publish()`. The page
  test reads the close code from the shipped HTML rather than restating it, so the two cannot drift
  apart.

### Meeting records get a home on disk ([#68](https://github.com/TONiiV/Kanal/issues/68))

- `WorkspaceStore` is the single seam between the application and meeting records on disk. Layout,
  under a folder the operator picks: `kanal-workspace.json` for the workspace's identity, and
  `meetings/<id>/meeting.json`, one folder per meeting holding its own artefacts. Every file
  carries a schema version.
- The list of *which* folders are workspaces lives outside them all, in the application's own
  profile. A workspace on a drive that is not plugged in has to keep its row in the sidebar, so the
  list cannot be a scan of folders that happen to be reachable.
- Meeting folders are named by id, never by title. Two meetings about the same thing on the same
  day is the ordinary case in this room, not the odd one, and neither may land on the other.
- Failures are reported next to whatever could still be read, never instead of it. An empty list
  where a year of meetings used to be is the one outcome the store may never produce, so a vanished
  workspace or meetings folder, an unopenable file, a corrupt payload, an orphaned meeting folder,
  and an unsupported schema each produce a `StoreProblem` while the surviving records still list.
  `StoreProblem` distinguishes what the operator can act on: `Invalid` (a blank name), `NotFound`, `FolderMissing`
  (plug the drive in), `Unreadable`, `Unwritable`, `UnsupportedVersion`.
- Three things a JSON deserializer does quietly that this store refuses. A missing or unsupported
  schema version is not half-parsed, because the next save could write unknown fields back as loss.
  A record whose id or title is simply absent is not a record: `System.Text.Json` fills a missing
  field with null, which would list a phantom meeting whose folder no later operation could open.
  And a record has to agree with where it is: an id becomes a folder name, so a hand-edited
  `"id": ".."` would otherwise have listed as an ordinary meeting whose Delete button took the
  workspace and every transcript in it with it. Ids are plain `[A-Za-z0-9_-]` tokens, and the folder
  a record sits in is the id that counts — which also stops a duplicated folder from listing one
  meeting twice, where only one of the two could ever be renamed or deleted again. Stored artifact
  locations are portable file names inside that folder; the public meeting record resolves them to
  full transcript and audio paths and refuses any path that escapes its meeting folder.
- Adding a folder that already holds a workspace adopts it under the name already on disk — picking
  last year's folder means "open this", so the stored name outranks the one typed into the box. A
  *copy* of such a folder — a restored backup, a share mounted twice — carries the original's id, so
  it is reported rather than adopted; registering it by id alone would silently repoint the one row
  at the copy and leave the original's meetings unreachable. A workspace that has simply *moved* is
  not that: the copy is only a copy while the folder already listed is still there. Paths are stored
  canonical — absolute, without a trailing separator, and with every symlink on the way down
  resolved, because on macOS `/tmp` and `/var` are themselves links and one folder reached by two
  spellings would otherwise become two workspaces over one set of files, and the second could delete
  the first's meetings. One folder holds one row, checked on both routes in — a `kanal-workspace.json`
  restored into the wrong folder cannot take it over. The list, not the folder's own marker file,
  holds the workspace's name: a rename made while the drive was out could not reach the marker, and
  reconnecting must not undo it.
- `ForgetWorkspace` is the workspace-removal operation: it removes the row and touches no file. The
  folder is the operator's, may be a share, and may hold the only copy of the transcripts; removing
  it from the sidebar must stay reversible. Meetings, which Kanal itself created, do delete.
- One limitation, taken deliberately. A registry file that cannot be parsed at all blocks every
  operation, including `ForgetWorkspace`; there is no repair from inside the application. A single
  unreadable *row* is reported and skipped, so it cannot hide its neighbours, but the file as a
  whole is refused rather than replaced. `SettingsStore` copies an unreadable file aside and carries
  on with defaults; doing that here would answer "you have no workspaces", which is the one thing
  this store may not say.
- Nothing is wired to the UI yet. This is the model the workspace sidebar ([#69](https://github.com/TONiiV/Kanal/issues/69))
  and meeting titles ([#73](https://github.com/TONiiV/Kanal/issues/73)) will sit on.

### The window becomes three declared regions ([#65](https://github.com/TONiiV/Kanal/issues/65))

The host window was one `DockPanel`: a top bar, a bottom status line, a fixed 272 px assistant
panel, and a transcript that was whatever remained. Batch 1 of
[#64](https://github.com/TONiiV/Kanal/issues/64) needs a workspace sidebar as well, and a
leftover-space transcript cannot survive two collapsible neighbours — that is precisely how the
prototype failed, and [the approved design](design/meeting-workspace.md) rules it out.

`MainWindow` is now a five-column grid: workspace sidebar, splitter, centre, splitter, assistant
sidebar. Only the centre column is starred, and it carries a `MinWidth` of 320 px, so no
combination of collapse and drag can squeeze the transcript to nothing — the grid refuses before
the layout does. The toolbar and status line moved inside the centre column with the transcript,
which is why they now stop at the sidebar edges rather than spanning the window.

Collapse and width live in one `SidebarViewModel`, instantiated twice — the left and right
behaviours are identical, and a mirrored `Left*`/`Right*` pair would be the same code written twice:

- A width is clamped to 180–480 px on the way in, so a drag cannot leave a sidebar at a width the
  next expansion has to inherit. The prototype's range is a calibration start, not an acceptance
  number, and the constants are one edit away.
- Collapsing sets the column to zero but keeps the chosen width, so re-expanding returns to it.
  The grid writes a collapsed column back as zero on every layout pass; the setter ignores that
  write rather than clamping it up to 180 and losing what the operator chose.
- The bounds are published to the `ColumnDefinition` as well, because `GridSplitter` takes its drag
  limits from the definition rather than from the binding source. Without that the pointer would
  keep travelling past 480 while the column snapped back — the handle and the edge coming apart.
  Those column bounds fall to zero while the sidebar is collapsed, or a `MinWidth` of 180 would
  hold the column open against the collapse.
- `CanExpand` gates the two expand affordances in the centre toolbar. They sit *outside* the
  toolbar's scroller: a collapsed sidebar is reachable only through them, so they may not be the
  part that scrolls out of sight.

The three header rules land on one line because the two sidebars **follow the toolbar's measured
height**, rather than all three sharing a constant. A constant lines them up only while the bar fits
it: with a microphone mode selected and no local model downloaded, the mode picker's description
wraps and the bar measures 96 px against the sidebars' 76 — a five-to-twenty pixel step in the one
rule that runs across the whole window. `Grid.IsSharedSizeScope` was tried first and does not
equalise these rows. So the bar reports its own height to the shell and the sidebars bind to it,
which also survives whatever #66 does to the bar's contents.

The window's floor is computed from the widths the sidebars currently hold, not fixed. Two sidebars
dragged to 480 demand 1290 px; against a fixed 690 the window could be shrunk until the assistant —
and the only control that reopens it — was off the right of the screen with no way back. The two
splitter columns are pinned rather than `Auto` for the same reason: `Auto` measured a pixel wider
than the handle, and a floor cannot account for a column whose width it does not set.

The two sidebar headers are mirrored rather than parallel. Each collapse control sits on the edge
its sidebar shares with the meeting and each title on the outer margin, so the two chevrons flank
the centre and point away from it. The assistant header previously carried both on the left, which
read as an accident rather than a choice once the workspace header stood beside it.

Deliberate limitations, all for the ticket queue rather than this PR:

- The workspace sidebar is a header and nothing else. Its contents — search, project selector,
  meeting list, settings — are [#69](https://github.com/TONiiV/Kanal/issues/69) and
  [#67](https://github.com/TONiiV/Kanal/issues/67). The header reads `WORKSPACE` as a placeholder;
  the design puts the brand lockup there, and #69 replaces it.
- The two full-width status rows are still present. The design removes them in favour of a compact
  state beside the transport, which is [#66](https://github.com/TONiiV/Kanal/issues/66)'s
  acceptance criterion, not this one's.
- **The centre toolbar's horizontal scrollbar is now visible at the default 1320 px window**, where
  before it appeared only on a narrow one: the bar has 776 px instead of the whole window. The
  design asks for clipping with the transport held visible instead, and #66 carries that same
  criterion — but it gets there by replacing every labelled control in the bar with an icon mark.
  Fitting the labelled bar into 776 px would be work the next ticket throws away, so this ships as
  a known regression rather than a silent one. The expand affordances are outside the scroller, so
  neither sidebar becomes unreachable in the meantime.

### Meeting workspace prototype approved and archived

- `/to-spec` synthesis published as [#64](https://github.com/TONiiV/Kanal/issues/64), labelled
  `ready-for-agent`. The [local specification](specs/meeting-workspace.md) contains 60 user stories,
  implementation/testing decisions and explicit unresolved future scope. Its prototype viewing
  instructions were subsequently updated to the design-document location.
  It links the existing speaker, replay, local-ASR and summary work items; no further interview
  is required to begin the confirmed UI scope.

- User confirmed the final B-based UI design. The authoritative specification is
  [Meeting workspace design](design/meeting-workspace.md); it replaces the iterative layout notes
  formerly collected here. Approval covers the visual direction, not completion of production code.
- At the user's request the approved [HTML prototype](design/meeting-ui.prototype.html) now lives
  beside the design documents and opens directly in a browser. The CMD launcher is removed.
  The earlier archive commit `4c4d3db` remains historical; the temporary viewing worktree is retired.
- [ADR 0051](adr/0051-peer-meeting-workspaces.md) records the accepted peer-workspace ownership
  boundary. [CONTEXT.md](../CONTEXT.md) captures domain terminology.
- Newly requested local-model titles (manual rename/regenerate) and a future listening-agent tab
  are captured in [Meeting intelligence design](design/meeting-intelligence.md). Lifecycle, context,
  proactive-action and sharing policies are still under interview; no provider implementation
  choice is implied by these notes.
- Prototype verification to date: JavaScript syntax and HTTP availability only. Production tests,
  visual validation, persistence, ASR integration and Agent functionality remain outstanding.
- Existing unrelated worktrees and production code were preserved. Documentation changes require
  no changelog feature claim.
- Speaker recognition updates the existing [#13](https://github.com/TONiiV/Kanal/issues/13);
  sentence audio replay is tracked in [#63](https://github.com/TONiiV/Kanal/issues/63). Confirmed
  goals and open scope are recorded in [Meeting evidence](design/meeting-evidence.md).

## 2026-09-04

### The control bar reads as two groups

- The toolbar is a `DockPanel` with a left cluster (transport, mode, languages) and a right cluster
  (microphone, export, settings) rather than one undifferentiated horizontal run. What the operator
  reaches for mid-meeting is now separated from what is set up once and left alone.
- The horizontal `ScrollViewer` is unchanged and still stretches the bar to the viewport, so the
  right cluster holds the edge at normal widths and the whole row scrolls when a long locale makes
  it too wide to fit. Nothing wraps and no control is dropped.
- The left cluster is declared first. Avalonia navigates the tree, not the laid-out position, so
  declaring the right cluster first put Export and Settings ahead of Start in Tab and screen-reader
  order while looking identical on screen.
- A 16 px margin holds the two clusters apart. Once the bar overflows, the `DockPanel` arranges at
  its extent and the clusters would otherwise meet at zero — at 1280 px, the documented minimum
  host width, the language flags sat flush against the input label with no space and no rule.
- Headless tests assert which cluster each control belongs to, that the clusters are declared in
  reading order, and the two measurable claims the arrangement rests on: the right cluster ends at
  the viewport edge while the bar fits, and the clusters keep their gap once it does not. Each
  width states which of the two halves it exercises, so a metric change cannot quietly push every
  case into one of them.
- The capture profile joins the left cluster beside the mode it qualifies; the computer-output
  selector joins the input selector on the right, and the JSON export sits beside the Markdown one.

### Language rows sit on one centre line

- Fluent pins a checkbox's tick box to the top of whatever height the control is given — as a local
  value inside its template, so no style can override it. The language rows set `MinHeight="42"` on
  the checkbox itself, which left the tick box five pixels above the flag and the name it belongs
  to. The offset was the same five pixels on every row, `(42−32)/2`; what varied was nothing, which
  is why it read as a systematic mistake rather than a glitch.
- The height now belongs to the row border and the checkbox keeps its natural height, centred
  inside it. Tick box, flag and name share one centre line. The band is `MinHeight="43"`, because
  Avalonia counts the hairline inside it: 42 px of content over a 1 px rule, the pitch the rows
  already had.
- The row's click target is the checkbox rather than the full band — the full width still toggles,
  the outer five pixels above and below no longer do. This is a choice, not a constraint: a style
  putting `Margin="0,5,0,0"` on the tick grid aligns the rows while keeping the full-height target.
  It was not taken because it depends on the internal shape of another library's template and would
  fail silently, and without a layout test in the suite nothing would say so.
- The fix holds while the row's content stays within the tick grid's fixed 32 px; the comment in
  the view names that dependency, since no test can.
- No test: this is layout, which `CLAUDE.md` keeps out of the suite. It was verified by measuring
  the rendered row geometry headlessly, before and after, on all twelve rows.

### Native meeting audio, slice 1: capture intent and informed Start

- Capture is now an explicit choice independent of the cloud/local speech pipeline: an in-room
  microphone profile and a discoverable online-meeting profile with separate microphone and
  computer-output selectors. Online Start remains visibly unavailable until the native adapters
  land, rather than opening a room that cannot hear the remote side.
- Every real room requires a fresh all-participant consent attestation. Online guidance says that
  remote participants cannot see Kanal and must be told verbally or in meeting chat; headphones
  and Do Not Disturb are stated alongside the whole-output capture choice.
- Live transcription has its own host and phone notice even with WAV recording disabled. Recording
  replaces it with stronger wording, pause changes both to held, and snapshots/cache preserve the
  state for late joins and reconnects. The two phone pages remain byte-identical.
- Markdown and JSON exports carry the capture profile and confirmation timestamp. In-room WAV
  recording retains its existing default; online WAV recording has a separate, off-by-default
  opt-in in Settings.

### Native online-meeting audio is the accepted path

- ADR 0050 replaces the proposed BlackHole/VB-Cable primary path with native microphone plus
  system-audio capture. Virtual drivers remain fallbacks; they do not solve the local-microphone
  half of an online call and leave machine-wide routing behind after the meeting.
- The work is split into three independently reviewed slices: capture profile and disclosure,
  Windows/macOS native system-audio adapters, then bounded synchronisation and mixing into the
  existing 16 kHz mono speech interface. Preserving local/remote channels is explicitly later.
- Windows uses WASAPI loopback. macOS 14.2+ uses a Core Audio process tap, while macOS 13 retains
  online capture through a ScreenCaptureKit compatibility adapter; older systems keep in-room
  microphone capture only.

### Open-source acknowledgements have their own window

- Settings now links to the open-source project index instead of rendering the entire index at the
  bottom of its already long form. The owner-modal window follows the changelog's dimensions,
  typography, rules, scrolling and guarded single-instance interaction, while retaining every
  project name, licence and source URL.
- The entry point is available in English, Chinese, German and Polish. Headless UI tests hold both
  sides of the change: every notice is visible in the dedicated window, and none remains embedded
  in Settings.

### A warm rounded app-icon tile

- Desktop, dock and browser icon derivatives now place the unchanged multicolour mark on a clean
  warm-beige rounded-square tile, with transparent outer corners and no border, shadow or lettering.
- The splash remains the standalone transparent mark, keeping the startup lockup visually light.
  Both treatments are generated deterministically from the same checked-in PNG source.

### One mark, generated for every surface

- The user-supplied 1536 × 1024 transparent PNG is now the single brand source. Its five incoming
  routes and three outgoing arrows retain the original gradients and edge treatment; no wordmark,
  font or generated replacement lettering is included. This is a deliberate, tightly scoped
  exception to the speaker-colour rule: the combination exists only inside the standalone mark.
- `design/kanal-icon.py` centres that PNG on a transparent square before deriving the splash mark,
  platform PNG, ICO, ICNS and two inlined web favicons. No SVG is generated or shipped, and the
  README displays the PNG directly.
- ICNS generation no longer depends on running `iconutil` on macOS. The script writes its modern
  PNG-backed chunks directly, which makes the full suite reproducible on every development and CI
  platform. A generator contract test checks the source dimensions, formats, absence of SVG,
  transparent canvas and byte-for-byte idempotence; CI rejects any generated-asset drift.

### A real startup surface

- Kanal now opens on a small, undecorated splash window while the main host view model and its
  device watcher are constructed. The main window is shown before the splash closes, so the
  desktop lifetime never sees a last-window gap; there is no artificial delay.
- The splash follows the reference's vertical hierarchy: application mark, lowercase `kanal`, a
  short rule and the README's canonical line: `One room. Every language.` The mark comes from the
  same packaged resource as the application window rather than a second embedded asset.
- A headless UI test holds the lowercase name and exact tagline and verifies that the packaged icon
  resolves.

### The meeting window has one control bar and four focused views

- Removed the duplicate `KANAL` wordmark from the window content and combined transport, mode,
  language, microphone, export and settings controls into one compact horizontal icon bar. The bar
  scrolls instead of wrapping, so German, Chinese and Polish labels never hide a meeting control.
- Split the 500-line main window into `IconBarView`, `MeetingRoomView`, `SidePanelView` and
  `StatusBarView`. Dialog ownership stays with the icon bar, column following and reordering stay
  with the meeting room, and `MainWindow` now owns only the shell, export picker and lifetime.
- Added a headless composition test that holds the four-region boundary and the absent in-content
  wordmark. Existing view-model tests continue to own room and column-order behaviour.

### Host, tools and tests target .NET 10

- Moved all eight projects from `net9.0` to `net10.0` and CI's `setup-dotnet` from `9.0.x` to
  `10.0.x`. No source change was needed: Avalonia 12.1.1, LLamaSharp 0.27.0, NAudio.Wasapi 2.3.0,
  CommunityToolkit.Mvvm, QRCoder and xunit.v3 all resolve unchanged, the solution builds with zero
  errors, and the 191 core + 108 UI tests pass. The 132 `xUnit1051` analyzer warnings are unchanged
  from the net9.0 baseline — the bump introduces no new diagnostics.
- `Directory.Build.props` keeps `RollForward=Major` for the same reason it was added, now stated
  against 10.0: a machine carrying only a newer runtime should still launch the host.
- Dropped the `DOTNET_ROLL_FORWARD=Major` workaround from `CLAUDE.md`: the SDK on the dev machines
  and the target framework now agree, so plain `dotnet test` works.
- `docs/PRD-v0.3.md` keeps its transcribed ".NET 9 + Avalonia" line — it is a dated transcription,
  not a living document — but now carries an inline note that the implementation moved to .NET 10,
  so the repo's authoritative requirements reference cannot be read as current on that point.

### VS Code debugging is configured in-repo

- Added `.vscode/` with F5 targets for the host (demo mode, and a live mode reading a gitignored
  `.env` for `GLADIA_API_KEY` / `KANAL_RELAY_*` / `KANAL_WEB_URL`), a prompted-argument launch for
  `tools/Kanal.Doctor`, and process attach; build/test tasks over `Kanal.slnx`; C# Dev Kit and
  Avalonia extension recommendations; and search/watch exclusions for `.worktrees/`, `bin/`, `obj/`.

## 2026-08-05

### A comment now has to earn its place

- `CLAUDE.md` gains a comment policy under **Working practices**: the default is no comment, and one
  survives only by carrying what the code cannot — a trap that would be "fixed" back if unrecorded,
  an external constraint or licence attribution, or a counter-intuitive decision whose rejected
  alternative looks better at a glance. XML doc that restates a signature, narration of the lines
  below it, atmospheric description, divider banners, and commented-out code go.
- Rationale that needs a paragraph now has a stated home: this log and the PRD, where design history
  is already kept and maintained, rather than a source file nobody re-reads on the next edit.
- No project sets `GenerateDocumentationFile` or `DocumentationFile`, and none escalates warnings to
  errors, so removing XML doc cannot break the build; no test asserts on comment text or doc
  presence. `RelaySecurityTests` scans `src/**/*.cs`, but only for the absence of credential
  strings.
- Documentation only. The codebase sweep the rule will be judged against is a separate PR; no
  comment was deleted here.

---

## 2026-08-04

### What Kanal is built on, named on screen (issue #35, 3 of 3)

An index at the bottom of Settings: project, licence, and where to read it. The people running this
in a meeting are the ones handing the binary around, so it belongs in the application rather than
only in a repository.

- Each notice carries the NuGet ids it covers, so the list can be read against the project files by
  hand. References excluded from the shipped build are left out — they are not distributed, so they
  carry no obligation. **Nothing enforces this**: a coverage test was written and then dropped on
  the owner's call, so adding a dependency means adding its entry in the same PR, and the list goes
  stale silently if that is forgotten.
- What no package scan can see is listed by hand and marked as such: code that arrives inside
  another package (Skia, HarfBuzz, ANGLE, llama.cpp) and OpenCC's conversion table, which is
  compiled into `Kanal.Core`. ANGLE ships in every Windows build and its licence is explicit that a
  binary redistribution reproduces the notice.
- One entry was removed rather than guessed at: a debugging aid whose package carries no licence
  file, no `<license>` and no `<licenseUrl>` — only a commercial copyright. Naming a licence that
  cannot be substantiated, on a screen headed "open source", is worse than omitting the entry. It
  is excluded from Release builds anyway.

Open, and a call for the repository owner rather than a defect: this is an index, and MIT and BSD-3
ask for the notice *text* to travel with the binary while Apache-2.0 asks for a copy of the licence.
Discharging that means shipping a `THIRD-PARTY-NOTICES.md` of a few hundred lines and somewhere to
read it. Until that is decided the wording here, in the README and in the code describes an index
and claims nothing more.

### A changelog you can read in the room (issue #35, 2 of 3)

The question "did something change since last week?" is asked in the room, by the person who
noticed — and the laptop running a meeting is not the machine anybody browses a repository on.

- `CHANGELOG.md` is embedded in the executable and parsed for a dialog behind Settings → Version.
  The file stays plain Markdown, readable on GitHub and in a diff, rather than becoming a data
  format only this parser understands: `## <version> — <date>` headings with ordinary bullets.
  Wrapped lines fold into their bullet, sub-bullets lose their markers, and inline code and
  emphasis are stripped — a dialog is a TextBlock, not a renderer, and the first cut put
  half-sentences and stray backticks on screen.
- `<Version>` in the host project is what the About section shows, and a test holds it against the
  newest changelog heading, so releasing is "write the entry, bump the version" and cannot be
  half-done. Dates are ISO and invariant: against the ambient culture a Thai or Umm al-Qura locale
  printed a year matching nothing in the repository.
- The changelog is a host surface like any other, so the unbranded rule reaches it: a test fails if
  a release entry names a vendor.
- **A version is not a release.** Nothing has shipped yet, so the file carries one heading —
  `1.0.1`, everything Kanal does on the day it ships — rather than a history reconstructed from
  commits that no operator ever received. The heading stays undated until the release is cut, and
  the dialog says so in the operator's language instead of leaving the line blank. From here each
  PR that adds a feature, fixes a bug or makes something measurably better appends its own bullet;
  refactors, tests and docs append nothing. The convention is in `CLAUDE.md` next to the progress
  log, and only the newest heading is allowed to be undated.

Left for the repository owner to decide: the entries are English on every language setting.
Translating release notes into four languages is a standing cost on every release, not a bug fix.

### The host keeps a record of itself (issue #35, 1 of 3)

A meeting cannot be replayed. Whatever went wrong happened once, in a room, with the other side of
the table waiting — and until now the only trace was a status line that the next status line
overwrote.

- **A log facade in the core, NLog in the host.** `Kanal.Core.Diagnostics` defines four levels, an
  `ILogSink` and a static `Log` — the same rule as the provider abstractions: the core states the
  capability, the host names the vendor. Nothing is written until a host installs a sink, so tests,
  `Kanal.Doctor` and any future embedder stay silent by default, and a sink that throws never
  reaches the caller. `LogSetup` builds the configuration in code rather than from `NLog.config`:
  the level has to change without a restart, and a config file beside the executable is one more
  thing that can go missing from a published build.
- **One file a day, rolled over at a size the operator sets.** `kanal-<date>.log` under the
  application-data directory keeps a stable name all day, so "send me today's log" names one file;
  rollovers are numbered beside it. Age is the retention policy (14 days) and a count derived from
  the file size is a runaway backstop only, bounding the folder at roughly 2 GB without standing in
  for the days — a fixed count silently undercut the promise, and no count at all let one loud day
  write 65 MB with nothing yet old enough to delete.
- **Changing the level mid-meeting keeps what is already written.** Handing NLog a fresh
  `FileTarget` over the open file made the new one open at a stale offset and overwrite a
  contiguous block of what had been flushed; the target is updated in place instead.
- **What actually gets logged.** Startup with version and OS, unhandled and unobserved exceptions,
  rooms opening and closing with their mode and languages, a session that ends on its own, a
  refused Start with the reason, a model that will not load, a relay that cannot be set up, relay
  publishes that fail, capture that stops under a live room, a recording that cannot be opened, an
  export that cannot be written, and a settings file that could not be read. Debug adds capture
  running, a frame count and snapshot publishes — counts, never content.
- **Nothing said in the room reaches the file.** Every line is capped, message and exception alike:
  a gateway behind a captive portal was writing its whole 20 KB error page per failure, retried
  every 15 seconds. And the transcription wire no longer falls back to putting a whole error
  *frame* into the error message — that frame quotes the request that caused it, which is an
  utterance.
- **One click to the folder**, because the person who has to send a log is on a call and will not
  be typing an `%APPDATA%` path into a file manager. `SystemFolders.Open` is the one line of
  platform branching, injected so a headless test never launches Explorer. If the folder cannot be
  written at all the panel says so — NLog defers file creation and swallows that failure, so the
  alternative was an empty directory under a promise of one file a day.
- **A hand-edited settings file no longer costs the operator their API key.** The level reads
  leniently, and anything else unreadable is copied to `settings.json.unreadable` before the
  defaults replace it — logged, which is why the sink is installed before settings are read.

Two of three: the changelog viewer and the open-source list follow on their own branches, since
they are independent of this one beyond sharing a dialog.

### Kanal traffic is behind an authenticated gateway

- Removed all Supabase project URLs and client API keys from the desktop source, compiled defaults,
  GitHub Pages, and invitation format. The operator now provisions `KANAL_RELAY_URL` and the secret
  `KANAL_RELAY_HOST_TOKEN` at runtime; the public URL identifies the gateway but grants no project
  access. Relay stays disabled rather than silently falling back to a shared public credential when
  either setting is absent.
- Added the `kanal-relay` gateway as a **Cloudflare Worker with Durable Objects** (`gateway/`).
  An authorised desktop can create a room and receives two signed, 12-hour HMAC capabilities: a
  publish-only host ticket and a receive-only reader ticket. Fan-out happens inside one Durable
  Object per room over hibernated WebSockets; reader sockets cannot send relay messages. The
  first draft of this PR was a Supabase Edge Function, rejected on a measured platform limit:
  Edge Functions cap wall clock at 150 s on the free plan (400 s paid), which would have forced
  every phone to reconnect every 2.5 minutes of a 90-minute meeting, and `EdgeRuntime.waitUntil`
  does not lift that cap. Vercel was rejected because its functions cannot hold WebSockets at
  all. Hibernated Durable Object sockets have no wall-clock limit and are on the Workers Free
  plan — and once a Durable Object does the fan-out, Supabase Realtime became a redundant hop,
  so no Supabase (or any backing-store) credential exists anywhere in the system any more.
- Replaced the single shared host bootstrap token with **per-device credentials**: the operator
  mints a one-time activation code (`?action=admin.code`), the desktop trades it for its own
  token (`?action=activate`), and a lost laptop is revoked alone (`?action=admin.revoke`)
  without rotating anyone else. The registry is a SQLite Durable Object storing only SHA-256
  hashes of codes and tokens. The wire protocol toward the desktop and the phone is unchanged.
- The gateway has its own test suite: 25 vitest cases running in real workerd via
  `@cloudflare/vitest-pool-workers` (`gateway/npm test`, wired into CI) covering the device
  lifecycle, role separation, envelope filtering, size limits, per-room isolation, subprotocol
  negotiation, ping/pong, receive-only enforcement, and the browser origin policy.
- Known reachability caveat, recorded in the README: `*.workers.dev` is blocked in mainland
  China, and a participant roaming through a Chinese carrier tunnels through the Chinese
  network even abroad — for that participant the Worker must sit on a custom domain.
- Replaced direct Realtime access in `GatewayRelayPublisher` and both copies of the static mobile
  page. The QR fragment now carries the gateway endpoint, reader ticket, 128-bit room capability,
  and ephemeral P-256 verification key. GitHub Pages opens the ticketed WebSocket, checks that the
  gateway's room/key claims match the invitation, then verifies every signed payload before
  touching UI state.
- Room rotation now includes the next reader ticket inside a message signed by the old room key, so
  connected phones can follow a restart without receiving a reusable host credential. The bearer
  invitation remains readable by anyone who obtains it until expiry; tickets are stateless and are
  not individually revocable before their 12-hour expiry.
- Added regression coverage for repository/build credential absence, gateway role separation,
  invitation shape, signature verification and tampering, room rotation, and byte-for-byte
  parity of the two static mobile pages.
- Fixed the first integration regression: relay configuration was optional in the UI but a missing
  gateway was treated as a fatal Start error. Relay setup now fails closed to a signed null
  transport, keeps the meeting running without a QR code, and shows a localized degraded-mode
  warning. No public Supabase fallback was reintroduced.

### Unit-test projects now follow the Core/UI boundary

- Replaced the former mixed test assembly with `tests/Kanal.Core.UnitTests` for core,
  audio, provider, serialization, orchestration, and non-visual service tests, plus
  `tests/Kanal.UI.UnitTests` for deterministic view-model and host application-state behavior.
- The UI suite no longer treats Avalonia rendering as a unit-test contract. Tests for palette
  values, control geometry, visual-tree content, icon resources, automation labels, and synthetic
  pointer/keyboard input were removed; mixed tests now drive commands and observable view-model
  state directly.
- Solution membership, provider friend-assembly declarations, CI commands, and contributor docs
  now name the two projects explicitly so either boundary can be built and tested independently.

---

### README is now the open-source project entry point

The root README was a compact architecture inventory followed by milestone checklists, relay
deployment notes, and a live risk log. That made it useful to the project owner but left a new user
to reconstruct what Kanal is, which modes work, what crosses the network, and how to try it.

- Reframed it around the user problem, working features, a keyless demo, live-room setup, and the
  desktop/mobile experience. Detailed milestones, measurements, and decision history remain here
  and in the PRD instead of being duplicated on the project landing page.
- Made the two privacy boundaries explicit: a mode describes where speech processing runs, while
  captions and room state still use the Supabase relay in every production mode. The README also
  documents the default local WAV recording, plain-JSON key storage, and self-hosting overrides.
- Derived the availability table, configuration names, settings locations, project map, supported
  capture platforms, and contribution invariants from the current code and CI configuration. The
  quick-start and `Kanal.Doctor` examples now use the executable command shape actually accepted by
  the projects.
- Added a concise limitations/roadmap section linking back to this log: local ASR and standalone
  cloud MT remain missing, Linux has no live-capture backend, and real Chinese↔Polish terminology
  validation remains the go/no-go gate.

---

## 2026-08-03

### Every `{l:T}` string now follows the language switch, and the tables live in JSON

- **Bug — `{l:T}` bindings never refreshed on a language change.** `Localizer` raised
  `PropertyChanged("Item[]")`, which is **WPF's** indexer notification name; Avalonia's reflection
  binding listens for `CommonPropertyNames.IndexerName` — `"Item"` — and never matched it. Every
  string set straight from XAML (Start, MODE, section headings, window titles) stayed frozen in
  the language its window opened in, while every view-model INPC property switched correctly,
  which left one screen speaking two languages. One-line fix: raise `"Item"`, now the
  `Localizer.IndexerName` constant, shared by the two view models that were string-matching
  `"Item[]"` on their own. Guarded by a headless test that opens `MainWindow`, flips
  `Localizer.Instance.Current`, and asserts the rendered `TextBlock` re-reads. The three
  localisation tests that switch the language off the UI thread became `[AvaloniaFact]`s — with
  the fix in place a switch genuinely reaches live bindings, so it must happen on the thread the
  bindings live on, exactly as in production.
- **Last hard-coded operator string.** The export status ("Exported to …") was composed inline in
  `MainViewModel`; it now uses the `status.exported` / `status.exported.audio` keys that already
  existed in all four languages. An audit of every remaining literal in `src/Kanal.Host` found
  nothing else user-visible outside the tables.
- **Tables migrated to `Localization/i18n/{en,zh,de,pl}.json`** — flat JSON, one file per
  language, embedded resources loaded once by `Strings` through `System.Text.Json` (no new
  package). `Localizer`'s API — indexer, `Format`, fallback to English then to the key — is
  unchanged, and all existing guards (identical key sets, placeholder parity, nothing left in
  English, no vendor names) keep running against the loaded tables.
- **Two new guards.** A repo-scanning test forbids literal user-visible text in `.axaml`
  (`Text=`, `Content=`, `ToolTip.Tip=`, `Title=`, …) outside a small whitelist of glyphs and the
  product name, so a hard-coded string can no longer slip past the language switch unnoticed; and
  `HelpNeverOverstatesPrivacy` gained a four-language twin — no translation of the mode help may
  promise "不联网", "nichts wird gesendet" or "bez sieci" any more than English may promise
  "no network".

### Input device hot-plug

The device dropdowns (main window and Settings) enumerated once at construction, so a USB
microphone plugged in after launch — the normal order of events when the mic lives in the
meeting-room drawer — never appeared without reopening the window.

- **`IAudioDeviceWatcher`** (Kanal.Audio): raises a payload-free `DevicesChanged` when the input
  set may have changed; the one correct reaction is to re-enumerate, and a device list on the
  event would invite acting on a stale one. `AudioCaptureFactory.TryCreateDeviceWatcher()` picks
  the platform implementation, and returns null rather than throwing when registration fails —
  a dropdown that misses a hot-plug is degraded, a start-up that dies over it is broken.
- **macOS**: `CoreAudioDeviceWatcher`, an `AudioObjectAddPropertyListener` on
  `kAudioObjectSystemObject` for `kAudioHardwarePropertyDevices` ('dev#') and
  `kAudioHardwarePropertyDefaultInputDevice` ('dIn ') — the default matters because
  `CoreAudioCapture` floats it to the top of the list. Removed symmetrically on dispose.
- **Windows**: `WasapiDeviceWatcher` via NAudio's `IMMNotificationClient` — no degradation
  needed, NAudio.Wasapi 2.3.0 already ships the interface. Add/remove/state/default-changed all
  refresh (a USB unplug often surfaces as a state change, not `OnDeviceRemoved`);
  `OnPropertyValueChanged` deliberately does not — it fires per property on every volume move.
- **View models**: both `MainViewModel` and `SettingsViewModel` re-enumerate on the event,
  marshalled through `Dispatcher.UIThread.Post` (callbacks arrive on CoreAudio/COM threads).
  The selection survives a refresh by its stable device id — enumeration builds fresh instances
  every time — and an unplugged selection falls back to the list head, which the backends order
  default-first. A capture already running keeps the device it opened: the dropdown updates, the
  meeting does not switch microphones mid-sentence. Listeners are released with the window that
  shows the list (`MainWindow.OnClosed` → `MainViewModel.Dispose`, `SettingsWindow.OnClosed` →
  `CancelDownloads`), since a native listener firing into a dead dialog would live forever.
- **Tests** (`DeviceHotplugTests`): the refresh/keep/fall-back logic runs against a hand-fired
  fake watcher over a mutable fake device list. The native wrappers are deliberately thin; the
  headless suite proves only that real registration and removal survive
  (`TheRealWatcherRegistersAndUnregistersCleanly` — a wrong P/Invoke signature dies there, not
  in a meeting). Firing them is a manual test: plug and unplug a USB microphone while the main
  window and Settings are open, on each platform.

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

### UI polish (fix/ui-polish)

Four small host-UI fixes from screenshot review, one PR:

- **Transport buttons share one width.** Start/Pause/Stop sized independently, and the Pause
  label carried a `Width="50"` hack that fit English only. The three buttons now sit in a
  `SharedSizeGroup` (scope on the transport StackPanel), so the widest label in the current
  chrome language sizes all three — "Zakończ" and "Weiter" included. This was originally guarded
  by a headless geometry assertion, removed when unit tests were split by the Core/UI boundary.
- **Masthead no longer repeats the pipeline status.** The `Transcription: … | Translation: …`
  pair duplicated what the mode selector already says, so the block is gone; the old
  corresponding rendering checks went with it. The `TranscriptionStatus` / `TranslationStatus`
  view-model properties stay — their label logic is still covered by
  `TranslationStatusTests` and mode-switch tests, and a future surface (status line, tooltip)
  is the likely place they resurface.
- **Settings scrollbar takes layout space.** The overlay scrollbar sat on top of the rightmost
  controls and section rules; `AllowAutoHide="False"` puts it in the layout, plus a 14 px right
  margin on the content so the ragged right edge clears the bar.
- **Model-row Delete matches its neighbours.** It was the only `ghost` (borderless) button in a
  row of outlined ones (Download / Cancel); it now wears the default outlined face. The last
  two are style-only changes verified by the existing suite.

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

## 2026-08-03

### Local model warm-up on Start (`fix/local-mt-warmup`)

Start with a local translation model opened the room first and let the weights load lazily on
the meeting's first final — so the opening sentences sat untranslated for however long llama.cpp
took to map a multi-gigabyte file, with nothing on screen saying why. Start now loads the model
to a working state *before* transcription begins, and the first sentence's translation delay is
plain inference latency.

- **Capability, not vendor.** A new `IWarmupProvider` in `Kanal.Core.Providers` declares
  "my backing resources load slowly and can be loaded ahead of use" — idempotent, cancellable.
  `LlamaSharpTextGenerator` implements it by pulling its existing lazy load forward (same gate
  as decodes, so warm-up can never race one); `LlamaSharpMtProvider` forwards to its generator.
  `MainViewModel.StartAsync` checks the interface, never a vendor: the scripted demo translator
  implements nothing and starts exactly as before.
- **The loading phase is visible and abortable.** While the model loads the masthead says so
  (`status.loadingmodel`, en/zh/de/pl; indeterminate wording — llama.cpp reports no progress),
  `IsStarting` refuses a second Start, and Stop stays offered: pressing it cancels the load via
  a `CancellationToken` that runs the whole way into `LLamaWeights.LoadFromFileAsync`. The load
  itself runs on a background thread, so neither the UI nor Stop blocks on it (the PR #20
  guarantee holds). A load that fails reports `status.modelloadfailed` and disposes the planned
  providers instead of opening a room that cannot translate; a cancelled load unwinds to Idle.
- **Ordering.** Warm-up completes before the relay is created, the previous room is redirected,
  or the ASR session starts — a room is never live, and no audio is ever captured, while the
  translator is not yet ready. Tests drive this through a new `PlanFilter` seam on
  `MainViewModel` (the `RelayPublisherFactory` shape): a gated warmable fake holds the load
  open while a wrapper records that ASR never started; plus generator-level tests for
  load-once, cancel-then-retry, and warm-up-after-dispose. 10 new tests, suite at 269.
