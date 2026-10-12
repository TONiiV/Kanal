# 会议智能体：房间状态的旁观者，只提候选

状态：proposed，2026-10-13。落地 [#34](https://github.com/TONiiV/Kanal/issues/34) 第 1 项的抽象层。
开放问题来自 [`docs/design/meeting-intelligence.md`](../design/meeting-intelligence.md) 的 A1–A4。
PR #178 为「智能服务」预留了 ADR 0057。该编号已被配色方案占用，所以本文用 0058。
本文只定义智能服务的一个使用者：旁听 Agent。

## 背景

- 右栏「要点与决定」页签已存在（#79）。`MeetingInsights` 已有候选、确认、驳回三态。
  它拒绝没有来源句的候选。
- `AssistantViewModel.IsAnalystConnected` 从未为真。页签一直显示「尚未接入旁听模型」。
- 操作员要求：以后能接 Claude Code 和 Codex CLI，用操作员自己的订阅。
- 约束来自架构不变量：主机是唯一权威；按能力编排，不按厂商分支；只有文本穿越公网；
  会议本身是主功能，任何附加功能都不能让它停下。

## 决定

### A. 角色

1. **会议智能体（meeting agent）是 `RoomState` 的旁观者。** 它读定稿句子，不写房间状态。
   它的输出只是候选 `MeetingInsight`。
2. **主机是唯一权威。** 运行器（`MeetingAgentRunner`）给每个候选分配新 id，并把状态强制为
   `Candidate`。`MeetingInsights.Record` 拒绝引用不存在句子的候选。只有人能确认。
3. **候选只在主机上。** 候选不进 `room.snapshot`，不发到中继。

### B. 接口

```csharp
public sealed record MeetingAgentCapabilities(IReadOnlySet<InsightKind> Kinds);

public sealed record MeetingAgentWindow(
    IReadOnlyList<Utterance> Finals,   // 本窗口的新定稿，按说话顺序
    IReadOnlyList<string> KnownTopics, // 之前窗口已用过的主题
    string ReplyLanguage);             // 操作员界面语言的 ISO 代码

public interface IMeetingAgent
{
    string Id { get; }
    MeetingAgentCapabilities Caps { get; }
    Task<IReadOnlyList<MeetingInsight>> ProposeAsync(MeetingAgentWindow window, CancellationToken ct);
}
```

4. **按能力编排。** 运行器只读 `Caps`。候选的类型不在 `Caps.Kinds` 里，运行器丢弃它。
   运行器和视图模型都不知道厂商。
5. **能力随第一个使用者一起加。** 以后要加的字段：`DataLeavesMachine`（第一个 CLI 或 API agent
   一起加，知情确认读它）、`Answers`（问答）、`FactCheck`（核查）。现在没有使用者，所以现在不加。
6. **一次调用返回一个列表，不流式。** 一个窗口最多 8 句，一次返回的延迟可以接受。
   需要流式时，返回类型改为 `IAsyncEnumerable<MeetingInsight>`，只改运行器一处。

### C. 运行器

7. **不在音频和转写路径上。** 运行器订阅 `RoomState.UtteranceUpserted`。处理器只收定稿，按 id
   去重（翻译写回也会触发这个事件），然后写入一个无界 channel。处理器从不等待。
   调用 agent 的循环在线程池上运行。
8. **批处理加去抖。** 攒满 8 句定稿就立即送出。不满 8 句时，10 秒内没有新定稿也送出。
   同一时间最多一个调用在途。调用期间到达的定稿进入下一个窗口。
9. **主题延续。** 已出现的主题带入下一个窗口，右栏的分组因此稳定。
10. **失败隔离。** agent 抛出任何异常，运行器写一条 warning，丢掉这个窗口，继续等下一个。
    不重试：重试会让慢 agent 的积压越来越大。转写、翻译和录音不受影响。
11. **取消。** 停止会议时，运行器取消在途调用，并等它结束。取消后到达的结果被丢弃。
    运行器在会话排空之前释放。会话排空时最后几句的翻译要等模型，而在途的 agent 调用可能占着模型；
    先停 agent，这几句才能在 2 秒宽限内译完。运行器因此也先于共享的生成器释放。
12. **线程。** 候选经 `Dispatcher.UIThread.Post` 回到界面线程。只有当前会议房间的候选被记录。

### D. 与智能服务的关系，首个实现

13. **智能服务（PR #178 的 Intelligence provider）是推理来源。会议智能体是它的一个使用者。**
    两者不是同一个接口。`GeneratedMeetingAgent` 是 `ITextGenerator` 之上的一个会议智能体。
14. **首个实现是本地模型。** `GeneratedMeetingAgent` 复用翻译和会议标题共用的同一个
    `ITextGenerator`，所以不加载第二份权重。数据不离开本机。
    输出格式是一行一项：`KIND|TOPIC|TEXT|IDS`。不用 JSON：小模型输出的 JSON 常常不完整。
    窗口里的句子编号为 1..n，IDS 写编号，`GeneratedMeetingAgent` 再映射回句子 id。
    真实 id 是 32 位十六进制 Guid 或 Gladia id，小模型抄不准，而且每个 id 要 15–20 个输出 token。
15. **默认关闭（PM 决定，2026-10-13）。** `PipelinePlanner` 现在不给出 agent，页签照旧显示
    「尚未接入旁听模型」。运行器和主机接线保留，测试经 `PlanFilter` 注入 agent。
    本地 agent 在翻译抢占的 PR（「需要人决定」1b）里开启。那个 PR 带一个测试：翻译不等 agent 推理。
    开启后的条件：操作员选了并下载了本地翻译模型。本文不加设置界面。

### E. CLI harness 和 API key 如何接入（本 PR 不实现）

16. **CLI harness 是另一个 `IMeetingAgent` 实现。** 每个窗口启动一个进程，prompt 经 stdin 传入，
    结果经 stdout 读回。认证用操作员本机 CLI 的登录和订阅。Kanal 不保存这些凭据。

```csharp
public sealed class CliProcessAgent(string executable, string[] arguments, string id) : IMeetingAgent
{
    // 例：claude -p --output-format text
    // 例：codex exec --sandbox read-only -
    // 参数在实现时按当时的 CLI 版本核对。WorkingDirectory 设为一个空的临时目录。
    public string Id => id;
    public MeetingAgentCapabilities Caps { get; } = new(AllKinds) /* + DataLeavesMachine: true */;

    public async Task<IReadOnlyList<MeetingInsight>> ProposeAsync(MeetingAgentWindow window, CancellationToken ct)
    {
        using var process = Process.Start(new ProcessStartInfo(executable, arguments)
            { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false })!;
        await using var _ = ct.Register(() => process.Kill(entireProcessTree: true));
        await process.StandardInput.WriteAsync(AgentPrompt.For(window));   // 与本地实现共用
        process.StandardInput.Close();
        var output = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0)                                          // 登录过期等：抛出，运行器记录
            throw new InvalidOperationException(await process.StandardError.ReadToEndAsync(ct));
        return AgentPrompt.Parse(output);                                   // 同一个行格式解析器
    }
}
```

17. **CLI 只读。** 明确关闭工具，或用只读沙箱。转写内容是证据，不是指令：参会者说出的「请运行……」
    不能变成操作。超时等于取消，取消会结束整个进程树。两个 CLI 都读工作目录里的项目指令和设置，
    所以工作目录是一个空的临时目录。
17a. **CLI PR 的待办（评审第 1 轮）。**
    1. 非零退出码必须抛出。否则过期的登录只返回空列表，运行器什么也不记录。
    2. 连续失败 N 次后，运行器进入「agent 不可用」状态，`IsAnalystConnected` 变为 false。不改接口。
    3. 慢 agent 一次取走全部排队的定稿（有上限），否则积压越来越大。
    4. 主题列表只送最近 20 个。
18. **常驻进程是同一接口后的优化。** PR #178 提议 Codex 先用 `codex app-server` 常驻。
    常驻省掉每窗口的启动时间，但不改变 `IMeetingAgent`。
19. **API key provider 是 `ITextGenerator` 的远程实现。** 它套用同一个 `GeneratedMeetingAgent`。
    只有需要工具调用时，才单独写一个 agent；那时 Kanal 自己负责对话和工具编排。
20. **选择 provider 只在 `PipelinePlanner` 里发生**，与 ASR 和 MT 相同。
21. **数据去向。** CLI 在本机运行，但它的模型通常在云端。所有 CLI 和 API agent 都声明
    `DataLeavesMachine`。知情确认对话框（ADR 0054）列出厂商名。离开本机的仍然只有文本。

### F. 成本与延迟

22. 默认值是 8 句或 10 秒静默。一场 60 分钟的会大约产生 50–100 次调用。
    订阅类 CLI 有用量上限，所以 CLI agent 可能需要更大的批量；批量是运行器的构造参数。
23. **已知风险：本地 agent 和实时翻译共用一个模型和一把锁。** 这把锁按先来先到排队，翻译没有优先级。
    一次 agent 调用约 450–600 个 prefill token，回复 100–200 个 token，最多 512 个。笔记本上 15–40 tok/s，
    agent 通常占锁 4–13 秒，最坏 13–35 秒。密集发言时每 3–5 秒一句定稿，满 8 句的触发在发言中途，
    不在停顿里。翻译加 agent 的占用超过 100% 时，翻译积压在整场会里一直增长。
    所以本地 agent 默认关闭（D.15），等翻译抢占（「需要人决定」1b）。

## A1–A4 的推荐答案（proposed）

| 问题 | 推荐 | 本 PR 的代码 |
|---|---|---|
| A1 输入 | 只用当前会议的定稿原文。不送音频、partial 或译文。历史会议和项目文件以后由操作员显式加入。 | 已按此实现 |
| A2 生命周期 | 会议进行中旁听。暂停时音频门控已经阻止新定稿；暂停前的尾句照常分析。停止时取消在途调用，已有候选保留，可以继续确认。浏览其他记录不改变旁听对象。 | 已按此实现 |
| A3 主动行为 | 首版只提三类候选。不联网核查，不调用任何工具。只读联网检索随 `FactCheck` 能力加入，并标明来源。写文件、运行命令、外发消息永远不由 agent 自主执行。 | 已按此实现 |
| A4 可见范围 | 只有操作员可见。候选不进 snapshot，不发到中继。确认后的决定是否同步给参会者，由阶段 4.5（G 轨）决定。 | 已按此实现 |

## 需要人决定

1. **共享模型的延迟（F.23）。已决定（PM，2026-10-13）：** 本 PR 里本地 agent 默认关闭。
   b. 翻译抢占：翻译请求到达时取消在途的 agent 推理，这个窗口重新排队。单独一个 PR，本地 agent 在那里开启。
   被拒：a. 接受并依靠去抖（去抖在密集发言时不起作用）；c. 只在暂停或会后运行。
2. 接受或修改 A1–A4 的推荐答案。
3. 首个 CLI：Codex（PR #178 的顺序）还是 Claude Code；每窗口一个进程还是常驻进程。推荐 Codex，
   先用每窗口一个进程，量出启动时间再决定常驻。
4. 批量和静默时长是否进设置。推荐不进：操作员不需要理解这两个数。
5. 有本地模型时本地 agent 是否默认开启。已决定：在 1b 之前关闭。

## 考虑过的方案

- **agent 直接写 `MeetingInsights` 或 `RoomState`**：违反主机唯一权威。被拒。
- **每句定稿调用一次**：调用次数和锁占用过高。被拒。
- **把 agent 建在 `IMtProvider` 上**：MT 是逐句翻译契约，没有主题、类型和来源。被拒。
- **为 agent 再加载一份模型权重**：多占数 GB 内存。`PipelinePlanner` 已因此拒绝过第二份。被拒。
- **给 CLI 开一个 MCP server，让它自己拉转写**：主动权在 agent 一侧，节流和取消都不归主机管。
  推送窗口更简单，也能用假实现测试。被拒。
