# Kanal PRD v0.3 — Avalonia 主机 + 只读手机端

> 原始文档为 HTML 版（2026-07-29，内部工具）。本文件是其内容的 Markdown 转录，作为仓库内的权威需求参考。

**主机**：.NET 9 + Avalonia · Win / macOS（转录自原始文档；实现已于 2026-09-04 升级到 .NET 10）
**手机端**：静态页 · 纯只读
**公网上跑什么**：只有文本
**首个目标**：下次波兰会议能用

## 00 — 定位修正：这是内部工具，不是产品

评判标准只有一条：**省下的时间是否多于花掉的时间。**

**真实场景**：波兰的自有项目。中国供应商不会英语，波兰服务商不会英语,你和合伙人说德语，合伙人会波兰语。目前靠中→德→波的**人力双重翻译**。已试过讯飞和 Google Translate，要么慢要么不准。

**关键差异**：没有共同语言兜底。这是「唯一可行的沟通方式」的场景——竞争对象是一个已经崩溃的流程。

### 相对 v0.2 的变更

| v0.2 | 状态 | v0.3 |
|---|---|---|
| Electron 主机 | 替换 | **Avalonia + C#**。跨平台 Avalonia/C# 本来就在做；麦克风采集、本地推理、录音缓存在 .NET 里是标准活 |
| 系统音频回环 / AEC | 删除 | 线下面对面会议没有远程参会者。远程改走第三方 bot，M1 |
| Cloudflare Workers + Durable Objects | 替换 | **托管 pub/sub**（Ably / Supabase Realtime），零服务端代码 |
| 自托管模型网关 | 删除 | 主机本身就是网关；本地模型经本地子进程 |
| 端到端加密 / 房间密钥 | 推迟 | 内部工具；中继上只有文本，风险可接受 |
| Provider 抽象 | 保留·提前 | 进 MVP。C# 接口 + DI |
| 核心 UI/UX | 冻结 | 主机多列 + 手机单列下拉，按已确认线框实现 |

## 01 — 架构：音频全在本地，公网只跑文本

- USB 会议麦 48 kHz → Avalonia 主机（采集 → 重采样 16 kHz PCM → IAsrProvider 流式 → IMtProvider（caps 缺翻译时）→ RoomState 本地权威副本 → 多列 UI 本地渲染）
- 主机 → 托管 pub/sub（仅文本）→ 手机静态页 × N（Vercel / CF Pages，只读单列 + 语言下拉）
- M0 妥协：用 Gladia 时音频要出本机；M2 换本地模型后音频边界才严格成立
- 录音缓存 → 会后重跑（M1）；二维码 = 房间 URL + code

### 为什么需要中继

主机是会议室里 NAT 后面的一台笔记本，手机连不上它。静态托管不能持有长连接。

| 选项 | 代码量 | 取舍 |
|---|---|---|
| **托管 pub/sub（选这个）** | 零服务端 | Ably、Pusher 或 Supabase Realtime。免费额度绰绰有余 |
| ~~自建 WebSocket 中继~~ | 一个 ASP.NET Core 服务 | 第三个要长期维护的部署单元，不值得 |
| ~~cloudflared 隧道回主机~~ | 零 | 延迟最低，但每次开会要起隧道。**留作备选**（pub/sub 被墙时切换） |

**网络现实检查**：在波兰开会时 Ably/Supabase 都能连；将来在中国境内开会则不可靠。中继做成可替换层（`IRelayPublisher`），换实现只改一个类。

## 02 — Provider 抽象：能力声明式，不按厂商分支

```csharp
public record AsrCapabilities(
    bool Streaming, bool Diarization, bool Translation, // false = 需要 IMtProvider
    bool AutoLanguageDetect, IReadOnlySet<string> Languages,
    LatencyClass Latency); // Realtime | Near | Batch

public interface IAsrProvider {
    string Id { get; }
    AsrCapabilities Caps { get; }
    Task<IAsrSession> StartAsync(AsrSessionOptions o, CancellationToken ct);
}

public interface IAsrSession : IAsyncDisposable {
    ValueTask PushAudioAsync(ReadOnlyMemory<byte> pcm16); // 16 kHz mono
    IAsyncEnumerable<AsrEvent> Events { get; }
}

public interface IMtProvider {
    string Id { get; }
    Task<IReadOnlyDictionary<string, string>> TranslateAsync(
        string text, string from, IReadOnlyList<string> to,
        IReadOnlyList<Utterance> context, CancellationToken ct);
}
```

**编排器逻辑只有一句判断**：`if (!asr.Caps.Translation) → 把 final 段落交给 IMtProvider`。

| 实现类 | Caps | 说明 |
|---|---|---|
| `GladiaAsrProvider`（M0 默认） | Streaming ✓ Diarization ✓ Translation ✓ AutoLang ✓ Realtime | 一次 WebSocket 拿全套。50€ 免费额度 ≈ 60 小时实时 |
| `WhisperAsrProvider`（离线基线） | 全 ✗，**Batch** | 有意标成 Batch——不进实时路径，只用来比质量 |
| `NemotronAsrProvider`（M2） | Streaming ✓ Translation ✗ Realtime | 本地。ZH/PL 在最高精度档。需配流式 Sortformer 分离 |
| `MossAsrProvider`（M1 纪要） | Diarization ✓ Batch | 会后整段重跑：128k 上下文、90 分钟单遍、离线全局聚类修说话人一致性 |
| `QwenMtProvider` | — | 本地翻译层 |

**本地模型怎么跑**：主机启动 Python 子进程（FastAPI + websockets），C# Provider 连本地 `ws://127.0.0.1`。对外仍是同一个 `IAsrProvider`。

## 03 — 事件与状态：主机是唯一权威

```csharp
public record Utterance(
    string Id,
    string SpeakerTag,        // 'S01' — 来自盲分离，会漂
    long TStartMs, long? TEndMs,
    string SrcLang, string SrcText,
    int Revision,             // partial 每次改写 +1
    UtteranceState State,     // Partial | Final
    bool CodeSwitch,
    double SpeakerConfidence,
    IReadOnlyDictionary<string, string> Translations);

public record Speaker(string Tag, string? DisplayName,
    IReadOnlyList<string> MergedFrom, string Color);
```

| 频道消息 | 说明 |
|---|---|
| `utterance.upsert` | partial/final 同一消息类型，按 `Id` 就地替换。幂等 |
| `translation.upsert` | 带 `SourceRevision`，落后当前版本直接丢弃 |
| `speaker.upsert` | 改名与合并共用。客户端**回改所有历史气泡** |
| `room.snapshot` | 全量状态：中途扫码入房 backlog；断线重连对齐 |
| `room.config` | 语言列表变更 |

**中途入房必须能看到前文**：先请求 `room.snapshot`，再接增量。

## 04 — 界面（已冻结）

| 规则 | 说明 |
|---|---|
| 译文在上，原文在下 | 原文是可信度锚点，一行不折行，点开展开 |
| 灰字 = 还会变 | partial 灰色，final 转黑 |
| 头像色跟人走 | 不跟语言走 |
| 手机切语言不重连 | 所有译文都在推送流里，切换纯客户端过滤 |
| 断线不白屏 | 保留最近 50 条本地缓存 + 重连状态 |
| 主机 4 列上限 | 超过 4 种语言时主持人指定上屏列，其余仍推手机 |
| 不做 TTS | 字幕是正确答案 |

**混说降级（需 M0 实测）**：中↔波之间「显示原文」失效（波兰人看不懂中文）。改为**逐词翻译 + 不确定标注**——确定的词翻出来，不确定的部分醒目标出并保留原文。

## 05 — 范围

**MVP 之内**：Avalonia 主机（Win+macOS）、麦克风采集+16kHz 重采样、Provider 抽象+Gladia 实现、多语言聊天室多发言人、一键改名与合并、主机 4 列、二维码+手机只读单列+语言下拉、snapshot 补历史、混说逐词降级、md/json 纪要导出。

**MVP 之外**：回环/AEC、远程参会者（M1 bot）、本地模型（M2）、MOSS 纪要重跑（M1）、声纹、E2E 加密、TTS、账号、术语表 UI、Teams 侧板。

## 06 — 计划

### D0（半天，不写产品代码）
- **A·音频采集**：Avalonia 打开麦克风→采样→重采样 16 kHz→写 WAV→听。**先在 macOS 上做**
- **B·术语质量**：真实料号和规格的话扔进 Gladia，只看中→波和波→中。**这一条决定整个项目有没有意义**

### M0（~2 周，硬 kill date）：下次波兰会议能用
- D1–2 音频管线（采集→环形缓冲→重采样→16 kHz PCM 帧，设备切换与断开恢复）
- D3–4 GladiaAsrProvider（ClientWebSocket、心跳、断线重连、事件归一化）
- D5–6 RoomState + 主机 UI（多列、partial 就地替换、灰转黑、改名合并）
- D7 接 pub/sub（C# SDK 发布 + 手机订阅）
- D8–9 手机前端（vanilla JS/Preact 几十 KB，连频道、气泡、语言下拉、snapshot、断线缓存）
- D10 混说降级 + 纪要导出 + 真人彩排（30 分钟模拟会）

**验收**：一场真实的波兰会议里，你和合伙人**停止**做人力双重翻译。

### M1（按需，不要提前做）：远程参会者 + 可交付纪要（Recall.ai 类 bot + MOSS 离线重跑）
### M2（按需）：本地模型（Python 子进程 + Nemotron 3.5 + 流式 Sortformer + Qwen）

## 07 — 风险

| 风险 | 严重度 | 缓解 / 待验证 |
|---|---|---|
| 中↔波技术术语翻不对 | 最高·未验证 | 无共同语言兜底。为真则项目无意义。D0-B 半天验完 |
| 跨平台音频采集 | 高·未验证 | NAudio 是 Windows-only；跨平台要 PortAudio 绑定 / OpenAL / SoundFlow。重采样自己写线性插值。**先在 macOS 上验** |
| 1.5 秒延迟能否支撑双向讨论 | 高·结构性 | 彩排时专门记录 60 分钟里因字幕**误解**对方几次 |
| 盲分离标签漂移 | 中·固有 | 一键改名（M0）+ 会后重跑（M1）。60 分钟超 10 次改名则交互失效 |
| 「音频不出本机」M0 不成立 | 中·沟通 | 对合伙人和供应商如实说明；M2 后才成立 |
| 时间预算 | 中·个人 | 第三条平行线。M0 两周拖成两个月就该停。设硬 kill date |
| pub/sub 在中国不可用 | 低·现在 | `IRelayPublisher` 抽象，换隧道或国内服务只改一个实现类 |

**停止条件**：D0-B 术语翻译不可用，或 M0 彩排后仍在做人力翻译——**停掉项目，不要改进它**。
