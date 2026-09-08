# 07: VibeVoice-ASR 真实录音到结构化转写验证

**What to build:** 维护者可重复运行代表性中文多人录音，查看原文、时间戳与说话人，获得采用结论。

**Blocked by:** None (can start immediately).

**Status:** PR review — implementation not started

**Parent:** [账号云服务、免登录反馈、手机页托管与录音导入规格](../../specs/auth-feedback-relay-import.md)。

**User stories:** 43、49–50

- [ ] 记录模型/运行时实际版本，区分 ASR/TTS、完整模型/CPU 路线，不传播未经实测的能力承诺。
- [ ] 验证输入解码、采样率和输入相对时间基准，输出可映射文字、说话人与起止时间。
- [ ] 记录中文多人/混语质量、处理耗时、内存/显存和成本，逐步增加测试时长。
- [ ] 结果映射有可重复测试，损坏输出可识别；失败保留选型开放，不把演示接口当生产服务。
- [ ] 实际付费计算使用受控预算；此票交付输入输出与采用证据，不搭通用模型框架。

