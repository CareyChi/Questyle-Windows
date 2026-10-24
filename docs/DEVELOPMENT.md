# Questyle Windows 开发文档

本文以 2026-10-24 当前源码为基准，记录全部现有功能、代码职责、运行流程和开发方式。协作约束见根目录 [AGENTS.md](../AGENTS.md)。

## 1. 当前范围

本项目从 Questyle Android `1.8.0 / build 78` APK 逆向重建 Windows USB Dongle 客户端，当前版本号为 `0.1.0`。现有界面包含「USB 设备」和「关于与移植状态」两个页面。

Wi-Fi 播放器、本地音乐及底部播放栏已从源码移除，对应服务、依赖和测试也已清理，不属于当前功能。`dist` 与根目录启动脚本仍指向此前构建；功能裁剪后尚未发布新包。

### 运行条件

| 项目 | 当前要求 |
| --- | --- |
| 系统 | Windows 10 2004 / 19041 或更新版本 |
| 发布架构 | x64 |
| 应用 | C#、.NET 8、WPF，目标 net8.0-windows10.0.19041.0 |
| USB 依赖 | 系统已有 UsbDk 驱动，程序目录包含 UsbDkHelper.dll |
| 设备匹配 | VID 0A12 或 0E6A，PID 4007 |
| 已验证硬件 | QCC Dongle Pro2，固件 3.71.3.1 |

发布形式为包含运行时的 Windows 程序，不依赖 Android 模拟器。当前 UI 和 USB 系统调用均依赖 Windows，不支持直接改参数发布到 macOS、Linux。

## 2. 功能清单

下文“已接入”表示已有调用实现，不表示在所有设备及状态下均完成实机验证。详细证据和历史验证结果分别见 [逆向方案](REVERSE_ENGINEERING.md) 与 [验证记录](VERIFICATION.md)。

### 2.1 USB 设备与状态

| 功能 | 当前行为 | 验证范围 |
| --- | --- | --- |
| 启动检测 | 使用 Windows HID 只读枚举匹配设备，显示检测结果；启动不自动进行原始 USB 接管 | 已在本机使用 |
| 手动读取 | 点击「读取设备」，建立短 USB 会话并执行双阶段握手 | Pro2 实机通过 |
| 设备身份 | 显示设备名称、固件版本、上次读取时间 | Pro2 实机通过 |
| 能力查询 | 分别读取 Dongle 和耳机支持的 codec | Pro2 实机通过 |
| 当前连接 | 显示当前 codec、耳机地址、耳机名称，并将地址带入连接管理 | Pro2 实机通过 |
| 选项查询 | 读取 LDAC、Adaptive、游戏模式和 Auracast 返回值 | 已有实机响应；选项可用性随耳机及设备状态变化 |
| 多设备处理 | 控制会话发现多个匹配 Dongle 时停止，让用户保留一个 | 已实现，非多设备选择器 |
| 重新枚举等待 | 上次释放后暂时找不到设备时，在限定时间内等待 | 已实现 |

界面检测到 HID 设备只代表已插入；只有成功握手与读取才证明私有控制可用。型号和序列号查询存在于 APK，但本机无响应，默认状态读取不包含它们。

### 2.2 声音与传输

| 功能 | 当前行为 | 验证范围 |
| --- | --- | --- |
| Codec 选择 | LDAC、aptX Adaptive、aptX HD、aptX、LHDC、AAC、LE Audio、SBC；是否出现取决于设备能力 | 掩码解析已验证，主动切换未逐项实测 |
| 能力筛选 | 已读到耳机能力时取双方交集；耳机能力列表为空时界面显示 Dongle 能力 | 当前实现行为，空列表不构成耳机支持证据 |
| LDAC 模式 | 音质优先、标准模式、连接优先 | 命令已恢复并接入，未逐项实机切换 |
| aptX Adaptive 模式 | 高音质模式、低延迟模式 | 命令已恢复并接入，未逐项实机切换 |
| 游戏模式 | 开、关；协议编码与 Auracast 不同 | 同值写入获得 80 ACK，回读一致；未覆盖开关转换 |
| Auracast | 广播开、关 | 已接入，未实机启用验证 |
| 应用设置 | 没有差异时直接结束；有差异时重新读取状态，检查耳机地址，发送变化项目，再取快照 | 已实现；设置完整覆盖仍需硬件验证 |
| 不支持的选项 | LDAC/Adaptive 根据返回值范围与耳机能力禁用；Auracast 返回值不为 0/1 时禁用 | UI 组件回归通过 |

写操作必须取得单字节 `80` ACK。应用完成后刷新快照，并比较 LDAC、Adaptive、游戏模式和 Auracast 请求值；当前 codec 切换会读取最新状态，但没有独立的目标 codec 等值断言。不能将这部分描述为所有设置都已验证生效。

游戏模式当前以布尔值表示，查询值为 `02` 时显示开启，其他值显示关闭；未知返回值的完整语义尚未建模。开发时应依据协议证据补充，不能据此推断所有非 `02` 值都表示正常关闭。

### 2.3 耳机管理与改名

| 功能 | 当前行为 | 验证范围 |
| --- | --- | --- |
| 搜索耳机 | 进入搜索，约 12 秒内逐个获取结果，按地址去重，结束后退出搜索 | 已接入，未逐项实机验证 |
| 取消搜索 | 操作时出现取消按钮；取消后仍尝试退出搜索并释放设备 | 清理路径已实现 |
| 选择地址 | 从结果列表带入地址，也可手动输入 6 字节蓝牙地址 | 地址负载有自动化覆盖 |
| 连接 | 向所选地址发送连接命令，稍后读取快照 | 已接入，未逐项实机验证 |
| 断开 | 断开当前耳机并读取快照 | 已接入，未逐项实机验证 |
| 忘记耳机 | 显示确认框，确认后向地址发送忘记命令并移除选中条目 | 已接入，未执行实机清除验证 |
| Dongle 改名 | 名称去首尾空白，限定 1–22 个 UTF-8 字节，发送并重新读取 | 参数边界有自动化覆盖，改名未实测 |

耳机地址是设备标识，不应写进公共示例。改名长度按字节计算，中文字符不能按一个字节计数。

### 2.4 操作状态与诊断

| 功能 | 当前行为 |
| --- | --- |
| 状态显示 | 页面顶部显示检测/读取状态，底部显示当前操作结果 |
| 操作互斥 | UI 不并发执行操作；控制层用信号量串行化原始 USB 会话 |
| 关闭处理 | 操作尚未结束时延迟关闭，并尝试取消搜索，等待设备释放 |
| 错误处理 | 显示当前操作失败原因；缺少 helper DLL、Windows 系统错误有对应提示 |
| 运行日志 | 记录启动检测、发送/接收报文、握手、释放与异常 |
| 日志长度 | 内存日志超过 200000 字符时保留末尾约 150000 字符 |
| 导出日志 | 用户选择路径导出文本文件 |
| 会话保存 | 关闭时写入数据目录的 last-session.log，覆盖上次记录 |
| 打开目录 | 关于页打开本地数据文件夹 |
| 官网入口 | 使用系统默认浏览器打开 Questyle 官网 |
| 关于页面 | 当前包含应用身份、功能/移植状态说明和日志入口；后续 UI 修改遵守 AGENTS.md 的精简要求 |

数据目录由 AppPaths 提供，默认 `%LOCALAPPDATA%\QuestyleWindows`，可用 `QUESTYLE_DATA_DIR` 覆盖。当前不持久化声音设置，不加载旧音乐库，不自动上传日志。原始日志可能包含耳机地址。

## 3. 代码结构与职责

| 文件或目录 | 职责 |
| --- | --- |
| [MainWindow.xaml](../src/Questyle.Desktop/MainWindow.xaml) | 窗口、资源样式、根容器 |
| [MainWindow.xaml.cs](../src/Questyle.Desktop/MainWindow.xaml.cs) | 页面构建、事件、状态展示、设置差异、日志交互 |
| [DongleController.cs](../src/Questyle.Desktop/Services/DongleController.cs) | 会话串行化、握手、命令、快照、搜索和业务解析 |
| [QuestyleProtocol.cs](../src/Questyle.Desktop/Services/QuestyleProtocol.cs) | QL 封包/解包、校验、IQuestyleTransport 接口 |
| [UsbDkTransport.cs](../src/Questyle.Desktop/Services/UsbDkTransport.cs) | 设备枚举、配置描述符、接管、interrupt I/O 与释放 |
| [HidTransport.cs](../src/Questyle.Desktop/Services/HidTransport.cs) | Windows HID 只读枚举；不承担设置发送 |
| [AppPaths.cs](../src/Questyle.Desktop/Services/AppPaths.cs) | 数据目录解析和创建 |
| [Questyle.Verification](../tests/Questyle.Verification/Program.cs) | 协议自动化检查和可选实机诊断入口 |
| [Questyle.UiVerification](../tests/Questyle.UiVerification/Program.cs) | 两项 USB 设置 UI 组件回归，无窗口显示 |
| [build.ps1](../build.ps1) | 验证后生成自带运行时的发布目录和 ZIP |
| analysis / tools | 本机逆向材料和工具，大部分被 Git 忽略 |
| dist | 既有发布物，当前与源码存在功能差异 |

当前通过 IQuestyleTransport 分隔帧交换与业务指令，但 DongleController 的会话创建仍直接使用 UsbDkTransport；不能因此宣称平台层已完全解耦。

## 4. 关键运行流程

### 设备读取

1. UI 调用 DongleController.ReadAsync，等待会话信号量。
2. UsbDk 枚举设备，解析第一个具备双向 interrupt 端点的 HID 接口。
3. 接管并等待稳定，先挂 IN 再发送带 BB 前缀的 QL 请求。
4. 进行 10/00、10/01 双阶段握手，第二步要求 80 ACK。
5. 查询身份、能力、连接与模式，构造 DongleSnapshot。
6. 无论成功或异常，最终释放设备，再更新界面或显示错误。

### 设置应用

从当前快照与界面选项计算差异，无差异不接管。需要修改时，新会话重新读取并比较耳机地址；地址变化即停止。发送各项差异，逐次判定 ACK，取新快照并进行现有回读比较。

### 传输约束

本机成功接口为 MI_01，OUT 01、IN 82，包长上限 64；实际端点由配置描述符选择。帧格式、codec 映射和拒绝响应见 [PROTOCOL.md](PROTOCOL.md)，此处不重复命令表。

UsbDk 会接管整个复合设备，期间 Windows 音频暂时中断。不得扩大成常驻接管或后台轮询。取消/超时必须完成传输清理后释放固定内存与句柄，结束时 StopRedirect。

## 5. 开发与最小验证

命令在项目根目录执行。优先使用本机 tools/dotnet 中的便携 SDK；没有该目录时使用已安装的 .NET 8 SDK，将命令前缀替换为 dotnet。

仅编译，不发布：

```powershell
./tools/dotnet/dotnet.exe build Questyle.sln -c Release
```

影响协议、解析或命令时运行：

```powershell
./tools/dotnet/dotnet.exe run --project tests/Questyle.Verification -c Release --no-build
```

影响 USB 设置界面行为时运行，数据与日常使用隔离：

```powershell
$previousDataDirectory = $env:QUESTYLE_DATA_DIR
$env:QUESTYLE_DATA_DIR = Join-Path (Get-Location) 'analysis/ui-verification'
try {
    ./tools/dotnet/dotnet.exe run --project tests/Questyle.UiVerification -c Release --no-build
} finally {
    if ($null -eq $previousDataDirectory) {
        Remove-Item Env:QUESTYLE_DATA_DIR -ErrorAction SilentlyContinue
    } else {
        $env:QUESTYLE_DATA_DIR = $previousDataDirectory
    }
}
```

`--no-build` 要求相关源码已经编译。协议程序默认不访问 USB；UI 程序只构造组件，不显示窗口。最近一次代码验证为协议 16 项、UI 2 项通过，本文编写没有重跑这些检查。

纯文档修改只核对内容与链接。相关检查已通过且没有新变更/失败时停止，不重复编译或换配置重测。

### 可选实机入口

以下参数追加到 Questyle.Verification 的 `--` 后，仅在任务包含相关硬件操作时使用：

| 参数 | 作用 | 设备影响 |
| --- | --- | --- |
| --usb | 只读枚举 Windows HID 信息 | 不开启原始控制会话 |
| --raw-list | 枚举 UsbDk 设备及配置描述符 | 不调用 StartRedirect |
| --snapshot | 握手并读取当前快照 | 短暂独占，中断音频 |
| --same-settings | 读取后写回当前游戏模式；仅当前 codec 匹配时写回对应模式，再比较回读 | 真实写操作，中断音频 |
| --raw-query | 调查用逐条查询，包含本机已知超时的型号/序列号命令 | 独占且可能长时间等待，不作为常规验证 |

这些入口使用测试程序指定的 `tools/usbdk-runtime/.../UsbDkHelper.dll` 路径，并要求系统已有 UsbDk 驱动。不能把它们加入默认构建检查。

## 6. 构建、运行与发布的区别

- `dotnet build`：生成本地编译产物，不替换 dist。
- 开发运行：使用已编译程序或 `dotnet run --project src/Questyle.Desktop -c Release --no-build`。窗口会显示，不能把开发运行混入无窗口组件检查。
- 根目录「启动 Questyle.cmd」：打开 dist 中的现有程序，不运行刚改好的源码。
- `build.ps1`：运行检查、执行 win-x64 self-contained publish、复制声明和许可证、生成 ZIP，是发布流程。

当前保持不打包状态。收到明确发布要求后再使用发布流程；发布时应检查输出目录内是否残留旧模块依赖，避免增量发布带入已删除的文件。第三方资源说明见 [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md)。

## 7. 尚未完成及讨论范围

| 项目 | 当前状态 |
| --- | --- |
| 更多实机覆盖 | 搜索/新配对、改名、codec 切换、Auracast 和各模式转换尚未逐项验证 |
| 音频与控制并行 | 未解决，现有方案为短暂独占 |
| EQ、大师音效、更多设备设置 | 未完成 |
| 账号、反馈、云端内容 | 未开放；云端初步兼容请求返回 401 decrypt failed |
| 固件 OTA | 有 Qualcomm/Gaia 线索，未移植、未执行固件写入 |
| macOS / Linux | 未实现，需要更换界面层并适配 USB 系统接口 |
| 网页端 | 仅评估过；尚无网页、桥接服务或浏览器实测 |

网页端讨论中的主要约束是当前控制接口属于 HID，普通 WebUSB 对此类接口存在限制；WebHID 又依赖系统 HID，而本机描述符方向与实际报文不一致。纯网页能否完成握手尚未验证。网页加本地辅助程序是候选架构，尚未得到实施授权，也不能消除各系统的 USB 适配工作。

## 8. 文档维护分工

- 本文：当前功能、实现职责、开发与发布入口。
- [REVERSE_ENGINEERING.md](REVERSE_ENGINEERING.md)：逆向过程、成功方案、证据位置和复现依据。
- [PROTOCOL.md](PROTOCOL.md)：帧格式、业务指令、字段和已知设备差异。
- [VERIFICATION.md](VERIFICATION.md)：实际完成的验证、设备和边界，不用计划替代结果。
- [README.md](../README.md)：用户入口与简明使用信息。

修改功能时只同步受到影响的文档，技术解释留在这些文件中，界面内容遵守 AGENTS.md。
