# Changelog

What changed, newest first. Kanal shows this file inside the application — Settings → Version →
*View changelog* — so an operator can answer "did something change since last week?" on the laptop
that is running the meeting.

Entries are written for the person using Kanal, not for the person who wrote the commit. One
heading per version, `## <version> — <yyyy-MM-dd>`; the version being worked towards carries no
date until it is released. A pull request that adds a feature, fixes a bug or makes something
measurably better adds its own bullet under that heading as it lands — nothing else does. The
newest heading has to match the version the build reports, and a test holds the two together.

## 1.0.0 — 2026-09-19

首个正式版。以下是 Kanal 发布当天能做的事。

- 双击即装：macOS 为已公证的磁盘映像，Windows 为 MSI，自带 .NET 运行时，无需另外安装任何东西。
- 现场会议实时翻译：主机采集房间声音、转写并翻译，参会者扫码后在自己手机上跟读；手机只收到文字。
- 主机最多四个语言栏，可拖拽或 Alt+←/→ 调整顺序；每部手机自选一种语言。
- 工具栏只有图形按钮，录制键旁不显示任何文字；提示栏可在右侧关闭。
- 按下录制先弹出确认框：说明会被转写与翻译，勾选「已告知所有人」才能开始；是否保存录音只对本场有效。
- 会议自动保存：转写逐句写入会议记录，录音存放在旁；主机中途崩溃也保留之前的内容。
- 左侧工作空间：搜索会议、新建会议、切换项目；每场会议的三点菜单可重命名（已结束的会议也可以）、用本地模型生成标题（会议进行中不可用）、导入、导出、导出迁移包、打开所在文件夹、删除（删除前二次确认，不进回收站）。
- 迁移包：一场会议打包成单个文件，可导入任何工作空间；已存在的会议可跳过或另存，绝不覆盖。
- 点击左栏任一会议即可只读查看，即便正在录制；顶部横条标明正在录制的会议并可一键返回。
- 转写导航条：右侧一列按发言人着色的刻度，悬停看是谁、何时、说了什么，点击跳转并标出落点句子。
- 右侧面板：「要点与决定」按主题汇总并附原话，须操作员确认；「文件」标签列出会议文件夹并可导入附件。
- 会议自动命名：使用本地翻译模型时，开场约一分钟后给出短标题，可随时重新生成或点击标题手工改名。
- 五种流水线模式，按转写／翻译各在何处运行来描述，并注明哪些数据离开本机；暂不可用的模式置灰并说明原因。
- 本地翻译在进程内运行：设置里有可下载的模型目录，模型在开房间前加载完毕，加载期间停止键上转一圈细环，底部状态栏写明正在加载的模型。
- 麦克风可在会前测试：电平表、峰值保持和一句诊断结论（无声、太轻、削波、噪声）。
- 传输控制：开始、暂停／继续、停止；暂停期间不转写、不翻译、不发送，房间与二维码保留。
- 字幕经你自己的认证中继网关传输，手机只拿到只读票据；未配置网关时会议照常进行，只是没有二维码。
- 主机界面支持英语、中文、德语、波兰语，切换即时生效；中文输出统一为简体。
- 设置分六个标签页；日志按天滚动、保留两周、从不外发；开源致谢与本更新日志均可在设置中查看。
- 本版本暂不含：电脑音频采集（线上会议一行不可选）、发言人区分（右侧「发言人」标签隐藏）、本地转写。
