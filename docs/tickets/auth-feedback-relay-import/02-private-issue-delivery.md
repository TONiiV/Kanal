# 02: 有效反馈自动转私有 issue 并通知维护者

**What to build:** 已接受反馈进入私有仓库，维护者收到邮件；用户不访问 GitHub，维护者人工回复。

**Blocked by:** T01.

**Status:** PR review — implementation not started

**Parent:** [账号云服务、免登录反馈、手机页托管与录音导入规格](../../specs/auth-feedback-relay-import.md)。

**User stories:** 13–16、19

- [ ] 三种有效类别都同步，凭据仅在服务端且仅有目标仓库所需权限。
- [ ] 收件、issue 同步与邮件通知状态独立；GitHub 故障不丢反馈。
- [ ] 重试和模糊超时用稳定反馈标识核对已有 issue，避免重复创建。
- [ ] 身份/邮箱留在私有存储，issue 通过反馈编号关联；私有评论不自动发送用户。
- [ ] 测试中断恢复、邮件失败、重复投递；上线验证仓库私有性、发送配置与维护者邮箱。

