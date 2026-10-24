# APK 逆向记录

目标：`com.questyle.android`，versionName 1.8.0 / versionCode 78。Flutter AOT，libflutter.so 显示 Dart 3.6.1；静态解析器将相同 snapshot hash 标记为 3.6.2，hash 为 `f956f595844a2f845a55707faaaa51e4`。没有原始 Dart 源码。

## 数据包

证据：JADX `u2/a.java` 的 `b`，`s2/h.java` 的 `D/u/p`；Dart `UsbInterfaceUtil` 的 ARM64 反汇编。

- 请求：`51 4C sequence command subcommand length payload checksum`。
- 响应：`51 4C sequence marker length payload checksum`。请求和响应的长度位置不同。
- 校验和是前面全部字节之和的负值模 256；本移植版验证长度、序号和校验和。
- Pro2 实机响应 marker 为 `41`，它不是错误状态码。APK 的 Java 层也忽略此字段。
- 成功写入的 payload 是单字节 `80`；不能把“USB 写入成功”当成“设备接受设置”。其他 payload 由具体查询解析。
- USB 发送的报告前缀为 `BB`；Pro2 实机响应前缀为 `CC`。

## USB 适配

APK 仅匹配 VID `0A12` 或 `0E6A`，PID `4007`，寻找第一个有双向 interrupt endpoint 的 HID interface。

本机 Pro2：MI_01，OUT `01` / IN `82`，最大包长 64。MI_05 虽然在 Windows 中暴露 HID collection，但没有配置中的 interrupt endpoint，不能用作 QL 控制。

Windows HID 描述符声明的方向与固件实际使用的 BB/CC 报告不一致。常规 WriteFile 和 HidD_SetOutputReport 对 BB 输出报参数错误；改用声明的 CC/FD 虽可写入，但设备不响应 QL 请求。使用电脑现有 UsbDk 做短暂独占控制可收发原始 interrupt 报告。必须先挂起 IN 读取再发送请求，并始终在 finally 中 StopRedirect。释放后存在设备重新枚举窗口，后续操作会等待至多 4 秒。

## 已恢复的指令

| 功能 | command | subcommand / payload |
| --- | --- | --- |
| 握手第一步 | 10 | 00，无 payload，返回挑战 |
| 握手第二步 | 10 | 01，原样回传挑战，要求 80 |
| 名称 / 改名 | 90 | 02 查询 / 01 UTF-8；APK 限制 22 字节 |
| 版本 | 20 | 00，8 字节，UI 展示 major.minor.patch.build |
| 设备/耳机支持的编解码 | 80 | 02 / 01，big-endian bit mask |
| 当前蓝牙状态 | 30 | 02；前 2 字节是当前编解码掩码，地址在 [4,10) |
| 耳机名称 | 30 | 01，payload 为地址 6 字节 |
| 切换编解码 | 50 | subcommand 为 code，无 payload |
| LDAC | E0 | FF 查询；01 音质、02 标准、03 连接 |
| aptX Adaptive | F0 | FF 查询；01 高音质、02 低延迟 |
| 游戏模式 | 11 | FF 查询；02 开、01 关 |
| Auracast | 22 | FF 查询；01 开、00 关，与游戏模式编码不同 |
| 搜索耳机 | B0 | 00 开始、01 读取一个结果、02 停止、FF 状态 |
| 耳机连接管理 | C0 | 00 连接 / 02 忘记，payload 为地址；01 断开 |

Codec code：aptX=1、aptX HD=2、aptX Adaptive=4、LHDC=7、LDAC=8、AAC=9、LE Audio=10、SBC=11。支持掩码取 bit `(code-1)`。

Pro2 固件 3.71.3.1 实机支持掩码 `06CB`。两次查询分别读到 aptX Adaptive 耳机及 LHDC 耳机的信息。在不支持 aptX Adaptive 的耳机连接时，F0/FF 返回 `83`，不能把它钳制成“低延迟模式”。LDAC 设置在非对应模式可返回 `84`，必须拒绝并显示真实结果。

型号 `15/01` 和序列号 `15/03` 虽存在于 APK，在本机 Pro2 无响应，因此默认查询流程不包含它们。没有对不明指令做遍历写入。

## 当前验证边界

已验证真实双阶段握手、名称、固件、codec mask、当前连接、耳机名称及选项查询；游戏模式写回原值获得 `80`，随后回读一致。没有为了测试主动切换用户的 codec、启用 Auracast、清除配对或写固件。搜索、配对、改名和其他模式切换仍需更多硬件覆盖。
