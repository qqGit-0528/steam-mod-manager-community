> 2026-11-01 更新：下文保留改造前的历史诊断，当前已实现的对象和图片缓存见[工坊缓存与加载流程](workshop-cache.md)。

# 创意工坊读取性能诊断

诊断日期：2026-10-28。问题表现为点击搜索后文字列表迟迟不出现。本文记录本机实测，不代表所有用户、查询或网络环境的固定耗时。

后续澄清：用户实际遇到的是首次进入资源页后，文字列表迟迟出现。下方原始采样和“本次修改”记录此前诊断与已经实现的优化；本轮对当前代码的首次进入复查见文末，不能将此前“没有查询缓存”的描述当作当前实现。

## 结论

主要等待发生在读取 Steam 官方工坊网页的网络过程。原实现每次重复搜索、翻页返回都重新联网，必须下载完整网页后才提取列表；没有查询缓存，因此每次都承受网络波动。文字列表并不等待所有封面完成。

不能仅凭这些样本将延迟归咎于 Steam 服务端：本机配置了本地代理，链路、代理和远端响应均可能影响网络阶段。诊断未改动系统代理或网络设置。

## 实测数据

采用应用相同的 .NET HTTP 路径，读取真实 Steam 数据，并分别测量响应头等待、响应体、解析和界面首次布局。以下是几次顺序采样，不是受控环境的统计基准。

| 项目 | 实测 |
| --- | --- |
| 热门列表初次读取 | 729 ms，其中响应头等待 664 ms、响应体 31 ms、解析 27 ms、分组/适配规则 6 ms |
| 同 URL 再次联网，无应用缓存 | 488 ms，其中响应头等待 436 ms、解析 18 ms |
| 另一轮热门列表新请求 | 13,679 ms，复现明显慢响应 |
| `RICE` 新查询 | 2,932 ms |
| `汉化` 新查询 | 752 ms |
| 同请求命中新增内存缓存 | 0.019～0.038 ms，仅数据读取，不包含控件重建和绘制 |
| 单条详情 API | 395 ms |
| 无压缩网页传输 | 344,123 bytes |
| 同网页 gzip 传输 | 86,115 bytes，字节数减少约 75% |
| 30 个封面后台加载 | 合计 13,373,698 bytes，4 个下载槽，本轮全部完成约 6.27 秒 |

压缩样本为同一连接后续请求，不能把全部时间差都归因于压缩。可靠结论是传输字节减少；网络延迟仍会波动。

界面基线单次采样：窗口构造/显示约 421 ms，本地初始化约 200 ms，搜索用例约 1,134 ms，首次列表布局约 119 ms；首帧已有 30 条文字，封面加载完成数为 0，验证文字不等待整页图片。

新版界面复测：本地初始化约 201 ms，新查询约 1,108 ms，首次布局约 51 ms；同条件重复搜索用例约 13 ms，布局约 27 ms，合计约 40 ms。该页 30 条 Steam 发布中仅显示 7 个适配声明匹配的模组。以上仍为单次界面采样，不能保证首次新查询没有网络长尾。

## 本次修改

- 工坊列表采用默认 2 分钟、最多 24 个查询页的内存缓存。搜索词、排序、页码、类型和版本标签分别隔离；无效响应与失败不缓存，取消仍生效。
- 元数据客户端启用 gzip、deflate 和 Brotli 自动协商/解压。压缩后依然对解压后的响应体执行大小限制。
- 网络读取后的网页解析不再捕获 UI 同步上下文，减少界面线程上的解析工作。
- 默认只展示当前 CK3 版本的适配声明匹配项；不为被隐藏的发布项启动封面下载。
- 最近两分钟验证过的详情覆盖较旧的列表数据，避免缓存回退已确认的声明；更新更晚的列表发布与过期验证数据不被锁死。
- 保留独立的图片加载与取消，避免把图片队列当成文字查询的完成条件。

首次新查询或缓存过期后仍需要访问 Steam。本次未实现离线全目录索引或服务端镜像，不能承诺所有新查询均即时返回。

## 验证方式

自动化测试使用内存 HTTP 和可推进时钟，覆盖缓存键隔离、过期、容量、失败不缓存、取消及严格适配筛选，不依赖 Steam 网络。

本地临时诊断程序和完整采样在忽略的 `artifacts/` 内，未提交用户安装目录、第三方封面或代理地址。

## 首次进入资源页复查（2026-10-28）

本轮针对首次进入后出现文字列表的等待：主要耗时在 Steam 网页读取，当前代码的解析和界面布局没有出现秒级计算开销。两轮真实无窗口界面采样共四次首屏，耗时约 0.96～3.90 秒；本轮未复现此前 13.7 秒的长响应，也不能把 Steam 服务端和代理链路各自的耗时继续拆开归因。原始数据在 `artifacts/workshop-recheck/results.json`、`ui-results-initial.json`、`ui-results.json`，诊断脚本也保留在同目录。界面采样通过真实 Avalonia 窗口、视图模型、应用服务与 Steam 请求测量，不含人工点击延迟与实体屏幕合成耗时。[VERIFY: artifacts/workshop-recheck/UiProgram.cs:22] [VERIFY: artifacts/workshop-recheck/ui-results-initial.json:3] [VERIFY: artifacts/workshop-recheck/ui-results-initial.json:31] [VERIFY: artifacts/workshop-recheck/ui-results.json:3] [VERIFY: artifacts/workshop-recheck/ui-results.json:31]

### 首次进入的实际调用链

进入页面时先切换界面状态，再自动读取热门第一页；如果启动初始化尚未完成，则先等设置、游戏发现、本地 Mod 扫描及配置清单读取完成，再请求 Steam。完整网页下载后才解析、分组、筛选与填充文字行，封面随后独立加载。[VERIFY: src/Ck3ModManager.Desktop/MainWindowViewModel.cs:122] [VERIFY: src/Ck3ModManager.Desktop/MainWindowViewModel.cs:214] [VERIFY: src/Ck3ModManager.Application/WorkspaceService.cs:20] [VERIFY: src/Ck3ModManager.Application/WorkspaceService.cs:28] [VERIFY: src/Ck3ModManager.Infrastructure/SteamWorkshopBrowser.cs:38] [VERIFY: src/Ck3ModManager.Desktop/WorkshopViewModel.cs:267] [VERIFY: src/Ck3ModManager.Desktop/WorkshopViewModel.cs:461]

| 第一轮首屏采样 | 启动初始化中进入 | 本地初始化完成后进入 |
| --- | ---: | ---: |
| 进入后继续等本地初始化 | 218 ms | 小于 1 ms |
| Steam 列表读取（含解析） | 1,419 ms | 950 ms |
| 返回后构建行及状态处理 | 12 ms | 8 ms |
| 首次列表布局 | 45 ms | 2 ms |
| 进入到首次列表布局完成 | 1,694 ms | 960 ms |
| 首次布局已有文字行 | 6 | 6 |
| 首次布局已完成封面 | 0 | 0 |
| 同进程同条件重读，含布局 | 35 ms | 11 ms |

第二轮同样测两种进入时机：首屏约 3,902／3,084 ms，其中列表读取约 3,630／3,074 ms，本地初始化剩余等待约 222／小于 1 ms，列表返回后处理及布局约 50／10 ms。同进程命中缓存后重读并布局约 38／12 ms。两轮对比复现了同一首页网络等待的明显波动。[VERIFY: artifacts/workshop-recheck/ui-results.json:3] [VERIFY: artifacts/workshop-recheck/ui-results.json:20] [VERIFY: artifacts/workshop-recheck/ui-results.json:31] [VERIFY: artifacts/workshop-recheck/ui-results.json:48]

每轮两列分别使用新元数据客户端、新内存缓存和新视图模型；第二列所在进程的 JIT、文件系统和 Avalonia 已预热，不能将它当作另一台冷启动机器。每次首次进入浏览器各自动调用一次，首次文字可见时图片任务均未完成，排除了“必须等所有封面下载好才出现文字”的解释。[VERIFY: artifacts/workshop-recheck/UiProgram.cs:17] [VERIFY: artifacts/workshop-recheck/ui-results-initial.json:13] [VERIFY: artifacts/workshop-recheck/ui-results-initial.json:41] [VERIFY: artifacts/workshop-recheck/ui-results.json:13] [VERIFY: artifacts/workshop-recheck/ui-results.json:41]

### 网络路径对照

同应用的 `SteamHttpClients.CreateMetadataClient()` 做热门、`RICE`、`汉化` 两轮顺序采样：默认路径的响应头等待约 368～1,261 ms，响应体读取/解压约 76～119 ms，解析约 12～28 ms，分组及声明匹配约 0.4～5.6 ms；六次请求总耗时约 489～1,414 ms，网络读取占各次总耗时约 95%～98%。这是有限样本，不是长期分位数统计。[VERIFY: artifacts/workshop-recheck/Program.cs:14] [VERIFY: artifacts/workshop-recheck/results.json:9]

运行时检查显示该 URL 使用应用默认代理；另外使用仅供诊断的 `UseProxy=false` 客户端直连，两次都在诊断设定的 8 秒期限内未收到响应头。该对照没有改动系统代理，也没有证明 Steam 服务端慢；它说明本机直连在本轮采样中不可用，不能将“关闭代理”作为已验证的提速办法。应用本身列表总超时仍为 30 秒。[VERIFY: artifacts/workshop-recheck/results.json:5] [VERIFY: artifacts/workshop-recheck/Program.cs:21] [VERIFY: artifacts/workshop-recheck/results.json:103] [VERIFY: src/Ck3ModManager.Infrastructure/SteamWorkshopBrowser.cs:37]

### 会放大首次等待的设计

- 当前已有两分钟、最多 24 页的缓存，但只保存在浏览器实例内存；每次启动创建新实例，首次进入没有上次首屏可展示，目标版本有效时必须重新联网。同一进程已经加载过后切走再进入，不会自动再次加载。[VERIFY: src/Ck3ModManager.Infrastructure/SteamWorkshopBrowser.cs:16] [VERIFY: src/Ck3ModManager.Infrastructure/SteamWorkshopBrowser.cs:22] [VERIFY: src/Ck3ModManager.Desktop/App.axaml.cs:23] [VERIFY: src/Ck3ModManager.Desktop/MainWindowViewModel.cs:124] [VERIFY: src/Ck3ModManager.Desktop/WorkshopViewModel.cs:249]
- 首次自动请求直到切入页面才开始；若初始化仍忙，还要叠加本地初始化剩余时间。列表读取必须等完整 HTML，而非先显示上次结果、后台更新。[VERIFY: src/Ck3ModManager.Desktop/MainWindowViewModel.cs:124] [VERIFY: src/Ck3ModManager.Desktop/MainWindowViewModel.cs:219] [VERIFY: src/Ck3ModManager.Infrastructure/SteamWorkshopBrowser.cs:38]
- 请求期间清空结果并显示初始空态；搜索、分页受全局 `IsBusy` 限制，网络慢会直接表现为内容区域长时间没有文字。底部状态会变为正在读取，但资源区域没有专用骨架加载态。[VERIFY: src/Ck3ModManager.Desktop/WorkshopViewModel.cs:261] [VERIFY: src/Ck3ModManager.Desktop/MainWindowViewModel.cs:363] [VERIFY: src/Ck3ModManager.Desktop/MainWindowViewModel.cs:84] [VERIFY: src/Ck3ModManager.Desktop/WorkshopViewModel.cs:229] [VERIFY: src/Ck3ModManager.Desktop/MainWindow.axaml:92]

建议后续优先给热门首页增加按筛选条件隔离的磁盘缓存，进入时显示明确标注时间的上次结果，再后台刷新；缓存结果仍需按当前游戏版本筛选。其次在本地环境就绪后预取首页、资源区域显示明确加载进度和错误重试。上述为针对当前首次等待路径的设计建议，本轮未修改产品行为。[VERIFY: src/Ck3ModManager.Desktop/App.axaml.cs:23] [VERIFY: src/Ck3ModManager.Infrastructure/SteamWorkshopBrowser.cs:27] [VERIFY: src/Ck3ModManager.Desktop/MainWindowViewModel.cs:214] [VERIFY: src/Ck3ModManager.Desktop/MainWindow.axaml:92]

## HTTP 缓存与结构化接口核验（2026-10-28）

HTTP 是通信协议，HTML 是页面格式，JSON 是数据格式。当前项目的列表适配器通过 HTTPS 下载 HTML 并提取其中 SSR JSON，详情则已经通过 HTTPS POST 获取 JSON；这两者都是 HTTP 请求，但不是同一种响应内容和读取路径。[VERIFY: src/Ck3ModManager.Infrastructure/SteamWorkshopBrowser.cs:38] [VERIFY: src/Ck3ModManager.Infrastructure/SteamWorkshopBrowser.cs:74] [VERIFY: src/Ck3ModManager.Infrastructure/SteamWorkshopSource.cs:24]

使用应用相同元数据客户端，对列表、详情和真实封面分别做一次请求，只保留缓存相关响应头，结果在 `artifacts/workshop-recheck/cache-headers.json`。本轮列表响应没有 ETag、Last-Modified、Cache-Control 或 Expires；详情 POST 响应只有 Expires，没有 ETag、Last-Modified 或 Cache-Control；封面则具有 ETag、Last-Modified 与 max-age。该有限采样不代表所有请求始终如此，但已经不足以支持“列表和详情普遍支持 ETag/304，可直接把通用 HTTP 磁盘缓存列为最大收益项”的假设。[VERIFY: artifacts/workshop-recheck/cache-headers.json:3] [VERIFY: artifacts/workshop-recheck/cache-headers.json:13] [VERIFY: artifacts/workshop-recheck/cache-headers.json:24]

[Valve 的 QueryFiles 文档](https://partner.steamgames.com/doc/webapi/IPublishedFileService#QueryFiles)提供结构化搜索接口，并将 `key` 列为必需参数。[ISteamUGC 文档](https://partner.steamgames.com/doc/api/ISteamUGC)则提供查询与缓存控制能力，需要另行验证 SDK 初始化、应用身份及本项目的接入条件；不能将其当成已有的匿名公开搜索替代接口。[工坊实现指南](https://partner.steamgames.com/doc/features/workshop/implementation#Item_Installation)说明实际工坊文件下载由 Steam 客户端执行，浏览列表的 HTML/JSON 请求不等于 Mod 文件下载。

因此改造顺序应调整为：先验证可稳定接入的结构化搜索数据来源；保留公开 HTML 适配器作为现阶段可用来源；把已验证的列表对象缓存与封面缓存独立设计。HTTP 层按真实响应头协商，业务层的旧数据展示必须显式标注时间并重新核验，不应将业务缓存冒充源站允许的 HTTP 缓存。本轮仅核验方案，未实现这些产品改动。[VERIFY: src/Ck3ModManager.Infrastructure/SteamWorkshopBrowser.cs:27] [VERIFY: artifacts/workshop-recheck/cache-headers.json:3] [VERIFY: artifacts/workshop-recheck/cache-headers.json:24]

用户提供的对照中还有三处需要区分：当前列表没有逐条补齐整页详情，输入框不自动随每个字符搜索；单纯滚动时，已有 Bitmap 的行也不会重新下载图片。批量详情、输入去抖都不是当前首屏延迟的直接修复点，封面重复传输主要出现在重新搜索销毁并重建行后。[VERIFY: src/Ck3ModManager.Desktop/WorkshopViewModel.cs:267] [VERIFY: src/Ck3ModManager.Desktop/WorkshopViewModel.cs:294] [VERIFY: src/Ck3ModManager.Desktop/MainWindowViewModel.cs:154] [VERIFY: src/Ck3ModManager.Desktop/WorkshopViewModel.cs:111] [VERIFY: src/Ck3ModManager.Desktop/WorkshopViewModel.cs:261]
