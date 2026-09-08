# 10: 手机字幕页面迁出 GitHub Pages 并保留扫码重连

**What to build:** 手机使用新自定义域名打开字幕页，扫码链接迁移后仍免登录且能从断线恢复。

**Blocked by:** None (can start immediately).

**Status:** PR review — implementation not started

**Parent:** [账号云服务、免登录反馈、手机页托管与录音导入规格](../../specs/auth-feedback-relay-import.md)。

**User stories:** 34–36、52

- [ ] 优先验证 EdgeOne Pages 自定义域名方案，记录部署区域、前置条件和页面实际可达性。
- [ ] 桌面链接、页面配置、网关允许来源、CI 与发布检查同步迁移，提供回退步骤。
- [ ] 保留缓存显示、无额外外部加载资源、长连接重连与免登录查看。
- [ ] 不弱化房间票据、设备凭据或 text-only 边界，分别报告页面与网关测试结果。
- [ ] 复用 #41 的实时网关可达性工作，#41 不阻塞静态迁移本身但阻塞最终整链验收。

