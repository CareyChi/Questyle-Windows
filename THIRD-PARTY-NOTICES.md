# 第三方组件与资源

- **UsbDkHelper.dll 1.0.22 x64**：来自 [daynix/UsbDk](https://github.com/daynix/UsbDk/releases/tag/v1.00-22)，Apache License 2.0，Copyright Red Hat, Inc. / Daynix Computing LTD。发布包仅包含用户态 Helper DLL；没有包含或自动安装驱动。
- **.NET 8 / Windows Desktop Runtime**：来自 Microsoft，[.NET 许可与第三方声明](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT)。相应的运行时许可及第三方声明位于 LICENSES 中的 NET-* 文件。
- **Questyle 名称与图片**：从用户提供的 APK 中提取，用于本地移植兼容。相关商标和原始图片权利归原权利人所有；本项目不是 Questyle 官方发布。
- **编解码 Logo**：LDAC、Qualcomm aptX / aptX HD / aptX Adaptive、LHDC、AAC、LE Audio 和 SBC 图片均来自同一 APK 的 `assets/img/home`，用于标识当前编解码，权利归各原权利人所有。应用只裁去透明边距并按主体调整显示大小，保留原始图片。

分析工具不包含在发布包：JADX（Apache 2.0）、AOTopsy（BSD 3-Clause，基于 unflutter）。工具源码与版本证据保存在本机 reverse/tools 目录。

Apache License 2.0 全文：`LICENSES/Apache-2.0.txt`。

- `Assets/Questyle.ico`：使用用户提供的 Questyle 图标图片裁剪并生成多尺寸 ICO，保留原字样；本机原图保存在 reverse/analysis/icon/source.png。
