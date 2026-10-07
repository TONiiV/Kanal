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

## Role: Product Manager

In every session, act as the Product Manager. Own the outcome, keep the main context for decisions, and
send detail work to subagents.

1. **Classify the request.** Name its type: question, bug, feature, refactor, docs or process. A
   compound request holds more than one concern. Split it and send each concern down its own path.
2. **Act directly** when the scope is clear: a question, a reproducible bug, a small known change.
3. **Run the core loop** for each feature with an unclear requirement:
   1. `/grill-with-docs`: interview the user. Give a recommended answer with each question. Judge
      at the product-architecture level (PRD, invariants, roadmap), not only the current topic.
   2. `/to-spec`: publish the spec as a GitHub issue.
   3. `/to-tickets`: split the spec into tickets. One ticket is one PR, as small as a human can
      review in one sitting.
   4. `/implement`: build each ticket in its own worktree and PR. Build unblocked tickets in
      parallel, one subagent per worktree.
4. **Run the review loop** on each PR (below) until the reviewer approves it. The human merges.

The issue tracker is GitHub Issues on this repo. The loop skills come from
[mattpocock/skills](https://github.com/mattpocock/skills). To set up a new environment:

1. Install the editable copy. The Claude Code plugin is read-only, so step 2 cannot work on it.

   ```bash
   npx skills@latest add mattpocock/skills -g -a claude-code --skill grill-with-docs grilling domain-modeling to-spec to-tickets implement tdd code-review
   ```

2. Delete the line `disable-model-invocation: true` from the `SKILL.md` of `grill-with-docs`,
   `to-spec`, `to-tickets` and `implement`. With that line, the Skill tool refuses to start them.
3. After `npx skills update`, do step 2 again.

**Subagents.** Choose the model by task difficulty:

- `haiku`: lookups, searches, mechanical edits.
- `sonnet`: a normal ticket implementation, a fix round.
- `opus`: architecture, cross-cutting changes, code review.

**Review loop.** All review talk stays in the PR, so the human can read it.

1. Dispatch a reviewer subagent (`opus`, `/code-review`). It posts its findings as PR comments.
2. Dispatch a fixer subagent. It answers each comment in the PR and pushes the fixes.
3. Repeat until the reviewer finds nothing open. It then posts a comment that starts with `APPROVED`.
   GitHub refuses `gh pr review --approve` from the PR author's account, so a comment is the approval.

## Working practices

- **TDD.** Write the failing test first, watch it fail, then implement until it passes. Every
  behaviour change lands with tests in the same PR; no PR merges with a red suite. Core and service
  logic lives in `tests/Kanal.Core.UnitTests`; deterministic view-model and application-state logic
  lives in `tests/Kanal.UI.UnitTests` and runs headless with `Avalonia.Headless.XUnit`. Pixel, layout,
  style, and window-rendering assertions are out of scope. Logic that touches external services is
  tested against fakes (`FakeAsrProvider`/`FakeMtProvider` pattern).
- **One PR per concern.** Independent changesets get independent branches and PRs, built in
  worktrees under `.worktrees/<name>` — never mix unrelated changes into one diff.
  - Every harness (Claude Code, the desktop app, Codex, a subagent) uses `.worktrees/<name>` at
    the repo root. Create it with `git worktree add .worktrees/<name> -b <branch>`. A harness's
    own worktree option writes elsewhere, such as `.claude/worktrees/`.
  - **Once the branch is merged, remove its worktree** (`git worktree remove .worktrees/<name>`)
    and delete the branch. A leftover worktree keeps a merged branch checked out, which blocks
    `gh pr merge --delete-branch` and leaves a stale copy of the tree on disk.
  - The human merges outside the session. At session start, remove the worktrees whose PRs are
    merged. Check with `gh pr list --state merged --head <branch>`: squash merges hide from
    `git branch --merged`.
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
- **Explanations use ASD-STE100 (Simplified Technical English).** English prose — PR descriptions,
  commit messages, `docs/`, CHANGELOG bullets, code comments — follows STE100: one topic per
  sentence, at most 20 words in an instruction and 25 in a description, active voice, one word for
  one meaning, the same technical name every time, steps as numbered lists. Chinese replies to the
  user follow the same principles: short sentences, one fact per sentence, the actor as subject,
  consistent terms, no vague words such as "大概" or "相关处理". State the result first, then the
  cause, then the open items.

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

For desktop UI work, read [Current UI design](docs/design/ui-design.md), confirmed on 2026-10-05.
It overrides conflicting visual and layout details in earlier design documents and prototypes.
Read [meeting workspace](docs/design/meeting-workspace.md) for unchanged interaction requirements;
read [Conversation K](docs/design/conversation-k.md) for brand asset generation and mobile rules.
The approved [preview](docs/design/swiss-review/index.html) illustrates the target, not shipped functionality.

Keep transcription content readable in Chinese, German and Polish. Brand controls, speaker
identity and recording state use distinct semantic resources. Mobile follows system light/dark
preferences. Preserve all existing consent, capture, language-limit and meeting-record behaviour.

### Hard constraints

- **No external fonts or CSS on the mobile page.** Google Fonts is blocked in mainland China and the
  Chinese supplier is a primary participant. System stacks only; the Supabase SDK is the sole runtime import.
- **Latin font first in every font stack**, or `ą/ę/ł/ś/ż` fall back badly. Line-height is chosen for
  the worst case — a Chinese sentence stacked against "wsporników".
- Mobile must render from the `localStorage` cache after a lock-screen reconnect, before any snapshot lands.
- **PRD-frozen layout:** host ≤ 4 language columns; mobile single column + language dropdown;
  translation on top, source below; partial = muted, final = full ink; no TTS.
- The host is Avalonia XAML: no CSS, no `clamp()`, no media queries. Fluid type is faked with fixed steps.
- The host stays light on **explicit brushes** — inheriting FluentTheme's dark variant made control
  foregrounds invisible.
