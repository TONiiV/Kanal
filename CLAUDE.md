# Kanal — working notes for Claude

Internal meeting-translation tool. Avalonia (.NET 10) desktop host captures room audio, streams it
through a pluggable ASR/MT chain, and broadcasts **text only** to read-only mobile clients. Built for
one real scenario: a zh/de/pl meeting with no shared language. See `README.md` for layout and
`docs/PRD-v0.4.md` for requirements and the roadmap.

```bash
dotnet build Kanal.slnx
```

```bash
dotnet test
```

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
  rationale that needs a paragraph belongs in `docs/PROGRESS.md` or `docs/PRD-v0.4.md`, where design
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

For any UI or brand work, read [Conversation K](docs/design/conversation-k.md) for current
colours, shape, typography, asset generation and verification. It supersedes the previous
monochrome-only and square-corner rules. Read [meeting workspace](docs/design/meeting-workspace.md)
for layout and interaction requirements. The historical prototype illustrates those interactions;
the application and Conversation K document define current visual styling.

Keep transcription content readable in Chinese, German and Polish. Brand controls, speaker
identity and recording state use distinct semantic resources. Desktop uses explicit light brushes;
mobile follows system light/dark preferences and loads without external fonts or stylesheets.
Preserve all existing consent, capture, language-limit and meeting-record behaviour.
