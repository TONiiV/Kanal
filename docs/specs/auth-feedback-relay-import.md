# Kanal：账号云服务、免登录反馈、手机页托管与录音导入

## Problem Statement

用户需要无需 GitHub 账号或直接访问 GitHub 即可向维护者提出建议、报告问题和提问。当前桌面没有反馈闭环或用户账号；共享云服务密钥不能安全分发，也没有邀请及用量控制。手机字幕页面依赖 GitHub Pages，页面与实时网关可达性相互独立，中国大陆用户需要整条业务流程可用。

用户还需要将已有录音归入工作空间，获得带时间戳和说话人的转写、翻译与摘要。已有诊断工具能读取 WAV，但不构成产品级导入、批处理或持久化功能。

## Solution

设置改为齿轮图标，其右侧增加对话气泡反馈按钮。任何用户均可通过联系邮箱提交文字反馈，首次验证邮箱并通过服务端防滥用检查；反馈私有保存并自动生成私有 GitHub issue，维护者通过邮箱人工回复。

本地功能免费，不要求账号。平台云服务通过账号、邀请和额度开放；保留用户自带 Gladia key 的入口。账号支持 Google、Apple、微信、手机号及显式绑定，使用系统浏览器完成桌面登录。Clerk 为初选，Supabase 用于数据；若认证方案不满足要求，允许适配或替换 Clerk。大陆用户必须有可靠登录路径，Google 在其服务可访问的网络使用。

手机字幕静态页面优先验证 EdgeOne Pages 与自定义域名；实时网关另外验证。录音复制到当前工作空间，用户明确启动云转写，生成完整会议记录。评估 VibeVoice-ASR 为录音引擎，实际效果、运行资源与网络路径必须验证后才能启用。

## User Stories

1. As a Kanal user, I want a gear settings icon, so that I can recognise the settings entry.
2. As a Kanal user, I want a feedback bubble immediately to the right of settings, so that I can report a problem without searching menus.
3. As a keyboard user, I want labelled and keyboard-operable controls, so that I can open and complete feedback accessibly.
4. As a visitor, I want to submit feedback without registering, so that account creation does not prevent me from reporting a failure.
5. As a reporter, I want to choose suggestion, bug report or question, so that maintainers understand my intent.
6. As a reporter, I want to enter a title, description and contact email, so that my feedback is understandable and answerable.
7. As a reporter, I want to verify my email without creating an account, so that I can submit with a usable contact address.
8. As a returning reporter, I want verification remembered for a limited period, so that I do not repeat unnecessary steps.
9. As a reporter, I want to preview optional application and system version information, so that I know what accompanies my report.
10. As a reporter, I want a receipt number after durable acceptance, so that I can refer to my feedback later.
11. As a reporter with an unstable connection, I want a retry not to duplicate my report, so that one action produces one report.
12. As a reporter in mainland China, I want to submit without contacting GitHub, so that GitHub access is not required.
13. As a maintainer, I want all valid feedback categories to become private issues automatically, so that no category requires manual copying.
14. As a maintainer, I want contact details and optional account identity held privately, so that feedback does not expose reporters publicly.
15. As a maintainer, I want a notification email and the contact address, so that I can reply manually.
16. As a maintainer, I want internal issue comments to remain internal, so that operational discussion is not emailed to reporters.
17. As a maintainer, I want request, verification and submission abuse limited on the server, so that attackers cannot flood the inbox or generate uncontrolled costs.
18. As a legitimate reporter on a shared network, I want recoverable rate-limit errors, so that other users' traffic does not permanently exclude me.
19. As a maintainer, I want failed issue delivery retained and retried, so that GitHub outages do not lose accepted feedback.
20. As a local user, I want local features to remain free and available without login, so that cloud account state does not interrupt local work.
21. As a cloud user, I want to sign in through my system browser and return to Kanal, so that authentication fits a desktop workflow.
22. As a cloud user, I want Google, Apple, WeChat and phone sign-in options, so that I can use a suitable verified identity.
23. As a mainland user, I want at least one reliable sign-in path on ordinary networks, so that I can reach cloud features.
24. As an account holder, I want to verify and bind additional sign-in methods, so that they access the same Kanal account.
25. As an account holder, I want binding conflicts to be explained and handled manually initially, so that two existing accounts are not silently merged.
26. As an account holder, I want quotas to remain stable across sign-in methods and devices, so that my entitlements have a consistent owner.
27. As an invited user, I want to activate cloud access on my account, so that I can use the platform's Gladia service.
28. As an invited user, I want a one-time 120-minute Gladia allowance, so that I can trial cloud transcription.
29. As a cloud user, I want remaining allowance and access failures explained, so that I understand when I can start processing.
30. As a maintainer, I want server-enforced quota and one concurrent cloud task per account, so that client manipulation cannot bypass limits.
31. As a maintainer, I want to grant additional allowance without automatic monthly renewal, so that trial costs remain controlled.
32. As a maintainer, I want the shared Gladia key confined to the server, so that desktop users cannot recover the platform credential.
33. As a user with my own Gladia account, I want to retain my own key configuration, so that I can pay my provider directly.
34. As a phone participant, I want to scan and view captions without creating an account, so that joining remains simple.
35. As a phone participant in mainland China, I want the page and caption connection to work, so that static hosting alone is not mistaken for success.
36. As a phone participant, I want reconnect and cached captions preserved after migration, so that changing hosts does not regress meeting use.
37. As a meeting operator, I want to import WAV, MP3 and M4A into my selected workspace, so that recordings belong with related meetings.
38. As a meeting operator, I want recordings up to 60 minutes and 500 MB validated before cloud processing, so that unsupported input fails early.
39. As a meeting operator, I want an owned local copy and a pending meeting record, so that importing does not rely on the original file remaining in place.
40. As a meeting operator, I want cloud upload disclosed and processing explicitly started, so that selecting a file does not silently upload it.
41. As a meeting operator, I want progress and cancel/retry actions, so that a long task has a manageable lifecycle.
42. As a meeting operator, I want failed processing to preserve local audio, so that I can recover without losing the recording.
43. As a meeting operator, I want text, speaker labels and valid start/end timestamps, so that I can review who said what and when.
44. As a meeting operator, I want imported results connected to translation and summary processing, so that imports become complete meeting records.
45. As a meeting operator, I want completed records to reopen after restart, so that processing results are durable.
46. As a meeting operator, I want source audio and transcript references to remain within the owning meeting, so that meetings cannot read each other's artifacts accidentally.
47. As a cloud recording user, I want temporary audio deleted after successful durable result storage, so that the cloud does not become an indefinite audio archive.
48. As a cloud recording user, I want failed temporary audio retained no longer than 24 hours, so that retries have a bounded retention window.
49. As a maintainer, I want VibeVoice GPU trials enabled separately from Gladia minutes, so that different costs are not conflated.
50. As a maintainer, I want evidence from representative Chinese multi-speaker recordings before enabling VibeVoice, so that claimed model capability is not confused with tested product quality.
51. As a cloud user, I want logout and expired-session handling to stop unauthorised cloud actions while retaining local work, so that access changes do not destroy records.
52. As a maintainer, I want release evidence for mainland login, email verification, feedback, cloud transcription and phone captions, so that the entire requested journey is tested.

## Implementation Decisions

- Respect the accepted peer-workspace ownership boundary and accountless-feedback ADR. Account identity, relay device credentials and phone room tickets are different concepts. Adding accounts must not silently grant relay publishing rights or require phone accounts.
- Place the gear and adjacent feedback control in the settings location of the approved workspace UI. Preserve existing settings, keyboard access and localisation. The new feedback icon is a small vector bubble with an ellipsis; no raster generation is required.
- Feedback has type, title, body, contact email, server-created timestamp, opaque receipt, and optional verified account identifier/display-name snapshot. Anonymous reports have no invented user ID. Optional system/application versions are previewable. No attachments in this version.
- Verify the feedback contact email initially and retain bounded verification state. Verification must be server-validated and bound to the relevant email; it must not register an account. Apply server-side payload limits, request/verification/submission rate controls, idempotency and recoverable error responses. Configuration values beyond approved product limits remain adjustable implementation/deployment settings.
- Persist accepted feedback before asynchronous delivery. Record GitHub synchronisation and email notification states separately from acceptance. Create private issues for all valid categories using server-side repository-scoped credentials. On ambiguous delivery failure, reconcile the existing issue by stable feedback identity before creating another. Contact and account identity remain in private feedback storage; issue contents should reference the receipt without duplicating identity fields.
- Maintainers receive email notifications and reply manually to the verified contact. Never automatically forward private issue comments. Feedback/result retention deadlines beyond the approved temporary-audio policy must be configured before release; no indefinite default is approved.
- Use a stable Kanal account for entitlements and associate verified external identities with it. Clerk is the initial identity provider, Supabase the selected data service. WeChat compatibility, +86 SMS and desktop session handoff require verification; adapting or replacing Clerk is authorised if necessary. This is not confirmation of a replacement vendor.
- Browser authentication must correlate the initiating desktop session with the returning result, reject replay/unrelated callbacks, save credentials appropriately and handle refresh/logout. Adding a login method requires proof of control. Existing account conflicts go to manual handling; nickname, unverified email and anonymous feedback must never trigger account merges.
- Platform cloud access requires login, invitation and allowance. Each invited account receives Gladia 120 minutes once, with no automatic renewal. Maintainers can top up; at most one cloud task is active per account. Identity binding, retries and new devices do not re-grant allowance. VibeVoice GPU trial permission and accounting are separate.
- Platform Gladia credentials are never embedded in clients or returned by configuration endpoints. The server enforces remaining use, concurrency, termination and revocation; returning an unrestricted live session URL is not sufficient cost control. Finalise enforceable session admission and accounting against verified provider behaviour before enabling access. User-owned Gladia keys remain supported as a distinct mode.
- Build recording import on the existing workspace/meeting persistence contract. Copy the original into an owned meeting folder, validate actual decodability/duration/size, and create a durable pending record. Accept WAV/MP3/M4A up to 60 minutes AND 500 MB. Invalid import cannot leave a false completed record or cross-workspace paths.
- Selecting a file does not upload it. Explicitly starting cloud processing requires disclosure and cloud access checks. Model jobs expose pending, uploading/processing, completed, failed and cancelled outcomes; distinguish provider completion from merely reaching file EOF. Preserve local audio on failure or cancellation and make retries idempotent where the same operation is resumed.
- Evaluate VibeVoice-ASR for file transcription, not speech synthesis. Integrate through an appropriate batch boundary, preserve input-relative timestamps and diarisation, and retain provenance. Use actual fixtures to determine format handling, resource usage and output quality; neither cloud GPU nor local CPU capability is assumed to have been verified. No silent model/provider fallback is approved.
- Durable imported transcripts feed meeting translation and summary workflows. Report each stage truthfully: a successful transcript with failed summary is still available and must not force an unnecessary second ASR job. Persist source audio associations for existing utterance playback work without duplicating its UI scope.
- Cloud audio is temporary: delete after results are durably saved and recoverable; failed jobs retain it for at most 24 hours. Cleanup and access isolation must be enforced and tested independently of the desktop remaining online. Long-term original audio lives in the local workspace.
- Prefer validating EdgeOne Pages with a custom domain for the static mobile page. Update desktop links, allowed browser origin and deployment checks together. Preserve no-account phone viewing, text-only relay, cached display and reconnect behaviour. Reuse the existing mainland gateway work; static migration alone does not fulfil it. Deployment region, domain configuration and long-lived connection hosting depend on observed results.

## Testing Decisions

- Test external behaviour at the highest existing application command/state seam. Reuse current headless Avalonia view-model tests, fake ASR/MT providers and meeting-store tests against temporary directories. Avoid per-component mock interfaces or assertions that mirror private implementation.
- For feedback, run submit/verify/retry through the application/API boundary with controlled email, clock and GitHub effects; test unauthenticated acceptance, wrong/expired proof, abuse responses, durable receipt, provider outage and ambiguous duplicate delivery.
- For accounts/cloud, test login completion, invalid callbacks, logout/expiry, cross-account access, binding conflicts, invite replay, single grant, concurrent admission, quota exhaustion and cancellation. Replace provider calls with deterministic substitutes while exercising real application policy.
- For recording import, test actual tiny audio fixtures and temporary workspaces, unsupported/corrupt input, boundary size/duration, restart, cancellation, failed processing, recoverable results, speaker/timestamp mapping and independent translation/summary retries. Test timed cleanup without waiting 24 real hours.
- Keep real-provider and mainland-network checks separate from deterministic CI. Verify deployed domain/origin, all supported login methods, a mainland-reliable login route, SMS/email delivery, server-enforced Gladia limits, VibeVoice performance and end-to-end caption reconnect. Record environments and failures; do not infer reachability from documentation.
- Inspect icons, layout and localisation visually; no pixel assertions. Preserve existing core/UI and gateway behaviour tests, broaden only where changes justify it. Use failing behaviour tests before implementation in line with repository practice.
- These proposed seams follow existing project practice and the approved design's behavioural acceptance plan. They are presented for review in this documentation PR; no test or application implementation is claimed.

## Out of Scope

- Production subscription checkout, recurring billing and the paid listening agent.
- Anonymous platform-funded cloud transcription or unlimited spending; local free use remains supported.
- Automatic merging of two established accounts, public feedback issues, file attachments or application-internal reply conversations.
- Automatic forwarding of private issue comments or publication of reporter identity.
- Cloud workspace synchronisation or long-term cloud recording archives.
- More than 60 minutes/500 MB per imported file, additional promised formats, cross-segment long-meeting speaker reconciliation, or guaranteed CPU model suitability.
- Reimplementing existing workspace persistence, general settings layout or utterance playback tickets.
- Claiming Google or any third-party service works on every mainland network, or claiming a hosting migration alone proves complete reachability.

## Further Notes

- This specification synthesises the user's confirmed Q1–Q16 decisions of 2026-09-07. The user requested an isolated branch and documentation PR as the delivery format. The product scope is confirmed; ticket breakdown and testing details are reviewable proposals, and no implementation completion is asserted.
- [Ticket breakdown](auth-feedback-relay-import-tickets.md), [design interview and research notes](../design/auth-feedback-relay.md), [accepted account/feedback ADR](../adr/0052-cloud-accounts-and-accountless-feedback.md).
- Reuse [workspace UI spec #64](https://github.com/TONiiV/Kanal/issues/64), [settings #67](https://github.com/TONiiV/Kanal/issues/67), [workspace persistence #68](https://github.com/TONiiV/Kanal/issues/68), [gateway reachability #41](https://github.com/TONiiV/Kanal/issues/41), and [utterance playback #63](https://github.com/TONiiV/Kanal/issues/63). General live-summary/listening-agent work remains [#34](https://github.com/TONiiV/Kanal/issues/34); imported-record summarisation must not wait for paid real-time agent work.
- #68 is open and its latest clarification preserves workspace folders when forgotten and confines artifacts to owned meeting folders. It is a prerequisite for durable recording import, not proof of already available persistence in this checkout.
- The future application's private feedback destination is separate from this repository's engineering tracker. Publishing this engineering specification does not select or create that private destination.
- Deployment needs a domain, sender identity, maintainer inbox, private feedback repository, service credentials and an explicit resource budget. Collect them during provisioning without embedding secrets in issues or app bundles.
- Source-based capability hypotheses belong to verification tasks. Prefer current official vendor/model documentation at implementation time; the interview's research notes are pointers, not a pinned dependency or measured benchmark.
