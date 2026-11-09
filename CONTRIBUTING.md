# 开发与 Pull Request 规范

本规范适用于协作者及参与本项目的 AI agent。所有合入 `main` 的变更通过 PR 审查；尚未完成或缺少必要验证的工作使用 Draft PR。


## Contributor quick start

Issues and PRs are welcome in English or Simplified Chinese.

1. Fork the project and create a branch from the latest `main`.
2. Keep the PR focused. For a bug report, include the game version, OS, steps to reproduce, and what you expected to see. Use a small fixture or a redacted excerpt for examples.
3. For code changes, run the checks below. For UI changes, attach a screenshot or describe the flow you tested. For a feature or behavior change, document its acceptance criteria in `spec/`.
4. Open a PR against `main` and include the results of the checks you ran.

```sh
dotnet restore SteamModManager.sln
dotnet build SteamModManager.sln --configuration Release --no-restore
dotnet test --solution SteamModManager.sln --configuration Release --no-build
git diff --check
```

## 1. 分支与变更范围

- 从最新 `origin/main` 创建独立分支，例如 `feat/workshop-download`、`fix/remove-task`、`perf/cache-budget`、`docs/pr-guidelines`。
- 一个 PR 解决一个明确问题。为该问题所需的代码、测试、文档和变更记录一起提交；无关功能、主题调整、依赖升级另开 PR。
- 多人或多个 agent 同时工作时，使用各自的分支和 worktree。提交前检查暂存区，只纳入本次任务拥有的文件或代码块，不把其他人的未提交工作一并提交。
- 不直接向 `main` 推送，不对 `main` 或他人的分支强制推送。合并前解决冲突，并针对解决冲突后的代码重新验证。

功能、行为修复和契约变更在 [软件规格模板](spec/TEMPLATE.md) 中写明范围、现有责任模块 与验收条件，再实施、审查并回填验证。每份 Spec 对应一项独立可验收的改动，PR 引用同一份记录。仓库入口见 [AGENTS.md](AGENTS.md)。

## 2. 提交与标题

提交消息和 PR 标题使用 `类型(范围): 中文说明`，直接说明改动后的行为。

常用类型：`feat`、`fix`、`perf`、`refactor`、`docs`、`test`、`ci`、`chore`。

示例：

```text
fix(download): 移除任务时自动取消下载
perf(cache): 限制搜索与封面缓存容量
docs(contributing): 增加 PR 提交规范
```

避免“更新代码”“修复一些问题”等无法判断范围的标题。PR 扩大或改变范围时，同步重写标题和说明。

## 3. PR 说明必须包含什么

使用仓库的 [PR 模板](.github/PULL_REQUEST_TEMPLATE.md)，按最终实现填写：

1. **问题与结果**：触发条件、原来的表现、修改后的表现；修复问题时给出可复现步骤。
2. **主要改动**：涉及的模块与必要设计选择，说明是否影响其他游戏、配置格式或已有用户数据。
3. **验证证据**：实际执行的命令、通过／失败／跳过数量；手工验证写明系统、游戏及版本、步骤和结果。
4. **风险与恢复**：已知限制、未验证的平台或场景、涉及持久化数据时的兼容与回退方式。无数据格式变更时可简写。
5. **关联资料**：有对应 Issue、设计文档或截图时给出链接。没有 Issue 不阻止提交 PR。

测试结果必须对应待合并的最新提交。夹具测试、界面渲染、真实 Steam 联调分别描述；未执行的检查明确写“未执行”和原因。不得把模拟来源的测试当作真实账户下载成功，也不得把“进程启动成功”当作完整功能验收。

UI 改动附修改后的截图；交互变化补充复现步骤，必要时提供前后对照或短录屏。认证二维码、账户信息、令牌及私人目录信息应从分享材料中移除或遮盖。

## 4. 必须完成的检查

修改应用代码、依赖、构建或 CI 时，执行：

```shell
dotnet restore SteamModManager.sln
dotnet build SteamModManager.sln --configuration Release --no-restore
dotnet test --solution SteamModManager.sln --configuration Release --no-build
git diff --check
```

- 项目引用方向由构建中的 [`SMM001` 检查](docs/architecture.md#允许的项目引用)约束。验收、所有权、契约和跨层写入仍需对照 Spec 审查。
- 构建与测试之间继续修改了代码，需要重新构建；不能用旧产物的 `--no-build` 结果证明新代码通过。
- 行为修复补充能复现问题的回归用例；已有用例充分覆盖时，说明复用了哪些检查。纯文案或样式调整可使用相关现有测试及渲染验证，不为实现细节增加重复测试。
- 仅修改 Markdown 或 PR 模板时，检查链接、内容一致性和 `git diff --check` 即可，在 PR 中说明未运行应用构建与测试的原因。
- 功能 PR 合并前完整回归必须通过；不能以“与本次无关”为由忽略失败。需要变更验收标准或处理其他分支问题时，先将 PR 保持为 Draft，并列明阻塞项。
- 现有 [Build and test](.github/workflows/build.yml) 的 Windows、macOS、Linux 三平台检查全部成功后才能合并。平台未完成实机验证的情况仍须在 PR 中列明。

## 5. 按改动类型验收

仅填写本次涉及的项目，不要求每个 PR 重测全部功能。

| 改动类型 | 必须核对的行为 |
| --- | --- |
| 下载与任务 | 连续点击不重复创建活跃下载；排队、授权、传输时移除会自动取消并等待退出；终态写入与迟到事件不会让已移除记录复活；取消或失败不会标为安装成功。 |
| 删除安装内容 | 确认前文件保留。核对目标路径、安装身份和物品锁。Steam 取消订阅失败时保留内容，成功后核对目标文件确实消失，其他 Mod 与运行配置仍可读取。 |
| 文件安装 | 校验通过后才发布内容；失败保留已有可用修订；路径约束、安装恢复及真实工坊身份正确；移除任务记录不会删除已安装 Mod。 |
| 搜索与缓存 | 只请求所需页面和详情；说明容量、过期与淘汰策略；覆盖旧缓存和残留临时文件；后台维护不阻塞 UI，不越界删除设置、配置或 Mod 内容。 |
| UI 与兼容状态 | 使用简短文案；“兼容”为绿色、“不兼容”为红色、“未知”为灰色；切换页面、游戏、版本后绑定正确，异步旧结果不会覆盖当前状态。 |
| 多游戏支持 | App ID、目录、缓存、配置和任务按游戏隔离；新增游戏分别列出浏览、发现路径、本地解析、安装的支持状态，不能仅凭工坊可访问宣称完整支持。 |
| 来源与认证 | 使用真实来源和真实进度；认证、网络、权限及内容不可用有明确结果；不保存或输出密码、刷新令牌等敏感信息。 |

遵守现有分层：Core 保存模型与规则，Application 编排用例，Infrastructure 处理网络和文件，Desktop 负责界面绑定。新增后台刷新时说明触发条件与范围，保持按需加载，避免引入未说明的全量预取或周期扫描。

## 6. 文档、日志与提交内容

- 变更说明写入 PR 描述，概述本次内容、实际验证及已知限制。私人工作记录（如 `log.md`、`progress.md`、`task_plan.md`）保留在本地，不提交到仓库。
- 用户可见行为、命令、配置或架构改变时，同步对应说明。新增或升级依赖时记录用途、版本和许可证，补齐必要声明。
- 提交前检查完整暂存差异及文件列表，排除缓存、真实 Mod／游戏／存档内容、登录凭证、`.env`、个人配置和本机绝对路径。
- `artifacts/`、`bin/`、`obj/`、`TestResults/`、安装包及截图产物不进入源码提交。需要分享的构建使用 CI 产物；截图通过 PR 附件提供。
- 临时任务计划、个人笔记和第三方参考仓库不随业务 PR 混入；长期架构与用户说明放入 `docs/`，单项变更规格与验收记录放入 `spec/`。

## 7. 审查与合并

- 作者先检查完整 diff，再请求至少一位非作者协作者审查。AI 自查可以提供证据，不能替代其他协作者在 GitHub 上的批准。
- 待合并提交需要至少一个有效批准；影响审查结论的新改动应重新请求审查。
- 未解决的正确性、数据完整性、敏感信息或关键交互问题阻止合并；审查意见逐项回复，未采纳时给出具体理由。
- PR 描述和最终代码一致，必需检查通过、冲突解决后再合并。默认使用 Squash merge，使一项完整变更对应一个主线提交。
- 合并后清理已完成的远端工作分支；删除本地 worktree 前确认没有未提交内容。

这些文件提供协作规范和提交模板。若要由 GitHub 强制拦截直接推送、缺少批准或 CI 未通过的合并，还需要仓库管理员另行配置远端分支保护；本文件本身不会启用保护规则。
