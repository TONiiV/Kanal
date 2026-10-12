# Kanal macOS Alpha 上线执行方案

## 一、上线概况

- **版本性质**：Pre-release Alpha
- **上线策略**：公开 GitHub Release。仓库公开，任何人都能下载附件（2026-10-07 决定）
- **发布环境**：直接生成正式 Developer ID 签名、公证并 staple 的 macOS 安装包
- **发布时间**：流水线完成且全部检查通过后尽快发布
- **发布节奏**：本 Alpha 单次发布；修复使用新的 Alpha 版本号，不覆盖旧产物
- **撤回条件**：应用无法启动或频繁崩溃、隐私行为异常、凭据泄露或房间隔离失效等任一严重问题
- **撤回时限**：决定撤回后 5 分钟内删除 Release 附件并通知测试者
- **通知范围**：仅参与测试的朋友
- **生成日期**：2026-09-02
- **签名与公证脚本**：见 PR #27（`installers/macos/sign.sh`、`notarize.sh`、`Kanal.entitlements`）。本文是操作方案与验收清单，不重复其实现细节。

## 二、建议交付物

首个版本使用 `1.0.0`，只发正式版，不带预发布后缀。产物名称由构建生成，格式如下：

```text
Kanal-<完整版本>-osx-arm64.dmg
Kanal-<完整版本>-osx-arm64.dmg.sha256
Kanal-<完整版本>-win-x64.msi
Kanal-<完整版本>-win-x64.msi.sha256
```

> **版本号规则。** Tag 的数字部分必须等于 `src/Kanal.Host/Kanal.Host.csproj` 里的 `<Version>`。
> `installers/release.sh` 在不一致时拒绝构建，并打印两个值。应用内的「查看更新日志」读取
> `CHANGELOG.md`，其最新标题必须是同一个版本。发新版本时：
>
> 1. 先在一个 PR 里提升 `<Version>` 和 `CHANGELOG.md` 标题。
> 2. 合并该 PR。
> 3. 再对 `main` 上的提交打同一数字版本的 tag，例如 `v1.0.0`。

第一轮只支持 Apple Silicon，以减少架构组合和本地模型原生库带来的风险。如果测试者有 Intel Mac，再增加独立的 `osx-x64` 产物；不要在未经完整原生库验证前合并成 Universal Binary。

GitHub 的 **Pre-release 不是私密发布**：公共仓库中的 Pre-release 仍然公开可下载。2026-10-07 起接受这一点：安装包直接通过公开 GitHub Release 分发。

## 三、发布流水线

### 3.1 构建

维护者在 GitHub 的 Releases 页面创建 tag（如 `v1.0.0`，数字部分等于 `<Version>`）并点击 Publish。`.github/workflows/release.yml` 随后在 macOS runner 上签名、公证 dmg，在 Windows runner 上打包 msi，并把两者上传到该 Release。两边都调用 `installers/release.sh`，并上传 `.sha256` 文件。

发布流水线不运行测试。维护者只给 `main` 上 `ci.yml` 已通过的提交打 tag。下面是脚本执行的步骤。

1. 拒绝脏工作区，并检查版本号与 `<Version>` 一致。
2. 发布自包含 Apple Silicon 版本：

   ```bash
   dotnet publish src/Kanal.Host/Kanal.Host.csproj \
     --configuration Release \
     --runtime osx-arm64 \
     --self-contained true \
     --output artifacts/publish/osx-arm64
   ```

3. 构造标准 `Kanal.app`：

   ```text
   Kanal.app/
     Contents/
       Info.plist
       MacOS/Kanal.Host
       Resources/kanal.icns
       Resources/其余发布文件
   ```

`Info.plist` 至少包含稳定的 bundle identifier、版本号、可执行文件、应用图标、最低系统版本，以及 `NSMicrophoneUsageDescription`。没有麦克风用途说明时，macOS 会阻止正常录音授权。

### 3.2 签名

Kanal 可以复用 DimensionX 所属 Apple Developer Team 的 Developer ID Application 证书和 App Store Connect notarization key。证书和公证密钥存放在仓库的 GitHub Secrets 中：`MACOS_CERT_P12`、`MACOS_CERT_PWD`、`MACOS_SIGNING_IDENTITY`、`NOTARY_KEY_P8`、`NOTARY_KEY_ID`、`NOTARY_ISSUER_ID`。不要提交证书文件。本机也可以运行 `installers/release.sh <版本号>` 打包，签名变量见 `docs/design/installers.md`。

打包流程应：

1. 把 `.p12` 导入 keychain（CI 上是临时 keychain）。
2. 对 `.app` 内所有 Mach-O、`.dylib` 和原生库由内向外签名。
3. 最后对 `Kanal.app` 使用 Developer ID、timestamp 和 Hardened Runtime 签名。
4. 验证签名 authority 确实是 `Developer ID Application`，不能只依赖 `codesign --verify`，因为 ad-hoc 签名也可能通过该命令。
5. 任一签名或公证变量缺失时让打包失败，禁止静默生成未签名发布包。

不要直接复制 DimensionX 的 PyInstaller entitlements。Kanal 使用 .NET、Avalonia 和 llama.cpp，需要先用本地模型做 Hardened Runtime 冒烟测试，再按实际失败点决定是否加入 JIT、可执行内存或 library-validation 例外；权限应保持最小化。

### 3.3 DMG、公证和回填

1. 把已签名的 `Kanal.app` 放入 DMG。
2. 对最终 DMG 签名。
3. 使用 `xcrun notarytool submit --wait` 提交公证。
4. 显式检查公证结果为 `Accepted`。
5. 对 DMG 执行 `xcrun stapler staple` 和 `xcrun stapler validate`。
6. 挂载 DMG，验证其中的 `.app`，再计算 SHA-256。

与 DimensionX 的裸 Mach-O 不同，Kanal 的 `.app`/DMG 可以且应当 staple，使测试者第一次启动时不依赖在线查询 Apple 公证票据。

## 四、发布前检查清单

### 自动检查

- [ ] 被打 tag 的提交在 `ci.yml` 中通过，Core 和 UI 测试全部绿色
- [ ] Release 构建成功
- [ ] `web/index.html` 与 `docs/index.html` 字节一致
- [ ] 发布目录不包含 Gladia key、relay host token、证书或 `.p8` 文件
- [ ] `codesign --verify --deep --strict --verbose=2 Kanal.app` 通过
- [ ] 签名详情包含 Developer ID Application authority、timestamp 和 Hardened Runtime
- [ ] notarization 状态为 `Accepted`
- [ ] `stapler validate` 通过
- [ ] `spctl --assess --type open --context context:primary-signature` 对 DMG 通过
- [ ] Release 附件里有 `.sha256` 文件，其值与 DMG 一致

### 干净 Mac 手工检查

- [ ] 从下载链接获取 DMG，而不是直接运行构建目录内的文件
- [ ] 双击 DMG、拖入 Applications、正常首次启动，无“无法验证开发者”警告
- [ ] 麦克风权限文案正确，允许和拒绝两条路径都不会崩溃
- [ ] 正常启动时模式列表中没有 Demo；以 `KANAL_ENV=development /Applications/Kanal.app/Contents/MacOS/Kanal.Host` 启动时 Demo 出现，且可启动、暂停、恢复和停止
- [ ] 配置 Gladia 后可捕获麦克风并产生字幕/翻译
- [ ] relay 正常时 QR 可加入；relay 缺失/失败时会议仍可继续且错误可见
- [ ] 本地录音开关、暂停边界、导出和输出目录正确
- [ ] 下载并运行本地 MT 模型，确认 Hardened Runtime 没有阻止 llama.cpp 原生代码
- [ ] 睡眠/唤醒、锁屏恢复、音频设备插拔后应用仍可用
- [ ] 应用完全退出后没有残留录音或后台进程

至少在一台不装开发工具、没有导入开发证书的 Apple Silicon Mac 上完成最后验收，避免开发机 keychain 掩盖签名问题。

## 五、分发与反馈

给测试者发送以下内容：

- GitHub Release 页面链接
- 附件 `.sha256` 文件里的 SHA-256
- 明确标注 `Alpha`，不得用于正式会议或敏感内容
- 支持的 macOS/CPU 范围
- 会发送哪些音频、字幕和 room state，以及本地 WAV 默认录制行为
- 一个统一反馈渠道和最少复现信息：macOS 版本、Mac 型号、操作步骤、截图/日志

Release 页面在附件上传完成前就已经公开。公证需要数分钟或更久。两个 job 都成功后，才通知测试者。

如果一个 job 失败，公开的 Release 就缺少该平台的安装包。在 Actions 页面点击「Re-run failed jobs」补齐。工作流不覆盖已有附件。如果失败的 job 已经上传了安装包、但没有上传 `.sha256` 文件，重跑会因附件已存在而失败。这时先在 Release 页面手动删除该安装包，再重跑。

GitHub 运行 tag 所在提交里的 `release.yml`。在本流水线合并之前的提交上打 tag，不会启动任何工作流。

不要在消息中发送 Gladia key、relay host token 或 Apple signing secrets。每位测试者使用自己的 Gladia key；relay host credential 只存在于发布/运行环境，不进入 DMG。

## 六、撤回与恢复

触发条件：应用无法启动、频繁崩溃、麦克风/录音行为与说明不符、安装包携带凭据、relay 权限越界或其他严重安全问题。

5 分钟撤回流程：

1. 删除 GitHub Release 的附件，或删除整个 Release。
2. 给全部测试者发送“停止使用并删除该版本”的通知。
3. 若涉及 relay host credential，立即轮换 token；若涉及第三方 API key，撤销对应 key。
4. 保留原 tag、commit、DMG hash 和公证记录用于调查，不覆盖原产物。
5. 修复后发布递增版本，例如 `1.0.1`，重新走完整签名、公证和验收流程。

由于桌面安装包无法远程卸载，“下架”只能阻止继续下载；已下载副本必须通过直接通知测试者停止使用。若漏洞可通过后端缓解，可以同时停用/轮换 relay 凭据，但不能把后端停用当作客户端撤回的替代品。
