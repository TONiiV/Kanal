# 账号、反馈、relay 与录音导入：任务拆分草案

状态：随独立文档 PR 审阅。按用户最新要求，将规格和逐项任务归档到 docs；本轮不创建 GitHub 子 issue。任务尚未实现。

| 编号 | 任务 | 前置依赖 |
|---|---|---|
| T01 | [免登录邮箱验证反馈：齿轮、入口与私有收件](../tickets/auth-feedback-relay-import/01-verified-feedback.md) | #67 |
| T02 | [有效反馈自动转私有 issue 并通知维护者](../tickets/auth-feedback-relay-import/02-private-issue-delivery.md) | T01 |
| T03 | [系统浏览器登录 Google、Apple 并绑定同一账号](../tickets/auth-feedback-relay-import/03-browser-account-login.md) | 无 |
| T04 | [微信、手机号登录绑定及大陆可靠登录路径](../tickets/auth-feedback-relay-import/04-mainland-identities.md) | T03 |
| T05 | [受邀账号使用平台 Gladia：120分钟及服务端限额](../tickets/auth-feedback-relay-import/05-invited-gladia-cloud.md) | T03 |
| T06 | [录音导入当前工作空间并持久化为待转写会议](../tickets/auth-feedback-relay-import/06-recording-import.md) | #68、#69 |
| T07 | [VibeVoice-ASR 真实录音到结构化转写验证](../tickets/auth-feedback-relay-import/07-vibevoice-feasibility.md) | 无 |
| T08 | [受邀用户云转写录音：取消、重试、落盘与清理](../tickets/auth-feedback-relay-import/08-cloud-recording-job.md) | T05、T06、T07 |
| T09 | [导入会议的翻译、摘要与独立失败恢复](../tickets/auth-feedback-relay-import/09-import-translation-summary.md) | T08 |
| T10 | [手机字幕页面迁出 GitHub Pages 并保留扫码重连](../tickets/auth-feedback-relay-import/10-edgeone-mobile-page.md) | 无 |
| T11 | [中国大陆整条业务链路验收与可恢复部署](../tickets/auth-feedback-relay-import/11-mainland-release-validation.md) | T02、T04、T05、T09、T10、#41 |

每票都有独立可验证的交付、验收标准、用户故事覆盖与阻塞关系。复用现有 #67/#68/#69/#41，不修改或关闭原父 issue。T07 是有实际输入输出和采用结论的技术试验。

测试从应用命令/可见状态切入，外部服务使用替身；真实提供商和大陆网络另行验收，无像素测试。

T01–T11 是本文档包内的任务编号，#67/#68/#69/#41 是现有 GitHub issue。未来若要求发布到 tracker，再按依赖顺序创建并映射真实编号；当前通过此 PR 审阅粒度、阻塞关系和测试边界。

