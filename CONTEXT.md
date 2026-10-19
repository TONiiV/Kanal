# Kanal

Kanal organises meeting transcription, translation and summaries within user-selected workspaces.

## Language

**Workspace**:
A named scope for a project, company, team or personal use, containing its own meeting records. Workspaces are peers rather than a company/project hierarchy.
The UI says "project" for a Workspace.
_Avoid_: Project scope

**Meeting record**:
The record of one meeting, including its transcript and associated summary.

**Meeting title**:
The human-readable name of a meeting record, distinct from its workspace name. It may originate from model generation or manual naming.

**Listening agent**:
A meeting assistant that follows an ongoing meeting, answers questions and offers suggestions or fact-check findings.
_Avoid_: ASR provider, translator

**User feedback**:
A suggestion, bug report or question submitted to the maintainers with a contact email. Submitting feedback does not require a Kanal account.
_Avoid_: GitHub issue (a separate maintainer tracking item)

**Kanal account**:
A user's identity for accessing Kanal cloud features and future subscription entitlements. Multiple verified sign-in methods can belong to the same account; local features do not require it.
_Avoid_: Relay device credential

**Mobile caption page**:
The page a participant opens on their phone to read captions from a Kanal session.
_Avoid_: Relay backend

**Room language**:
A language that a meeting shows and translates into. The host shows one column for each room language. The mobile page offers them in a dropdown. A room language never limits which language transcription may recognise.
_Avoid_: Recognition language, source language

**App update**:
A newer release of the desktop host. Models, the mobile caption page and the relay are updated separately and are not app updates.
_Avoid_: OTA (too broad), model download

**Update check**:
The host asking whether an app update exists and telling the operator. It never downloads or installs anything, and an unanswered check changes nothing.

**Minimum supported version**:
The oldest host version still allowed to start a meeting. A meeting already running is never interrupted by it.

**Utterance playback**:
Playback of the original recorded audio corresponding to a selected utterance in a meeting transcript.
_Avoid_: Text-to-speech

**Decision map**:
A view connecting a meeting topic to alternative proposals and a resulting decision, with references to the source transcript. A model-suggested decision remains a candidate until a person confirms it.

**Key fact**:
A value in an utterance that must survive translation unchanged: a number with its unit, a tolerance, a part number, a standard number or a date.
_Avoid_: Entity, keyword

**Glossary**:
A list of terms for one project, which the operator enters by hand. Each row has one cell for each of zh, de, pl and en, and a "do not translate" flag. Kanal saves it in `kanal-glossary.json` in the project folder.
_Avoid_: Vocabulary (Gladia's name for its own input), dictionary

**Intelligence provider**:
A source of language-model inference for Kanal: a local model, the operator's own subscription through a locally installed CLI, or an API key. Term extraction, semantic checks, minutes, meeting titles and listening-agent answers use it; none of them is it. It never translates live speech.
_Avoid_: Agent, AI, listening agent

**Colour scheme**:
The host's choice between Light, Dark and System, where System follows the operating system. It is a per-machine preference and never reaches the mobile caption page.
_Avoid_: Theme, dark mode (as a setting name)

**Accent theme**:
A named set of brand colours, with one light and one dark variant, that tints the host's controls. It never changes reading surfaces, ink, recording state or speaker colours.
_Avoid_: Skin, colour scheme
