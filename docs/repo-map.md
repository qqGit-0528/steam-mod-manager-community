# 仓库导航

按任务找到真实入口与对应验证。模块依赖和数据归属见[架构约定](architecture.md#架构约定)，执行命令见[协作规范](../CONTRIBUTING.md#4-必须完成的检查)。

| 工作范围 | 现有入口和责任模块 | 回归入口 |
| --- | --- | --- |
| 工坊搜索、详情、封面与版本筛选 | [WorkshopViewModel](../src/Ck3ModManager.Desktop/WorkshopViewModel.cs)、[WorkshopService](../src/Ck3ModManager.Application/WorkshopService.cs)、[SteamWorkshopBrowser](../src/Ck3ModManager.Infrastructure/SteamWorkshopBrowser.cs)、[工坊缓存说明](workshop-cache.md) | [WorkshopCacheFlowTests](../tests/Ck3ModManager.Desktop.Tests/WorkshopCacheFlowTests.cs)、[StrictWorkshopFilteringTests](../tests/Ck3ModManager.Desktop.Tests/StrictWorkshopFilteringTests.cs) |
| 下载、重试、任务移除 | [DownloadsViewModel](../src/Ck3ModManager.Desktop/DownloadsViewModel.cs)、[WorkshopDownloadService](../src/Ck3ModManager.Application/WorkshopDownloadService.cs)、[SteamClientWorkshopDownloader](../src/Ck3ModManager.Infrastructure/SteamClientWorkshopDownloader.cs) | [WorkshopDownloadServiceTests](../tests/Ck3ModManager.Tests/WorkshopDownloadServiceTests.cs)、[SteamClientDownloadTests](../tests/Ck3ModManager.Infrastructure.Tests/SteamClientDownloadTests.cs) |
| 路径发现、本地扫描与已安装 Mod 删除 | [WorkspaceService](../src/Ck3ModManager.Application/WorkspaceService.cs)、[LocalModLibrary](../src/Ck3ModManager.Application/LocalModLibrary.cs)、[SteamGameLocator](../src/Ck3ModManager.Infrastructure/SteamGameLocator.cs)、[Ck3ModScanner](../src/Ck3ModManager.Infrastructure/Ck3ModScanner.cs)、[安装分类](../src/Ck3ModManager.Infrastructure/ModInstallationClassifier.cs)、[InstalledModRemover](../src/Ck3ModManager.Infrastructure/InstalledModRemover.cs) | [InstalledModRemoverTests](../tests/Ck3ModManager.Infrastructure.Tests/InstalledModRemoverTests.cs)、[DesktopTests](../tests/Ck3ModManager.Desktop.Tests/DesktopTests.cs) |
| 配置导入、播放集与启动游戏 | [ProfileImportService](../src/Ck3ModManager.Application/ProfileImportService.cs)、[GameActivationViewModel](../src/Ck3ModManager.Desktop/GameActivationViewModel.cs)、[WorkspaceService](../src/Ck3ModManager.Application/WorkspaceService.cs)、[配置应用说明](game-mod-activation.md) | [WorkspaceServiceTests](../tests/Ck3ModManager.Tests/WorkspaceServiceTests.cs)、[桌面测试](../tests/Ck3ModManager.Desktop.Tests/) |
| 应用接线、命令行、发布 | [桌面组合根](../src/Ck3ModManager.Desktop/App.axaml.cs)、[CLI](../src/Ck3ModManager.Cli/Program.cs)、[本地脚本](../scripts/)、[三平台 CI](../.github/workflows/build.yml) | [解决方案](../SteamModManager.sln)、[全部测试](../tests/) |

## 规格记录

- [工坊下载去重与跟踪资源释放](../spec/spec-design-workshop-download-lifecycle.md)
- [删除已安装 Mod](../spec/spec-design-installed-mod-removal.md)
- [统一本地 Mod 刷新与当前安装状态](../spec/spec-design-local-installation-state.md)
- [区分工坊来源与安装管理方式](../spec/spec-design-mod-installation-management.md)
- [配置导入与启动准备归属应用层](../spec/spec-design-profile-workflows.md)
- [下载卡片显示 Mod 兼容性](../spec/spec-design-download-compatibility-label.md)
- [本次仓库规范化的验收](../spec/spec-process-repository-sdd.md)
