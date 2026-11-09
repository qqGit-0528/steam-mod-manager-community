---
title: "工坊下载去重与跟踪资源释放"
date_created: 2026-11-07
owner: "下载任务用例"
---

# 背景与目标

同一工坊项的订阅回调可能早于 Steam 排队状态。应用需跟踪同一份传输并释放结束或失去响应的任务资源。本规格根据已实现的改动补录。

## 1. 范围与非目标

覆盖桌面、CLI 的原生工坊下载，以及旧版任务历史的兼容。一个工坊项由 Steam 游戏 App ID 和工坊物品 ID 共同标识。“移除任务”是删除应用侧记录。取消 Steam 订阅和删除安装内容属于[删除安装规格](spec-design-installed-mod-removal.md)。

## 2. 需求与验收条件

| ID | 给定 | 当 | 则 |
| --- | --- | --- | --- |
| AC-01 | 同游戏同工坊项已有活跃任务 | 连续点击或并发启动 | 调用方取得同一任务。跨实例锁冲突时第二个实例不进入传输，也不覆写已有历史。 |
| AC-02 | 新订阅已确认、Steam 排队标记尚未出现，或该项已在传输 | 桥接处理状态 | 跟踪 Steam 自行排队的任务，无额外提交。已安装且无待更新时复用内容。 |
| AC-03 | 桥接无响应或传输停滞 | 连接后 1 分钟无桥接消息，或连续 10 分钟无状态与字节进展 | 结束应用跟踪并释放队列，保留 Steam 后台处理。 |
| AC-04 | 下载结束或用户移除任务 | 写完终态或收到迟到事件 | 释放取消资源。迟到快照不会恢复已移除任务，Steam 文件保留。 |

## 3. 责任模块与实现步骤

1. [WorkshopDownloadService](../src/Ck3ModManager.Application/WorkshopDownloadService.cs) 负责活跃任务、队列、状态与取消资源，复用已有任务身份。
2. [SteamClientDownloadLoop](../src/Ck3ModManager.Infrastructure/SteamClientDownloadLoop.cs) 区分新订阅、自行排队和显式更新。[SteamClientWorkshopDownloader](../src/Ck3ModManager.Infrastructure/SteamClientWorkshopDownloader.cs) 管理桥接进程和响应超时。
3. [FileWorkshopDownloadStore](../src/Ck3ModManager.Infrastructure/FileWorkshopDownloadStore.cs) 负责物品锁和历史持久化。[DownloadsViewModel](../src/Ck3ModManager.Desktop/DownloadsViewModel.cs) 展示最新有效快照。

## 4. 契约、依赖与兼容性

沿用任务快照 JSON、原有工坊标识与 Steam 安装目录。移除任务的应用接口从返回是否清理 Steam 内容改为仅等待任务记录移除。旧版自管任务继续清理自身的暂存目录。

## 5. 测试策略

| 验收 | 验证 |
| --- | --- |
| AC-01 | [应用层并发点击](../tests/Ck3ModManager.Tests/WorkshopDownloadServiceTests.cs)与[双实例锁冲突](../tests/Ck3ModManager.Desktop.Tests/SteamClientDownloadsUiTests.cs)。 |
| AC-02、AC-03 | [桥接与 Steam 状态夹具](../tests/Ck3ModManager.Infrastructure.Tests/SteamClientDownloadTests.cs)覆盖延迟排队、已有传输、直接复用、停滞和模拟子进程退出。 |
| AC-04 | [应用层资源释放及迟到历史读取](../tests/Ck3ModManager.Tests/WorkshopDownloadServiceTests.cs)与[桌面文件保留](../tests/Ck3ModManager.Desktop.Tests/SteamClientDownloadsUiTests.cs)。 |

## 6. 审查与执行结果

- 审查重点是锁持有方才写历史，排队与完成来自 Steam，应用侧移除只影响任务记录。
- 原修复在 macOS arm64 完成还原、Release 构建与 `dotnet test --solution SteamModManager.sln --configuration Release --no-build`，559 项通过。隔离副本撤掉四处保护后对应四项回归失败。
- 当前规范化复验结果见同目录的[仓库规范化验收](spec-process-repository-sdd.md)。真实账户新下载与长时间内存采样仍待实机验证。

## 7. 相关资料

[工坊下载使用说明](../docs/workshop-download.md)
