# 匣流 v1.1.0 交付报告

## A. 原项目与改造

基线为 `7984cfe`，公开仓库 `yuchen0x1/XiaFlow`，默认分支 `main`，许可证 MIT。v1.0.0 已发布 Windows x64 安装版和便携版。原应用以 .NET 10 WPF 提供首页、快速压缩/解压、批量分析、归档浏览、备份、恢复、任务和历史、设置、关于页面。归档由 7-Zip 独立进程与 K4os LZ4 实现。Windows 托盘、经典右键菜单、单实例管道、注册表和计划任务都在本地工作。应用无 Web API 或数据库。配置、任务、历史、备份清单为 JSON，密码库以 DPAPI 加密。安装与便携版沿用用户数据路径和原存储格式。

原有 98 项自动测试通过，实机启动 Windows 主界面。主要技术债务包括发布流程缺 PR CI、版本重复定义、更新只读 Release Notes 首个 SHA-256、共享核心绑定 Windows、归档页单文件操作的空列表。保留既有 GUI 工作流，添加通用核心目标和命令行入口，补上签名更新与发布门禁。

## B. 平台评估

A 为直接交付，B 为有明确价值且需专项适配，C 为目前收益较低，D 为受系统限制。评估参考实际原生入口、运行时、权限和维护成本。[竞品与技术调研](research.md)

| 平台 | 判定 | 核心、权限和交互 | 成本、分发和更新 |
| --- | --- | --- | --- |
| Windows 11 x64 | A 桌面、CLI | WPF、拖放、Shell、托盘、计划任务和 DPAPI。CLI 走非交互命令 | 现有技术栈，安装版签名更新，便携版和 CLI 版本 ZIP |
| Windows 10 x64 | A 兼容目标 | 最低内部版本 17763。受系统生命周期和实际硬件影响 | 沿用同包。需在仍受支持的 Windows 10 发行服务渠道独立验证 |
| Windows ARM64 | C 原生 | WOW/仿真与原生 7-Zip、Shell 集成需分别验收 | 增加 ARM64 包和 Runner，当前桌面只发 x64 |
| Linux 通用 | B CLI | 本地文件和 7zz，同一归档逻辑；桌面托盘、DPAPI、任务计划需重做 | CLI 走 .NET 与官方引擎、ZIP 分发；GUI 另评估 |
| Ubuntu | B CLI | x64 Runner 覆盖 Ubuntu 24.04。目录权限按 Unix 执行 | .NET 10 与 7zz 可单独安装，版本 ZIP |
| Debian | B CLI 兼容目标 | 可复用 Linux CLI。运行时、glibc、7zz 需按版本核对 | 共享版本 ZIP，发行版专项未验证 |
| Fedora | B CLI 兼容目标 | CLI 不依赖 WPF。需核对运行时及 SELinux 环境 | 共享版本 ZIP，RPM 暂无 |
| Arch Linux | B CLI 兼容目标 | 滚动系统需验证 .NET 与引擎兼容 | 共享版本 ZIP，发行版专项未验证 |
| macOS Intel | B CLI | 原生 x64 .NET/7zz。Finder、Keychain、系统权限需桌面专项 | CLI ZIP，DMG/PKG 和签名公证需额外身份与验收 |
| macOS Apple Silicon | B CLI | 原生 arm64 .NET/7zz。无需复用 WPF 窗口 | CLI ZIP，通过同一版本维护 |
| Linux/macOS 图形桌面 | C | 需要重新设计文件选择、权限、安全存储、调度和平台 UI | AppImage/DEB/RPM/Flatpak/DMG 需独立长期维护 |
| Android 手机 | C | SAF 文档提供器、电池与后台限制，WPF/7z.exe 无法迁移 | 需专用引擎和原生界面，APK/商店签名与更新 |
| Android 平板 | C | 同手机文件权限，另需分栏、触控和大屏验证 | 依赖 Android 产品基础，独立 UI 测试 |
| Android 折叠屏 | C | 同 Android，需窗口变化与多实例文件选择 | 需真机及姿态测试 |
| iOS | C | 文档选择器与沙盒适合手动归档，长期后台备份受限 | Swift 界面、签名、App Store 审核和发布权限 |
| iPadOS | C | 文件选择、多任务和拖放需要独立产品设计 | 同 iOS 分发，需额外大屏验收 |
| 桌面 Web | C | 局部可在浏览器处理，持续本地备份和系统集成受限 | 全功能需服务端隔离、认证、配额和部署 |
| 移动 Web | C | 浏览器文件权限及内存、后台生命周期更紧 | 无现成 Web 产品可发布 |
| 响应式 Web | C | 需新界面与服务/API，窗口缩放无法替代系统能力 | 随 Web 专项维护 |
| PWA | C | 可离线浏览和手动归档，Service Worker 不提供持续可靠的本地文件备份 | 需要独立缓存、导入和版本迁移方案 |
| Chrome 扩展 | D | 扩展无法覆盖系统备份、全盘文件和 Windows Shell | Native Messaging 仍依赖独立桌面宿主 |
| Edge 扩展 | D | 与 Chrome 同类限制 | 增加商店流程却只提供浏览器子功能 |
| Firefox 扩展 | D | 浏览器文件访问和宿主权限不适合该桌面工作流 | 独立扩展分发和适配，收益不足 |
| Docker | C | 适合 CLI 批处理，挂载、UID、权限和数据保护需额外配置 | CLI 已提供，镜像与签名另评估 |
| NAS | C | 需匹配厂商 CPU、包体系、计划任务和存储权限 | Linux CLI 是移植基础，无 NAS 原生包 |
| 私有化 | C | CLI 可在本地运行，服务化涉及授权和持久任务 | 需明确服务模型后维护 |
| 云服务器 | C | 非交互 CLI 可使用。公网服务需私有数据与任务隔离 | 无云服务发布和运维依赖 |
| CLI | B 已实现 | Windows/Linux/macOS 共享归档模型。文件清单可输出 JSON，操作靠退出码和取消 | 一个 framework-dependent ZIP，依赖 .NET 10 与同架构 7-Zip |

## C–H. 实施与验收口径

- 实现与文档。Windows WPF 更新 UI 加入验证、进度、速度、取消、详情、通知去重与跳过。CLI 支持 ZIP/7z 写入和解压、文件清单、校验及 SHA-256。先检查归档清单、存储空间与目标，再用暂存目录完成解包。
- 仓库基础设施。CI 跑 Windows 构建、测试和原生 CLI 矩阵，Security 跑 CodeQL 和依赖审查，Dependabot 与 Maintenance 周期运行。Issue 表单、PR 模板和私密漏洞报告已配套。详情见 [维护指南](maintenance.md)。
- 自动更新。官方 Releases 的签名元数据约束资产。旧 v1.0.0 用户手动迁移一次，后续验证签名、包长度及哈希后启动 Inno 向导。可手动回退并保留数据。现有发布签名为项目更新签名，Authenticode 待证书。
- 本机验证。原版测试 98/98。改造后运行完整 Windows 套件 112/112，Windows CLI 的 ZIP/7z 中文路径、内容摘要和已存在目录保护通过。远程独立运行记录见 [Actions](https://github.com/yuchen0x1/XiaFlow/actions)。
- 发布证据。安装器、便携包、跨平台 CLI、校验表、签名元数据、引擎对应源码在 [Releases](https://github.com/yuchen0x1/XiaFlow/releases) 中随标签提供。真实远程状态以 Release 及对应 Actions 为准。第一版内置新公钥，未来新标签才能进行已发布客户端的在线签名升级闭环验收。
- 维护证据。GitHub Security、CI、CLI、Release 与 Maintenance 分别有可查看记录，正向流程与失败排查见维护指南。独立 Windows 10、Debian、Fedora、Arch、SmartScreen、远程密钥轮换和自动失败回滚仍属待验证或未实现项。

## 逆向暴露面

1. **总览**。基线扫描为 15 个文件、9 个 PE，评分 62/100（D，表示代码可读程度）。这是开源产品，保留 .NET 托管程序集以保持可构建和可调试。
2. **发现**。PE-PDB（高，CodeView PDB 路径）；PE-SIGN（高，应用和安装器无 Authenticode）；CLR-ASM（高，公开的托管 IL 可读）；PE-CFG（低，启发式命中需按编译器与二进制核对）。
3. **证据与修复**。基线构建目录有项目 PDB 及第三方 CodeView。发布命令使用 `DebugType=none`，包内检查拒绝 PDB。第三方 PE 保持原样，项目签署 Windows 更新元数据。Windows 系统签名须另行使用正式证书。
4. **客户端无法保密的内容**。更新公钥、引擎参数和本地业务逻辑都随应用分发，允许查阅。签名私钥留在受保护的发布环境，客户端只持公钥。产品无需托管授权或收费判定。
5. **修复顺序**。先隔离私钥、启用发布资产锁定，再验证更新签名和出包边界。剩余目标为 Authenticode、密钥轮换演练与真实旧客户端升级闭环。
