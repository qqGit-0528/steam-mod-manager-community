# Steam Mod Manager 开发入口

本项目是 .NET 10 / Avalonia 桌面工具，用于按游戏浏览 Steam 工坊、管理本地 Mod 和运行配置。应用在 `src/`，验证在 `tests/`。

## 开工

1. 读[架构约定](docs/architecture.md#架构约定)与[仓库导航](docs/repo-map.md)，定位现有责任模块 和对应测试。
2. 功能、行为修复和契约改动使用仓库的 [规格驱动开发技能](.agents/skills/steam-mod-manager-sdd/SKILL.md)。从 [软件规格模板](spec/TEMPLATE.md)建立一项独立可验收的主记录。
3. 在已确认的范围内持续执行。验收歧义会改变结果，或需要破坏性迁移、权限变更、公开契约调整时，先明确影响并确认。保留用户已有改动，凭证和真实 Mod 数据留在本机。

## 边界与验收

- Core 定义模型、规则和端口。Application 与 Infrastructure 分别依赖 Core。Desktop 与 CLI 负责组合与入口。状态和写入点按架构约定维护。
- 从 Spec 逐项复核行为与失败路径，按[协作规范的验证命令](CONTRIBUTING.md#4-必须完成的检查)运行检查。失败原因不明时保留门禁，记录事实。
- 每次变更概要追加到本地 `log.md`，缺失则新建，文件不提交。分支、PR 和合并按[协作规范](CONTRIBUTING.md)执行。
