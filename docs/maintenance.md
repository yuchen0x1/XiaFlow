# 匣流维护指南

## 发布

`main` 经 PR 更新。Windows 构建、测试与 CLI 原生验证均通过后合并。维护者从 `main` 创建与 `Directory.Build.props` 一致的 `vX.Y.Z` 标签。开始前更新 README、发行说明及平台变更记录。紧急修复也走同一发布链。

Release 工作流重新执行 NuGet 审计、构建、测试、CLI 矩阵和 Windows 安装升级。Windows 安装和便携包使用同一发布目录，公开包排除应用 PDB。GitHub `release` 环境仅接受版本标签，构建过程不接收签名密钥。发布作业签署元数据并对照 GitHub 返回的每个资产摘要，完整上传后把草稿转为不可变 Release。遇到失败先检查 tag 和草稿，只有未公开草稿可补齐，已发布版本通过新补丁版本修正。

```powershell
pwsh -File scripts/validate.ps1
pwsh -File scripts/smoke-cli.ps1
pwsh -File scripts/package.ps1 -Installer
pwsh -File scripts/check-release.ps1
```

发行流程维护时核对 Inno Setup 和 7-Zip 的版本、引擎来源和固定摘要。7-Zip 升级时同步替换 Windows 引擎、Unix smoke 测试来源、相应源码资产和检查清单。小版本可以使用相同引擎。

## 更新信任

安装版从 GitHub 读取候选版本。只有官方仓库稳定版有应用内安装入口。下载的 RSA-PSS/SHA-256 分离签名由内嵌公钥验证，签名 JSON 绑定版本、平台、架构、最低 Windows 版本、资产名称、字节数和 SHA-256。客户端验证 HTTPS 资产主机、字节数和文件摘要后启动 Inno Setup，安装成功后重新打开原路径的应用。运行中的任务阻止退出升级。

本次使用 GitHub `release` 环境密钥 `XIAFLOW_UPDATE_SIGNING_KEY` 签署元数据。公钥在 `src/XiaFlow.Core/Update/public-key.txt`。维护者用户下的 Windows DPAPI 加密恢复副本位于 `%LOCALAPPDATA%\XiaFlowRelease\update-signing-key.dpapi`。该副本依赖原账户的 DPAPI 凭据，应由维护者纳入加密备份。签名私钥只在受保护的发布环境及维护者恢复流程解封。公开仓库保留公钥。

密钥丢失或疑似泄漏时撤下发布环境密钥，并通过可信渠道告知用户暂停应用内升级。轮换需要可信地分发包含新公钥的客户端。GitHub 发布证明与项目更新签名均有独立校验路径。Windows 手动安装仍可能显示 SmartScreen，项目当前无 Authenticode 证书。

## 升级失败与恢复

v1.0.0 用户应手动运行 v1.1.0 安装包一次。便携版和 CLI 对照 `SHA256SUMS.txt` 或 `gh release verify-asset TAG FILE` 后，把新版解包到新目录。验证通过再替换原来的启动路径，保留旧目录可快速手动恢复。

安装版失败时保留 `%APPDATA%\XiaFlow` 和备份归档。先备份这些数据，再重跑同版本官方安装程序。若需降级，核对旧包校验值后使用已知可用版本重新安装。此版本沿用原 JSON 数据格式。应用当前提供手动回退流程，安装中断、重启失败和系统策略拦截需要结合日志诊断。

## 日常检查

- Dependabot 每周检查 NuGet 和 Actions。先审查变更与许可证，再合入通过构建和测试的 PR。
- NuGet Audit 检查直接及传递依赖，CodeQL 分析 C#，PR Dependency Review 检查引入的依赖。GitHub 私密安全报告处理问题详情。
- Maintenance 每周检查最新发布、资产链接、GitHub 发布证明和更新签名。每月保存 30 个参考项目的 GitHub 活动快照。大版本发版前用快照复查 [竞品与技术调研](research.md)。
- 测试、平台与发布状态记录见 [交付报告](delivery.md) 和 [GitHub Actions](https://github.com/yuchen0x1/XiaFlow/actions)。
