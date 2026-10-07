# Domain docs

How the engineering skills use this repo's domain documentation. The layout is single-context.

## Before exploring, read these

- **`CONTEXT.md`** at the repo root: the glossary.
- **`docs/adr/`**: read the ADRs that touch the area you work in.

Product requirements and the roadmap are in `docs/PRD-v0.4.md`. Design history is in
`docs/PROGRESS.md`.

The `/domain-modeling` skill (reached through `/grill-with-docs`) adds glossary terms and ADRs when a
term or decision is resolved. Do not create them in advance.

## Use the glossary's vocabulary

When your output names a domain concept (in an issue title, a refactor proposal, a hypothesis, a test
name), use the term as `CONTEXT.md` defines it. Do not use a synonym that the glossary lists under
_Avoid_.

If the concept you need is not in the glossary, either you are inventing language the project does
not use (reconsider), or there is a real gap (note it for `/domain-modeling`).

## Flag ADR conflicts

If your output contradicts an existing ADR, say so explicitly. Do not override it silently:

> _Contradicts ADR 0054 (meeting record lifecycle and storage), but worth reopening because…_
