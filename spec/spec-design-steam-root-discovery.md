---
title: "Windows 非默认目录的 Steam 安装发现"
date_created: 2026-11-11
owner: "SteamGameLocator"
---

# 背景与目标

用户把 Steam 安装在自定义目录（非 Program Files，示例：`D:\SteamLibrary`），启动后只看到 7 个内置游戏，已安装的骑马与砍杀2（AppId 261550）没有出现。原因是根目录发现只检查固定默认位置，而 `%LOCALAPPDATA%\Steam`（只有 htmlcache 和 local.vdf 的缓存目录）恰好存在，被当成 Steam 安装：既不报「未发现 Steam」，又因为该目录没有 `steamapps` 而扫不到任何库。

目标是让 Steam 安装位置可从 Windows 注册表补全，并且只有真正包含 `steamapps` 的目录才被当作 Steam 根目录。

## 1. 范围与非目标

范围：Steam 根目录收集（默认位置、Windows 注册表、测试注入的根目录）、根目录合法性校验（`steamapps`）、以及由此影响的库扫描与已安装游戏发现。

非目标：不遍历磁盘搜索 Steam；不改变 macOS/Linux 的默认位置；不改变用户手动填写的 Steam 路径的存在性检查；不改变发现型游戏的工坊判定规则；不处理 Steam 已安装但从未启动（尚未生成 `steamapps`）的极端情况。

## 2. 需求与验收条件

| ID | 给定 | 当 | 则 |
| --- | --- | --- | --- |
| AC-01 | Steam 装在自定义盘符或目录，注册表 `HKCU\Software\Valve\Steam\SteamPath` 指向它 | 启动或保存设置后刷新 | 该目录作为根目录被扫描，库中的已安装游戏可被发现 |
| AC-02 | 存在只有缓存内容的同名目录（如 `%LOCALAPPDATA%\Steam`，没有 `steamapps`） | 收集自动根目录 | 该目录被忽略；若没有其他有效根目录，状态栏给出未发现 Steam 的提示 |
| AC-03 | 注册表项缺失、无权限或当前不是 Windows | 读取注册表 | 忽略错误继续，其他根目录与流程不受影响 |
| AC-04 | 用户保存的 Steam 路径存在且包含 `steamapps` | 启动时读取保存路径 | 与修复前行为一致，不产生重复库 |

## 3. 责任模块与实现步骤

- 新增 `src/Ck3ModManager.Infrastructure/SteamRootDiscovery.cs`：Windows 下依次读取 `HKCU\Software\Valve\Steam\SteamPath`、`HKLM\SOFTWARE\WOW6432Node\Valve\Steam\InstallPath`、`HKLM\SOFTWARE\Valve\Steam\InstallPath`；读取失败返回空集合。
- 修改 `src/Ck3ModManager.Infrastructure/SteamGameLocator.cs`：`FindLibraries` 把自动根目录（`steamRoots` 为空时的默认位置 + 注册表）与测试注入根目录统一经 `LooksLikeSteamRoot` 过滤，要求 `<root>/steamapps` 存在；版本声明与库内路径逻辑不变。
- `SteamGameDiscovery` 不改动：仍通过 `FindLibraries` 取得库列表并逐个扫描 `appmanifest_*.acf`。

## 4. 契约、依赖与兼容性

- 公开 API 不变；新增 `internal` 的 `SteamRootDiscovery` 与 `SteamGameLocator.LooksLikeSteamRoot`，测试项目通过既有 `InternalsVisibleTo` 访问。
- 使用 .NET 10 目标包/共享框架自带的 `Microsoft.Win32.Registry`，不新增 NuGet 依赖；仅在 Windows 上调用，其他平台直接返回空集合。
- 已保存的配置与路径无需迁移；被过滤的只会是自动候选目录，用户显式路径仍按原有逻辑处理。

## 5. 测试策略

- `tests/Ck3ModManager.Infrastructure.Tests/SteamRootDiscoveryTests.cs`：
  - `RequiresSteamAppsDirectory`：空目录不合法，加入 `steamapps` 后合法（AC-02）。
  - `FindLibrariesIgnoresConfiguredRootsWithoutSteamApps`：注入伪根与合法根，只有合法根进入库列表，伪根不进入已知根（AC-01/AC-02）。
  - `RegistryRootsAreEmptyOutsideWindows`：非 Windows 平台注册表读取返回空（AC-03）。
- 既有 `SteamGameDiscoveryTests` 覆盖库解析、清单读取、发现型游戏与保存路径的回归（AC-04）。
- 真实 Windows 上的注册表读取与界面显示由用户或 Windows CI 复核，本次 Linux 开发环境无法实机验证。

## 6. 审查与执行结果

- `dotnet build SteamModManager.sln --configuration Release`：0 error（82 warnings，均为既有警告）。
- `dotnet test --solution SteamModManager.sln --configuration Release --no-build`：630 passed / 0 failed（新增 3 项）。
- `git diff --check`：通过。
- Windows 实机探针（同一份 Infrastructure 代码，win-x64 发布后在该机运行）：注册表返回 3 条根目录（`HKCU\Software\Valve\Steam\SteamPath` 与两条 `HKLM` `InstallPath`，均指向同一自定义安装目录），每条都包含 `steamapps`；发现型游戏列表包含 `steam-261550`（Mount & Blade II: Bannerlord），`%LOCALAPPDATA%\Steam` 假根未进入根目录列表。CK3 未安装，因此 CK3 侧仍提示手动指定目录，符合预期。
- 说明：探针通过 WSL interop 调用 Windows 上的 .NET 10 运行时执行，未改动用户安装目录；Linux 环境的注册表分支仍只由单测覆盖。

## 7. 相关资料

- `spec/spec-process-repository-sdd.md`
- `README.md` 的 Game discovery 段
- 复现现场：Steam 根目录为自定义目录（示例 `D:\SteamLibrary`，真实路径已脱敏），注册表 `SteamPath` 指向该目录，`libraryfolders.vdf` 仅一个库
