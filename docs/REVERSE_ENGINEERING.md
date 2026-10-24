# Questyle APK 逆向与 Windows 移植方案

记录日期：2026-10-24。本文记录本项目已经取得证据、实现并验证的方案，供后续维护和继续移植使用。

## 1. 已成功的方案与验证范围

当前可用方案是：从 APK 的 Android 插件恢复 USB 帧格式，从 Flutter AOT 恢复业务命令，再通过 Windows 上已有的 UsbDk 驱动直接收发 USB interrupt 报文，用 C# / WPF 重建桌面客户端。

已在 **QCC Dongle Pro2、固件 3.71.3.1** 上完成双阶段握手、设备及耳机信息读取，并验证游戏模式写回原值获得设备确认、随后回读一致。客户端为 Windows x64 原生程序，发布包包含 .NET 8 运行时。

| 模块 | 已取得的结果 | 验证等级 |
| --- | --- | --- |
| USB 通信 | 设备识别、接口选择、双阶段握手、报文收发及校验 | 实机通过 |
| USB 状态 | 名称、版本、设备/耳机 codec 掩码、当前连接、耳机名称及模式查询 | 实机通过；不支持的模式会返回拒绝值 |
| 游戏模式 | 当前值写回、收到 `80`、再次读取一致 | 实机通过；尚未覆盖开关状态转换 |
| 其他 USB 操作 | codec 切换、LDAC/Adaptive、Auracast、搜索、连接、断开、忘记及改名已接入 | 静态恢复并实现，未逐项实机验证 |
| 云端接口 | 找到部分加密流程，初步请求返回 `401 decrypt failed` | 尚未打通 |
| 固件 OTA、完整 EQ 等 | APK 内存在相关线索 | 尚未完成移植 |

USB 控制会话会暂时独占整个 Dongle，中断 Windows 音频；释放后设备重新枚举。音频播放与私有控制无中断并行仍待解决。

## 2. 分析对象和方法

原文件为项目根目录的 `base.apk`，分析期间未修改。

| 项目 | 值 |
| --- | --- |
| 包名 | `com.questyle.android` |
| Android 版本 | `1.8.0` / versionCode `78` |
| APK SHA-256 | `82D4B6DE172A5497E1566E0CBB7E25D7F9120D296FB9D6EB84F3A52315813390` |
| 主业务二进制 | `lib/arm64-v8a/libapp.so`，Flutter AOT |
| 原生插件 | `classes.dex` 中的 Java/Kotlin 层 |

采用两层交叉分析：

1. 解包 APK，使用 JADX 读取 Android 插件。`u2/a.java`、`s2/h.java` 提供 USB 封包、设备筛选和端点选择等证据。
2. 提取 `libapp.so` 字符串并用 AOTopsy 定位 `UsbInterfaceUtil`、`AudioSinkUtil` 等类，结合 ARM64 反汇编恢复命令、参数和解析规则。
3. 将恢复结果写成独立协议实现，用真实设备响应和自动化用例验证。遇到伪代码歧义时回看汇编，不把伪代码当作原始 Dart 源码。

没有恢复出原项目 Dart 源码。`libflutter.so` 的版本字符串为 3.6.1，AOT 静态解析器将同一 snapshot hash 标记为 3.6.2；hash 为 `f956f595844a2f845a55707faaaa51e4`。后续分析应同时记录 hash，避免只凭版本标签判断兼容性。

本项目使用过的 AOTopsy 命令如下，均在项目根目录运行；工具和分析产物保存在本机 `tools`、`analysis` 中，不包含在发布包内：

```powershell
./aotopsy.exe doctor analysis/apk/lib/arm64-v8a/libapp.so
./aotopsy.exe analysis/apk/lib/arm64-v8a/libapp.so --out analysis/aot --decompile --quiet
./aotopsy.exe _debug decompile-native --lib analysis/apk/lib/arm64-v8a/libapp.so --all --filter UsbInterfaceUtil --max 50 --out analysis/usb-dart
```

其他已分析的目标包括 `AudioSinkUtil`、`QuestyleDongleConnect`、`HttpCrypto`、`HttpUtil` 和 `NetworkBase`。AOTopsy 工具来源为 [BroNils/aotopsy](https://github.com/BroNils/aotopsy)。

## 3. Windows USB 通信的关键突破

### 3.1 选择真正的控制接口

APK 的匹配规则为：VID 是 `0A12` 或 `0E6A`，且 PID 是 `4007`。随后选择第一个同时具备 interrupt IN 和 interrupt OUT 的 HID class（`03`）接口。

本机 Pro2 的有效配置为：

| 项目 | 实测值 |
| --- | --- |
| 接口 | `MI_01` |
| OUT endpoint | `01` |
| IN endpoint | `82` |
| 最大 USB 包长 | 64 字节 |
| 实际发送前缀 | `BB` |
| 实际接收前缀 | `CC` |

不能仅按 Windows HID collection 的出现顺序选择：`MI_00` 是音频消费控制输入；`MI_05` 虽暴露 HID collection，但 USB 配置中没有可用的中断端点。当前实现从配置描述符动态选择端点，上表是本机证据，不是所有设备的固定配置。

### 3.2 常规 HID 路径失败的原因

本机 `MI_01` 对应的 HID 描述符声明了 `BB` 输入、`CC` 输出，但固件实际控制协议使用 `BB` 输出、`CC` 输入。

- 通过 Windows `WriteFile` 或 `HidD_SetOutputReport` 发送 `BB`，会遇到参数错误。
- 改发描述符允许的 `CC` / `FD`，没有得到有效 QL 响应。
- 更换到其他 HID collection，不能解决接口和报文方向不匹配的问题。

因此成功实现采用 **UsbDk 原始 USB interrupt 传输**。本次电脑已存在并运行 UsbDk 驱动，没有安装、替换或卸载设备驱动；客户端使用官方 1.0.22 x64 `UsbDkHelper.dll`。只复制此 DLL 并不能代替系统驱动。

### 3.3 已验证的会话顺序

```text
DongleController（串行控制会话）
  → UsbDk 枚举设备、读取配置、StartRedirect
  → 等待 300 ms
  → 先挂 IN 读取，再发 BB + QL 请求
  → 完成双阶段握手
  → 执行查询或操作，检查响应及 ACK
  → finally / Dispose 中 StopRedirect
  → Windows 恢复设备和音频接口
```

当前实现使用 `UsbDk_ReadPipe` / `UsbDk_WritePipe`，传输类型为 interrupt（`2`）。IN 读取放在专用任务中，提前约 40 ms 挂起后再发送 OUT；这是本机已验证成功的收发顺序。发送长度为前缀加实际帧长度，不额外添加 Windows HID 的 report ID `00`。

异步传输使用固定内存和 OVERLAPPED。超时后先 `AbortPipe`，等待完成通知，再释放缓冲区及 OVERLAPPED，避免底层仍在访问已释放内存。当前单次交换超时参数为 2 秒。

控制层用信号量串行化会话，任何异常都释放设备。释放后若暂时枚举不到设备，下次操作最多重试 10 次、每次 400 ms。检测到多个匹配 Dongle 时提示用户保留一个，避免误操作。

## 4. 已恢复的 QL 帧与握手

以下数值均为十六进制。表中的帧不包括 USB `BB` / `CC` 前缀。

| 方向 | 帧格式 |
| --- | --- |
| 请求 | `51 4C seq command subcommand length payload checksum` |
| 响应 | `51 4C seq marker length payload checksum` |

关键规则：

- 请求的 length 在偏移 5，payload 从偏移 6 开始；响应分别在偏移 4 和 5。两种格式不能共用同一字段偏移。
- `checksum = (-sum(此前所有帧字节)) & 0xFF`，整帧字节和模 256 为 0。
- 检查帧头、长度、校验和及序号；当前序号从 2 开始，255 后回到 1。
- 64 字节 USB 包包括 1 字节前缀和 7 字节请求开销，因此请求 payload 最多 56 字节。
- 实机响应 `marker=41` 不能解释成错误状态；Android 插件也没有用它判定操作失败。
- 握手第二步和写操作成功时，要求 payload **恰好为单字节 `80`**。USB 写入成功本身不能证明设备接受了命令。

握手第一步发送 `10/00`，取得设备返回的挑战；第二步发送 `10/01`，将挑战原样回传。以下为一次实机成功记录：

```text
OUT: BB 51 4C 02 10 00 00 51
 IN: CC 51 4C 02 41 08 10 B7 CA C6 C6 FA A3 BD A1
OUT: BB 51 4C 03 10 01 08 10 B7 CA C6 C6 FA A3 BD D0
 IN: CC 51 4C 03 41 01 80 9E
```

中间的 8 字节挑战仅是该次记录，运行时必须使用当次响应，不能硬编码。

## 5. 业务指令与解析规则

完整速查见 [PROTOCOL.md](PROTOCOL.md)。核心命令如下；“恢复”只表示取得协议证据，实机覆盖范围以第 1 节为准。

| 功能 | command / subcommand | 负载或结果 |
| --- | --- | --- |
| 读名称 / 改名 | `90/02` / `90/01` | UTF-8；改名限制 1–22 个 UTF-8 字节 |
| 固件版本 | `20/00` | 8 字节；界面取 `[0].[1].[6].[7]` |
| Dongle / 耳机支持的 codec | `80/02` / `80/01` | 大端位掩码 |
| 当前蓝牙状态 | `30/02` | 前 2 字节为 codec 掩码，`[4,10)` 为耳机地址 |
| 耳机名称 | `30/01` | 请求为 6 字节地址；响应为地址 + UTF-8 名称 + 末尾 codec 字节 |
| 切换 codec | `50/<code>` | 无 payload，要求 `80` ACK |
| LDAC | `E0/FF` 查询 | 设置 `01` 音质、`02` 标准、`03` 连接 |
| aptX Adaptive | `F0/FF` 查询 | 设置 `01` 高音质、`02` 低延迟 |
| 游戏模式 | `11/FF` 查询 | 设置 `02` 开、`01` 关 |
| Auracast | `22/FF` 查询 | 设置 `01` 开、`00` 关 |
| 搜索耳机 | `B0/00`、`B0/01`、`B0/02` | 开始、逐个取结果、停止；另有 `B0/FF` 状态查询 |
| 连接 / 断开 / 忘记 | `C0/00`、`C0/01`、`C0/02` | 连接、忘记携带 6 字节地址，断开无 payload |

游戏模式和 Auracast 的布尔值编码不同，不能用同一个“开=1、关=0”的通用函数。

codec code 与位掩码关系是 `1 << (code - 1)`：

| Codec | aptX | aptX HD | aptX Adaptive | LHDC | LDAC | AAC | LE Audio | SBC |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Code（十进制） | 1 | 2 | 4 | 7 | 8 | 9 | 10 | 11 |

Pro2 实测掩码为 `06CB`；耳机支持情况需单独查询，界面提供双方能力的交集。版本响应 `03 47 68 03 75 51 03 01` 按 APK 的显示规则解析为 `3.71.3.1`，不能直接把前四字节作为四段版本号。

实机上，不支持当前耳机的 Adaptive 查询出现 `83`，非对应状态下的 LDAC 设置出现 `84`。这些是已观察到的拒绝结果，尚不足以定义所有固件的通用错误码。未知值必须保留为不支持/错误状态，不能钳制为一个合法模式。

型号 `15/01`、序列号 `15/03` 有静态证据，但本机 Pro2 查询超时，默认流程已排除。没有对未知指令进行遍历写入。

应用设置前会重新读取状态；若耳机地址变化则中止，避免将旧耳机设置应用到新连接。仅发送发生变化的项目，要求 ACK 并读取回报。搜索取消时，清理路径仍尝试发送 `B0/02`，随后释放设备。

## 6. 代码和证据位置

| 位置 | 用途 |
| --- | --- |
| [QuestyleProtocol.cs](../src/Questyle.Desktop/Services/QuestyleProtocol.cs) | QL 编码、解码、校验和及传输接口 |
| [UsbDkTransport.cs](../src/Questyle.Desktop/Services/UsbDkTransport.cs) | 配置解析、原始 USB、异步 I/O、释放 |
| [DongleController.cs](../src/Questyle.Desktop/Services/DongleController.cs) | 握手、业务指令、会话、状态解析 |
| [MainWindow.xaml.cs](../src/Questyle.Desktop/MainWindow.xaml.cs) | 能力筛选、设置应用、回读及错误显示 |
| `analysis/decompiled/sources/u2/a.java`、`s2/h.java` | Android USB 插件证据 |
| `analysis/aot/asm/UsbInterfaceUtil/`、`AudioSinkUtil/` | ARM64 指令和业务参数证据 |
| `analysis/usb-dart/combined.dart` 等 | 静态恢复伪代码，需交叉核对 |
| `analysis/hardware-verification.log` | 实机同值写入和回读记录 |

`analysis` 和 `tools` 是本机分析目录，部分内容被 Git 忽略。原始诊断可能含设备地址，不随程序发布；本文未列出真实耳机地址。

## 7. 复现和验证

以下命令从项目根目录运行。需要本机 `tools/dotnet` 或相应 .NET 8 SDK；运行实机测试还需要已安装的 UsbDk 和测试程序指定位置的 helper DLL。

默认 USB 协议验证，不接管 USB：

```powershell
./tools/dotnet/dotnet.exe run --project tests/Questyle.Verification -c Release
```

USB 界面组件验证使用独立数据目录：

```powershell
$env:QUESTYLE_DATA_DIR = Join-Path (Get-Location) 'analysis/ui-verification'
./tools/dotnet/dotnet.exe run --project tests/Questyle.UiVerification -c Release
Remove-Item Env:QUESTYLE_DATA_DIR
```

实机状态验证会短暂独占 USB 并中断音频，完成握手后查询状态：

```powershell
./tools/dotnet/dotnet.exe run --project tests/Questyle.Verification -c Release -- --snapshot
```

可选 `--same-settings` 会读取当前状态，然后写回原游戏模式；仅在当前 codec 匹配时写回对应 LDAC/Adaptive 模式，并验证回读。它会产生真实写操作，未加入默认构建测试。本次确认成功的写入证据是游戏模式同值写入，不代表所有模式切换均已验证。

当前源码 Release 编译零错误、零警告；USB 协议 16 项、USB 界面组件 2 项，共 **18 项自动化检查通过**。实际硬件验证另行记录，详情见 [VERIFICATION.md](VERIFICATION.md)。本次功能裁剪没有重新操作硬件。

以后需要发布时，可运行 `./build.ps1` 验证并生成自带运行时的 `dist/Questyle-Windows-x64` 和 ZIP。本次只更新源码、编译与验证，不生成或更新发布包；现有 dist 是此前构建。

## 8. 后续移植边界

- 优先补充 USB 搜索/配对、改名、各 codec 模式及 Auracast 的逐项实机验证，不能只用 `80` 模拟响应替代硬件结果。
- 云端部分仅恢复了 RSA 包装请求密钥、AES ECB/PKCS7 等加密线索，初步官方请求返回 `401 decrypt failed`，尚无可用云端方案。
- Qualcomm/Gaia OTA 尚未移植，未进行固件写入；完整 EQ、账号和更多设置也不属于已完成范围。
- 仍需研究能与 Windows 音频并行工作的控制通道，当前成功方案依赖短暂 UsbDk 独占。
