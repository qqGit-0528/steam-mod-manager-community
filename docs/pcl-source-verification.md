# `mod-model-comparison.md` 的 PCL 源码引用核对

核对日期：2026-10-29。基准源码：本地 `repo/PCL`，HEAD 为 `93ede5d9e638c99fcec853a9ba1eecd91ceee7f5`，与 `docs/mod-model-comparison.md` 开篇声明的版本一致。

用途：该文档的事实层大量依赖于 PCL 的行号引用，正文用 `[VERIFY: PCL/路径:行号]` 标记待确认。本文逐条核对全部标记，判定引用是否真的支持原文论断。核对只读源码，不修改原文档。

## 方法

1. 抽取全部 `[VERIFY: ...]` 内联标记：25 条（不含开篇的格式示例）。
2. 抽取全部 GitHub 行锚链接 `blob/93ede5d…/路径#L行号`：10 条，其中 5 条是内联标记没有覆盖的额外引用。
3. 去重后共 **30 个文件:行号**，逐条取出该行及前后 2–4 行上下文，判断是否落在原文断言的对象上。
4. 对没有行号的论断（字段清单、依赖解析语义、任务框架）另行抽查。

## 结果

**30 / 30 全部命中，没有错文件、没有错行、没有行号漂移。** 引用的类声明行（如 `ResourceVersion.vb:4`、`ModLoader.vb:263`）属于可接受的锚点。

| 文档断言 | 引用 | 实际源码 | 结论 |
| --- | --- | --- | --- |
| 资源项目记录平台与项目 ID | `ResourceProject.vb:11` | `Public ReadOnly Platform As ResourcePlatforms`；`Id` 在 :24 | 命中 |
| 发布记录 ID | `ResourceVersion.vb:4`、`:11` | `Public Class ResourceVersion`；`Public Id As String` | 命中 |
| 发布记录游戏版本 | `ResourceVersion.vb:45` | `Public GameVersions As List(Of String)` | 命中 |
| 发布记录哈希 | `ResourceVersion.vb:95` | `Public Hash As String`，注释为 SHA1 或 MD5 | 命中 |
| 本地文件关联线上项目 | `LocalResourceFile.vb:114`、`:128` | `Project As ResourceProject`；`ProjectVersion As ResourceVersion` | 命中 |
| 搜索同时传游戏版本与加载器 | `ResourceSearcher.vb:46`、`:50` | `ModLoaders`；`GameVersion` 字段 | 命中 |
| 该条件被转成平台参数 | 无行号（表内论断） | CurseForge `&gameVersion=`（:88）；Modrinth `facets=[["versions:…"]]`（:109–110） | 命中 |
| 从平台响应提取必需依赖 | `ResourceVersion.vb:139`、`:141`、`:198` | CurseForge 取 `relationType = 3`（:141–143）；Modrinth 取 `dependency_type = "required"`（:198–201），两者都排除 Fabric/Quilt API | 命中 |
| 补全项目资料并加载前置 | `ResourceVersion.vb:266` | `LoadDependencies` 用 `ResourceProject.FromProjectIds` 回填 `File.Dependencies`（:266–275） | 命中 |
| 详情页显示“前置资源”入口 | `PageDownloadCompDetail.xaml.vb:255`、`:264` | :255 汇总版本依赖；:264 才是界面文本 `"前置资源"` | 命中 |
| 保存流程下载用户选中文件 | `PageDownloadCompDetail.xaml.vb:521` | `New LoaderDownload("下载文件", {File.ToNetFile(...)})` | 命中 |
| 下载位置推荐检查实例条件 | `PageDownloadCompDetail.xaml.vb:411`、`:450` | `IsInstanceSuitable` 按实例判断；加载器用 `AllowedLoaders.Flags.Intersect(...)` | 命中 |
| 实例有独立的版本目录 | `ModMinecraft.vb:240`、`:277` | `PathIndie` 按 `VersionArgumentIndieV2` 在 `PathVersion` 与 `McFolderSelected` 间选择 | 命中 |
| 运行目录沿用该选择 | `ModLaunch.vb:2107` | `StartInfo.WorkingDirectory = McInstanceSelected.PathIndie` | 命中 |
| 整合包安装器读取清单 | `ModModpack.vb:366`、`:379` | `InstallPackModrinth`；遍历 `Json("dependencies")` 取 minecraft / forge 等版本 | 命中 |
| 整合包复制配置覆盖目录 | `ModModpack.vb:408` | `CopyOverrideDirectory(.../"overrides")` 与 `client-overrides` | 命中 |
| 按路径、地址、哈希下载 | `ModModpack.vb:440` | `New NetFile(Urls, TargetPath, New FileChecker With {.Hash = …sha1…})` | 命中 |
| 任务框架组织下载与环境 | `ModLoader.vb:263`、`:467`、`ModNet.vb:1311`、`ModLaunch.vb:122` | `LoaderTask`、`LoaderCombo`、`LoaderDownload`；启动清单里组合这些 loader | 命中 |
| 启动后持续监控 | `ModLaunch.vb:2173` | `Dim Watcher As New Watcher(Loader, McInstanceSelected, WindowTitle)` | 命中 |

## 核对过程中确认的两个细节

- **依赖只是“标注”，不是自动递归安装。** `LoadDependencies` 只把前置项目信息回填到 `File.Dependencies`，真正的下载发生在用户点击后的 `LoaderDownload`（`PageDownloadCompDetail.xaml.vb:521`）。原文“不应描述为递归选出兼容版本并自动装齐所有依赖”的判断与源码一致。
- **PCL 侧有两套前置来源。** CurseForge 走 `relationType = 3`，Modrinth 走 `dependency_type = "required"`，且都显式排除了 Fabric API / Quilt API（`ResourceVersion.vb:143`、`:200`）。这部分原文只引了 CurseForge 与 Modrinth 各一处，覆盖完整。

## 两处可以补强的引用（不是错误）

1. 表格里 `ResourceProject` —「记录平台与项目 ID」只锚到 `:11`（Platform），`Id` 在 `:24`；两行都引会更贴合断言。
2. `LocalResourceFile` —「关联线上项目、已安装发布和可更新发布」引了 `:114` 与 `:128`，而“可更新发布”对应的是 `:133 UpdateFile`，目前没有锚点。

## 结论

`mod-model-comparison.md` 的事实层可以信任：30 个引用全部落在正确位置，叙述性的依赖语义判断也与源码一致。文档中的 `[VERIFY]` 标记属于“作者主动留下待确认”的痕迹，现在可以清理；但清理原文属于单独决定，本次没有改动被核对的文档。
