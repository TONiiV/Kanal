# 03: 系统浏览器登录 Google、Apple 并绑定同一账号

**What to build:** 用户在系统浏览器登录后回到 Kanal，可验证绑定另一身份，退出仍能使用免费本地功能。

**Blocked by:** None (can start immediately).

**Status:** PR review — implementation not started

**Parent:** [账号云服务、免登录反馈、手机页托管与录音导入规格](../../specs/auth-feedback-relay-import.md)。

**User stories:** 20–22、24–26、51

- [ ] Clerk 初选、Supabase 数据；如方案不能满足最终身份需求，记录适配或替换结论。
- [ ] 浏览器回调绑定发起会话且防重放；安全保存、过期/刷新与退出均有真实路径。
- [ ] 不同邮箱/Apple 隐藏邮箱不按昵称合并；在已登录账号内验证绑定，既有双账号冲突人工处理。
- [ ] 账号与 relay 设备凭据区分，不自动授予发布权限；本地无需账号。
- [ ] 替身测试回调、过期、越权、绑定冲突；真实 Google/Apple 凭据下验证桌面往返。

