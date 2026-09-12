# Kanal

Kanal organises meeting transcription, translation and summaries within user-selected workspaces.

## Language

**Workspace**:
A named scope for a project, company, team or personal use, containing its own meeting records. Workspaces are peers rather than a company/project hierarchy.
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

**Utterance playback**:
Playback of the original recorded audio corresponding to a selected utterance in a meeting transcript.
_Avoid_: Text-to-speech

**Decision map**:
A view connecting a meeting topic to alternative proposals and a resulting decision, with references to the source transcript. A model-suggested decision remains a candidate until a person confirms it.
