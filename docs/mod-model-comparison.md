# MC 与 CK3 Mod 的共同模型，以及 PCL 的实现

记录日期：2026-10-27。本文用于讨论管理器设计，不代表 CK3 项目已实现这些功能。PCL 依据本地检出的 `93ede5d9e638c99fcec853a9ba1eecd91ceee7f5` 源码；`[VERIFY: PCL/路径:行号]` 相对于上级工作目录中的参考仓库。

## 比较的层次

这里主要比较 Minecraft Java 版的 Forge/Fabric 类 Mod 与 CK3 Mod 的管理需求。以 Fabric 为例，运行时元数据可以声明 Java 类入口、版本、依赖和不兼容项；CK3 的真实 Mod 仓库则可以看到描述文件、脚本和资源目录。两者运行时不同，但都需要管理资源身份、适用环境和实际安装内容。[Fabric 官方元数据文档](https://docs.fabricmc.net/develop/loader/fabric-mod-json)、[RICE 原作者仓库](https://github.com/cybrxkhan/RICE-for-CK3)

## 1. 都要区分 Mod、本次发布与实际文件

“我装了哪个 Mod”与“我装的是它的哪份内容”是两个问题。CK3 的 RICE 描述文件分别保存名称、Mod 版本、适用游戏版本和工坊 ID；这些字段本身就不应混为一个版本号。[RICE descriptor.mod](https://github.com/cybrxkhan/RICE-for-CK3/blob/master/RICE/descriptor.mod)

PCL 将它们拆成三个对象：

| PCL 对象 | 具体含义 | 对 CK3 的启发 |
| --- | --- | --- |
| `ResourceProject` | 某个平台上的项目，记录平台与项目 ID。 | 工坊条目或其他来源中的一个 Mod 项目。 |
| `ResourceVersion` | 该项目某次发布，记录发布 ID、游戏版本、文件名、下载地址与哈希。 | 一次具体发布；来源缺少可靠发布 ID 时，仍需保存内容校验信息。 |
| `LocalResourceFile` | 磁盘上的文件，关联线上项目、已安装发布和可更新发布。 | 用户实际安装的目录、压缩包或已保存的文件集合。 |

代码证据：[ResourceProject](https://github.com/Meloong-Git/PCL/blob/93ede5d9e638c99fcec853a9ba1eecd91ceee7f5/Plain%20Craft%20Launcher%202/Modules/Resource/ResourceProject.vb#L11)、[ResourceVersion](https://github.com/Meloong-Git/PCL/blob/93ede5d9e638c99fcec853a9ba1eecd91ceee7f5/Plain%20Craft%20Launcher%202/Modules/Resource/ResourceVersion.vb#L4)、[LocalResourceFile](https://github.com/Meloong-Git/PCL/blob/93ede5d9e638c99fcec853a9ba1eecd91ceee7f5/Plain%20Craft%20Launcher%202/Modules/Resource/LocalResourceFile.vb#L114)。

[VERIFY: PCL/Plain Craft Launcher 2/Modules/Resource/ResourceProject.vb:11] [VERIFY: PCL/Plain Craft Launcher 2/Modules/Resource/ResourceVersion.vb:11] [VERIFY: PCL/Plain Craft Launcher 2/Modules/Resource/ResourceVersion.vb:45] [VERIFY: PCL/Plain Craft Launcher 2/Modules/Resource/ResourceVersion.vb:95] [VERIFY: PCL/Plain Craft Launcher 2/Modules/Resource/LocalResourceFile.vb:128]

设计推论：工坊 ID 适合标识项目，但不能单独代表被冻结的内容。对目录型 CK3 Mod，可以设计逐文件清单和整体内容标识，具体方案待实现验证。

## 2. 都需要判断某次发布适合哪个游戏环境

PCL 搜索时同时传入 `GameVersion` 和 `ModLoaders`，转换为 CurseForge 参数或 Modrinth 筛选条件；下载位置推荐也会检查目标实例的游戏版本和加载器。这体现的是“发布具有运行条件，安装目标也有运行条件”，然后比较两者。[ResourceSearcher](https://github.com/Meloong-Git/PCL/blob/93ede5d9e638c99fcec853a9ba1eecd91ceee7f5/Plain%20Craft%20Launcher%202/Modules/Resource/ResourceSearcher.vb#L46)

[VERIFY: PCL/Plain Craft Launcher 2/Modules/Resource/ResourceSearcher.vb:46] [VERIFY: PCL/Plain Craft Launcher 2/Modules/Resource/ResourceSearcher.vb:50] [VERIFY: PCL/Plain Craft Launcher 2/Pages/PageDownload/Resource/PageDownloadCompDetail.xaml.vb:411] [VERIFY: PCL/Plain Craft Launcher 2/Pages/PageDownload/Resource/PageDownloadCompDetail.xaml.vb:450]

CK3 已有作者填写的 `supported_version`；未来管理器还可以记录主 Mod 版本、汉化对应版本和补丁适用范围。这些属于拟建的兼容规则，不能把 Forge/Fabric 当成 CK3 主 Mod 的同一种机制。元数据符合条件只能作为候选证据，不能替代组合实测。[RICE 描述文件实例](https://github.com/cybrxkhan/RICE-for-CK3/blob/master/RICE/descriptor.mod)

## 3. 都存在前置、子 Mod 与兼容补丁关系

Fabric 元数据具有必需依赖和不兼容声明；CK3 原作者也有使用 `dependencies` 声明主 Mod、要求补丁排在主 Mod 后面的实例。[Fabric 文档](https://docs.fabricmc.net/develop/loader/fabric-mod-json)、[LoV 纹理补丁作者说明](https://github.com/AlexandruDodita/CK3AGOT-LoV-Texture-Opt)

PCL 从平台返回的数据提取必需依赖项目 ID，补全项目资料，在详情页显示“前置资源”入口。普通 Mod 的保存流程下载用户选中的文件，这一流程不应描述为递归选出兼容版本并自动装齐所有依赖。[版本依赖解析](https://github.com/Meloong-Git/PCL/blob/93ede5d9e638c99fcec853a9ba1eecd91ceee7f5/Plain%20Craft%20Launcher%202/Modules/Resource/ResourceVersion.vb#L139)、[前置资源界面](https://github.com/Meloong-Git/PCL/blob/93ede5d9e638c99fcec853a9ba1eecd91ceee7f5/Plain%20Craft%20Launcher%202/Pages/PageDownload/Resource/PageDownloadCompDetail.xaml.vb#L255)

[VERIFY: PCL/Plain Craft Launcher 2/Modules/Resource/ResourceVersion.vb:141] [VERIFY: PCL/Plain Craft Launcher 2/Modules/Resource/ResourceVersion.vb:198] [VERIFY: PCL/Plain Craft Launcher 2/Modules/Resource/ResourceVersion.vb:266] [VERIFY: PCL/Plain Craft Launcher 2/Pages/PageDownload/Resource/PageDownloadCompDetail.xaml.vb:264] [VERIFY: PCL/Plain Craft Launcher 2/Pages/PageDownload/Resource/PageDownloadCompDetail.xaml.vb:521]

设计推论：我们可以先清楚展示缺失的主 Mod、汉化和补丁，再单独建设版本约束、排序与自动安装能力。

## 4. 都需要管理多套环境

PCL 的 `McInstance` 表示一个实例，`PathIndie` 根据设置决定使用版本目录还是共享目录；运行目录也采用这个选择。这让不同组合有机会使用各自的 Mod、配置与存档目录。[McInstance.PathIndie](https://github.com/Meloong-Git/PCL/blob/93ede5d9e638c99fcec853a9ba1eecd91ceee7f5/Plain%20Craft%20Launcher%202/Modules/Minecraft/ModMinecraft.vb#L240)

[VERIFY: PCL/Plain Craft Launcher 2/Modules/Minecraft/ModMinecraft.vb:240] [VERIFY: PCL/Plain Craft Launcher 2/Modules/Minecraft/ModMinecraft.vb:277] [VERIFY: PCL/Plain Craft Launcher 2/Modules/Minecraft/ModLaunch.vb:2107]

对 CK3，这是可借鉴的产品需求：为不同主 Mod 或不同存档维护不同配置。不过，CK3 如何选择或切换这些目录需要另外验证；PCL 的目录隔离本身也不等于自动保存历史快照、冻结文件或任意回滚。

## 5. 都需要分享或重建一组资源

PCL 的 Modrinth 整合包安装器读取游戏与加载器版本，再按文件路径、下载地址和哈希下载资源，同时复制配置覆盖目录。其核心思路是“清单说明环境需要什么，安装器把清单落到磁盘”。[整合包安装源码](https://github.com/Meloong-Git/PCL/blob/93ede5d9e638c99fcec853a9ba1eecd91ceee7f5/Plain%20Craft%20Launcher%202/Modules/Minecraft/ModModpack.vb#L366)

[VERIFY: PCL/Plain Craft Launcher 2/Modules/Minecraft/ModModpack.vb:379] [VERIFY: PCL/Plain Craft Launcher 2/Modules/Minecraft/ModModpack.vb:408] [VERIFY: PCL/Plain Craft Launcher 2/Modules/Minecraft/ModModpack.vb:440]

设计推论：CK3 配置清单可记录游戏版本、各 Mod 的具体内容标识、来源、加载顺序、汉化、补丁和配置。精确恢复仍取决于文件可取得，或事先保留了本地内容。

## 6. 都需要把文件操作与启动做成可观察的流程

PCL 用 `LoaderTask`、`LoaderDownload`、`LoaderCombo` 组织下载、准备环境和启动；Watcher 在进程启动后持续监控。这里体现的通用能力是进度、失败、取消以及日志证据。[任务框架](https://github.com/Meloong-Git/PCL/blob/93ede5d9e638c99fcec853a9ba1eecd91ceee7f5/Plain%20Craft%20Launcher%202/Modules/Base/ModLoader.vb#L467)、[启动任务清单](https://github.com/Meloong-Git/PCL/blob/93ede5d9e638c99fcec853a9ba1eecd91ceee7f5/Plain%20Craft%20Launcher%202/Modules/Minecraft/ModLaunch.vb#L122)

[VERIFY: PCL/Plain Craft Launcher 2/Modules/Base/ModLoader.vb:263] [VERIFY: PCL/Plain Craft Launcher 2/Modules/Base/ModLoader.vb:467] [VERIFY: PCL/Plain Craft Launcher 2/Modules/Base/ModNet.vb:1311] [VERIFY: PCL/Plain Craft Launcher 2/Modules/Minecraft/ModLaunch.vb:122] [VERIFY: PCL/Plain Craft Launcher 2/Modules/Minecraft/ModLaunch.vb:2173]

设计推论：CK3 可以使用同样的分阶段思路组织扫描、下载、校验、安装、生成加载配置和启动。任务失败传播不代表自动事务回滚，游戏正常启动也不证明组合长期可玩。

## 对当前项目最有价值的共同模型

可以优先讨论四种对象：`ModProject`（哪个 Mod）、`ModRelease`（哪次发布及约束）、`InstalledMod`（实际内容和位置）、`Profile`（一组选择及运行配置）。前面三种借鉴 PCL 的资源分离方式，Profile 借鉴实例管理；CK3 的加载顺序、主 Mod 关系与验证结果需要我们扩展设计。

需单独研究 CK3 的内容覆盖、作者要求的顺序和兼容补丁。不能只因游戏版本相同就判定组合兼容，也不应把所有 CK3 内容都简化为“后加载的一律覆盖前面的”。RICE 作者对软冲突、补丁和排序的说明提供了实际案例。[作者兼容说明](https://cybrxkhansmods.godaddysites.com/compatibility)
