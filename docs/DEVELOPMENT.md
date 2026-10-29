# Questyle Windows 开发文档

本文以 2026-10-29 当前源码为基准，记录现有功能、代码职责、运行流程和开发方式。协作约束见根目录 [AGENTS.md](../AGENTS.md)，界面细节见 [UI 样式规范](UI-STYLE.md)。

## 1. 当前范围

本项目从 Questyle Android `1.8.0 / build 78` APK 逆向重建 Windows USB Dongle 客户端，当前版本号为 `0.1.1`。原始 APK 在本机 reverse/base.apk。现有界面按「设备」「PEQ」「设置」「关于」排列，设备改名位于设置页。

Wi-Fi 播放器、本地音乐及底部播放栏已从源码移除，对应服务、依赖和测试也已清理，不属于当前功能。根目录启动脚本已删除；`dist` 已更新为包含独立 PEQ 的本地发布包，不纳入 Git。

### 运行条件

| 项目 | 当前要求 |
| --- | --- |
| 系统 | Windows 10 2004 / 19041 或更新版本 |
| 发布架构 | x64 |
| 应用 | C#、.NET 8、WPF，目标 net8.0-windows10.0.19041.0 |
| USB 依赖 | 系统已有 UsbDk 驱动，发布包 runtime 目录包含 UsbDkHelper.dll |
| 设备匹配 | VID 0A12 或 0E6A，PID 4007 |
| 已验证硬件 | QCC Dongle Pro2，固件 3.71.3.1 |

发布形式为包含运行时的 Windows 程序，不依赖 Android 模拟器。当前 UI 和 USB 系统调用均依赖 Windows，不支持直接改参数发布到 macOS、Linux。

## 2. 功能清单

下文“已接入”表示已有调用实现，不表示在所有设备及状态下均完成实机验证。详细证据保存在本机 `reverse/docs/REVERSE_ENGINEERING.md`，历史验证结果见 [验证记录](VERIFICATION.md)。

### 2.1 USB 设备与状态

| 功能 | 当前行为 | 验证范围 |
| --- | --- | --- |
| 启动检测 | 启动/激活时和 Windows USB/HID 设备通知触发只读枚举；空闲时 USB 移除立即清空状态，插入后自动更新页面。前台未读取时自动尝试一次短会话，失败不重试，后台只检测、不接管 | 已在本机使用 |
| 手动读取 | 未检测到设备时显示「读取设备」，连接后显示「重新读取设备」；手动读取建立短 USB 会话并执行双阶段握手 | Pro2 实机通过 |
| 设备身份 | 固件版本位于设备名右侧，下面显示设备能力，仅在耳机有效连接时显示耳机能力；不显示上次读取时间 | 身份查询历史实机通过；新布局离屏检查通过 |
| 能力查询 | 分别读取 Dongle 和耳机支持的 codec | Pro2 实机通过 |
| 当前连接 | 有效非零地址及已识别的当前 codec 才视为已连接；零地址不显示连接、Logo 或当前模式，不写入记忆 | Pro2 实机通过 |
| 选项查询 | 读取 LDAC、Adaptive、游戏模式和 Auracast 返回值 | 已有实机响应；选项可用性随耳机及设备状态变化 |
| 多设备处理 | 控制会话发现多个匹配 Dongle 时停止，让用户保留一个 | 已实现，非多设备选择器 |
| 重新枚举等待 | 上次释放后暂时找不到设备时，在限定时间内等待 | 已实现 |

界面检测到 HID 设备只代表已插入；只有成功握手与读取才证明私有控制可用。型号和序列号查询存在于 APK，但本机无响应，默认状态读取不包含它们。

### 2.2 音频设置

| 功能 | 当前行为 | 验证范围 |
| --- | --- | --- |
| Codec 选择 | LDAC、aptX Adaptive、aptX HD、aptX、LHDC、AAC、LC3（协议 code 10，APK 称 LE Audio）、SBC；是否出现取决于设备能力；顶部显示实际当前编码的 APK Logo；音频面板不再重复显示「当前」行 | 掩码解析已验证，主动切换未逐项实测 |
| 能力筛选 | 已读到耳机能力时取双方交集；耳机能力列表为空时界面显示 Dongle 能力 | 当前实现行为，空列表不构成耳机支持证据 |
| LDAC 模式 | 音质优先、标准模式、连接优先 | 命令已恢复并接入，未逐项实机切换 |
| aptX Adaptive 模式 | 高音质模式、低延迟模式 | 命令已恢复并接入，未逐项实机切换 |
| 关闭麦克风 | 与原游戏模式一致：勾选发送 11/02，取消发送 11/01；ACK 后立即结束旧会话，等待控制 HID 恢复，再以新会话确认地址、编码、模式和开关后保存；耳机尚未重连时同会话等待原耳机最多 15 秒。回读失败或耳机仍未重连时保留本次运行内的待确认请求，手动读取不会先恢复旧偏好；两种模式的描述符都声明硬件音量，但 Windows 新端点有时用硬件音量（音量写入并保存在 Dongle），有时只做数字衰减（QueryHardwareSupport 为 0），与模式无关；切换确认后等待 Windows 新建的端点（最多 10 秒），无硬件音量的端点设为 100%，有硬件音量的端点写回本次运行最后一次读到的硬件音量（未读到过则不改）；LC3 下隐藏 | 用户诊断日志确认开关会重启 USB 接口及旧偏好写回问题；用户日志确认回读时开关已生效但耳机编码尚未恢复；2026-10-25 实机两向切换均等待重连后确认通过 |
| Auracast | 仅当前 LC3 时显示广播开关；只接受单字节 00/01，空、多字节或其他值保持未知；设置后必须匹配回读 | 已接入；异常回复和关闭回读有模拟检查，未实机启用验证 |
| 应用设置 | 无连接时禁用音频面板并清空协议；没有差异时保存已确认的偏好后结束；有差异时检查耳机地址、发送变化项目，再取快照 | 已实现；设置完整覆盖仍需硬件验证 |
| 按编码显示选项 | 模式及专属功能随协议选择立即切换；非当前协议的模式可以编辑，未知原值保留空选项。应用时先切换并确认协议，再验证查询值和应用明确选择；隐藏项不参与写入，顶部 Logo 保留实际编码 | UI／模拟回归通过，新的组合应用流程未实机验证 |

写操作必须取得单字节 `80` ACK。应用完成后刷新快照，并比较实际请求的 LDAC、Adaptive、游戏模式和 Auracast 值；codec 切换单独执行并断言回读的目标 codec，随后根据新快照显示可调项。不能将静态及模拟检查描述为实机生效。

内部游戏模式使用可空布尔值：单字节 `02` 为开启、`01` 为关闭，其他返回值为未知并禁用麦克风控件。APK 提示开启游戏模式会关闭 Dongle 通话，因此 UI「关闭麦克风」与游戏模式同值；这不证明 Windows 上每种耳机都支持麦克风。按用户要求 LC3 隐藏麦克风开关，不发送额外游戏模式命令。

当前 APK 的 USB 工具、设备模型和编解码对话框仅有 LDAC 与 Adaptive 调节，没有找到 LHDC 模式命令。已按用户确认放弃新增 LHDC 模式，只保留 LHDC 编解码切换。

### 2.3 耳机管理与改名

| 功能 | 当前行为 | 验证范围 |
| --- | --- | --- |
| 搜索耳机 | 磨砂背景弹窗中搜索约 12 秒，结果实时按地址去重；开始前按 APK 查询 B0/FF，仅单字节 80 时先以 B0/02 退出残留搜索，再发送 B0/00；点击结果取消搜索，释放会话后按“连接”中的弹窗流程连接 | 模拟检查通过，未逐项实机验证 |
| 取消搜索 | 弹窗底部取消、弹窗外部或 Esc 均取消；开始指令也处于退出清理范围，确认开始后发送 B0/02 并校验 ACK，开始回复失败时先查询搜索状态，仅确认仍在搜索时退出；实机 Pro2 的开始 ACK 先于搜索初始化完成，开始确认未满 2 秒时取消，清理会先等待满 2 秒再发送停止；清理不受取消令牌打断，失败如实报错。B0/FF 在该固件搜索和退出后均返回 82，不能把非 80 当作已经退出的实机证据。结束搜索并释放设备后才能连接。未选配对目标时保留当前连接状态，取消后的接口恢复期不会因临时空枚举清空页面 | 取消按钮、模拟释放及空枚举回归检查通过，未新增实机验证 |
| 本地耳机记录 | 确认连接后保存到启动程序旁的 paired-headphones.json；启动恢复列表，按地址更新名称。MAC 固定右对齐，删除按钮悬停覆盖；蓝色绑定最近确认的连接地址，不随列表选择移动 | 文件及离屏回归通过；尚无不打断音频的蓝牙状态实时监听 |
| 连接 | 点击保存列表或弹窗结果；当前已连接地址直接返回，不更新状态或接管 USB。弹窗结果：发送 B0/00，轮询 B0/01 至发现目标且 30/02 地址归零（最多 12 秒），在搜索模式中发送 C0/00（APK connectAudioSink 顺序；搜索结束后 Pro2 对从未配对过的 MOMENTUM 4 返回 82）；等待结束后查询 B0/FF，仅为 80 时发送 B0/02（连上后 B0/02 实测为 83）。保存列表：先读连接状态（30/02），Dongle 空闲时直接发送 C0/00；已有耳机时发送 B0/00 让 Dongle 释放当前耳机，轮询状态至地址归零（最多 5 秒），再发送 B0/02 退出搜索后发送 C0/00。有耳机连接时 C0/00 返回 83、搜索模式中返回 81，因此不能省略这两步。任一步未获 80 或超时，在同一会话读取并显示实际状态后报错；获 80 后不释放 USB，在同一会话每约 0.3 秒查询 30/02，目标地址与可识别编码出现即读取快照（APK 的 15 秒窗口为上限），从而只中断一次音频且界面随连接完成更新。快照为目标时恢复声音设置、保存记录并标蓝；否则显示实际状态并报错，不保存。会话本身失败时没有新状态，保留最近快照；失败释放后同样保留接口恢复状态，短暂空枚举不清空页面或禁用列表 | 2026-10-25 正式代码实机同会话等待版双向切换 Pudding ⇄ MOMENTUM 5 两轮共 4 次均确认，各 6–7 秒（耳机非配对模式）；目标耳机关机时持有 USB 的 15 秒内未连上，按失败显示；空闲直连仅探针验证一次；弹窗流程仅探针实机验证一次（MOMENTUM 4 首次配对，C0/00 为 80，约 3 秒连上），正式代码弹窗路径未实机验证；回读不符时显示实际状态有模拟检查 |
| 断开当前设备 | 无有效蓝牙连接时禁用；断开当前耳机并读取快照，仍有连接时报告失败；不改变自动连接策略 | 无连接按钮状态离屏检查通过；断开未逐项实机验证 |
| 删除记录 | 鼠标悬停行右侧出现删除按钮，与断开按钮使用相同圆角模板；删除当前连接条目时先断开并确认回读，再删除本地设备及声音设置记忆；失败保留记忆。删除其他条目不影响当前连接，不发送忘记配对命令 | UI/文件与模拟断开检查通过；原 Forget 协议方法仍保留，未新增实机验证 |
| Dongle 改名 | 名称去首尾空白，限定 1–22 个 UTF-8 字节，发送并重新读取；首次读取的原名保存在 data/original-device-name.txt，恢复按钮使用该名称执行普通改名；名称输入框与两个按钮均与设备页读取按钮等高 | 参数边界、控件等高及文字未裁切有自动化覆盖，改名未实测 |

耳机地址是设备标识，不应写进公共示例。改名长度按字节计算，中文字符不能按一个字节计数。

### 2.4 操作状态与诊断

界面字体按 Microsoft YaHei → SimHei → 系统消息字体回退，无调节项；普通控件和标签 14、次要信息和日志 13、块标题 19、侧边栏标签 20。标签用细空格增加字距，品牌在侧栏水平居中，底部使用居中的 v版本号；每日构建在版本号后接下划线和构建提交前 7 位（如 v0.1.0_4c0701e），关于页同样以 Version: 后接该形式显示。音频与耳机卡片固定等高 416、内边距 20，列表固定高 192，设备页每行高 42.75、行距 5.25，行本体缩至原来的 95% 后每条仍占 48，使用不透明度 0.04 的极淡阴影，完整显示 4 条，MAC 位于名称右侧，删除按钮只在行悬停时显示；滚动条宽 8、圆头且无箭头；已连接条目使用浅蓝选中样式。设备名称输入框、协议、模式和语言下拉框统一圆角 7，使用圆头描边箭头与圆角弹出列表。外框为侧边栏同色的单物理像素抗锯齿边框与较重阴影，外框底色同样使用侧边栏颜色，避免浅色底透过左侧圆弧；操作按钮、开关及勾选框统一保留 1 DIP 默认灰色描边及微弱阴影，侧栏、关闭按钮、输入框、下拉框及选项、滚动条保留原样，颜色和透明度变化通常使用约 140 毫秒过渡，下拉菜单及选项、侧栏取消选中使用 100 毫秒。去除默认虚线焦点提示，关闭按钮按真实指针位置显示悬停/按住颜色；移出、隐藏与恢复均清除残留状态，键盘焦点不维持红色。页面内容、顶部标题与底部状态文字共用左侧 36 的边距；窗口不可手动调整尺寸；宽度按字体测量和截图比例确定，高度随设备页内容计算，底部空隙等于标题到首张卡片的间距。首次显示即预留重新读取按钮宽度与两行能力信息高度；读取／连接／断开／插拔只更新内容，不重新调整窗口尺寸，尺寸测量仅发生在初始化、DPI 或语言变化时。应用清单声明 [PerMonitorV2](https://learn.microsoft.com/en-us/windows/win32/hidpi/setting-the-default-dpi-awareness-for-a-process)，WPF 使用 DIP，并读取当前屏幕工作区和 DPI；空间不足时整体等比适配，未固定窗口像素尺寸。设置/关于页复用设备页空间，日志高度随剩余空间调整；设备列表和日志保留内部滚动。


| 功能 | 当前行为 |
| --- | --- |
| 状态显示 | 顶部显示检测到 USB 设备 / 未插入 USB 设备；底部仅保留原背景和高度，操作提示文字隐藏；内部状态与诊断日志保留 |
| 操作互斥 | UI 不并发执行操作；控制层用信号量串行化原始 USB 会话 |
| 关闭处理 | 操作尚未结束时延迟关闭，并尝试取消搜索，等待设备释放 |
| 窗口与托盘 | 无系统标题栏，四周圆角与阴影；元素块以外的背景可拖动、双击不最大化；右上角关闭隐藏到托盘；托盘双击/打开恢复，右键退出才结束进程 |
| 单实例 | 当前 Windows 会话内通过命名 Mutex 保留唯一实例，第二进程用命名事件通知原实例恢复，并请求前台激活权限 |
| 错误处理 | 显示当前操作失败原因；缺少 helper DLL、Windows 系统错误有对应提示 |
| 运行日志 | 记录启动检测、发送/接收报文、握手、释放与异常 |
| 日志长度 | 内存日志超过 200000 字符时保留末尾约 150000 字符 |
| 导出日志 | 写入安装目录 data/Questyle-diagnostics-时间.txt，状态栏报告保存结果 |
| 会话保存 | 关闭时写入数据目录的 last-session.log，覆盖上次记录 |
| 打开目录 | 关于页打开本地数据文件夹 |
| 官网入口 | 使用系统默认浏览器打开 Questyle 官网 |
| 关于页面 | 应用身份、版本、官网和诊断入口 |

软件设置提供简体中文、繁体中文和 English，JSON 翻译位于 language 根部，即时应用并将选择保存到 data/settings.json。开机自启滑动开关读取和修改当前用户 HKCU/Software/Microsoft/Windows/CurrentVersion/Run 的 Questyle 项，命令中的 EXE 完整路径加引号；只有用户点击开关才写入。此系统注册使用 [Windows Run 启动项](https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys)。

主窗口不保留透明阴影边距，实际边界与外框一致。阴影由独立附属分层窗口绘制，中心透明，阴影为 32 DIP 模糊、0 偏移、不透明度 0.38，四边扩散一致，设置 [WS_EX_TRANSPARENT / WS_EX_NOACTIVATE](https://learn.microsoft.com/en-us/windows/win32/winmsg/extended-window-styles)，随主窗口移动、缩放、隐藏和退出。移动路径只在 WM_WINDOWPOSCHANGED 中用 SetWindowPos 改变阴影原生窗口的位置。阴影外观由 `ShadowFrame` 在 DPI 改变时预渲染一次（内框 130 DIP，四角各 92 DIP 覆盖圆角与模糊范围），绘制时四角原样、四边拉伸并对齐整像素；更新动画只改其 Bounds，逐帧不做模糊、布局或几何裁剪。普通验证只离屏检查外环绘制，不显示窗口。

数据目录由 AppPaths 提供，默认启动 EXE 所在目录的 data，日志和诊断导出只写入该目录；应用启动时 TEMP/TMP 指向 data/temp。仅验证程序通过 QUESTYLE_DATA_DIR 使用独立目录，不自动迁移或删除旧的 LocalAppData 日志。已确认应用的声音设置保存到 data/audio-preferences.json：协议及 LDAC/Adaptive 模式按耳机地址分别保存，关闭麦克风和 Auracast 为全局选择。成功连接及首次/手动读取时在同一短 USB 会话内自动恢复；协议切换先读取并确认，再应用对应模式和全局开关，所有写入仍需 ACK 和匹配回读。未连接、耳机不支持保存协议或相关查询未知时不发送对应设置。未点击应用的编辑不保存。LC3 不发送麦克风指令，非 LC3 不发送 Auracast 指令。删除耳机记录同时删除该耳机的模式记忆，保留全局选择；已连接耳机名称和地址保存在 AppPaths.PairingFile，即启动 EXE 旁的 paired-headphones.json。QUESTYLE_DATA_DIR 同时覆盖测试耳机记录路径。记录文件需要程序目录可写，保存失败会报告错误，不转换成保存成功。不加载旧音乐库，不自动上传日志。原始日志可能包含耳机地址。

插拔通知使用 [RegisterDeviceNotification](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerdevicenotificationw)，只接受已知 VID/PID 的 USB/HID 接口。枚举在后台线程执行，合并重复通知；旧枚举结果不得覆盖后来收到的移除事件。控制会话中的接口消失延后到释放后处理，成功释放后等待正常 HID 恢复，不触发循环自动读取。空闲移除清空界面时复位单次自动读取，再次插入且窗口在前台时只自动读取一次；读取失败不重试。退出前注销通知，无定时轮询或常驻独占。

### 2.5 Dongle PEQ

PEQ 使用本项目独立的原生 Windows APO，仅安装到已识别的 Dongle 播放端点；包含前级、多段参数滤波、可拖动频率轴、数值拖动、文本/JSON 导入、Equalizer APO／Wavelet／EasyEffects 导出、按耳机型号自动保存及外部文件检测、显式安装和原音效恢复。型号配置位于软件根目录 `eq-config`，按 Dongle 上报的耳机名称选择，同名共用；默认实时写入已安装的音频驱动，也可切换手动应用。驱动状态自动只读更新，不保留手动刷新按钮。不依赖 Equalizer APO，不新增 USB 指令。开发 DLL 未签名，安装要求、导出限制及实际验证边界见 [PEQ 说明](PEQ.md)；原理分析与本机证据单独存于 reverse/docs/EQUALIZER_APO_ANALYSIS.md。

安装/恢复仍可能需要重新插拔以重建 Windows 音频图。2026-10-26 自动刷新实验未通过，候选代码保留在 reverse，未接入当前产品，也未更新 dist；参数应用仍使用已有 APO 的热更新。

## 3. 代码结构与职责

| 文件或目录 | 职责 |
| --- | --- |
| [MainWindow.xaml](../src/Questyle.Desktop/MainWindow.xaml) | 窗口、资源样式、根容器 |
| [MainWindow.xaml.cs](../src/Questyle.Desktop/MainWindow.xaml.cs) | 页面构建、事件、状态展示、设置差异、日志交互 |
| [DongleController.cs](../src/Questyle.Desktop/Services/DongleController.cs) | 会话串行化、握手、命令、快照、搜索和业务解析 |
| [QuestyleProtocol.cs](../src/Questyle.Desktop/Services/QuestyleProtocol.cs) | QL 封包/解包、校验、IQuestyleTransport 接口 |
| [UsbDkTransport.cs](../src/Questyle.Desktop/Services/UsbDkTransport.cs) | 设备枚举、配置描述符、接管、interrupt I/O 与释放 |
| [HidTransport.cs](../src/Questyle.Desktop/Services/HidTransport.cs) | Windows HID 只读枚举；不承担设置发送 |
| [AppPaths.cs](../src/Questyle.Desktop/Services/AppPaths.cs) / [HeadphoneHistory.cs](../src/Questyle.Desktop/Services/HeadphoneHistory.cs) | 诊断与程序目录解析、已连接耳机记录加载/保存/删除 |
| [App.xaml.cs](../src/Questyle.Desktop/App.xaml.cs) / [SingleInstance.cs](../src/Questyle.Desktop/Services/SingleInstance.cs) / [LanguageResources.cs](../src/Questyle.Desktop/Services/LanguageResources.cs) | 应用启动、托盘生命周期、单实例通知和 language 文化资源解析 |
| [DeviceChangeWatcher.cs](../src/Questyle.Desktop/Services/DeviceChangeWatcher.cs) | 原生 USB/HID 接口插拔通知、设备过滤及通知句柄释放 |
| [HeadphoneAudioMemory.cs](../src/Questyle.Desktop/Services/HeadphoneAudioMemory.cs) | 按耳机保存已确认协议/模式、保存全局麦克风及 Auracast 选择 |
| [DongleVolume.cs](../src/Questyle.Desktop/Services/DongleVolume.cs) | 读取 Dongle 播放端点音量及是否为硬件音量；麦克风切换后按端点类型设置 Windows 新建端点的音量 |
| [UiLanguage.cs](../src/Questyle.Desktop/Services/UiLanguage.cs) / [StartupRegistration.cs](../src/Questyle.Desktop/Services/StartupRegistration.cs) | 应用翻译、语言偏好保存及当前用户开机自启注册 |
| [StyleTransitions.cs](../src/Questyle.Desktop/StyleTransitions.cs) | 当前交互控件的画刷及透明度过渡，保留原样式目标值与绑定 |
| [SmoothScrolling.cs](../src/Questyle.Desktop/SmoothScrolling.cs) | 列表、页面及文本视口的短时滚轮过渡、反向响应与交互中断 |
| [WindowShadow.cs](../src/Questyle.Desktop/WindowShadow.cs) | 独立鼠标穿透阴影及主窗口位置、大小和可见性跟随 |
| [Questyle.Verification](../tests/Questyle.Verification/Program.cs) | 协议自动化检查和可选实机诊断入口 |
| [Questyle.UiVerification](../tests/Questyle.UiVerification/Program.cs) | USB 设置显示、麦克风映射、关闭/退出和跨进程通知检查；离屏渲染，无窗口显示 |
| [build.ps1](../build.ps1) | 验证后生成自带运行时的发布目录和 ZIP |
| [Verify-Package.ps1](../tests/Verify-Package.ps1) | 用隔离数据目录启动发布 EXE 的无窗口检查，验证 runtime 中的 WPF/WinForms/原生运行库加载；不创建托盘、不触发 USB 读取 |
| reverse | 本机 APK、逆向材料、工具及逆向文档，整个目录被 Git 忽略 |
| dist | 仅当前源码的本地发布目录及 ZIP，不保留旧版，不纳入 Git |

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

从当前快照与界面选项计算差异，无差异不接管。需要修改时，新会话重新读取并比较耳机地址；地址变化即停止。协议及模式差异逐次判定 ACK 并回读。麦克风开关最后发送，收到 ACK 就结束旧会话；仅在此次操作期间以只读枚举等待控制 HID 恢复（最多 8 秒），再建立新会话确认；该会话若读到无耳机，按连接流程轮询 30/02 等待原耳机（最多 15 秒）后回读，仍未重连则保留待确认请求并报错。未经匹配回读不保存成功；不新增周期控制会话或自动重发写命令。

### 传输约束

本机成功接口为 MI_01，OUT 01、IN 82，包长上限 64；实际端点由配置描述符选择。帧格式、codec 映射和拒绝响应记录在本机 `reverse/docs/PROTOCOL.md`，此处不重复命令表。

UsbDk 会接管整个复合设备，期间 Windows 音频暂时中断。不得扩大成常驻接管或后台轮询。取消/超时必须完成传输清理后释放固定内存与句柄，结束时 StopRedirect。

## 5. 开发与最小验证

命令在项目根目录执行。优先使用本机 reverse/tools/dotnet 中的便携 SDK；没有该目录时使用已安装的 .NET 8 SDK，将命令前缀替换为 dotnet。

仅编译，不发布。新增原生 PEQ 需要先按 [PEQ 开发说明](PEQ.md) 配置 reverse 中的便携 llvm-mingw；构建不执行 APO 安装：

```powershell
./reverse/tools/dotnet/dotnet.exe build Questyle.sln -c Release
```

影响协议、解析或命令时运行：

```powershell
./reverse/tools/dotnet/dotnet.exe run --project tests/Questyle.Verification -c Release --no-build
```

影响 USB 设置界面行为时运行，数据与日常使用隔离：

```powershell
$previousDataDirectory = $env:QUESTYLE_DATA_DIR
$env:QUESTYLE_DATA_DIR = Join-Path (Get-Location) 'reverse/analysis/ui-verification'
try {
    ./reverse/tools/dotnet/dotnet.exe run --project tests/Questyle.UiVerification -c Release --no-build
} finally {
    if ($null -eq $previousDataDirectory) {
        Remove-Item Env:QUESTYLE_DATA_DIR -ErrorAction SilentlyContinue
    } else {
        $env:QUESTYLE_DATA_DIR = $previousDataDirectory
    }
}
```

`--no-build` 要求相关源码已经编译。协议程序默认不访问 USB；UI 程序只构造组件、启动独立通知测试进程并离屏渲染，不显示窗口。2026-10-25 修正 PEQ COM 聚合后，UI/生命周期 142 项、原生 APO 离线 25 项通过，这些自动检查不安装 APO 或改音频绑定；另按用户授权完成本机端点安装/恢复及数字回环验证，见 [验证记录](VERIFICATION.md)。协议此前 20 项通过，本轮未重跑。离屏 PNG 写入独立测试数据目录。原生验证命令见 [PEQ 说明](PEQ.md)。

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

这些入口使用测试程序指定的 `reverse/tools/usbdk-runtime/.../UsbDkHelper.dll` 路径，并要求系统已有 UsbDk 驱动。不能把它们加入默认构建检查。

## 6. 构建、运行与发布的区别

- `dotnet build`：生成本地编译产物，不替换 dist。
- 开发运行：使用已编译程序或 `dotnet run --project src/Questyle.Desktop -c Release --no-build`。窗口会显示，不能把开发运行混入无窗口组件检查。
- 原根目录启动脚本已删除；本地发布程序入口为 dist/questyle-windows-x64/Questyle.exe。
- `build.ps1`：运行检查、自动结束当前发布目录的 Questyle 进程、清空已验证路径的 dist（不备份旧版本）、执行 win-x64 self-contained publish、整理 runtime 和 language、验证发布程序、生成 ZIP，是发布流程。`-SkipTests` 仅用于本轮已完成相关检查后打包，避免重复测试；不再要求用户手动退出旧程序。本机型号 EQ 文件在打包前暂存，ZIP 仅含空 `eq-config`，完成或失败后恢复本机文件。

本项目没有使用 WinRT API，将 IncludeWindowsSDKRefFrameworkReferences 设为 false，保留目标系统版本并由 SDK 正常解析运行依赖，去掉 Microsoft.Windows.SDK.NET.dll 与 WinRT.Runtime.dll；同时移除已无引用的 logo.png、qms12.png。保留完整 WPF/WinForms 基础运行时，不逐个猜测删除框架 DLL；[WPF/WinForms 不支持常规裁剪](https://learn.microsoft.com/en-us/dotnet/core/deploying/trimming/incompatibilities)。发布检查验证这些依赖和素材不再进入包内。

发布包根部包含 Questyle.exe、runtime、language 和空 eq-config；按 AGENTS.md 不附带 LICENSES、THIRD-PARTY-NOTICES.md、README.md，源仓库中的文件保留。DLL、运行库、图片集中在 runtime，用户提供的图标裁剪后生成 16–256 像素多尺寸 ICO，并用于根 EXE、窗口和托盘。三个应用翻译 JSON 放在 language 根部，通过 `SatelliteResourceLanguages=zh-Hans;zh-Hant` 只发布简繁中文的框架资源程序集，移至 `language/zh-hans` 和 `language/zh-hant`；英语使用框架自带的中性资源，不附带其余 11 种语言资源；LanguageResources 在默认资源解析失败时从指定文化目录加载，发布检查逐一验证 WPF/WinForms 资源的实际路径。SDK CreateAppHost 将根 EXE 绑定到 runtime/Questyle.dll；不引入额外启动进程。AppContext.BaseDirectory 指向 runtime，AppPaths.ApplicationDirectory 取实际 EXE 所在目录，耳机记录保存在该目录。打包清理前暂存已有耳机记录和 data 中的非临时数据，压缩包生成后或任一步失败时均写回本机目录，因此 ZIP 不携带本机耳机信息、日志或缓存。所有发布文件夹（含 assets、codecs 及地区资源目录）均为小写；源素材目录名与程序集文件名不受此规则影响。

2026-10-28 按用户要求清理无用语言资源后运行 build.ps1 -SkipTests 打包，版本保持 0.1.0，用于 GitHub v0.1.0 功能预览发布；无窗口发布检查确认两个地区资源目录、原生 PEQ DLL 的加载及频响计算。后续仅在明确要求发布时使用发布流程。第三方资源说明见 [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md)。

### 版本检查与 CI 发布约定

设置页提供自动更新开关和正式／每日构建通道，保存到 `data/updates.json`；「检查版本更新」按钮位于关于页按钮行最右侧，结果显示在按钮右边。默认开启自动检查并使用正式通道；正常启动时检查一次，以后每 5 分钟检查一次，关闭后手动检查仍可用。切换通道后，无论是否开启自动检查都立即检查新通道。

当前程序带有 DevBuildCommit 的是每日构建，否则视为正式版。所选通道与当前程序所属通道相同时，只提示更新的版本。两者不同时（即切换过通道），直接提示所选通道的最新版本，即使它的版本号更低。已经安装的同一版本（版本号和构建提交都相同）永远不会再次提示，因此每日通道最新的是正式版时，装完不会反复提示。`UpdateChecker` 使用 GitHub Releases API，HTTP 客户端沿用 .NET 默认代理，读取 HTTP_PROXY、HTTPS_PROXY、ALL_PROXY（支持 HTTP／SOCKS）或 Windows 用户代理／PAC；不探测端口、不绕过代理或证书校验。失败写入日志，手动检查显示简短结果。

正式通道只接受带发布 ZIP 的正式 `vX.Y.Z`；每日通道从正式版和 `dev-build-提交前7位` 中选最近公开的版本，构建提交及基准版本分别来自标签和发布说明 `App-Version`；旧的数字编号标签不再识别。程序通过程序集元数据记录 DevBuildCommit、BuildTimestamp。提交哈希没有先后顺序，因此同一基准版本下，提交不同且发布时间晚于当前程序构建时间的每日构建才算更新。发现更新后，侧栏版本号右侧显示红色 New，整行居中；仅检测到更新后，在版本号四周统一 14 DIP 的命中区域悬停，上方显示与侧栏横向居中的圆角阴影浮块，标题为 New version: x.x.x 或 Dev build xxx；显隐使用 100 毫秒淡入淡出，离开后延迟 150 毫秒开始关闭，移入浮块取消关闭。

点击浮块中的更新按钮后执行下载、安装和进程交接（`MainWindow.UpdateFlow.cs`、`UpdateInstaller`）：

1. 旧进程界面元素依次淡出并轻微缩小，窗口可见区域（圆角裁剪）与独立阴影窗口用同一缓动、同一起始帧收缩到居中的 380×160 DIP 更新窗口；三行内容随后依次淡入。HWND 尺寸不变，裁剪外为透明且鼠标穿透。过渡期间界面元素临时使用设备分辨率 BitmapCache（先在静止帧建立缓存再开始动画），阴影只移动预渲染的边角，两个分层窗口逐帧只做合成；思路与后续优化方向见[性能优化](PERFORMANCE.md)。
2. 下载 GitHub 资产 API 地址（`Accept: application/octet-stream`）到 `data/temp/update/package.zip`，30 秒无数据视为超时，完成后校验 GitHub 公布的大小和 SHA-256；Releases 列表中没有 `sha256:` digest 的资产不作为可安装更新。进度每 50 毫秒刷新，大小按 1024 进制显示为两位小数的 KB／MB／GB；数字和单位按最宽字形固定单元绘制，`/` 居中，数值变化不移动整行。
3. 标题切换为「正在安装: 版本」后解压，确认根目录 `questyle-windows-x64/Questyle.exe`。运行时会一直以共享删除权限打开已加载的托管程序集，这类文件可以重命名，但包含它们的目录不能重命名，因此按文件移动：先把旧程序的全部文件按相对路径移到 `previous`，再把新文件逐个放入；`data`、`eq-config`、`paired-headphones.json` 不打包也不替换。替换前已创建启动参数和事件，旧进程此后不再从程序目录加载新文件。任一步失败即逆序恢复。移动文件要求它没有未共享删除权限的打开句柄，因此窗口图标、设备图和编码 Logo 都以 `BitmapCacheOption.OnLoad` 一次读入内存后立即关闭文件。
4. 新进程以 `--update-handoff data/temp/update/handoff.json` 启动，先以透明表面显示，按物理像素把同样内容的更新窗口放到旧窗口相同位置（窗口角到卡片的偏移取整像素，跨 DPI 移动后重新定位，最多 4 次），显示阴影并等待 4 个渲染帧后设置 `Local\Questyle.Windows.Update.<旧进程 PID>` 事件；旧进程收到后立即隐藏并正常退出。新进程等待旧进程退出后才获取单实例锁、托盘与更新检查，后台删除临时目录，再让三行依次淡出、窗口区域伸展到新版本自己的尺寸（中心不变）、界面元素依次淡入，最后恢复普通阴影与交互并执行一次常规前台检测。
5. 新进程退出或 30 秒内未发出事件时，旧进程结束它、恢复旧文件并展开回原界面，状态栏显示简短错误，详情写入日志；新进程交接前的异常写入 `data/update-handoff-error.log`。下载阶段请求退出会取消下载并恢复后退出，安装阶段等待完成或恢复。更新期间暂停自动检查、USB 自动读取和窗口交互。

设置页处于显示状态时，在 600 毫秒间隔内连续点击侧栏顶部标题 8 次，设置页底部显示本次运行有效的「开发者选项」，其中「模拟更新流程」按当前通道生成一个更新（正式版补丁号加 1，每日构建号加 1），自动检查不覆盖它。点击更新后只把下载换成模拟：后台把当前程序目录按发布 ZIP 布局无压缩打包（排除用户数据），进度按约 5 秒随机波动速度增长，总大小为程序文件合计；解压、替换、启动新进程、交接和展开与正式更新相同，因此会真实替换当前程序目录中的文件（内容与当前版本相同）。

[Windows CI](../.github/workflows/windows.yml) 使用 Windows 2022、固定 .NET 8.0.426 与 llvm-mingw 20261020；[依赖准备脚本](../.github/scripts/Prepare-Ci.ps1) 下载公开工具及固定 Win32Metadata 提交的头文件并验证 SHA-256。这些临时文件进入原有 reverse 路径，APK、逆向证据和本机日志不上传。流程依次编译、运行无 USB 协议模拟／无窗口 UI／原生离线检查、打包并验证发布程序；PR 仅保存 ZIP 构建产物。

main 的成功构建以提交哈希前 7 位发布 `Dev build xxx`，真实 Git 标签使用 `dev-build-xxx`，不包含空格；同一提交重复构建时标签已存在，发布明确报错。发布前的草稿还没有 Git 标签，脚本从发布列表中按草稿和目标标签找到它并校验附件。附带 ZIP 的大小及 GitHub SHA-256 验证通过后才公开，再删除其他已公开预发布及旧 dev-build 标签，正式版本和正式标签保留。工作流串行执行，预发布列表跟随新发布时间更新；正式版仍保留 GitHub Latest 标记。正式发布从 Actions 手动运行 Windows CI，channel 选 stable，使用项目中尚未发布的版本号；已存在的标签明确报错，不把新二进制附加到旧源码标签。正式发布保留最近的每日构建，新每日构建成功后再替换它。

云端构建与发布结果见[验证记录](VERIFICATION.md)；真实代理连通性尚未验证。

## 7. 尚未完成及讨论范围

| 项目 | 当前状态 |
| --- | --- |
| 更多实机覆盖 | 搜索/新配对、改名、codec 切换、Auracast 和各模式转换尚未逐项验证 |
| 音频与控制并行 | 未解决，现有方案为短暂独占 |
| 硬件 EQ、大师音效 | 已确认的 APK 流程属于 Wi-Fi 设备；未发现 Dongle USB 硬件 EQ 写入接口。新增 PEQ 为 Windows 端点软件处理，见 [PEQ 说明](PEQ.md) |
| 更多设备设置 | 未完成 |
| 账号、反馈、云端内容 | 未开放；云端初步兼容请求返回 401 decrypt failed |
| 固件 OTA | 有 Qualcomm/Gaia 线索，未移植、未执行固件写入 |
| macOS / Linux | 未实现，需要更换界面层并适配 USB 系统接口 |
| 网页端 | 仅评估过；尚无网页、桥接服务或浏览器实测 |

网页端讨论中的主要约束是当前控制接口属于 HID，普通 WebUSB 对此类接口存在限制；WebHID 又依赖系统 HID，而本机描述符方向与实际报文不一致。纯网页能否完成握手尚未验证。网页加本地辅助程序是候选架构，尚未得到实施授权，也不能消除各系统的 USB 适配工作。

## 8. 文档维护分工

- 本文：当前功能、实现职责、开发与发布入口。
- 本机 `reverse/docs/REVERSE_ENGINEERING.md`：逆向过程、成功方案、证据位置和复现依据，不纳入 Git。
- 本机 `reverse/docs/PROTOCOL.md`：帧格式、业务指令、字段和已知设备差异，不纳入 Git。
- [VERIFICATION.md](VERIFICATION.md)：实际完成的验证、设备和边界，不用计划替代结果。
- [README.md](../README.md)：用户入口与简明使用信息。

修改功能时只同步受到影响的文档，技术解释留在这些文件中，界面内容遵守 AGENTS.md。
