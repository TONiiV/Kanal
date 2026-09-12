# 账号、用户反馈与手机字幕页面托管

状态：产品范围已确认，2026-09-07。Q1–Q16 已完成，进入文档 PR 与实现交接；技术验证与部署配置尚未完成，不代表功能已经实现或上线。

正式交接见[规格](../specs/auth-feedback-relay-import.md)和[任务索引](../specs/auth-feedback-relay-import-tickets.md)。本文件保留访谈决策与初步研究记录；逐项任务作为文档 PR 内容审阅。

## 已明确的需求

- 设置按钮换成齿轮图标。
- 设置按钮右侧增加 User feedback 按钮及图标；点击打开反馈窗口。
- 反馈类型为建议、Bug report、疑问；无需登录，只需留联系邮箱，并设计防攻击和注水措施。
- 记录反馈时间和联系邮箱；匿名反馈没有可信账号 ID 或用户名，不伪造身份。已登录反馈可关联经验证的账号，身份信息留在私有反馈记录。
- 认证初选 Clerk，数据使用 Supabase；用户允许在微信、+86 或大陆可用性不满足时适配或替换 Clerk，以需求为先。
- 设计无需终端用户访问 GitHub 的反馈收集及自动生成 GitHub issue 流程。
- 准备不使用 GitHub Pages 的手机字幕页面托管方案，EdgeOne Pages 为候选。
- 支持录音导入，并核实 VibeVoice 语音识别能力的集成可行性；是否采用尚未决定。
- 本地功能免费；登录用于使用平台提供的 Gladia 云功能，未来付费订阅可提供实时 agent 等能力。
- 登录需支持 Google、Apple、微信、手机号，同一账号可绑定这些登录方式；供应商支持和实现路径待核实。
- 中国大陆普通网络下整条链路可用为验收目标，覆盖注册登录、反馈、云功能和手机字幕，不限于静态网页。
- 三类有效反馈全部自动生成私有 GitHub issue；终端用户无需 GitHub 账号或网络访问。
- 录音允许在明确告知用户后上传云端处理；导入产物为当前工作空间中的完整会议记录，包含原音、带时间戳/说话人的转写，并接现有规划中的翻译与摘要流程。

## 代码事实

- 应用为 Avalonia/.NET 桌面应用。`src/Kanal.Host/Views/IconBarView.axaml` 的设置按钮使用自定义矢量图标，并打开现有设置窗口。
- 当前没有 Clerk/Supabase 用户认证。网关的每设备激活凭证与手机的房间票据不是用户账号。
- `web/index.html` 为手机字幕静态页面，`docs/index.html` 为 GitHub Pages 镜像；实时字幕网关独立位于 `gateway/`，使用 Cloudflare Worker 与 Durable Objects。
- 静态托管迁移需要同步考虑 `KANAL_WEB_URL`、网关 `KANAL_ALLOWED_ORIGIN` 和现有镜像一致性检查。迁移静态页不等于迁移实时网关。

## 决策树

第一轮已回答（用户改变了原先反馈必须登录的要求）：

1. 反馈免登录；登录用于平台云服务。本地功能免费。
2. 整条链路需要中国大陆普通网络可用。
3. 所有反馈类别自动进入私有 issue。
4. 接受明确告知后的云端录音处理。
5. 接受完整会议记录并接入翻译/摘要。
6. 补充 Google、Apple、微信、手机号登录及相互绑定需求。

第二轮已全部按推荐确认：

7. 首版邀请＋每账号额度，数值见 Q13。
8. 首次验证反馈邮箱并短期记住，叠加服务端限流与反滥用；不创建账号。
9. 本地保留原音，云端成功后删除，失败最多保留24小时供重试。成功删除以结果可靠保存、可恢复获取为前提。
10. 需求优先，允许适配或替换 Clerk；尚未选定替代供应商。
11. 国内至少一条可靠登录路径，Google 在其服务可访问的网络使用。这不削弱国内反馈、云服务和字幕完整链路的验收要求。
12. 系统浏览器登录并回到桌面，在已登录账号内验证绑定其他方式；首版已有双账号合并冲突人工处理。

第三轮已全部按推荐确认；以下数值是首版产品配置，不是已测性能或成本结论：

13. 每个受邀账号一次性120分钟 Gladia 额度，不自动续赠，同时最多1个云任务，管理员可补额度；VibeVoice GPU 单独核算并人工开通试用。
14. WAV/MP3/M4A，单文件最多60分钟且不超过500MB；复制到当前工作空间后手动开始云转写，可取消和重试，失败保留本地录音。长会议分段与跨段说话人一致性另行验证。
15. 对话气泡图标；类别、标题、正文、邮箱，首版无附件；应用和系统版本可预览后附带。提交返回编号，后台邮件通知维护者，由维护者通过联系邮箱人工回复，暂不自动转发私有 issue 评论。
16. 保留自带 Gladia key，费用归用户自己的服务账号；手机扫码继续免登录。首版做账号、邀请和额度，支付订阅及付费实时 agent 后续上线。

其余工作分为技术验证和部署配置：身份供应商适配与绑定/恢复、浏览器会话交接、云用量服务端强制限制、可靠 issue 同步、模型质量和资源测试、域名与邮件发送服务、私有仓库目标、部署区域及大陆端到端验收。不会将缺少验证的供应商能力写成已完成。反馈和云端转写结果的保留期限列为上线前配置项，当前未指定，不代表无限期保留；临时音频期限已确定。

## 已确认的交互与后端流程

反馈入口采用带省略号的对话气泡矢量图标，与齿轮保持尺寸和线条风格一致；正式矢量绘制和窗口实现列入开发任务。

流程：反馈窗口 → 联系邮箱与内容 → 首次邮箱验证与防滥用检查 → 后端记录反馈 → 异步同步私有 GitHub issue → 返回应用内可读的反馈编号与状态。由服务端持有 GitHub 凭证，终端用户无需 GitHub 账号；记录成功与 issue 同步成功应分别表达。该流程尚未实现，不代表服务已部署。

服务端生成提交时间；只有已验证的登录会话才可关联用户 ID，并将用户名作为快照。仅填写的邮箱不自动视为已验证身份。反馈不公开，维护者通过联系邮箱人工回复；保留期限属于上线前配置项。

平台 Gladia key 仅保存在服务端，不自动写入客户端设置。后端验证登录和云服务资格后授权处理，需能限制单次时长、并发、总额度及撤销访问；具体代理/会话授权方式待验证，不能仅靠客户端计时限制成本。保留用户自填 key 功能，费用由用户自己的服务账号承担。

防滥用实现分两层：入口限制请求体大小、请求频率与发送验证码频率，并对异常请求挑战或拒绝；收件层使用幂等提交标识防止网络重试重复创建 issue，对内容重复和突发请求进行拦截或隔离。不能仅按 IP 永久封禁或仅靠客户端隐藏字段；共享网络可能有多个正常用户，攻击者也能轮换邮箱。有效反馈全部自动转私有 issue，不把明显攻击请求也强行同步。首次验证邮箱、首版无附件已确认；具体限额与验证状态有效期作为可调整配置，需在集成测试中覆盖误拦和重试反馈。

云用量和未来订阅归属于稳定的 Kanal 账号，不随 Google/Apple/微信/手机号绑定而重置额度；绑定新方式必须验证控制权。已存在的两个账号不是普通绑定，首版人工处理记录、用量和订阅归属冲突。

## 实现交接与验收

1. **验证身份和网络基础**：验证浏览器登录回桌面、四种登录方式与绑定、+86 短信及国内至少一条可靠登录路径。若 Clerk 无法满足，按已授权范围评估适配或替换，不降低产品要求。
2. **交付图标和反馈闭环**：齿轮与反馈矢量图标、反馈窗口、邮箱验证、限流、私有存储、异步 issue 和维护者邮件通知。模拟 GitHub 故障与请求重试，确认反馈不丢失且不会重复建 issue；私有评论不外发。
3. **交付账号与平台云额度**：邀请绑定账号，一次性120分钟 Gladia 额度、1个云任务并发，服务端强制计量和拦截，管理员补额度。验证绑定身份、重登和更换设备不会重复赠送，取消和断网能释放任务资源。
4. **交付录音导入**：依赖已批准的工作空间持久化设计，实现本地复制、格式/时长/大小校验、手动启动、取消和重试。用真实中文多人录音验证 VibeVoice 的文字、时间戳、说话人、处理时间与内存；未通过前不承诺模型已选定，不静默替换引擎或上传服务。云端音频成功后删除、失败24小时内清理须可验证。
5. **迁移手机页并验收整条链路**：EdgeOne Pages 为优先验证的静态托管方案，将 `web/index.html` 部署至自定义域名，更新桌面 `KANAL_WEB_URL` 和网关允许来源；调整 GitHub Pages 镜像及对应 CI 规则。现有实时网关独立验证，若不可达需另行选择可用的长连接部署方案，不能把静态迁移当作网关可用性的证明。使用大陆网络验证登录、邮箱验证、反馈提交、云转写、字幕首次连接及重连；无需用户访问 GitHub。

部署时配置实际域名、邮件发送域与维护者邮箱、私有 issue 仓库、身份提供商凭据和云资源；不将任何共享密钥放入桌面包或静态页面。资源付费、生产配置与数据保留策略应在部署方案中明确，当前产品范围确认不等于授权无限云支出。

## 官方资料核实

- [Supabase 的 Clerk 集成](https://supabase.com/docs/guides/auth/third-party/clerk)：可接受 Clerk session token，身份主键应按 Clerk 的字符串 `sub` 处理。桌面登录、会话交接和刷新仍需单独验证，不能把 Web 登录组件当成 Avalonia 原生控件。
- [EdgeOne 域名说明](https://pages.edgeone.ai/document/domain-overview)与[自定义域名说明](https://pages.edgeone.ai/document/custom-domain)：生产候选方案应使用自定义域名；当前系统域名在中国大陆访问有临时预览链接限制。是否选择包含中国大陆的加速区域及相应部署条件，需要在网络目标确定后细化。
- [GitHub 创建 issue API](https://docs.github.com/en/rest/issues/issues#create-an-issue)：支持拥有 Issues 写权限的 GitHub App installation token。由后端创建 issue、用户不访问 GitHub 是据此提出的架构方案；重试、去重和内容公开策略是本项目仍需设计的部分。
- [Clerk Google](https://clerk.com/docs/guides/configure/auth-strategies/social-connections/google)与 [Apple](https://clerk.com/docs/guides/configure/auth-strategies/social-connections/apple)是直接支持的登录方式；Google 不允许 WebView 登录，系统浏览器为桌面端推荐方向。Apple 隐藏邮箱可能与其他身份邮箱不同，部分 Apple 账号没有邮箱，账号邮箱不应与反馈必填联系邮箱混为一谈。
- [Clerk 提供商列表](https://clerk.com/docs/react/guides/configure/auth-strategies/social-connections/overview)未列出微信；[自定义 OIDC provider](https://clerk.com/docs/guides/configure/auth-strategies/social-connections/custom-provider)是需要验证的适配入口，尚未证明微信可直接接入。
- [手机号登录配置](https://clerk.com/docs/guides/configure/auth-strategies/sign-up-sign-in-options)提供短信 OTP；+86 是否可在目标租户启用及三网到达率尚未验证，不能承诺大陆手机登录已经可用。
- [Clerk 账号绑定](https://clerk.com/docs/guides/configure/auth-strategies/social-connections/account-linking)支持基于已验证邮箱的关联；不同邮箱的身份建议通过已登录账号内的显式验证绑定，不根据昵称或匿名反馈邮箱自动合并。

## 录音导入的现有基础

已有 `WavFileAudioSource` 可读取 16-bit PCM WAV、转单声道/16kHz 并按帧输出，目前用于诊断工具而非桌面导入入口；它会整文件读入内存，不能直接视为适合长录音的实现。

现有 `IAsrSession` 面向实时音频推送，没有显式输入结束、批任务进度和完成结果的完整约定。录音导入需要明确批处理生命周期与会议记录落盘，再决定 VibeVoice 适配方式。

`docs/design/meeting-workspace.md` 已批准录音进入待转写流程；此次访谈应补充格式、处理位置、模型与失败恢复等细节，不重新打开已确认的入口方向。当前会议数据仍以内存状态和手动导出为主，完整工作空间记录持久化属于尚待实现的依赖。

## VibeVoice 可行性初查

应评估语音识别 VibeVoice-ASR，而非语音合成模型。以下为官方资料描述，尚未在本项目运行验证：

- [完整 ASR 文档](https://github.com/microsoft/VibeVoice/blob/main/docs/vibevoice-asr.md)说明单次约 60 分钟、50 多种语言，以及文字、说话人和起止时间输出；可通过[官方 vLLM 服务方式](https://github.com/microsoft/VibeVoice/blob/main/docs/vibevoice-vllm-asr.md)适配后端 HTTP 接口。显存、真实会议耗时及准确率需要独立验证。
- [官方 VibeASR.cpp](https://github.com/microsoft/VibeASR.cpp)及 [BitNet 模型](https://huggingface.co/microsoft/VibeVoice-ASR-BitNet)提供本地 CPU 路线，量化权重约 1.58 GB；短音频基准不能作为小时录音性能证据，也不能直接沿用完整模型的时长与说话人区分能力承诺。
- [官方音频处理代码](https://github.com/microsoft/VibeVoice/blob/main/vibevoice/processor/audio_utils.py)通过 FFmpeg 解码多种音视频格式；[ASR 处理器](https://github.com/microsoft/VibeVoice/blob/main/vibevoice/processor/vibevoice_asr_processor.py)使用 24kHz 单声道。适配时不能直接照搬现有实时音频源的 16kHz 配置。

候选集成边界为本地 CLI 或后端 HTTP 批转写服务，映射到会议记录；不要求把模型移植进 .NET。先用中文多人真实录音验证时间戳、说话人区分、耗时、内存和失败恢复，再决定默认引擎。演示站点不作为生产服务承诺。

已确认的账号与反馈边界记录于 ADR-0052；托管、身份供应商兼容方案和模型选型仍处于设计阶段。
