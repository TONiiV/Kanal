# ADR 0062：会议记录发布到 Notion，录音可选，OAuth 经过 gateway

状态：accepted，2026-10-19。

操作员要求一键把会议的转写和录音放进团队的 Notion（#19）。我们决定新增"发布"（Publish）：操作员手动把一场已结束的会议单向复制到 Notion。
`.kl` 仍是唯一的导出。连接用 Notion 公共集成的 OAuth，`client_secret` 存在 gateway Worker 里。录音可以随发布一起上传，默认不上传。

## 决定

- **发布不是导出。** 导出只有 `.kl` 文件（ADR 0054 决定 20–23）。发布的副本不会回到 Kanal。再发布一次，就在 Notion 新建一个页面，旧页面不变。
- **录音可选，默认不选。** 这收窄了"只有文本经过公网"这条不变量。新的说法是：会议进行中只有文本经过公网。会议结束后，操作员主动发起的发布可以把录音送到操作员选的目标。
- **附件不发布。** 附件是操作员拖进来的任意文件。把附件送上公网要另做决定。
- **连接像 ChatGPT connector。** 操作员在 Settings 的 Connectors 标签页点"连接"。浏览器打开 Notion 授权页，操作员在页面选择器里选择 Kanal 能访问的页面。一个 Notion 工作区是一个连接，可以有多个连接。
- **gateway 保存 `client_secret`。** Notion 用授权码换 token、刷新 token 和撤销 token 都要用 `client_secret`。桌面安装包保存不了 secret。gateway 加三个无状态接口：换 token、刷新 token、撤销 token。gateway 不保存 token。
- **回调经过本机回环地址。** Notion 把授权码回调到 gateway 的 HTTPS 地址。gateway 再把授权码重定向到桌面程序的 `127.0.0.1` 监听端口。桌面程序通过 HTTPS 请求 gateway 换 token。token 不出现在任何 URL 里。
- **不需要 Kanal 账号。** Notion 是操作员自己的服务。gateway 不计费，也不保存任何内容，所以不属于 ADR 0052 里的"平台云服务"。
- **token 存放。** access token 和 refresh token 存在 `settings.json`，明文，和 Gladia、DeepL 的密钥一样（ADR 0060）。改用系统钥匙串时，所有密钥一起改。
- **超过上限只发转写。** 发布前，Kanal 读取 `max_file_upload_size_in_bytes`。录音超过上限时，Kanal 只发布转写，并告诉操作员原因和上限。

## 考虑过的方案

- **内部集成 token：** 操作员要自己去 Notion 建集成，再把 token 粘贴进 Kanal。这不是 connector 体验，被操作员否决。
- **Notion 托管的 MCP 服务器：** 它支持动态注册和 PKCE，不需要 secret。但 MCP 工具是给 LLM 用的，接口没有版本保证，而且没有查到它能上传文件。
- **永远不传录音：** 不变量不变，但不满足操作员的需求。
- **独立的 OAuth 服务：** 多一个部署和一套密钥。gateway 已经在 Cloudflare 上运行。

## 后果

- gateway 第一次保存一个第三方 secret。`gateway/README.md` 写的"仓库、桌面程序和手机页面不含任何服务端凭据"仍然成立：secret 是 Worker secret，不在仓库里。
- 有人要用 owner 的账号在 Notion 建公共集成，选"Any workspace"，再把 client id 和 secret 配到 Worker。不上架 Marketplace，就不需要 Notion 的安全审核。
