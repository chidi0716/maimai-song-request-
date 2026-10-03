# SongRequest · 点歌台 mod

游戏里跑一个本机网页：搜歌、点难度，游戏直接跳过去。**不改任何游戏文件。**

> 本项目 fork 自 **[ZCM-X/maimai-song-request-](https://github.com/ZCM-X/maimai-song-request-)**，感谢原作者 **[ZCM-X](https://github.com/ZCM-X)** 的点歌台 mod。
> 这个版本在原作基础上加了远程分享、定数显示与筛选、简繁搜索、繁体中文界面等功能（细节见 [CHANGELOG](CHANGELOG.md)）；部分稳定性修复已由原作者并入其 v1.0.5。

## 致谢

- 原作：**[ZCM-X](https://github.com/ZCM-X)** 的 [maimai-song-request-](https://github.com/ZCM-X/maimai-song-request-)（MIT 许可，见 [LICENSE](LICENSE)）
- 曲目别名数据来自 **MuNET 别名数据库**，原始社区别名由 **[clansty](https://github.com/clansty) 的 SongSearch 项目**整理与贡献 —— 感谢作者与社区贡献者。
- 简繁 / 日文字形对照表来自 [OpenCC](https://github.com/BYVoid/OpenCC)（Apache-2.0，见 [NOTICE](NOTICE)）

## 功能

- **曲目表实时来自游戏**：换版本、热导入自制谱都会自动跟上，不用改配置
- **网页搜索**：曲名 / 艺术家 / 曲目 ID / 社区别名（空格分词、忽略大小写与全半角）
- **简繁通用搜索**：简体 / 繁体 / 日文字形互通（樱 / 櫻 / 桜），曲名、艺术家、别名都适用
- **点难度块直接跳曲**：BASIC…Re:MASTER 五档带等级与**定数**（如 13.7），点了游戏就跳到该曲该难度
- **等级 / 定数筛选与排序**：`12`、`13+`、`12.0`、`13.4` 两种写法通用
- **DX / 标准** 徽章与筛选（点哪一行就跳哪种谱面）；该版本玩不了的谱面按游戏判断置灰
- **曲绘封面**：直接从游戏资源取图
- **实时游玩面板**：曲名 / 难度 / Combo / 分数 / 判定 / Life（SSE 推送）
- **手机可用**：同局域网手机浏览器打开即点歌；另有 OBS 透明浮层 `/overlay`
- **繁体中文（台湾）界面**
- **远程分享**：默认自动生成公网链接（免注册免设置），发给不在同一网络的人也能远程点歌。几条线路同时起、能用的都列出来：Cloudflare 免费隧道，以及不经过第三方、国内也能用的 IPv6 直连 / IPv4 直连（NAT 打洞）

## 安装

**将 dll 放置在游戏的 mods 文件夹，游戏加载完成后浏览器打开本地 IP 加 8790 端口即可点歌，例 `127.0.0.1:8790`。**

- 需要 [MelonLoader](https://melonwiki.xyz/) 0.6.x（Mono 版本）
- 构建：`dotnet build src/SongRequestMod.csproj -c Release -p:GameDir="<游戏Package目录>"`

更多：接口 [`docs/API.md`](docs/API.md) · 开发笔记 [`docs/DEVELOPMENT.md`](docs/DEVELOPMENT.md) · 使用说明 [`docs/使用说明.md`](docs/使用说明.md) · 数据来源 [NOTICE](NOTICE) · 许可 [MIT](LICENSE)
