# 第三方组件声明（THIRD-PARTY NOTICES）

匣流 XiaFlow 1.0.0 随程序分发的第三方组件及其许可证：

## 1. 7-Zip 26.06（7z.exe、7z.dll）

- 版权所有 (c) 1999-2026 Igor Pavlov
- 许可证：**LGPL-2.1**，并附加 **unRAR 限制**
- 分发形式：官方安装包中提取的未修改二进制（`engine/x64/7z.exe`、`engine/x64/7z.dll`）
- 许可全文：[`resources/licenses/7-Zip-License.txt`](resources/licenses/7-Zip-License.txt)（随程序分发于 `licenses/7-Zip-License.txt`）
- unRAR 限制摘要：unRAR 源码受 unRAR 授权约束，不得用于重建 RAR 压缩算法。**匣流因此不提供 RAR 创建功能，仅提供 RAR 解压。**
- 源码：https://www.7-zip.org / https://github.com/ip7z/7zip

## 2. SharpCompress 0.50.9

- 版权所有 (c) 2012 Adam Hathcock 与贡献者
- 许可证：**MIT**
- 用途：归档格式识别参考与扩展读取能力
- https://github.com/adamhathcock/sharpcompress

## 3. K4os.Compression.LZ4.Streams 1.3.5（含 K4os.Compression.LZ4、K4os.Hash.xxHash）

- 版权所有 (c) 2019 Miha Zupan, penguinol
- 许可证：**MIT**
- 用途：LZ4 帧格式流式压缩 / 解压
- https://github.com/MihaZupan/K4os.Compression.LZ4

## 4. .NET 10 Runtime / WPF

- 版权所有 (c) .NET Foundation 与 Microsoft
- 许可证：**MIT**
- 自包含分发运行时二进制
- https://github.com/dotnet/runtime

## 5. Windows API（操作系统自带）

- Shell_NotifyIcon、NtSuspendProcess/NtResumeProcess、schtasks.exe、DPAPI（CryptProtectData）
  均为 Windows 操作系统组件，随系统许可分发，匣流仅通过 P/Invoke 调用。

---

以上所有组件以其原始许可再分发。匣流 XiaFlow 不复制任何参考产品（7-Zip、Bandizip、
360压缩、WinRAR、NanaZip、PeaZip）的品牌、商标、图标或受版权保护的代码。
