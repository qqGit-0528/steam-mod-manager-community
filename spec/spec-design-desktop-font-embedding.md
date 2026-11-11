---
title: "桌面端内嵌中文字体"
date_created: 2026-11-11
owner: "Desktop"
---

# 背景与目标

在未安装中文字体的系统上（最小化 Linux 环境、部分容器与 CI 镜像），桌面端中文界面会整屏显示方框（tofu）。用户希望不额外安装系统字体也能正常阅读中文界面。

## 1. 范围与非目标

- 范围：Desktop 应用的全局字体资源、主窗口字体绑定与随程序分发的字体许可文件。
- 非目标：不修改中英文资源与语言切换逻辑，不引入运行时字体下载，不改动 CLI。
- 拉丁字母、数字与常用标点由同一字体覆盖；emoji 与特殊符号仍走平台字体回退。

## 2. 需求与验收条件

| ID | 给定 | 当 | 则 |
| --- | --- | --- | --- |
| AC-01 | 系统未安装任何中文字体 | 启动桌面应用 | 中文界面文字正常显示，不出现方框 |
| AC-02 | 系统未安装任何中文字体 | 切换到英文界面 | 英文文字正常显示，布局不受影响 |
| AC-03 | 任意系统 | 应用加载 | 字体从程序内嵌资源加载，不要求系统安装 |
| AC-04 | 字体随程序分发 | 检查分发内容 | 附带 SIL OFL 1.1 许可证文本 |

补充：字体资源缺失或加载失败时，应用仍应使用平台默认字体启动，不因字体问题崩溃。

## 3. 责任模块与实现步骤

- `src/Ck3ModManager.Desktop/Assets/Fonts/NotoSansSC-Regular.otf`、`OFL.txt`：新增内嵌字体与许可证。
- `src/Ck3ModManager.Desktop/Ck3ModManager.Desktop.csproj`：`AvaloniaResource Include="Assets/**"` 已存在，字体随程序集打包，无需改动。
- `src/Ck3ModManager.Desktop/App.axaml`：新增 `AppFontFamily` 资源与 `Window` 样式，使所有窗口默认使用该字体。
- `src/Ck3ModManager.Desktop/MainWindow.axaml`：主窗口字体改为引用该资源，避免局部覆盖。

## 4. 契约、依赖与兼容性

- 新增约 8.3 MB 静态资源，安装包体积相应增加；不改变公开接口、配置格式或持久化数据。
- 字体为 SIL OFL 1.1，允许随程序分发；保留版权声明与许可证全文。
- 旧系统若已安装中文字体，行为不变，仅优先使用内嵌字形。

## 5. 测试策略

- 单元测试：解析 `AppFontFamily` 资源，确认字体可解析且包含中文字形（`中`）。
- 渲染验证：在无中文字体的环境（本机 WSL，`fc-list :lang=zh` 为空）用 headless 截图对比修复前后界面。
- 通用门禁：`dotnet restore/build/test` 与 `git diff --check`。

## 6. 审查与执行结果

- `dotnet build SteamModManager.sln --configuration Release --no-restore`：通过，0 error。
- `dotnet test --solution SteamModManager.sln --configuration Release --no-build`：628 项全部通过，含新增的 `FontEmbeddingTests`。
- `git diff --check`：通过。
- 渲染验证：在本机（WSL，`fc-list :lang=zh` 为空）用 `PARADOX_UI_SCREENSHOT_DIRECTORY` 运行 headless 截图测试；修改前主界面中文全部显示为方框，修改后中文正常显示，英文、数字与布局不受影响。截图见 PR 描述。
- 残余风险：新增约 8.3 MB 资源使安装包变大；字体资源加载失败时回退到平台默认字体，不会导致启动失败（未单独构造该故障场景）。

## 7. 相关资料

- [协作规范](../CONTRIBUTING.md)
- [桌面中英文切换](spec-design-desktop-language.md)
