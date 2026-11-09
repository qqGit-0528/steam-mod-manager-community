# 当前下载任务与进度实现核验

2026-11-03 后续更新：本文为接入前审计；默认下载入口现已换成本机 Steam，真实新下载与已安装复用均通过 C# 链路验证，当前行为见[下载说明](workshop-download.md)。


核验日期：2026-11-03。本轮只读取源码、安装程序集元数据和现存任务记录，运行已有测试；没有新建账户授权或下载任务，没有改动下载业务逻辑。

## 结论

当前已经有独立的 Steam 工坊内容下载实现和字节进度传递链路；没有接入 Steam 客户端的任务队列或 GetItemDownloadInfo。现存真实任务记录不能证明文件传输成功，不能把可控来源测试通过等同于真实 Steam 下载验收通过。

| 检查项 | 可核对结果 |
| --- | --- |
| 应用任务创建 | 有。点击后以 App ID、PublishedFileId 去重，分配 TaskId，Task.Run 启动后台流程。 |
| 来源及清单 | 有。通过 SteamKit 查询工坊身份、depot、解密密钥、manifest 请求码和文件清单。 |
| 文件传输 | 有。调用 SDK 的 DownloadDepotChunkAsync，取得解压后的文件块并实际写入 staging 文件。 |
| 内容进度 | 有。每个成功写入的文件块累加 completed，约每 250 ms 向应用服务报告一次，再转为任务快照和界面绑定。 |
| Steam 客户端任务进度 | 未接入。安装程序集没有对应 DownloadItem／GetItemDownloadInfo 成员引用，源码也没有对应适配器。 |
| 本次真实下载成功 | 尚无证据。现存最新任务 0 字节、无总大小、无修订、无安装目录，错误为授权轮询 FileNotFound；未找到应用自管内容的 installation.json。 |

## 创建与传递链路

1. `DownloadsViewModel.DownloadSelectedWorkshop` 调用 `WorkshopDownloadService.Start`，随后把快照加入下载列表。
2. `Start` 在锁中查重，创建本工具的任务，并通过 Task.Run 执行 RunAsync；这一步没有向 Steam 客户端下载队列提交物品。
3. `RunAsync` 等待队列，调用 provider.ResolveAsync；只有清单解析成功并检查已有内容之后，才调用 provider.DownloadAsync。
4. `SteamWorkshopContentProvider.DownloadAsync` 调用 Steam CDN SDK，文件块写入成功后执行 completed += written，再报告 IProgress。
5. 应用服务 Update 写入不可变 WorkshopDownloadSnapshot 并触发 Changed。桌面把事件投递到 UI 线程，DownloadRow.Update 通知绑定，Percent 根据 Bytes / TotalBytes 计算。

代码位置：`src/Ck3ModManager.Desktop/DownloadsViewModel.cs`、`src/Ck3ModManager.Application/WorkshopDownloadService.cs`、`src/Ck3ModManager.Infrastructure/SteamWorkshopContentProvider.cs`、`src/Ck3ModManager.Core/WorkshopDownloads.cs`。

进度并非定时增加的模拟百分比，但有以下明确语义：

- 分母是 manifest 文件解压后大小的总和。
- 分子是成功写入的内容字节，加上已经存在且通过 SHA-1 复核、可直接复用的完整文件字节。
- 速度另以成功内容块的压缩长度累计，不包含失败请求重传、协议开销和清单请求流量。
- 单个内容块接收期间没有逐网络包更新；块完成并写入后才增加内容进度。
- 排队、解析、等待扫码和校验安装阶段使用不定进度动画。等待扫码时蓝色条块不是已下载百分比，此时尚无可显示的真实内容进度。

## 二维码为什么会出现

独立 SteamKit 会话与电脑 Steam 客户端的既有会话不是同一个对象，代码需要自行完成认证。匿名阶段遇到 AuthenticationNeededException 才会进入 QR；但目前分类过宽：工坊详情任何非成功结果、游戏信息缺失、depot key 任意非 OK 结果或 manifest 请求码为零，都会被归为需要认证，而没有充分区分服务失败、物品不可用和真正缺少账户权限。

安装版失败后 Reset 会销毁本次连接，重启也不会保存授权，因此可能再次要求扫码。修改中的共享内存授权版本仍有“QR 已取得令牌、随后 CM 登录断线时令牌尚未缓存”的已知边界，还没有发布；不能说用户本机已经用到了该改进。

扫码只是授权阶段，出现二维码不能证明下载已经开始；授权轮询返回 FileNotFound 也不应直接解释成工坊物品不存在或地区限制。

## 安装程序集核对

通过 PEReader 只读解析 `/Applications/Ck3ModManager.app/Contents/MacOS/Ck3ModManager.Infrastructure.dll`，没有执行其中任何 Steam 登录或网络方法：

- provider 构造参数只有 allowAuthentication。
- 存在 DownloadDepotChunkAsync、WriteAsync 和 BeginAuthSessionViaQRAsync 的成员引用。
- 不含修改中新加的 SteamDownloadAuthentication 类型。
- 没有客户端 DownloadItem／GetItemDownloadInfo 的成员引用。
- 基础设施程序集 SHA-256：`fe0c0cb2c005125844630c58f2ade3a993ddd94fadd135e5230d97a85eaa856a`。

工具和原始结果保存在忽略的 `artifacts/download-progress-audit/`；不将实际账户信息、二维码或令牌写入报告。

## 测试证据与限制

本轮 Release 构建通过。定向复跑 WorkshopDownloadServiceTests 8 项、InAppDownloadTests 5 项，全部通过、无跳过。

这些测试使用可控 provider：服务层手动报告 2/4、4/4 字节，桌面测试写入受控 descriptor 文件。因此它们验证任务去重、状态传递、文件提交和界面联动，不能证明真实 Steam 账户、CDN、真实 Mod 文件已经贯通。

真实验收仍需记录同一任务的非零内容字节增长、实际文件与 manifest 校验、安装完成以及本地库结果。在该证据齐全前，本项目只能表述为“下载链路实现存在，真实 Steam 下载尚未验收通过”。
