# CK3 Mod 工具竞品调研

调研日期：2026-10-27。

目标：确认是否已存在类似 PCL 的 CK3 桌面软件，集中解决 Mod 下载、游戏版本匹配、整合配置与启动。

方法：公开网页检索，并核对项目作者仓库、文档、发布页及 Valve / Paradox 官方文档。本轮未下载安装软件，作者声明的功能不代表已经实测其准确率、稳定性或下载成功率。

## 结论

已经有人做过，而且存在与设想相当接近的项目。不能以“市场上没有人做”为立项前提。

现有工具分别覆盖工坊浏览下载、Mod 排序、冲突提示、播放集管理与本地快照。值得进一步验证的产品方向，是把“一个存档所需的游戏版本、具体 Mod 文件版本、汉化、兼容补丁和加载顺序”保存为可恢复、可复现的环境，并提供经过实际测试的兼容配置。

这是基于本次功能对照的产品判断。公开检索不能证明全球没有其他完整实现，也不足以估算市场规模或付费意愿。

## 主要竞品

| 项目 | 已公开的相关能力 | 与版本问题有关的边界 |
| --- | --- | --- |
| [Adromedary’s Paradoxical Launcher](https://github.com/Adromedary/Adromedary-s-Paradoxical-Launcher) | 针对 CK3 开发测试；内置工坊浏览、游戏版本筛选、Mod / 合集下载、自动排序、冲突和崩溃分析、游戏启动。 | 最接近设想的直接竞品。版本检查读取作者声明，不等于实际测试兼容；未核实任意历史 Mod 版本获取或完整环境回滚。 |
| [Irony Mod Manager](https://bcssov.github.io/IronyModManager/games.html) | CK3 Mod 管理、加载顺序、合并；支持完整集合导出。 | 官方矩阵明确 CK3 不支持 Conflict Solver；工具本身不安装 Mod，集合也不能直接切换 Mod 版本。 |
| [The Royal Court](https://github.com/Juincy/The-Royal-Court) | 专门面向 CK3；播放集、自动排序、文件及定义冲突检查、兼容补丁提示、日志和崩溃辅助、直接启动；列有中文支持。 | README 明确不下载 Mod；主要解决已安装 Mod 的配置和诊断，未核实历史版本管理。 |
| [CK3 Playset Preserver](https://github.com/nullheimer/CK3-Playset-Preserver) | 按播放集顺序把当前 Mod 合成一个本地快照，避免源 Mod 更新影响这份快照。 | 直接覆盖“保存可玩的旧环境”需求。必须提前保存，不能凭空恢复已经被覆盖且没有备份的旧文件。 |
| [Shadow](https://github.com/Posdaca-Commune/Shadow/blob/main/docs/README.zh-CN.md) | 有中文文档；支持 CK3、本地与 Workshop Mod 发现、播放集、官方播放集导入、DLC 和存档管理、启动。 | “发现已安装 Mod”不能写成“内置下载器”；未从公开文档确认历史版本获取和环境快照回滚。 |

### 发布与实现补充

- Adromedary 的 [Releases](https://github.com/Adromedary/Adromedary-s-Paradoxical-Launcher/releases) 可见 2026 年 10 月的公开版本，当前核对版本为 2.0.1。项目较新，实际成熟度需要实测。[versions.py](https://github.com/Adromedary/Adromedary-s-Paradoxical-Launcher/blob/main/versions.py) 的兼容判断比较 `supported_version` 与游戏主、次版本，并支持通配符；信息不明时返回未知。这是声明检查。
- The Royal Court 的 [v0.26.0](https://github.com/Juincy/The-Royal-Court/releases/tag/v0.26.0) 发布页显示 10 月 27 日，早期下载介绍中的 0.17.0 已不能代表当前版本。其中文支持说明也意味着“有中文界面”本身不足以构成明显差异。
- Irony 的 [安装说明](https://github.com/bcssov/IronyModManager/wiki/Installing-New-Mods) 明确由 Steam、Paradox Mods 或用户手动安装后，再由 Irony 检测。[集合文档](https://github.com/bcssov/IronyModManager/wiki/Collection-Mods) 列出完整文件导出和版本切换限制。
- Shadow 的 [v1.2.3](https://github.com/Posdaca-Commune/Shadow/releases/tag/v1.2.3) 发布页和中文文档可核对。没有足够依据将它描述为成熟的国内商业产品。

## 其他候选

- [Parallax Mod Manager](https://github.com/Official-Husko/parallax-mod-manager)：声明支持 CK3 等 P 社游戏，但作者明确仍处于早期开发，只有 Stellaris 在真实安装上验证过。应列为潜在竞品，不能当作已验证的 CK3 方案。
- [Paradoxos Mod Manager Rework](https://github.com/ThibautSF/ParadoxosModManagerRework/blob/master/README.md)：旧项目，README 将 CK3 标为未测试，并提示与新版 Playset 系统的兼容问题。
- [kcw2034/ck3-modmanager](https://github.com/kcw2034/ck3-modmanager)：提供图形界面、播放集与文件冲突提示；仓库显示已于 2026-03-12 归档。

## 重要变化：Steam 已有分支版本匹配机制

Valve 在 2026 年 1 月公布了新的工坊版本兼容能力。[官方公告](https://steamcommunity.com/groups/steamworks/announcements/detail/497183817277113901) 与 [Steamworks 文档](https://partner.steamgames.com/doc/features/workshop/itemversioning) 说明：游戏开发者可启用游戏分支版本支持，Mod 作者可为不同上传版本标注支持的游戏分支，Steam 再下载适用于当前分支的最新 Mod 版本。

使用它需要游戏开发者配置旧分支并启用功能，还需要作者或上传工具提供兼容范围。同一分支上的兼容 Mod 仍可能继续自动更新，因此这种匹配与锁定一套完全相同的文件是两种能力。

本轮没有确认 CK3 是否已实际启用并完整使用该机制。工坊上的“Compatible Version”标签不能单独证明已经接入。后续技术验证应优先检查这一点，避免重复建设平台已有能力。

Paradox 的 [官方回滚说明](https://support.paradoxplaza.com/hc/en-us/articles/21690250550546-How-to-play-older-versions-of-games-rollback) 介绍通过 Steam 属性中的 Betas 选择已提供的旧游戏版本。这不代表任意游戏版本或任意 Mod 历史文件都可取得。

## 对我们产品的建议

第一版可以围绕“恢复一套已经验证能玩的 CK3 环境”设计：

1. **扫描并说明问题**：识别游戏版本、已安装 Mod、汉化和依赖；将作者声明匹配、已实测匹配与未知清楚区分。
2. **保存与恢复环境**：保存 Mod 文件快照、文件校验值、加载顺序和关联存档；启动前检测变化，允许恢复已有快照。
3. **提供少量实测配置**：先维护一个热门大型 Mod 的明确组合，记录游戏版本、主 Mod、汉化、补丁、顺序和测试结果。
4. **记录版本来源**：按每个来源实际提供的能力获取对应版本；旧文件不可取得时明确显示缺失，并保留手动导入入口。

“自动适配”在产品中需要有具体含义：筛选已有兼容版本、安装依赖、应用已有补丁与恢复快照，都可以形成可验证的功能；仅修改版本声明不能作为兼容成功的证据。真正修改旧 Mod 的脚本和内容需要另外验证，不能从版本号匹配直接推导。

建议最先做的验证，是将同一份实测配置在两套干净环境中恢复，核对文件与顺序，再测试启动、读档及继续游戏。竞品的下载便利度、误报率和快照恢复能力也应在实际体验后比较。

## 补充：PCL 的运作模式与可借鉴之处

补充日期：2026-10-27。

PCL 的核心角色是桌面启动器和安装管理工具。Mod 作者通常把作品发布到 CurseForge、Modrinth 等资源平台；PCL 接入平台数据，展示资源、筛选版本并下载、安装文件。Minecraft 运行时的 Mod 加载则由 Forge、Fabric、NeoForge 等另外的组件完成，PCL 负责安装和配置这些组件。

可将职责分为四层：作者制作资源；平台保存资源和版本信息；PCL 完成搜索、下载、安装、环境管理与启动；加载器在游戏运行时加载 Mod。PCL 的主要工作流不要求作者再把全部 Mod 上传给 PCL，也超出了网页链接导航的范围。

整合包可以通过“清单＋配置”的方式分发。例如 Modrinth 的 `.mrpack` 记录游戏和加载器版本、待下载文件的地址与校验值，还可附带配置覆盖文件。启动器导入后按清单下载资源、准备运行环境和复制配置。不同格式也可能直接包含部分资源，不能把所有整合包都视为只有链接。

对 CK3 的建议是先做“外部资源来源＋自己的兼容配置目录＋本地安装管理器”。我们提供软件下载和版本管理，后续让整合包维护者分享具体文件版本、加载顺序、补丁和测试结果；游戏实际读取 Mod 可以沿用 CK3 自身机制。资源获取要按各来源实际支持的接口和历史文件供应情况实现，不能假定 Steam 工坊与 Minecraft 平台拥有相同的开放能力。自建完整 Mod 文件托管平台可作为后续独立选择。

原始依据：

- [PCL 官方项目](https://github.com/Meloong-Git/PCL)：下载和游戏启动模块。
- [PCL 整合包安装源码](https://github.com/Meloong-Git/PCL/blob/main/Plain%20Craft%20Launcher%202/Modules/Minecraft/ModModpack.vb)：解析平台清单、准备加载器、下载文件并安装配置。
- [CurseForge 作者投稿说明](https://support.curseforge.com/support/solutions/articles/9000197241-creating-and-submitting-a-project)：作者在平台创建项目、上传文件并标注支持版本。
- [Modrinth 整合包格式](https://support.modrinth.com/en/articles/8802351-modrinth-modpack-format-mrpack)：文件下载、校验值、游戏与加载器依赖、配置覆盖目录。
- [Fabric 官方说明](https://fabricmc.net/)：Fabric Loader 的运行时加载器职责。
