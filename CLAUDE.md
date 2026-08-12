# Kanal — working notes for Claude

Internal meeting-translation tool. Avalonia (.NET 9) desktop host captures room audio, streams it
through a pluggable ASR/MT chain, and broadcasts **text only** to read-only mobile clients. Built for
one real scenario: a zh/de/pl meeting with no shared language. See `README.md` for layout and
`docs/PRD-v0.3.md` for requirements.

```bash
dotnet build Kanal.slnx
```

```bash
dotnet test
```

(If only .NET 10 is installed, tests need `DOTNET_ROLL_FORWARD=Major dotnet test`.)

## Working practices

- **TDD.** Write the failing test first, watch it fail, then implement until it passes. Every
  behaviour change lands with tests in the same PR; no PR merges with a red suite. Core and service
  logic lives in `tests/Kanal.Core.UnitTests`; deterministic view-model and application-state logic
  lives in `tests/Kanal.UI.UnitTests` and runs headless with `Avalonia.Headless.XUnit`. Pixel, layout,
  style, and window-rendering assertions are out of scope. Logic that touches external services is
  tested against fakes (`FakeAsrProvider`/`FakeMtProvider` pattern).
- **One PR per concern.** Independent changesets get independent branches and PRs, built in
  worktrees under `.worktrees/<name>` — never mix unrelated changes into one diff. **Once the
  branch is merged, remove its worktree** (`git worktree remove .worktrees/<name>`) and delete
  the branch; a leftover worktree keeps a merged branch checked out, which blocks
  `gh pr merge --delete-branch` and leaves a stale copy of the tree on disk.
- **Comments are the exception.** Prose in a source file is prose nobody re-reads when the code
  beneath it changes, so the default is no comment — in C#, TypeScript, and the JavaScript inside
  `web/index.html` alike. Keep one only if it carries what a competent reader cannot derive from the
  code: a **trap** that gets "fixed" back if it is not recorded (Avalonia's reflection binding
  listens for the indexer name `"Item"`, never WPF's `"Item[]"` — see `Localizer.IndexerName`;
  without the note, every bound string silently freezes on the next language switch); an **external
  constraint or attribution** (the OpenCC `TSCharacters` table is Apache-2.0 —
  `Kanal.Core/Text/SimplifiedChinese.cs`); or a **counter-intuitive decision** whose rejected
  alternative looks better at a glance. One line, stating the constraint — not the story. Delete
  everything else: XML doc restating the signature (`/// <summary>ISO code of the language the
  chrome is currently in.</summary>` over `CurrentLanguage`), prose narrating the lines below it,
  atmospheric description on enum members (`LevelMeter`'s "lost in the room"), divider banners,
  commented-out code. Kanal is an application, not a published library — XML doc is no API contract
  here, and no project sets `GenerateDocumentationFile`, so deleting it cannot break the build. A
  rationale that needs a paragraph belongs in `docs/PROGRESS.md` or `docs/PRD-v0.3.md`, where design
  history is already kept and will actually be maintained — not in the source file.
- **Progress log.** Plans, design changes and status live in [`docs/PROGRESS.md`](docs/PROGRESS.md);
  update it in the same PR as the work it describes.
- **Changelog.** A PR that adds a feature, fixes a bug or makes something measurably better adds one
  bullet to [`CHANGELOG.md`](CHANGELOG.md) under the heading being worked towards — written for the
  operator, not the committer. Refactors, tests and docs add nothing. A version heading gets its
  date only when that version is released.

## Architecture invariants

- The host is the single authority; clients are projections. Late join and reconnect are served by
  `room.snapshot`.
- Capability-driven orchestration, no vendor branching. The orchestrator's only decision is
  `if (!asr.Caps.Translation)` → route finals through `IMtProvider`.
- The relay is a replaceable layer (`IRelayPublisher`).
- Only text crosses the public network (M0 caveat: Gladia receives audio).
- Merges are non-destructive — utterances keep their original diarization tag; clients resolve via
  `Speaker.MergedFrom`.
- `web/index.html` and `docs/index.html` must stay **byte-identical**; `docs/` is the GitHub Pages copy.

## Design Context

Full context lives in [`.impeccable.md`](.impeccable.md) — read it before any UI work. Summary:

### Users

One **operator** driving the host laptop mid-meeting (a few controls, readable from a metre away), and
3–8 **participants** who scan a QR and glance at a read-only phone page one-handed between sentences.
Content is technical and unforgiving — part numbers (`KX-4402`), tolerances, delivery dates. Misreading
one character costs more than reading three fewer lines. Nobody is a designer; nobody will be trained.

### Brand Personality

**Precise. Calm. Unbranded.** An instrument, not a product — it should feel like equipment that was
already in the room. Terse, factual voice. The only emotional goal is confidence that what is on screen
is exactly what was said. A joke in a translation bubble is a bug.

### Aesthetic Direction

**Swiss editorial typography.** Strict grid, strong type-size hierarchy, generous white space, hairline
rules as structure. Character comes from typesetting and rhythm — never texture, gradients, or effects.
Left-aligned, ragged right, never centred.

Mobile follows `prefers-color-scheme`; the host stays light but on **explicit brushes** — inheriting
FluentTheme's dark variant previously made control foregrounds invisible.

Anti-references: SaaS card grids, glassmorphism, purple→blue gradients, cyan-on-dark, chat bubbles with
fat rounded corners and drop shadows, monospace as shorthand for "technical", centred hero layouts.

### Design Principles

1. **The live utterance is the design.** The newest line carries the most visual weight — through space
   and rule weight, not through re-colouring. Finalised history recedes in contrast, never in legibility.
2. **Typography is the only ornament.** Hierarchy from size, weight, and measure. No cards in cards, no
   shadows faking depth, no rules that aren't structural.
3. **The only colour on screen is people.** Rust / ochre / pine identify a *person* across rename and
   merge. Chrome is ink and paper only. Never rely on colour alone — the tag or name is always rendered.
4. **Set for three scripts at once.** Latin, Polish diacritics, and CJK share every surface. Latin font
   must come *first* in every stack or `ą/ę/ł/ś/ż` fall back badly. Line-height is chosen for the worst
   case — a Chinese sentence stacked against "wsporników".
5. **Nothing the PRD froze may move.** Host ≤ 4 language columns; mobile single column + language
   dropdown; translation on top, source below; partial = muted, final = full ink; no TTS.

### Hard constraints

- **No external fonts or CSS on the mobile page.** Google Fonts is blocked in mainland China and the
  Chinese supplier is a primary participant — a webfont means a hanging request and a fallback for the
  people who most need the page. System stacks only. The Supabase SDK is the sole runtime import.
- The host is Avalonia XAML: no CSS, no `clamp()`, no media queries. Fluid type is faked with fixed steps.
- Mobile must render from `localStorage` cache after a lock-screen reconnect, before any snapshot lands.
