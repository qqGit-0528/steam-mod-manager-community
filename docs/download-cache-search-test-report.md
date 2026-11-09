# 下载、缓存与搜索测试汇总

2026-11-07 · 当前工作区 · macOS 15.6 arm64 · Apple M4 Pro / 24 GiB · .NET SDK 10.0.403 · Release

## 全量回归

| 测试项目 | 通过 | 失败 | 跳过 | 测试项目耗时 |
| --- | ---: | ---: | ---: | ---: |
| Core / Application | 183 | 0 | 0 | 3.322 秒 |
| Infrastructure | 275 | 0 | 0 | 4.576 秒 |
| Desktop | 117 | 0 | 0 | 2.550 秒 |
| **全量** | **575** | **0** | **0** | **4.874 秒** |

三个测试项目并行执行。按测试类归档的专项用例共 390 项，另有 14 项相邻回归。计数包含参数化用例和少量集成测试，可用于定位覆盖范围。

| 范围 | 用例数 | 已覆盖行为 |
| --- | ---: | --- |
| 下载 | 77 | 同一 Mod 并发合并、已安装内容复用、取消与重试、终态落盘、校验与安装、历史删除、桌面任务状态 |
| 缓存 | 87 | 新鲜与过期、并发回源合并、磁盘重读与损坏降级、容量驱逐、封面重验证、界面旧缓存与新结果切换 |
| 搜索和详情 | 226 | 查询构造与编码、Steam 页面解析、页码与筛选、版本分组、详情识别、界面交互及封面请求边界 |
| 相邻回归 | 14 | 安装状态同步、文件监听及删除已安装 Mod |

同一 Mod 的并发下载在离线条件下没有复现重复启动。探针先让 Steam 下载替身阻塞，再向活动任务追加 100 个并发请求。两轮均返回同一任务对象，首次完成前只调用替身一次。应用自管下载并发合并、匹配修订复用、取消后释放句柄、Steam 下载服务去重这 4 项既有回归各复跑 10 轮，40 次全部通过。真实 Steam 客户端传输和跨进程并发下载未实测。 [应用层下载测试](../tests/Ck3ModManager.Tests/WorkshopDownloadServiceTests.cs#L12) · [Steam 下载服务回归](../tests/Ck3ModManager.Desktop.Tests/SteamClientDownloadsUiTests.cs#L33)

## 可复现的性能问题

P50、P95 为两轮各自统计值的范围，单位为毫秒。分配量是每次操作的进程级托管分配中位数。

| 优先级 | 路径 | 测量结果 | 代码行为 |
| --- | --- | --- | --- |
| 高 | 下载历史全量读取 | 100 条 P50 **1.92–1.94**，1,000 条 **20.35–21.84**，5,000 条 **84.21–85.16**。5,000 条 P95 **88.60–92.18**，单次分配 **10.10 MiB**，三组均返回 100 条 | 每次遍历所有游戏的历史 JSON，逐个反序列化，最后才取最近 100 条。界面加载历史经过此入口。 [历史读取](../src/Ck3ModManager.Infrastructure/FileWorkshopDownloadStore.cs#L216) · [界面入口](../src/Ck3ModManager.Desktop/DownloadsViewModel.cs#L247) |
| 高 | 已完成下载任务在会话中持续留存 | 不同 Mod 从 100 个增至 1,000 个时，同一服务持有的终态任务从 **100 个增至 1,000 个**。每 1,000 次查询最后一个任务的 P50 从 **0.18–0.19** 增至 **1.60–1.61** | 结束时从活动索引移除，终态任务仍保存在列表，Steam 最新快照也保存在索引中。界面的下载通知经由线性 `IsCurrent` 查询。此处确认了对象留存与查询放大，尚未量化常驻内存。 [任务查询](../src/Ck3ModManager.Application/WorkshopDownloadService.cs#L60) · [任务收尾](../src/Ck3ModManager.Application/WorkshopDownloadService.cs#L315) · [界面通知](../src/Ck3ModManager.Desktop/DownloadsViewModel.cs#L190) |
| 中 | 强制刷新逐条回写详情 | 30 条结果从无详情缓存到全部已缓存时，P50 **1.52–1.70 → 13.06–15.45**，约 **9 倍**。后者 P95 **15.08–17.46**，每次分配 **2.29 MiB** | 刷新发出两份页面，合计读详情 90 次。即使远端替身返回相同修订，已有详情仍触发 30 次详情写入，每次写入都持锁执行缓存维护，另有一次页面写入。 [刷新流程](../src/Ck3ModManager.Application/WorkshopService.cs#L78) · [详情写入](../src/Ck3ModManager.Infrastructure/FileWorkshopCacheStore.cs#L108) |
| 中 | 元数据首次读等待全目录维护 | 读取同一缓存页，目录中有 0、16、64 条详情时，P50 分别为 **0.22–0.24**、**0.58–0.64**、**1.53–1.82**。64 条分配约 **1.30 MiB** | 新存储实例的首次读取等待维护。首次过期扫描打开并解析现有文件，后续写页即使在过期检查间隔内也枚举目录。稳态写页 P50 从 **0.33–0.38** 增至 **0.54–0.56**。夹具规模在 64 条详情上限内。 [维护路径](../src/Ck3ModManager.Infrastructure/FileWorkshopCacheStore.cs#L272) · [容量常量](../src/Ck3ModManager.Core/WorkshopCache.cs#L6) |
| 低 | 封面内存命中仍复制整图 | 32 KiB 与 2 MiB 载荷每次分别分配 **33,512 B** 与 **2,097,896 B**，2 MiB P50 **0.08–0.13**，命中未请求 HTTP 替身 | 返回完整副本以隔离调用方对数组的修改。大图频繁访问可能增加 GC 压力，本轮只测量每次分配。 [封面返回](../src/Ck3ModManager.Infrastructure/CachedWorkshopImageSource.cs#L61) · [缓冲区隔离测试](../tests/Ck3ModManager.Infrastructure.Tests/WorkshopPreviewCacheTests.cs#L17) |

热搜索的 30 条结果每次仍检查 30 次详情。生产文件缓存 P50 **0.39–0.44 ms**，纯内存存储 **0.003–0.008 ms**。已缓存的详情读取会查询文件时间戳，命中也会更新访问顺序。该样本的绝对耗时较小。30 条合成结果的 HTML 解析 P50 **0.54–0.58 ms**，增加 512 KiB 页面注释后分配量仍约 **61 KiB**。这个解析样本未发现明显放大。 [列表补齐](../src/Ck3ModManager.Application/WorkshopService.cs#L241) · [文件缓存命中](../src/Ck3ModManager.Infrastructure/FileWorkshopCacheStore.cs#L174) · [HTML 解析](../src/Ck3ModManager.Infrastructure/SteamWorkshopBrowser.cs#L73)

## 较慢用例的耗时口径

TRX 时长包含夹具与等待。下载、缓存、搜索三组的用例时长中位数分别为 4、2、0 ms。0 ms 是记录精度下的结果。

| 用例 | TRX 耗时 | 实际覆盖 |
| --- | ---: | --- |
| `UnchangedDirectoriesDoNotCauseAdditionalScans` | 457 ms | 验证目录无变化时不重复扫描，含显式等待及监听防抖。 [测试](../tests/Ck3ModManager.Infrastructure.Tests/FileSystemLocalModMonitorTests.cs#L8) |
| `SteamItemHistoryRemovalCanRunForDifferentItemsConcurrently` | 308 ms | 20 轮各生成 24 条磁盘历史，再并行删除，耗时包括夹具落盘。 [测试](../tests/Ck3ModManager.Infrastructure.Tests/SteamClientDownloadTests.cs#L15) |
| `DiskBudgetEvictsOldDocuments` | 303 ms | 先创建 35 个约 1 MiB 的元数据文件，再验证容量驱逐。 [测试](../tests/Ck3ModManager.Infrastructure.Tests/WorkshopDiskCacheTests.cs#L231) |
| `LastModifiedIsUsedWhenThereIsNoEntityTag` | 276 ms | 包含临时目录缓存写入、HTTP 替身和条件重验证。 [测试](../tests/Ck3ModManager.Infrastructure.Tests/WorkshopPreviewCacheTests.cs#L158) |
| `SilentConnectedBridgeTimesOutAndReceivesStop` | 197 ms | 测试刻意让桥接替身保持静默以覆盖超时。 [测试](../tests/Ck3ModManager.Infrastructure.Tests/SteamClientDownloadTests.cs#L289) |
| `FilterArrowShowsAllChoicesAndSelectionAndTypingReachTheSearch` | 168 ms | 包含 Avalonia 窗口、布局与输入事件。 [测试](../tests/Ck3ModManager.Desktop.Tests/WorkshopViewModelTests.cs#L56) |

## 复现与边界

两轮探针各启动一个 Release 进程，共 23 个场景。每个场景先预热 3 次，再顺序测量 24 或 30 次，每轮保留 636 条样本。用 `Stopwatch` 计墙钟时间，P95 取排序后第 `ceil(0.95 × n)` 个样本，`GC.GetTotalAllocatedBytes(true)` 的前后差值包含异步续体的分配。文件夹具位于临时目录，元数据冷读每次创建新存储实例，操作系统文件缓存保持正常工作。搜索回源和封面 HTTP 使用固定替身，封面载荷只测字节交付，未做解码或渲染。

覆盖缺口包括真实 Steam 下载速率、真实用户历史规模、Windows/Linux 的磁盘性能、长时间运行后的托管堆和 RSS。短期分配与任务对象数量应分别跟踪。性能样本作为本机基线保留，未设置跨机器耗时门禁。本轮变更为诊断文档及本地测量夹具。

从项目根目录执行。汇总脚本统计 `trx` 目录中的全部文件，每批回归应归档上一批 TRX 后再采集。

```sh
audit=artifacts/download-cache-search-audit
dotnet restore SteamModManager.sln
dotnet build SteamModManager.sln --configuration Release --no-restore
dotnet test --solution SteamModManager.sln --configuration Release --no-build \
  --no-ansi --no-progress --report-xunit-trx --results-directory "$PWD/$audit/trx"
dotnet build "$audit/probe/Probe.csproj" --configuration Release
for run in 1 2; do
  dotnet "$audit/probe/bin/Release/net10.0/Probe.dll" \
    > "$audit/probe-run-$run.csv" 2> "$audit/probe-run-$run-session.log"
done
python3 "$audit/summarize.py" > "$audit/analysis.txt"
git diff --check
```

本机证据目录 `artifacts/download-cache-search-audit/` 由 Git 忽略。里面保留 3 份 TRX、575 条用例时长、两轮采样、会话留存与专项复跑日志，以及可重跑的探针源代码。 [Spec](../spec/spec-process-download-cache-search-tests.md)

## 修复后复测

同机运行两轮，每轮 27 个场景、732 条样本。全量 Release 回归 **581 项通过，0 失败、0 跳过**，其中新增 6 项回归。

| 场景 | 修复前 P50 | 修复后 P50 | 同口径变化 |
| --- | ---: | ---: | --- |
| 读取 5,000 条新历史，返回最近 100 条 | 84.21–85.16 ms | 11.76–13.11 ms | 每次托管分配从 10.10 MiB 降至 3.25 MiB。枚举文件元数据后，只解码可能进入前 100 的记录。 |
| 读取 5,000 条旧历史 | 84.21–85.16 ms | 首次 71.15–73.09 ms，重复读取 10.68–11.03 ms | 旧文件仍按 JSON 时间排序。首次解析在工作线程执行，后续按文件元数据核验缓存。 |
| 完成 1,000 项后查询 1,000 次任务当前状态 | 1.60–1.61 ms | 0.032–0.033 ms | 终态留存从 1,000 个降为 100 个。通知查询通过任务 ID 定位，活动任务独立保留。 |
| 强制刷新 30 条已缓存详情 | 13.06–15.45 ms | 1.64–1.78 ms | 相同修订的详情写入从 30 次降为 0 次，仍写入一份查询页。 |
| 新存储首次读取页，目录已有 64 条详情 | 1.53–1.82 ms | 0.31 ms | 预算内仅检查目录容量和目标内容。发生超限时继续清理并淘汰。 |
| 2 MiB 封面内存命中 | 可修改数组每次分配 2,097,896 B | 只读流每次分配 712 B | 桌面解码入口改用只读流。需要可修改数组的原入口仍复制，以保持缓存隔离。 |

下载去重探针在两轮均确认，同一 Mod 的 100 个追加并发请求复用一个活动任务。旧记录无需迁移。排序历史的读取仍受目录文件数量影响，旧记录在新存储实例首次读取时需要解析。封面数据测量截止于只读流交付。真实 Steam 联调、长期 RSS 和其他平台性能待验证。

复测原始结果、探针和 TRX 位于本机 `artifacts/download-cache-search-fix/`。构建沿用解决方案的 Release 门禁。 [下载历史与任务留存规格](../spec/spec-design-download-history-performance.md) · [工坊缓存规格](../spec/spec-design-workshop-cache-performance.md)
