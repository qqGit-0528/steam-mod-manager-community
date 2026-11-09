---
title: "删除已安装 Mod"
date_created: 2026-11-07
owner: "本地安装管理用例"
---

# 背景与目标

用户需要在本地 Mod 库删除磁盘上的安装。Steam 工坊安装还需取消订阅，以免重新下载。本规格根据已实现的改动补录。

## 1. 范围与非目标

覆盖已扫描到的本地目录、压缩包、旧版自管修订以及支持 Steam 桥接的工坊安装。安装 ID 标识选中的内容路径，工坊 ID 标识订阅。删除单个选中安装，关联配置由用户自行调整。下载任务移除见[下载规格](spec-design-workshop-download-lifecycle.md)。

## 2. 需求与验收条件

| ID | 给定 | 当 | 则 |
| --- | --- | --- | --- |
| AC-01 | 已安装列表选中有效 Mod | 点击删除、取消或切换选择 | 确认区展示目标名称与路径，只有点击独立的确认按钮才开始删除。 |
| AC-02 | 目标位于受支持的本地安装路径 | 确认并重新扫描匹配成功 | 删除选中内容和指向它的本地包装描述文件，刷新列表并保留相邻 Mod。 |
| AC-03 | 目标位于受支持的 Steam 工坊目录 | 取消订阅失败、未确认或物品锁冲突 | 应用报告失败并跳过磁盘删除。确认成功且无活跃传输后才由应用清理残留。 |
| AC-04 | 目标在受支持目录外、经过目标符号链接或包含另一已安装项 | 确认删除 | 拒绝操作并保留文件。 |

## 3. 责任模块与实现步骤

1. [InstalledModRemovalViewModel](../src/Ck3ModManager.Desktop/InstalledModRemovalViewModel.cs) 管理选中项与独立确认，结果交给现有列表刷新流程。
2. [WorkspaceService](../src/Ck3ModManager.Application/WorkspaceService.cs) 在当前游戏目录重新扫描，核对身份、路径和嵌套安装。
3. [InstalledModRemover](../src/Ck3ModManager.Infrastructure/InstalledModRemover.cs) 负责路径约束、物品锁和文件副作用。[SteamClientWorker](../src/Ck3ModManager.Infrastructure/SteamClientWorker.cs) 确认对应游戏和物品的订阅状态。

## 4. 契约、依赖与兼容性

新增 Core 端口 `IInstalledModRemover` 和内部 Steam 桥接取消订阅命令。继续沿用已有安装身份、物品锁、配置和历史格式。未接入桥接的游戏由 Steam 客户端处理工坊移除。部分未被扫描器识别的旧版工坊容器也通过 Steam 管理。

## 5. 测试策略

| 验收 | 验证 |
| --- | --- |
| AC-01、AC-02 | [Avalonia 确认与真实临时文件删除](../tests/Ck3ModManager.Desktop.Tests/DesktopTests.cs)，以及[目录、压缩包和包装描述文件夹具](../tests/Ck3ModManager.Infrastructure.Tests/InstalledModRemoverTests.cs)。 |
| AC-03 | [物品锁和文件保留](../tests/Ck3ModManager.Infrastructure.Tests/InstalledModRemoverTests.cs)，以及[取消订阅回调与下载标记](../tests/Ck3ModManager.Infrastructure.Tests/SteamClientDownloadTests.cs)。 |
| AC-04 | [外部路径与目标符号链接夹具](../tests/Ck3ModManager.Infrastructure.Tests/InstalledModRemoverTests.cs)。嵌套安装拒绝路径已审查，尚无专门夹具。 |

## 6. 审查与执行结果

- 审查重点是独立确认、重新核对路径、取消订阅成功后才进行文件操作，以及配置仍由原有入口维护。
- 原实现于 macOS arm64 完成还原、Release 构建与 `dotnet test --solution SteamModManager.sln --configuration Release --no-build`，567 项通过。已查看确认界面的无窗口渲染。
- 当前规范化复验结果见[仓库规范化验收](spec-process-repository-sdd.md)。真实 Steam 账户取消订阅和各平台锁定文件行为仍待实机验证。

## 7. 相关资料

[删除已安装 Mod 的操作说明](../docs/workshop-download.md#删除已安装的-mod)
