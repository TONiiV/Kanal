# 会议标题与旁听 Agent：设计讨论

状态：2026-09-07 新增需求，讨论中；不作为 provider 接入或权限边界的已批准实现规格。
已完成 UI 基线见 [会议工作空间](meeting-workspace.md)。

## 已确认目标

- 中央会议标题默认由本地模型生成，支持手动重命名与主动重新生成。
- 右侧未来增加“旁听 Agent”页签。
- Agent provider 可接入本地 harness CLI，或使用 API key 的模型服务；交互参考 T3 Code。
- 接通后持续旁听，用户可随时提问；Agent 可主动推测有用的信息、提供建议及实时事实核查。

“CLI 在本机运行”与“推理及数据留在本机”是不同事实。标题的本地生成要求不自动扩展成
所有 Agent provider 必须本地推理；反过来，选择远程 Agent 也不改变标题默认本地生成要求。
实际 provider、协议、认证、平台支持和模型能力均需核实。

## 决策树与当前待答问题

| 分支 | 当前开放问题 | 建议，尚未确认 |
|---|---|---|
| T1 标题生命周期 | 何时首生成；手工命名后如何重生成 | 有足够转写时首次生成；手工名称不自动覆盖；重新生成先预览后替换；失败保留现有标题 |
| A1 旁听输入 | 音频还是转写；能否使用工作空间历史和文件 | 首版只消费当前会议的定稿转写；历史与项目文件显式加入上下文 |
| A2 连接生命周期 | 暂停、停止、切换会议时是否继续处理 | 暂停停止新增输入与主动分析，已知内容仍可问答；结束后可问答但不旁听；切换浏览记录不改变旁听对象 |
| A3 主动行为 | 建议频率、联网核查及执行权限 | 侧栏呈现建议；允许主动只读检索并标明来源；修改文件、运行命令或外发消息需独立授权 |
| A4 发言范围 | Agent 内容是操作者私有还是同步给参会者 | 首版仅操作者可见，避免将猜测直接作为会议事实广播 |

待上述分支定下后，再讨论：provider 首发集合和连接体验、上下文增量与断线补齐、预算及
调用节流、核查结果如何更新、保存与删除策略、重新生成并发结果及手工标题冲突。
对“实时”的延迟目标应随模型和硬件验证制定，不能从原型动画推导。

## 场景，用于后续验收设计

- 本地标题模型运行中，用户手工改名；旧结果返回时如何处理？
- 会议进行中浏览上一场记录，Agent 问答究竟针对哪一场？
- 暂停时已有联网核查在进行，是否取消；返回结果应标记什么时间？
- 发言人提出未经证实的价格或交期，Agent 的候选建议如何与已确认决定区分？
- CLI 已连接但其实际使用云端模型，操作者在哪里得知数据去向？
- 项目文档包含命令、链接或指令时，Agent 如何将其作为证据而非操作授权？

## 实现现状

HTML 原型不含上述 Agent 实现，也未实现真实模型生成标题。现有 ASR／翻译 provider 不等于
Agent provider；实际可复用边界应依据代码调查，而非只按“provider”一词合并接口。
## 调查结果（2026-09-07）

核对 T3 Code 官方仓库当日 `main`，不是固定提交；实现前应重新核对协议版本。

- T3 Code 将 provider 定义为受控的 Agent runtime，driver 是集成类型，provider instance
  拥有独立配置与生命周期，session 与线程关联。参见
  [官方术语](https://github.com/pingdotgg/t3code/blob/main/docs/internals/glossary.md)。
- Adapter 归一化协议事件与命令，隔离配置、会话和权限。参见
  [Provider 约束](https://github.com/pingdotgg/t3code/blob/main/docs/internals/providers.md)。
- 配置支持可执行文件路径、环境变量、API key 和自定义 base URL，但这不能等同于任意模型 API
  都能直接成为完整 Agent。Kanal 的“harness 接入”和“直接模型 API 接入”应分别评估；
  后者需要自行负责对话与工具编排。参见
  [安装与 provider 配置](https://github.com/pingdotgg/t3code/blob/main/docs/user/install.md)。
- T3 服务端拥有进程、认证与副作用；可参考其边界，不直接搬入编码专属的工作树／检查点流程。
  参见 [架构说明](https://github.com/pingdotgg/t3code/blob/main/docs/internals/overview.md)。

Kanal 可复用的现有代码线索：

- `src/Kanal.Core/Room/RoomState.cs` 的 `UtteranceUpserted`、`RecentFinals`、`Snapshot`；
  `Models/Utterance.cs` 的 ID、revision、时间戳与 final 标记，可作为转写观察和来源定位基础。
- `src/Kanal.Core/Room/MeetingSession.cs` 已有暂停音频门控；暂停前输入仍可能产生定稿，
  旁听暂停策略需要明确处理这种情况。
- `src/Kanal.Providers.LocalMt/ITextGenerator.cs` 和 `LlamaSharpTextGenerator.cs` 可评估为
  本地标题生成基础；现有接口是单次生成，并不含完整 Agent 对话、工具、调度契约。
- `src/Kanal.Host/Services/PipelinePlanner.cs` 仍将本地 ASR 标为不可用；所查生产代码中
  尚无已实现的会议标题生成或旁听 Agent 层。
