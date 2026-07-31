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
  behaviour change lands with tests in the same PR; no PR merges with a red suite. UI behaviour is
  tested headless (`Avalonia.Headless.XUnit`, see `tests/Kanal.Tests/HostUiTests.cs`); logic that
  touches external services is tested against fakes (`FakeAsrProvider`/`FakeMtProvider` pattern).
- **One PR per concern.** Independent changesets get independent branches and PRs, built in
  worktrees under `.worktrees/<name>` — never mix unrelated changes into one diff.
- **Progress log.** Plans, design changes and status live in [`docs/PROGRESS.md`](docs/PROGRESS.md);
  update it in the same PR as the work it describes.

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
