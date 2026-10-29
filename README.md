# Questyle Windows 移植版

从本机 `reverse/base.apk`（Questyle Android 1.8.0 / build 78）逆向重建的 Windows 原生客户端。目前是 **0.1.1 功能预览版**，不是全功能移植完成版。

当前源码聚焦 USB Dongle 控制。`dist` 是包含独立 PEQ 的本地 Windows x64 发布物，不纳入 Git；免插拔刷新实验未通过，未并入产品。根目录启动脚本已移除。

## 运行

从 [GitHub Releases](https://github.com/CareyChi/Questyle-Windows/releases/tag/v0.1.1) 下载 `questyle-windows-x64.zip`，完整解压后运行 **Questyle.exe**；本机构建入口为 **dist/questyle-windows-x64/Questyle.exe**。发布版包含 .NET 运行时，不需要安装 Android 模拟器、Flutter 或 .NET SDK。压缩包请完整解压，运行库、DLL 与图片集中在 `runtime` 文件夹，简繁中文的框架资源及简体中文、繁体中文、英语翻译在 `language` 文件夹，不能单独移动根目录的启动程序。

系统要求：Windows 10 2004（19041）或更新版本，x64。USB 控制需要系统已有的 **UsbDk** 驱动。历史实机验证使用了本机已有的 UsbDk，没有安装、替换或卸载设备驱动；PEQ 刷新实验请求过 Windows 设备重启，结果见 [验证记录](docs/VERIFICATION.md)。

USB 控制会临时独占整个 Dongle，因此会暂停 Windows USB 音频。每次读取、设置或搜索完成后自动释放设备，Windows 随后恢复音频接口。不要在需要连续播放的场景中频繁刷新。此版本尚未实现无需暂停音频的 HID 驱动适配。

## 已实现

| 模块 | 功能与验证范围 |
| --- | --- |
| 设备 | Windows 设备通知自动更新插拔状态，前台检测到 Dongle 且尚未读取时，自动尝试一次短会话读取，拔出后再次插入会重新自动读取一次；连接后按钮显示「重新读取设备」。身份与能力查询有历史 Pro2 实机验证，新自动读取策略通过模拟检查。 |
| 音频设置 | 编解码切换；按实际当前编码显示 LDAC 或 aptX Adaptive 模式。「关闭麦克风」勾选即原游戏模式开启；切换后 Windows 新播放设备有时直接控制 Dongle 音量、有时只做数字衰减，软件按实际类型设置其音量；LC3 下隐藏麦克风、显示 Auracast。只有有效非零耳机地址和实际编码同时存在才显示已连接；顶部显示当前编码 Logo，设置后检查回读。原游戏模式同值写入与回读有历史实机验证，其他切换未逐项实机验证。LHDC 保留编码切换，没有已确认的模式调节命令。 |
| 耳机管理 | 保存确认连接过的耳机，点击记录连接、悬停行内删除本地记录；搜索弹窗实时显示结果，点击条目结束搜索后连接。支持断开、设备改名。协议/模式按耳机记忆，软件连接或读取后自动恢复；麦克风和 Auracast 使用全局选择。搜索、配对及新交互未逐项实机验证。 |
| PEQ | 设置前的独立参数均衡器，支持前级、最多 64 段、可拖动频响和数值、文本/JSON 导入以及 Equalizer APO、Wavelet、EasyEffects 导出。配置按耳机名称自动保存到软件根目录 `eq-config/型号名.json`，外部修改同步界面；默认实时生效，也可选择手动应用。列表启用标题旁的总开关可暂停所有滤波器，保留逐项勾选和前级增益；频率轴使用右键回中。自己的原生 APO 仅绑定 Dongle 播放端点，提供安装/卸载音频驱动按钮及自动状态检测；不依赖 Equalizer APO。本机安装/恢复及 48 kHz 数字回环有历史验证，导出和使用限制见 [PEQ 说明](docs/PEQ.md)。 |
| 设置 | 简体中文、繁体中文和 English 即时切换并保存到 data/settings.json；开机自启滑动开关使用当前用户启动项，不需要管理员权限。支持自动检查更新和正式／每日构建通道，手动检查按钮位于「关于」；自动检查默认开启，每 5 分钟一次。切换通道后立即检查，并提示该通道的最新版本。发现更新后在版本号旁显示 New，悬停显示版本及更新按钮；点击后窗口收缩为更新窗口，下载并校验后替换程序文件，由新版本进程无缝接管并展开（流程见[开发文档](docs/DEVELOPMENT.md)）。尚未针对真实 GitHub 发布实测下载安装。 |
| 诊断 | 本地日志、诊断导出、操作超时与设备释放；不自动上传日志。 |
| 窗口与托盘 | 圆角、阴影、无边框、单实例，不可手动调整尺寸，窗口按内容与 Windows 工作区缩放；元素块以外的背景可拖动。关闭隐藏到托盘，双击托盘或再次启动恢复界面，托盘右键可退出。设备名称修改和恢复首次读取的原名位于「设置」。 |

## 尚未完成

- 官方云端曲库和推荐内容：已恢复 RSA + AES 请求流程的部分证据，兼容性探测返回 HTTP 401 `decrypt failed`，当前没有作为可用功能开放。
- 固件 OTA：未开放固件写入。APK 中存在 Qualcomm/Gaia 升级流程，需要进一步移植和硬件验证。
- 账号/反馈、产品手册和更多系统设置。
- USB 音频与私有控制同时运行的驱动方案。

APK 的 EQ 与大师音效属于 Wi-Fi 设备功能；目前未发现 Pro2 的 USB 硬件 EQ 写入接口，不作为已确认的 Dongle 待移植功能。

## 使用

1. 插入 Dongle，打开「设备」并等待前台首次自动读取；需要时点击「重新读取设备」。
2. 选择传输协议或可调项，点击「应用音频设置」。切换编码后按新状态显示可调项；无调节项时隐藏。设置没有变化时不会重新接管设备。
3. 点击已保存的耳机连接。新增耳机时，打开搜索弹窗并将耳机置于配对模式，点击搜索结果连接；点击「取消搜索」、弹窗外部或按 Esc 退出搜索。

已确认连接过的耳机保存在启动程序旁的 `paired-headphones.json`，程序目录需要可写；删除行只删除本地记忆，不清除 Dongle 内部配对。已确认的协议/模式及全局开关选择保存到 data/audio-preferences.json，未应用的编辑不保存。日志与诊断导出保存在启动程序旁的 data，临时文件位于 data/temp；导出日志直接写入该目录。测试通过 QUESTYLE_DATA_DIR 将记录、日志与临时文件一并隔离。

## 构建和验证

仅编译和验证源码，不生成发布包。以下使用本机便携 SDK；从仓库克隆后若没有 reverse 目录，安装 .NET 8 SDK 并将命令前缀替换为 `dotnet`。PEQ 使用独立的原生 APO，构建还需按 [PEQ 说明](docs/PEQ.md) 配置便携 llvm-mingw；该页面位于设置前，仅对 Dongle 端点提供参数均衡：

```powershell
./reverse/tools/dotnet/dotnet.exe build Questyle.sln -c Release
./reverse/tools/dotnet/dotnet.exe run --project tests/Questyle.Verification -c Release --no-build
$env:QUESTYLE_DATA_DIR = Join-Path (Get-Location) 'reverse/analysis/ui-verification'
./reverse/tools/dotnet/dotnet.exe run --project tests/Questyle.UiVerification -c Release --no-build
Remove-Item Env:QUESTYLE_DATA_DIR
```

USB 协议检查此前 20 项通过，本轮未重跑无关协议检查。Release 编译、原生 APO 离线 25 项和界面/生命周期 142 项通过；这些自动检查不显示窗口、不安装 APO、不修改音频绑定或操作 USB。另按用户授权完成本机 APO 安装/恢复及数字回环测量，详情见 [验证记录](docs/VERIFICATION.md)。本次已运行 `./build.ps1 -SkipTests` 打包，并通过发布程序无窗口检查；版本保持 0.1.0。脚本优先使用项目 `reverse/tools/dotnet` 中的便携 SDK，否则使用 PATH 上的 .NET 8 SDK，生成自带运行时的 x64 目录及 ZIP，只保留最新版。已有耳机记录与非临时 data 数据保留在本机发布目录，不加入新 ZIP。发布包按 AGENTS.md 不附带 LICENSES、THIRD-PARTY-NOTICES.md 和 README.md，原文件仍在源仓库。

可选实机只读验证：

```powershell
./reverse/tools/dotnet/dotnet.exe run --project tests/Questyle.Verification -- --snapshot
```

`--same-settings` 会短暂接管设备，并把当前游戏模式以及当前编解码对应的模式写回原值，再检查回读；它不是自动化构建的一部分。

项目结构：`src/Questyle.Desktop` 是 WPF 客户端；`tests` 包含验证程序；`docs` 保存开发与验证文档；`reverse` 集中保存本机 APK、分析产物、工具及逆向文档，整个目录不纳入 Git。

项目文档：[功能与实现](docs/DEVELOPMENT.md)、[性能优化](docs/PERFORMANCE.md)、[协作约束](AGENTS.md)、[验证记录](docs/VERIFICATION.md)。本机逆向方案与协议速查位于 `reverse/docs`，不随仓库上传。

原 APK 的 SHA-256：`82D4B6DE172A5497E1566E0CBB7E25D7F9120D296FB9D6EB84F3A52315813390`。原文件未修改。


CI 与更新通道发布规则见 [开发文档](docs/DEVELOPMENT.md)，云端构建与发布结果见[验证记录](docs/VERIFICATION.md)。
