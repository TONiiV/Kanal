# Kanal：会议工作空间、转写与会议智能

## Problem Statement

操作者需要在会议中快速控制收音、转写、翻译并查看内容。当前主机工具栏拥挤、横向滚动，
按钮、语言选择与设置缺乏稳定的组织方式；多场会议也缺少清楚的项目归属与浏览入口。
会后需要识别谁说了什么、回听原话、生成标题和摘要，并在会中获得可追溯的信息帮助。

用户已完成并确认 B 方案原型，要求将其整理为实现规格。现有原型仅演示部分交互，
不能将示例数据、模型选项或演示按钮视为已实现功能。

## Solution

采用左右贯穿全高的可折叠、可调宽侧栏，中间集中会议操作和转写正文。左侧管理工作空间
与会议记录，设置固定在底部；右侧展示要点、决策和发言人管理，并为未来旁听 Agent
提供独立页签。操作者可选择本地转写／翻译模型、用本地模型生成会议标题、导入会议材料、
从句子回放原始录音。既有暂停、停止与知情确认语义保持有效。

本规格是分阶段实现的总体入口：已确认 UI 可立即实施；新功能的明确目标同时落入工作项，
尚未确认的权限、生命周期和数据策略列于 Further Notes，不作为隐含授权或既定设计。

## User Stories

1. As a meeting operator, I want full-height workspace and assistant sidebars, so that navigation and meeting content remain distinct.
2. As a meeting operator, I want to collapse either sidebar independently, so that I can give more space to the transcript.
3. As a meeting operator, I want to reopen a collapsed sidebar from the centre toolbar, so that its controls remain reachable.
4. As a meeting operator, I want to drag either sidebar's inner edge, so that I can adjust space for long meeting names or decision notes.
5. As a meeting operator, I want expanded sidebars to retain their chosen width, so that temporary collapse does not reset my layout.
6. As a meeting operator, I want aligned header separators and controls, so that the interface remains calm and readable.
7. As a meeting operator, I want the central transcript to remain usable in every sidebar state, so that hiding a panel never collapses my content.
8. As a meeting operator, I want a clear processing-mode picker, so that I know how transcription and translation are performed.
9. As a meeting operator, I want a capture-mode button immediately beside processing mode, so that I can choose an in-room or online meeting.
10. As a meeting operator, I want microphone selection beside the transport controls, so that I can quickly check the input device.
11. As a meeting operator, I want recording controls centred in the middle column, so that I can find them while following the conversation.
12. As a meeting operator, I want a red record icon while idle, so that starting a meeting is unambiguous.
13. As a meeting operator, I want yellow pause and red stop controls while running, so that I can distinguish their actions immediately.
14. As a meeting operator, I want resume after pause and cancellation during model loading, so that I retain the existing meeting lifecycle controls.
15. As a meeting operator, I want a compact processing state near transport, so that status does not consume two full-width rows.
16. As a meeting operator, I want the transport to remain visible as the window narrows, so that I can always pause or stop.
17. As a meeting operator, I want coherent SVG icons with short tooltips and explanatory flyouts, so that fewer visible labels do not remove meaning.
18. As a meeting operator, I want the QR entry at the right of the centre toolbar, so that participants can join without opening settings.
19. As a meeting operator, I want settings at the bottom of the left sidebar, so that infrequent configuration does not crowd meeting controls.
20. As a meeting operator, I want settings organised by vertical left-hand tabs, so that I can find audio, models and workspace configuration.
21. As a meeting operator, I want to search meetings directly below the brand, so that I can locate a record quickly.
22. As a meeting operator, I want a separate new-meeting button beside search, so that creating a meeting takes one clear action.
23. As a meeting operator, I want a folder-style workspace selector, so that I know which project owns the records I see.
24. As a meeting operator, I want peer workspaces for projects, companies, teams or personal use, so that I can organise meetings without a mandatory hierarchy.
25. As a meeting operator, I want each workspace to use a selected local folder, so that meeting records, transcripts and summaries have a durable home.
26. As a meeting operator, I want the workspace add menu to offer new project and import meeting, so that both creation paths are discoverable.
27. As a meeting operator, I want to import an audio recording as a meeting, so that it can be transcribed and reviewed.
28. As a meeting operator, I want to import text as a meeting record, so that existing notes or transcripts can join the workspace.
29. As a meeting operator, I want import and export in each meeting's ellipsis menu, so that these actions are associated with the correct record.
30. As a meeting operator, I want to browse each meeting's transcript and summary, so that I can revisit its content.
31. As a meeting operator, I want the meeting title above the transcript without a redundant project-name header, so that content receives more space.
32. As a meeting operator, I want a local model to generate the default meeting title, so that records become recognisable without manual naming every time.
33. As a meeting operator, I want to rename a meeting manually, so that its title reflects my own context.
34. As a meeting operator, I want to regenerate a meeting title, so that I can obtain a better description after the conversation develops.
35. As a meeting operator, I want overlapping circular language flags to the right of the meeting title, so that language selection remains compact.
36. As a meeting operator, I want to select up to four target display languages, so that I can support the participants without losing readability.
37. As a meeting operator, I want original speech and translations clearly distinguished, so that I can verify technical wording.
38. As a meeting operator, I want no redundant transcript/summary/decision tabs across the central content, so that those controls do not duplicate the assistant panel.
39. As a meeting operator, I want local transcription models managed in settings, so that I can choose a model suitable for my hardware and languages.
40. As a meeting operator, I want Nemotron 3.5 ASR Streaming 0.6B evaluated as the preferred local model, so that the implementation follows my selected direction.
41. As a meeting operator, I want local translation and summarisation model choices with accurate availability, so that I am not offered unsupported functionality.
42. As a meeting operator, I want a fresh participant-awareness confirmation before starting a real meeting, so that I remember to explain the processing to everyone.
43. As a remote participant, I want the operator to explain transcription and optional recording, so that I do not depend on seeing the host application.
44. As a meeting operator, I want audio-file retention separate from transcription, so that starting transcription does not silently change my recording choice.
45. As a participant, I want pause to stop audio reaching transcription and recording, so that pause has a consistent meaning.
46. As a meeting operator, I want live points and a topic-to-proposal-to-decision map, so that I can follow the conclusions being formed.
47. As a meeting operator, I want model-suggested decisions marked as candidates until confirmed, so that suggestions are not mistaken for accepted commitments.
48. As a meeting operator, I want decision and fact-check references to lead back to supporting material, so that I can inspect the evidence.
49. As a meeting operator, I want speaker attribution in the transcript, so that I can tell who made a statement.
50. As a meeting operator, I want speaker naming and non-destructive merging in the right sidebar, so that I can correct attribution without losing original tags.
51. As a meeting operator, I want to click a sentence and replay its recorded audio segment, so that I can verify the exact words.
52. As a meeting operator, I want an explicit unavailable state when source audio or alignment is missing, so that a text-only record does not pretend to support replay.
53. As a meeting operator, I want a future listening-agent tab in the right sidebar, so that I can consult an assistant alongside the meeting.
54. As a meeting operator, I want to connect an agent through a local harness CLI or an API-key model provider, so that I can use an appropriate runtime.
55. As a meeting operator, I want the connected agent to follow the meeting continuously, so that I do not have to restate every new discussion point.
56. As a meeting operator, I want to ask the listening agent questions at any time, so that I can resolve uncertainty during the discussion.
57. As a meeting operator, I want proactive suggestions about potentially useful information, so that I can notice relevant gaps or opportunities.
58. As a meeting operator, I want timely fact-check findings with distinguishable evidence and uncertainty, so that model speculation is not presented as verification.
59. As a meeting operator, I want accessible names and keyboard routes for icon controls, so that the compact interface remains operable without a pointer.
60. As a meeting operator, I want unavailable models, devices, files and providers to report actionable states, so that failures do not silently change the meeting's behaviour.

## Implementation Decisions

- **批准的 UI 基线。** 实现最终 B 方案：左右贯穿全高的侧栏，中间工具栏与正文；取消顶部项目名称行及两行状态栏。侧栏顶栏和中间工具栏同高。保留可拖宽边缘、独立折叠与恢复。
- **明确布局归属。** 左栏、中栏、右栏分别占用固定的布局区域；隐藏一栏不能让正文自动进入零宽占位。原型曾出现此问题，正式实现必须消除该失败模式。
- **工具栏顺序。** 中栏左侧为处理模式及其右侧收音模式；录制、暂停／继续、停止及麦克风位于中央；二维码靠右。设置归左栏底部。收窄时中央优先，两侧裁切，保持无横向滚动。
- **录制状态机。** 保留现有 Start、Pause/Resume、Stop、加载取消和停止中防重复行为。音频保存与转写独立；真实开始前每次重新确认知情，在线会议提醒告知远程参与者。会中用紧凑状态和参会端说明表达处理状态。
- **工作空间边界。** 按已接受 ADR 0051 使用平级 Workspace。每个工作空间对应用户选择的本地文件夹，拥有自己的 Meeting records。公司／项目层级未采用。
- **记录操作。** 搜索与独立新建会议入口位于品牌下。项目旁添加菜单支持创建项目和导入新会议。记录三点菜单提供导入／导出；录音与文本导入均在目标范围，正式支持格式和持久化 schema 尚未冻结。
- **标题与语言。** Meeting title 区别于 Workspace 名称；默认通过本地模型生成，支持手工重命名和重生成。标题右侧保留圆形重叠旗标选择最多四种显示语言。没有额外“目标显示语言”一行或中央重复页签。
- **设置与模型。** 纵向设置页签覆盖通用、输入、本地 ASR、翻译、总结及工作空间，并保留现有关于和许可入口。Nemotron 3.5 为本地 ASR 首选；Handy 是模型管理及兼容目标的参考，不能直接当作已验证的 Kanal 支持清单。先验证 Windows NVIDIA，调查 CPU／Apple Silicon。
- **能力驱动。** 保留现有基于 ASR 能力决定翻译路由的原则，收音模式与处理模式独立。标题模型与 Agent provider 分开配置；本地 CLI 不保证本地推理。现有本地文本生成模块可评估复用于标题，不能把单次生成接口直接当作完整 Agent 运行时。
- **会议助手。** 要点／决策和发言人分别位于右侧页签；候选决定经人工确认，能回看来源。非破坏合并保留原始发言人标签。识别模型和逐句回放分别由 #13、#63 跟踪。
- **回放基础。** 评估现有 Utterance ID、revision、起止时间与录音模块，建立稳定音频定位。必须验证暂停、重连和分段后的时间轴；可空结束时间不构成可播放区间。回放原音，不使用 TTS 代替。
- **Agent 接入。** 后续新增独立的旁听 Agent 会话／provider 层。参考 T3 Code 的 runtime adapter 与配置实例隔离；本地 harness 与直接 API 模型是不同接入路径。直接 API 接入需要明确谁负责对话、工具与调度。
- **数据流复用。** 评估 RoomState 的发言更新、定稿集合和快照作为旁听输入，避免重复采集。但是否只接收定稿、是否使用音频及历史上下文尚未决定，不能把候选方案写成既定契约。
- **分批实施。** 首先 UI 与设置重排，然后本地模型管理及 ASR，再推进持久化记录、标题、总结、识别与回放；Agent 为后续工作。每批保持可运行与可独立验证，避免把全部未来能力塞入一次 UI PR。

## Testing Decisions

- 优先从最高的已有入口验证行为：主机视图模型的命令、会议会话与公开 RoomState 事件。测试观察命令可用性、会议状态、显示数据及音频去向，不断言私有字段、调用顺序或原型 DOM。
- 现有 Hermetic 视图模型构造、Avalonia Headless、假 ASR／翻译 provider 和演示模式是先例；复用 Start/Stop、Pause、warm-up cancellation、speaker rename/merge、capture-profile、language-limit 和 export 测试模式。
- 录制回归覆盖启动前确认、取消加载、停止中重复操作、暂停不向 ASR／WAV 送音频、恢复与新会话。已知暂停前的输入可能延迟定稿，应有针对性的契约测试。
- 侧栏开合、宽度选择、搜索、会议选择与设置导航尽量从同一主机入口验证。像素、顶栏对齐、长名称、不同宽度及多语言字体使用独立视觉验收，不用内部布局结构测试替代。
- 新的持久化、标题和回放能力优先通过主机命令加可替换的存储／模型／播放器边界测试。若需新 seam，在应用服务边界增加一个窄接口，避免为每个组件新增测试专用接口。
- 标题测试使用可控生成结果和延迟，验证手工命名、取消、失败与旧结果到达的行为；覆盖范围以最终确定的标题冲突策略为准，不擅自选定覆盖策略。
- 导入与回放使用临时工作空间及短音频／文本 fixture，覆盖缺失文件、空区间、时间偏移、不同会议资源隔离、损坏数据及文件读取失败。是否支持会中回放决定是否添加并发写读和重采集用例。
- 发言人识别的真实模型效果需独立语料验证，包括中文／德语／波兰语、重叠讲话、现场与在线混音；CI 用假结果验证标签及手工修正流程，不下载大模型。
- Agent 后续通过会话级假 provider 验证增量上下文、断线、取消、来源和状态；权限及主动行为边界明确后再固定相应断言。
- 运行受影响测试及仓库要求的构建／测试。原型语法和 HTTP 检查仅证明演示文件可解析、可提供，不能证明桌面 UI 或真实推理可用。

## Out of Scope

- 在 UI 重排批次内一次实现全部模型、工作空间和 Agent 后端。
- 将一次性 HTML 覆盖脚本直接作为生产组件，或继续开发未选 A／C 布局。
- 未经新决定引入公司→项目层级、账号协作平台或任意跨工作空间访问。
- 将“发言人识别”默认为跨会议声纹身份匹配；将声道标签默认为逐人识别。
- 将支持音频回放解释为默认开启音频保存，或用 TTS 伪装原录音。
- 自动授予旁听 Agent 任意文件修改、命令执行、消息外发或全项目访问权。
- 声称知情弹窗本身即构成完整 GDPR 合规，或本地 CLI 即保证音频／文本不离开设备。
- 变更现有文本中继边界、破坏性发言人合并、未请求的云端发布或模型能力承诺。

## Further Notes

- 用户已确认样品设计完成；总体规格由已讨论内容直接合成，不开展新访谈。
- 原型随仓库设计文档保存为 `meeting-ui.prototype.html`，可直接使用浏览器打开，无需启动器。
  早期本地归档提交 `4c4d3db` 保留为历史。
- #13 已补充发言人识别需求；#63 跟踪句子回放；#49 为本地 ASR；#34 为实时总结。
  #49 的 Whisper-first 方案与本次 Nemotron 首选存在历史差异，实施时以本次用户偏好为方向并重新核实。
- ADR 0050 的音频捕获与知情要求继续适用；本次替换主机提示位置。ADR 0051 固定工作空间归属边界。
- 未确认事项不阻塞已定 UI，但相关功能进入实施前需记录明确契约：标题首生成时机、手工改名与重生成冲突；
  工作空间内活动会议与浏览记录关系；Agent 的输入／历史范围、暂停结束行为、主动联网与预算、结果分享对象；
  具体 harness／API 协议；发言人身份范围；会中回放与音频保留策略。
- 可作为后续提案但尚未获确认：标题在有足够定稿后生成且不自动覆盖手工名；Agent 先消费本场定稿、建议仅操作者可见；
  首版回放限已结束且有录音的会议。这些不能因标签为 ready-for-agent 而被误当作已接受需求。
- T3 Code 参考：官方 [术语](https://github.com/pingdotgg/t3code/blob/main/docs/internals/glossary.md)、
  [provider 边界](https://github.com/pingdotgg/t3code/blob/main/docs/internals/providers.md)、
  [接入配置](https://github.com/pingdotgg/t3code/blob/main/docs/user/install.md)。核对日期 2026-09-07，未固定其提交。
