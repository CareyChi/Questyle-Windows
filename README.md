# Questyle Windows 移植版

从本机 `reverse/base.apk`（Questyle Android 1.8.0 / build 78）逆向重建的 Windows 原生客户端。目前是 **0.1.0 功能预览版**，不是全功能移植完成版。

当前源码聚焦 USB Dongle 控制。本次已完成源码裁剪和验证，尚未重新打包；`dist` 仍是此前发布的程序，根目录启动脚本已移除。

## 运行

已有发布物可运行 **dist/Questyle-Windows-x64/Questyle.exe**。发布版包含 .NET 运行时，不需要安装 Android 模拟器、Flutter 或 .NET SDK。压缩包使用时请完整解压后运行，保留 DLL 和 Assets 文件夹。

系统要求：Windows 10 2004（19041）或更新版本，x64。USB 控制需要系统已有的 **UsbDk** 驱动。本次在这台电脑上检测到并使用了现有的 UsbDk，没有安装、替换或卸载设备驱动。

USB 控制会临时独占整个 Dongle，因此会暂停 Windows USB 音频。每次读取、设置或搜索完成后自动释放设备，Windows 随后恢复音频接口。不要在需要连续播放的场景中频繁刷新。此版本尚未实现无需暂停音频的 HID 驱动适配。

## 已实现

| 模块 | 功能与验证范围 |
| --- | --- |
| USB 设备 | 自动识别 APK 中的 VID/PID、双阶段握手、设备名称、固件版本、当前耳机和支持的编解码。QCC Dongle Pro2 已实机验证。 |
| 声音设置 | 已接入编解码切换、LDAC 三模式、aptX Adaptive 两模式、游戏模式、Auracast；只显示耳机与 Dongle 共同支持的编解码。设备拒绝的操作会显示错误，支持的设置会读取回报。游戏模式同值写入与回读已实机验证，其他切换未逐项实机验证。 |
| 耳机管理 | 已接入搜索、连接、断开、忘记耳机、设备改名；协议来自 APK。耳机名称查询已实机验证，搜索与配对操作尚未逐项实机验证。 |
| 诊断 | 本地日志、诊断导出、操作超时与设备释放；不自动上传日志。 |

## 尚未完成

- 官方云端曲库和推荐内容：已恢复 RSA + AES 请求流程的部分证据，兼容性探测返回 HTTP 401 `decrypt failed`，当前没有作为可用功能开放。
- 固件 OTA：未开放固件写入。APK 中存在 Qualcomm/Gaia 升级流程，需要进一步移植和硬件验证。
- 账号/反馈、完整 EQ 与大师音效、产品手册和更多系统设置。
- USB 音频与私有控制同时运行的驱动方案。

## 使用

1. 插入 Dongle，在「USB 设备」点击「读取设备」。等待音频控制会话完成。
2. 选择耳机支持的编解码或设置，点击「应用声音设置」。不支持的选项保持禁用；设置没有变化时不会重新接管设备。
3. 搜索耳机时，将耳机置于配对模式；搜索期间可取消。连接完成后重新读取状态。

最近会话日志保存在 `%LOCALAPPDATA%\QuestyleWindows`。测试可通过 `QUESTYLE_DATA_DIR` 指定独立目录。

## 构建和验证

仅编译和验证源码，不生成发布包。以下使用本机便携 SDK；从仓库克隆后若没有 reverse 目录，安装 .NET 8 SDK 并将命令前缀替换为 `dotnet`：

```powershell
./reverse/tools/dotnet/dotnet.exe build Questyle.sln -c Release
./reverse/tools/dotnet/dotnet.exe run --project tests/Questyle.Verification -c Release --no-build
$env:QUESTYLE_DATA_DIR = Join-Path (Get-Location) 'reverse/analysis/ui-verification'
./reverse/tools/dotnet/dotnet.exe run --project tests/Questyle.UiVerification -c Release --no-build
Remove-Item Env:QUESTYLE_DATA_DIR
```

当前 USB 协议 16 项、USB 界面组件 2 项验证通过。界面组件验证不显示窗口，也不操作硬件。需要打包时再运行 `./build.ps1`；脚本优先使用项目 `reverse/tools/dotnet` 中的便携 SDK，否则使用 PATH 上的 .NET 8 SDK，完成验证后生成自带运行时的 x64 目录及 ZIP。

可选实机只读验证：

```powershell
./reverse/tools/dotnet/dotnet.exe run --project tests/Questyle.Verification -- --snapshot
```

`--same-settings` 会短暂接管设备，并把当前游戏模式以及当前编解码对应的模式写回原值，再检查回读；它不是自动化构建的一部分。

项目结构：`src/Questyle.Desktop` 是 WPF 客户端；`tests` 包含验证程序；`docs` 保存开发与验证文档；`reverse` 集中保存本机 APK、分析产物、工具及逆向文档，整个目录不纳入 Git。

项目文档：[功能与实现](docs/DEVELOPMENT.md)、[协作约束](AGENTS.md)、[验证记录](docs/VERIFICATION.md)。本机逆向方案与协议速查位于 `reverse/docs`，不随仓库上传。

原 APK 的 SHA-256：`82D4B6DE172A5497E1566E0CBB7E25D7F9120D296FB9D6EB84F3A52315813390`。原文件未修改。
