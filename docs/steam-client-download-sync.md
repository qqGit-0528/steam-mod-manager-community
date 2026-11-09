# App 与 Steam 客户端下载同步：接入结果与历史定位

2026-11-03 更新：桌面端与 CLI 已改用本机 Steam 托管下载，并更新到本地应用。实现方式与使用边界见[当前下载说明](workshop-download.md)。下文保留接入前的核对，不能视作当前代码仍在使用独立 SteamKit。

实际 C# 联调已经验证：3830179207 订阅并复用成功；3829971096 新下载的 Steam 网络字节增长至293616，安装849889字节，收到成功完成并可由本地扫描器识别。无本工具二维码。客户端不联网曾返回 NoConnection（结果3），联网恢复后成功；未据此认定地区授权问题。

## 接入前核对（历史）

核对日期：2026-11-03。本文是源码、官方接口说明和本机文件的只读核对结果，尚未执行 Steamworks 初始化、订阅或客户端下载请求，不代表桥接方案已经实测可用。

## 当前为什么没有同步

当前下载入口始终创建本工具的 `WorkshopDownloadService` 任务，provider 是 `SteamWorkshopContentProvider`，没有 Steam 客户端 provider 的选择分支。

| 环节 | 当前 App | Steam 客户端 |
| --- | --- | --- |
| 连接与登录 | SteamKit 建立独立网络会话；修改中的内存授权复用仍属于这条独立会话路线 | Steam 客户端现有登录会话 |
| 下载任务 | 本工具 `_active` 字典，按 App ID、工坊 ID 去重 | Steam 自己的工坊下载队列；本工具尚未查询或加入 |
| 文件目录 | 应用数据目录的 `content/steam/<AppId>/<PublishedFileId>/revisions/` | Steam 库的 `steamapps/workshop/content/<AppId>/<PublishedFileId>/` |
| 内容修订与完成 | 本工具 manifest、文件校验、wrapper 和 installation.json | Steam 管理的安装记录与内容修订 |
| 进度与错误 | SteamKit 传输进度 | 尚未接入客户端的下载字节与结果回调 |

`FileWorkshopDownloadStore.FindVerifiedAsync` 只查找本工具的修订目录，不会因为 Steam 中已经安装或正在下载同一个物品而复用它。本工具的下载锁也只约束自身实例，不能让 Steam 的下载任务加入同一把锁。

`Ck3ModScanner` 能发现 Steam 工坊内容，`FileSystemLocalModMonitor` 能监测本地目录变化，但它们只负责更新本地库，不负责请求 Steam 下载、导入 Steam 任务或取得 Steam 的真实传输进度。因此“Steam 下载后本地列表能看到”与“App 和 Steam 共用下载任务”是两种不同的能力。

## 本机核对

- CK3 的 Steam 工坊目录有 10 个内容目录，Victoria 3 有 22 个，并存在各自的 `appworkshop_<AppId>.acf`，记录已安装物品的 manifest 与大小。
- 本工具对应的几个测试物品目录只有下载锁，尚无成功发布的修订；不能把 Steam 已有内容误报为本工具下载完成。
- 已安装桌面程序是 arm64；本机 CK3 和 Victoria 3 的 `libsteam_api.dylib` 都包含 arm64，因此未发现这个环境下必然的 CPU 架构阻断。
- CK3 的本机库导出了 `SteamAPI_Init`、`SteamAPI_RunCallbacks`、`SteamAPI_SteamUGC_v016` 以及 DownloadItem、GetItemState、GetItemDownloadInfo、GetItemInstallInfo、SubscribeItem 的 flat API。符号存在只证明接口可被进一步验证，不证明 App 已成功初始化或拥有可用的运行上下文。

这些检查未修改 Steam 清单、订阅、工坊内容或登录信息。ACF 是本机实现细节，适合作为只读发现和诊断资料，不能据其字段直接宣称精确字节进度，也不应伪造清单让 Steam“接管”外部下载。

## 官方接口提供的路径

- `ISteamUGC.DownloadItem` 请求下载或更新。返回 true 仅表示请求开始，需要等待 `DownloadItemResult_t` 后才能访问内容。
- `GetItemState` 查询客户端当前状态；`GetItemDownloadInfo` 在资料可用时提供实际已下载字节和总字节。不可用时应显示等待或未知，不能由目录大小估算出伪进度。
- `GetItemInstallInfo` 返回安装内容目录；完成回调还要核对 App ID 和物品 ID。
- `SubscribeItem` 增加订阅并促使内容尽快下载安装。它改变用户的持续订阅状态，不能把订阅与一次临时下载悄悄混为一谈；未订阅内容可能只被临时缓存。
- `SteamAPI_Init` 要求运行中的 Steam、正确的 App ID、相同 OS 用户上下文、当前账户拥有对应许可及可用的应用配置。App ID 文本不是权限授予，不能把写一个 steam_appid.txt 当作正式接入完成。

官方来源：[ISteamUGC](https://partner.steamgames.com/doc/api/ISteamUGC)、[工坊集成指南](https://partner.steamgames.com/doc/features/workshop/implementation)、[Steam API 初始化](https://partner.steamgames.com/doc/api/steam_api#SteamAPI_Init)。

## 还需要实测和设计的边界

1. **第三方 App 上下文**：先用隔离的小工具验证本机官方库能否初始化正确游戏上下文，并读取已安装物品的状态和目录；不靠复制客户端 Cookie 或登录令牌。还需检查多游戏、回调、进程退出与正式分发条件。隔离的每游戏 bridge 是待验证的设计，尚非已确定支持方式。
2. **取消语义**：公开 ISteamUGC 没有与当前本工具“单项立即取消”完全等价的已接入操作。`SuspendDownloads` 会影响所有工坊下载；`UnsubscribeItem` 会改变订阅并可能删除内容。不能用任一个偷偷替代删除本工具任务。需要明确区分本工具下载和 Steam 托管任务的可取消范围。
3. **单一任务归属**：以 App ID、PublishedFileId 关联两侧，但只允许一个选中的下载引擎写同一份当前内容。Steam 已安装或正在下载时，优先复用或跟踪，避免再次创建独立传输。
4. **更新与版本固定**：直接引用 Steam 目录意味着跟随 Steam 更新。固定旧版需要另外建立完整快照，不能仅保存那个会变化的目录路径；不得把版本锁定与自动跟随更新同时承诺为默认行为。

## 下一步验证顺序

先验证 Steam 客户端桥接的初始化、单物品状态和真实安装目录，再验证一次下载的字节变化及完成回调。如果这些均成功，再将 Steam 客户端作为在线同步模式的下载来源，App 负责展示、去重、本地索引与播放集。独立 SteamKit 下载保留为明确的另一种能力，而不是让两条传输同时写入 Steam 管理目录。

本轮结论是：没有证据说明 Steam 原理上不能向应用提供下载状态；当前不能同步的直接原因是项目尚未接入这条客户端任务链路。修复二维码缓存可以改善独立下载体验，但不会自动让 Steam 识别和管理本工具的下载。
