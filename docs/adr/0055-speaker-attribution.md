# 发言人分离：实时暂定标签，会后权威重算

状态：proposed，2026-09-08。落地 [#13](https://github.com/TONiiV/Kanal/issues/13) 与
[`docs/design/meeting-evidence.md`](../design/meeting-evidence.md) 一直悬着的「发言人识别」范围问题。
运行时选择继承 [ADR 0053](0053-local-transcription-model-and-runtime.md)。

## 背景

房间里只有一支麦克风，而整套 UI 是按「知道谁在说话」设计的。

- **Gladia 实时接口不返回 speaker。** 2026-07-30 实测：`/v2/live` 的响应里没有 speaker 字段，
  `GladiaWire` 落到 `?? "S01"`，整场会议塌成一个标签。这不是没打开的开关——`/v2/live` 的请求体
  里根本没有 diarization 参数，那是**预录**接口才有的。
- **`GladiaAsrProvider.Caps` 却声明 `Diarization: true`。** 能力表是编排器和 PRD 唯一据以推理的
  东西，而这一行是假的。
- 右栏的发言人页签、重命名、非破坏合并、`Speaker.MergedFrom` 的客户端解析——全都在，全都没有
  数据可作用。在一场技术谈判里，「谁承诺了这个交期」经常就是最要紧的那部分。
- ADR 0053 把本地 ASR 定在 sherpa-onnx 上，并明说 `Caps.Diarization: false`、分离是 #13 的地盘、
  是另一个模型。本文就是那个模型的选型。

## 调研

2026 年 9 月的实际可选项，按「能不能从 .NET 无 Python 跑起来」和「许可证能不能商用」两条筛：

| 方案 | 许可证 | .NET 可达性 | 流式 | 说话人上限 | 结论 |
|---|---|---|---|---|---|
| **sherpa-onnx 离线分离**（pyannote-segmentation-3.0 + 嵌入模型） | 分割 MIT，3D-Speaker／WeSpeaker 嵌入 Apache-2.0 | **官方 C# 绑定已有** | 否 | 无上限 | **采用** |
| **sherpa-onnx 说话人嵌入 + 在线聚类** | 同上 | **官方 C# 绑定已有** | 逐句 | 无上限 | **采用**（实时暂定层） |
| NVIDIA Streaming Sortformer v2 | 权重 CC-BY-4.0，NeMo Apache-2.0 | 可导出 ONNX，但状态机在图外 | 是，1.04 s | **4 人（训练时写死）** | 推迟 |
| Streaming Sortformer v2.1 | NVIDIA Open Model License | 同上 | 是 | 4 人 | 推迟 |
| Revai reverb-diarization v1／v2 | **仅限非商用** | sherpa-onnx 已打包 | 否 | — | **出局** |
| DiariZen | 代码 MIT，**权重 CC-BY-NC-4.0** | — | 否 | — | **出局** |
| diart | 代码 MIT，依赖 pyannote 权重 | 无非 Python 路径 | 是 | 无上限 | 出局 |
| pyannote Live-1 | 商业托管 API，权重不开放 | 违反「只有文本穿越公网」 | 是 | — | 出局 |

两个许可证地雷已经排掉：Rev 的 Reverb 分离模型（sherpa-onnx 恰好也打包了它，文档自己打了
Caution）和 DiariZen 的权重都是**非商用**。这与 NLLB／Seamless 是同一类坑，必须在选型阶段拦下。

另需注意：**权重许可 ≠ 训练数据许可**。WeSpeaker 的模型训练自 VoxCeleb／CN-Celeb，两者本身偏研究
用途，正式发布前要单独过一遍。

### 为什么不是 Streaming Sortformer

它是唯一同时满足「可商用权重 + 可用的非 Python ONNX + 仍在维护」的**流式**分离模型：NeMo 主线已有
官方 `streaming_export()`，HuggingFace 上有现成 ONNX 权重（fp32 492 MB／int8 141 MB），
`altunenes/parakeet-rs` 用 Rust + ONNX Runtime 证明了它能脱离 Python 跑起来。1.04 s 延迟档在
DIHARD III ≤4spk 上 DER 15.09%。技术上确实能上。推迟的理由是两条，都与延迟无关：

1. **4 说话人是训练时写死的上限**，5 人以上明显退化（DIHARD III ≥5spk DER 42.56%）。而
   `.impeccable.md` 写的是「3–8 位参会者」。社区有 6/8 人的微调版，但许可证继承关系没标清楚。
2. **AOSC（说话人缓存）被刻意留在 ONNX 图外**——cache 与 FIFO 是普通张量输入输出，缓存更新逻辑
   （log 分数阈值、strong/weak top-k boost、每说话人的静音占位帧）加上 128 维 log-mel 前端要用
   C# 重写一遍。参考实现是 Rust 的约 1300 行纯数值代码，在 .NET 里没有任何测试向量可对照。抄错
   一个常数就会安静地产出错误的归属，而错误的归属正是这个功能最不能犯的错。

对照之下，采用的方案分割模型 **5.7 MB**（int8 1.5 MB）、嵌入模型 **26–40 MB**，全部走 sherpa-onnx
已有的 C# 绑定，**不引入任何新的原生依赖**——ADR 0053 已经把这个运行时带进来了。

### 行业里同类产品的做法

Meetily（MIT，本地 Whisper／Parakeet）到 2026 年 9 月**仍未发出分离功能**；Hyprnote/anarlog 用
pyannote，是**事后**处理。没有一个本地优先的会议工具在做实时逐人分离。这不是巧合。

## 决定

### A. 分层

1. **两层归属，非破坏地叠加。** 实时层在会中给出**暂定**标签；权威层在会议结束后对完整录音重跑
   一遍，覆盖暂定结果。会中拿到的宁可粗，也好过让操作员盯着一份「只有一个人」的转写开完整场会。
2. **权威层是 sherpa-onnx 的离线分离**：`OfflineSpeakerDiarization`，pyannote-segmentation-3.0
   分割，3D-Speaker／WeSpeaker 取嵌入，聚类给全局标签。它要整段音频，所以只能在会后跑；代价换来
   的是无说话人数上限、与语言无关。
3. **实时层是逐句嵌入 + 在线聚类**：每条**定稿**句子取其音频切片，`SpeakerEmbeddingExtractor`
   算一个向量，`SpeakerEmbeddingManager.Search(embedding, threshold)` 在本场会已见过的人里找；
   命中即复用标签，未命中（返回空串）就 `Add` 一个新的「说话人 N」。聚类中心随会议生长，不需要
   预先知道有几个人。切片短于 **0.3 s** 的句子不参与——嵌入在那个长度上不稳。
4. **重算不销毁原始标签。** 权威层写入新标签时，原始暂定标签按既有的 `Speaker.MergedFrom` 语义
   保留——那套非破坏合并本来就是为这件事造的，这里第一次真正用上它。
5. **人手工命名过的说话人不被重算改写**，与 `Titling` 里 `NamedByHand` 的既有原则一致。

### B. 编排

6. **分离是一个独立服务，不是 ASR 的能力。** 新增 `ISpeakerAttributionService`，编排器的判断是
   `if (!asr.Caps.Diarization)` → 把定稿路由给它。这与既有的
   `if (!asr.Caps.Translation)` → `IMtProvider` 是同一个形状，不新增供应商分支。
7. **`Caps.Diarization` 说实话。** `GladiaAsrProvider` 当前声明 `true`，是假的，改为 `false`；
   本地 ASR 按 ADR 0053 也是 `false`。`FakeAsrProvider` 保留 `true`，用来测「provider 自带分离」
   那条路径不被误接管。
8. 分离**不改变**中继边界。归属在主机上算完，出去的仍然只有文本。

### C. 一条会议时间轴

9. **建立唯一的会议时间轴**，ASR 的 `TStartMs`／`TEndMs` 与录音文件的字节偏移在其上对齐，跨越
   暂停间隙、重连与分段。今天这个对齐**从未被验证过**（`meeting-evidence.md` 已标记）。
10. 这条时间轴同时是 [#63](https://github.com/TONiiV/Kanal/issues/63)（点击句子回放原录音片段）
    的地基，两边共用一份实现，不各做一套。
11. 实时层从 `MeetingSession.AudioAccepted` 取音频——那个事件已经是暂停感知的（暂停时既不推给
    ASR 也不落盘），归属自然继承同一个语义。保留一个有界环形缓冲；定稿来得太晚、音频已经老化出
    缓冲的句子标记为未归属，而不是猜一个。

### D. 说话人数是操作员知道的事

12. **让操作员告诉它房间里有几个人。** `FastClusteringConfig` 二选一：`NumClusters` 已知，或
    `Threshold` 自行聚类。上游明确记录了不给人数时精度明显下降，且阈值需按音频逐个调
    （[#1466](https://github.com/k2-fsa/sherpa-onnx/issues/1466)，维护者的回复就是「自己调」）。
    而这恰好是操作员**看一眼会议室就知道**的一个数——这是本方案相对流式模型的一项结构性优势，
    不该浪费。
13. 右栏发言人页签提供人数输入与「重新分离」；留空则走阈值路径。改人数触发重算，重算仍然非破坏。

### E. 模型与目录

14. 分离模型进 Settings 的模型目录，复用 #88 的共享下载器与 #90 的目录记录形状（id、显示名、
    仓库、文件、大小、SHA-256、许可证）。
15. **下载地址不是 HuggingFace 形状。** sherpa-onnx 的模型挂在 GitHub release 上，不走 HF 的
    gated 授权、不需要 token。共享下载器已经能接受完整 URL——[#88](https://github.com/TONiiV/Kanal/pull/88)
    把 `IDownloadableFile.DownloadUrl` 定义成一个地址而不是一段路径，是 `LocalModelInfo` 自己在为
    它那批条目拼 HF 路径。所以本文不需要改下载器，分离模型直接给出 release 地址即可。
16. 默认嵌入模型**由实测决定**，不由参数量决定。候选：`3dspeaker_campplus_zh_en_16k-common_advanced`
    （28.3 MB）、`3dspeaker_eres2net_base_sv_zh-cn`（39.6 MB）、`wespeaker_zh_cnceleb_resnet34_LM`
    （26.5 MB）、`nemo_en_titanet_small`（40.3 MB，RTF 最好）。**3D-Speaker 的 zh-cn 模型训练自
    20 万说话人的普通话语料，德语和波兰语没有代表性，也查不到公开的 DER 数据**；说话人嵌入通常
    跨语言可迁移，但「通常」不是本项目的验收标准。
17. 分离模型未下载时，发言人归属安静地不可用——转写照常，标签仍是单一的，不弹错误、不阻塞会议。

### F. 界面上的诚实

18. **暂定标签必须看得出是暂定的。** 会中显示的归属带未定态；会议结束、权威层跑完后转为确定态。
    把推测显示成事实，比不显示更糟。
19. **重叠语音本方案处理不了，且不假装能。** pyannote-segmentation-3.0 内部确有重叠类别，但
    sherpa-onnx 的分离输出每段只带**一个** `Speaker` 标签，重叠会被归给某一个簇。这是已知限制，
    写进文档而不是留给用户去发现。
20. **`Utterance.SpeakerConfidence` 暂时没有真实来源。** 逐段置信度（`Segment.Confidence`，
    silhouette 系数）只存在于 sherpa-onnx 未发布的 master 分支上；在它随版本发布之前，UI 不显示
    置信度，而不是显示一个编出来的数。
21. 会后重算给出进度（`ProcessWithCallback` 提供进度回调），且**可跳过**——它跑在会议已经结束
    之后，不该挡住导出。

### G. 质量基线

22. 验收指标在实现前定下，实测记录进 `docs/PROGRESS.md`，与既有的 ASR／MT 基准并列：中文／德语／
    波兰语分别、重叠讲话、现场单麦与在线混音分别。
23. 参照量级：pyannote 3.1 在 AMI 会议语料上 DER 约 12–14%。**这是一个会犯错的功能**，UI 与文案
    按「需要人工修正」设计——重命名与合并是主路径，不是补救措施。

## 实施切片

每片一个 PR，各自留下全绿的测试套件。

### 1. 一条会议时间轴（决定 9–11）

ASR 时间戳与录音偏移的对齐，跨暂停、重连、分段；有界音频环形缓冲；顺手把
`GladiaAsrProvider.Caps.Diarization` 从 `true` 改成 `false`（决定 7）。**#63 依赖本片。**

### 2. 分离模型目录与下载（决定 14–17）

目录记录（含许可证）、就绪状态、未就绪时的静默降级。下载器不需要改。

实现注意：嵌入模型的 release tag 是 `speaker-recongition-models`——上游把 recognition 拼错了，
URL 必须照抄。

### 3. 会后权威分离（决定 2、4、5、21）

`OfflineSpeakerDiarization` 跑完整录音，段落映射回句子，非破坏地覆盖标签，带进度与跳过。

### 4. 实时暂定标签（决定 1、3、6、18）

`ISpeakerAttributionService` 与 `if (!asr.Caps.Diarization)` 路由；逐句嵌入 +
`SpeakerEmbeddingManager` 在线聚类；暂定态在 UI 上可见。

### 5. 右栏发言人页签接上真实数据（决定 12–13、18–20）

重命名与合并作用于真实标签；人数输入与「重新分离」；不显示编造的置信度。

### 6. zh／de／pl 实测与基准（决定 16、22–23）

选定默认嵌入模型与聚类阈值，数字进 `docs/PROGRESS.md`。

## 验证

- 一场四人录音跑完权威层后，句子的说话人标签与人工标注大致一致，原始暂定标签仍可从
  `Speaker.MergedFrom` 追回。
- 会中第二个人开口后的若干句内出现「说话人 2」，且标注为暂定。
- 手工命名过的说话人不被会后重算改写。
- 暂停期间的音频既不进转写也不进归属；恢复后时间轴不错位。
- 分离模型未下载时，会议照常进行，没有错误弹窗。
- 主机被强杀后重开，已完成的会议仍可补跑权威层。
- CI 用假结果验证标签流转、重命名与合并，不下载大模型。

## 后果

- **不引入新的原生依赖。** sherpa-onnx 已由 ADR 0053 带进来；分割 5.7 MB + 嵌入 26–40 MB 相对
  已有的 ≈0.7 GB ASR 与 ≈2.7 GB MT 可以忽略。
- **会议结束时多一个以分钟计的处理步骤。** 上游实测 RTF（单线程 Mac）：pyannote fp32 +
  ERes2Net base 为 0.297，配 TitaNet small 为 0.119。一小时录音约 **7–18 分钟**，可用
  `NumThreads` 压缩。所以决定 21 的「可跳过」不是客气话。
- **一小时 16 kHz 单声道是约 230 MB 的托管 `float[]`**，而 `Process` 一次性收整段。长会议必须
  分块处理，这是切片 3 的主要工程量。
- `Process` 是**同步阻塞**调用，没有 async 重载，必须自己丢到后台线程；进度回调委托在调用期间
  必须保持强引用，否则被 GC 回收——与 `MacSystemAudioCapture` 里 `GC.KeepAlive(onFrame)` 是同一个坑。
- 每个 RID 一个原生包（`org.k2fsa.sherpa.onnx.runtime.*`），打包问题与 ADR 0053 的完全一致，
  不是新增的。
- 归属是**逐句**粒度，不是逐帧。一句话中间换人不会被拆开——这与今天转写的呈现粒度一致。
- 实时层与权威层可能给出不同答案，操作员会看到标签在会议结束时变化。这是设计，不是缺陷，但必须
  在界面上说清楚。
- `Caps.Diarization` 从此是真的，PRD 里基于它的推理需要复核。
- 依赖一个仍在快速变动的上游：NuGet 上的 `org.k2fsa.sherpa.onnx` 是 1.13.5，落后仓库两个版本；
  离线分离目前有一个 open 的 use-after-free（[#3826](https://github.com/k2-fsa/sherpa-onnx/pull/3826)）。
  升级要看变更日志，不能盲跟。

## 明确排除

- **实时逐帧分离（Streaming Sortformer）。** 见上。重新评估的触发条件是：出现许可证清晰、支持
  8 说话人的权重，**且**有可在 .NET 中对照验证的参考向量。
- **重叠语音的逐人拆分。** 见决定 19。
- **跨会议声纹身份识别（「这是王工」）。** `docs/specs/meeting-workspace.md` 第 120 行已把「将
  『发言人识别』默认为跨会议声纹身份匹配」列为不做，#13 也在每条路径上把它排除。另有一条本文
  新增的理由：声纹是生物识别数据，为一场有德国和波兰参会者的会议建立可跨会议匹配的声纹库，与
  「录音知情确认」完全不是一个量级，需要独立的法律与同意设计。**这是本文替用户作出的唯一一个
  范围判断，可以推翻**——技术上 `SpeakerEmbeddingManager` 持久化到工作空间即可，成本很低，
  贵的是同意流程。
- **每人一支麦克风的多声道分离**（#13 的选项 1）。硬件方案，与本文不冲突，但不在此。
- **把「本地／远端声道」当作逐人识别。** 同上第 120 行。ADR 0050 的混音流不因本文而拆开。

## 推翻的既有决定

| 位置 | 改动 |
|---|---|
| `GladiaAsrProvider.Caps` | `Diarization: true` → `false`（该声明一直是假的） |
| `docs/PRD-v0.3.md` | 基于「Gladia 提供分离」的推理需复核 |
| `docs/design/meeting-evidence.md` | 待答问题 S1 由本文的「明确排除」回答 |
