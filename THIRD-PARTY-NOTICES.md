# 第三方依赖声明

项目使用的第三方组件包括：

- Avalonia 12.1.4：跨平台桌面 UI，MIT。许可证见[上游源码](https://github.com/AvaloniaUI/Avalonia/blob/12.1.4/licence.md)。

- SteamKit2 3.5.0：Steam 网络协议与内容下载库，LGPL-2.1；上游 https://github.com/SteamRE/SteamKit ，固定版本源码 https://github.com/SteamRE/SteamKit/tree/3.5.0 。许可证副本见 `licenses/SteamKit2-LGPL-2.1.txt`。通过独立 NuGet 程序集引用，未修改库源码；发布目录保留可替换程序集。
- QRCoder 1.6.0：在本地生成 Steam 登录二维码，MIT；上游 https://github.com/codebude/QRCoder 。许可证副本见 `licenses/QRCoder-MIT.txt`。
- Microsoft.Data.Sqlite 10.0.0：读写 P 社启动器的 SQLite 播放集并创建一致性备份，MIT；上游 https://github.com/dotnet/efcore 。许可证副本见 `licenses/Microsoft.Data.Sqlite-MIT.txt`。
- SQLitePCLRaw.bundle_e_sqlite3 2.1.14 及其同版本 core/provider/native 依赖：随应用提供跨平台 SQLite，Apache-2.0；上游 https://github.com/ericsink/SQLitePCL.raw 。固定 2.1.14 以替代上游默认依赖的存在已知漏洞的 2.1.11。许可证副本见 `licenses/SQLitePCLRaw-Apache-2.0.txt`。

本应用的下载适配代码为独立实现，未复制或打包 DepotDownloader、PCL 的业务源码。

Steam 和各游戏名称及相关商标归各自权利人所有，用于识别兼容的服务和游戏。
