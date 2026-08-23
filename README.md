# BingWallpaper

一个干净、便携、开源的 Bing 每日壁纸客户端（Windows / 单文件 / 托盘常驻）。

> 截图占位：`docs/screenshot-dark.png`（深色模式下的设置界面与托盘菜单）。
> 首个构建产物在真机运行后补充。

---

## 特性

- 常驻托盘，无主窗口，默认每小时检查一次今日壁纸
- 4K（`_UHD.jpg`）/ 1080p（`_1920x1080.jpg`）分辨率由客户端自己决定，不受接口返回值影响
- 支持 14 个常见市场（设置界面为只读下拉框；INI 里可手填任意市场代码，程序会把它补进列表）
- 最近 8 天历史壁纸缩略图网格，一键切换；托盘菜单可直接上一张 / 下一张
- 可锁定某一张壁纸，不再随检查间隔自动更换，重启后依旧保持
- 六种填充方式：填充 / 适应 / 拉伸 / 平铺 / 居中 / 跨区
- 手写深色模式，**在 Windows 10 上同样有效**（不依赖仅 Windows 11 可用的 `Application.SetColorMode`），可跟随系统实时切换
- 完全便携：配置、日志、壁纸全部位于程序目录，删除整个文件夹 = 完整卸载
- 零第三方依赖（仅 BCL + P/Invoke），**单个几百 KB 的 exe，无需安装任何运行时**

## 系统要求

- Windows 10 1903（build 18362）及以上，64 位；**Windows 10 LTSC 2021（19044）满足要求**
- **无需安装 .NET 运行时**：程序基于 .NET Framework 4.8，该版本自 Windows 10 1903 起随系统内置

## 安装与使用

1. 从 [Releases](../../releases) 下载 `BingWallpaper.zip` 并解压，得到一个 `BingWallpaper` 文件夹
2. 把这个文件夹放到一个**有写入权限**的位置（例如 `D:\Tools\`，或 U 盘）
   - 放在 `C:\Program Files` 之类不可写的位置时，程序会明确报错退出，不会偷偷改写 `%APPDATA%`
3. 双击文件夹里的 `BingWallpaper.exe`，托盘出现图标后即开始工作

### 为什么用 .NET Framework 4.8 而不是 .NET 10？

因为要做到「体积小 **且** 免安装」，在 Windows 上只有这一条路：

| 方案 | 体积 | 是否需要安装运行时 |
|---|---|---|
| .NET Framework 4.8（本项目） | ~200 KB | **否**，Windows 10 1903+ 内置 |
| .NET 10，依赖框架 | ~280 KB | 是，需装 .NET 10 Desktop Runtime |
| .NET 10，自包含单文件 | ~47 MB | 否 |

.NET 5 及以后（.NET Core 血统）**没有任何版本内置于 Windows**，所以「把 .NET 10 降到 .NET 8」并不能免安装。
能免安装的最高版本就是 .NET Framework 4.8——4.8.1 只在 Windows 11 22H2+ 内置，Win10 上仍需手动安装。
体积小的开源 Windows 工具（例如 shadowsocks-windows 4.x）走的也是同一条路。

代价是 .NET Framework 不再演进，且缺少一些现代 API；本项目相应地自己实现了 JSON 解析之外的
兼容处理（见「技术说明」）。

托盘右键菜单：

```
当前壁纸日期与标题    点击用浏览器打开图片来源（版权链接）
──────────────
上一张 / 下一张      在最近 8 天内前后切换（不改变锁定状态）
选择日期...          打开历史壁纸窗口，点选一张即设为壁纸并锁定
立即刷新             只更新图片列表，不会解除锁定
锁定当前壁纸 ✓       勾选后不再随检查间隔自动更换；取消勾选立即回到今日壁纸
──────────────
打开壁纸目录
──────────────
设置...
退出
```

锁定期间第一行的日期会带上中括号（`[2026-08-23] · 图片标题`），解除锁定即恢复。

### 目录布局

```
<程序目录>\
├─ BingWallpaper.exe
├─ BingWallpaper.ini        # 配置，纯文本可手改
├─ BingWallpaper.log        # 日志（512KB 轮转，保留一个 BingWallpaper.log.1）
└─ wallpapers\
   └─ 20260818_WhyteCliffP_UHD.jpg
```

壁纸文件名是 `<日期>_<图片标识>_<分辨率>.jpg`，其中图片标识取自接口返回的 `OHR.` 标记，
**跨市场相同**——同一张照片同时下发到 de-DE / en-IN / fr-FR 时，本地只会存一份，
切换市场时命中已有文件就完全不联网下载。

### 配置文件

```ini
[General]
Market=zh-CN
Resolution=UHD              ; UHD 或 1920x1080
Fit=Fill                    ; Fill / Fit / Stretch / Tile / Center / Span
Theme=System                ; System / Light / Dark
RefreshIntervalHours=1
KeepDays=30                 ; 0 = 永久保留
RunAtStartup=false
PinnedWallpaper=            ; 锁定的壁纸文件名，留空 = 跟随检查间隔自动更换
```

程序不接受任何命令行参数：能配置的东西都在这个文件里。

---

## 与微软官方 Bing Wallpaper 的差异

这是本项目存在的核心理由。官方客户端会做的事情，本项目**一件都不做**：

| | 官方 Bing Wallpaper | 本项目 |
|---|---|---|
| 注入桌面右键菜单 | 会 | **不会** |
| 修改浏览器默认搜索引擎 / 主页 | 安装流程中会引导 | **不会** |
| 常驻推送资讯、广告、活动弹窗 | 会 | **不会** |
| 劫持桌面点击、附加桌面浮层 | 会 | **不会** |
| 安装到 `%LOCALAPPDATA%` 并注册卸载项 | 会 | **不会**，单文件便携 |
| 遥测 / 账号登录 | 有 | **无**，除 Bing 图片接口外不联网 |
| 卸载残留 | 有 | 删除文件夹即彻底卸载 |

本程序只做一件事：把 Bing 每日图片下载下来，设为壁纸。

---

## What this program touches（本程序改动了什么）

透明是这个项目的底线，所以逐条列清楚。

### 本程序自己写入的位置

| 位置 | 何时写入 | 说明 |
|---|---|---|
| `<程序目录>\BingWallpaper.ini` | 首次启动、修改设置时 | 配置文件 |
| `<程序目录>\BingWallpaper.log(.1)` | 运行期间 | 日志，512KB 轮转 |
| `<程序目录>\wallpapers\*.jpg` | 下载壁纸时 | 按保留天数自动清理；当前使用中的文件和已锁定的文件永不删除 |
| `HKCU\Control Panel\Desktop` 的 `WallpaperStyle` / `TileWallpaper` | 每次设置壁纸 | **设置壁纸的必要条件**：必须先写这两个值再调用 `SystemParametersInfoW`，否则填充方式不生效 |
| `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 的 `BingWallpaper` | **仅当你在设置里打开「开机自动启动」** | 值为加引号的 exe 绝对路径。程序移动位置后，下次启动会自动修正该值；关闭开关时会删除该值 |

除此之外，本程序不写入 `%APPDATA%`、`%LOCALAPPDATA%`、「我的图片」或任何程序目录之外的位置。

### Windows 自己写入的位置（不是本程序所为）

只要**任何**程序（包括系统「个性化」面板）设置了壁纸，Windows 就会自行写入以下位置：

| 位置 | 说明 | 如何清理 |
|---|---|---|
| `HKCU\Control Panel\Desktop` 的 `Wallpaper` | 当前壁纸路径，由 `SystemParametersInfoW` 写入 | 通过系统「个性化 → 背景」重新选择壁纸即可覆盖 |
| `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Wallpapers` 的 `BackgroundHistoryPath0..4` | 最近 5 张壁纸的历史记录 | 可手动删除这些值 |
| `%APPDATA%\Microsoft\Windows\Themes\TranscodedWallpaper` | Windows 转码后的壁纸副本 | 可手动删除，系统会在下次换壁纸时重建 |
| `%APPDATA%\Microsoft\Windows\Themes\CachedFiles\` | 壁纸缓存 | 同上 |

要彻底清理：在设置界面**取消勾选「开机自动启动」**（这会删除 Run 键下的 `BingWallpaper` 值），然后删除整个程序目录。
上面列出的由 Windows 自行写入的位置，本程序无法安全移除，需按表中说明手动处理。

---

## 构建

需要 .NET SDK（用于 `dotnet` 命令）与 .NET Framework 4.8 目标包（Visual Studio 或 Build Tools 自带）：

```powershell
dotnet publish src/BingWallpaper/BingWallpaper.csproj -c Release -o publish/BingWallpaper
```

产物是单个 `publish\BingWallpaper\BingWallpaper.exe`，没有附带 DLL，也没有 `.exe.config`。

CI（`.github/workflows/build.yml`，`windows-latest`）在每次 push 时构建，并以 `-warnaserror`
保证零编译警告，产出上传为 artifact；打 `v*` 标签时另外把 `BingWallpaper.zip` 挂到 GitHub
Release 上。两个下载解压后都是一个 `BingWallpaper` 文件夹，程序的配置、日志和壁纸都写在里面。

版本号由 CI 从 git 决定：打标签的构建取标签名（`v1.2.0` → `1.2.0`），其余构建取
`git describe`（如 `1.2.0-5-gabc1234`），因此开发构建能直接对应到具体提交。

## 技术说明

- C# + WinForms（`net48`，C# 最新语言版本），UI 全部由代码构建，无窗体设计器、无 `.Designer.cs`
- JSON 用 `JavaScriptSerializer`（`System.Web.Extensions`，随 .NET Framework 内置）解析，
  因为 `System.Text.Json` 不属于 .NET Framework，而本项目不引入任何 NuGet 包
- 按系统 DPI 感知（`dpiAware=true`）+ `AutoScaleMode.Dpi` 缩放；.NET Framework 的 WinForms 只有在
  额外的 app.config 开关下才支持 PerMonitorV2，为保持单文件而放弃该特性（多显示器不同缩放时由系统拉伸）
- 显式套用系统 UI 字体（`SystemFonts.MessageBoxFont`）：.NET Framework 的控件默认字体仍是
  MS Sans Serif 8.25pt
- 图标是一个多分辨率 `.ico`（256/128/64/48/32/24/20/16），同一个文件既由 `ApplicationIcon` 编进 exe 的
  Win32 资源（资源管理器、任务栏、Alt+Tab），也作为嵌入资源打包：托盘按当前 DPI 要 16/20/24 像素，而
  `Icon.ExtractAssociatedIcon` 只会给出 32×32；窗口图标同样要显式赋值，WinForms 不继承 exe 图标
- 16/20/24 三帧是从 128 帧重采样生成的，不是逐级均值缩小：均值缩小出来的小帧笔画落在半像素上，
  标题栏和托盘里看着发虚
- 深色模式手写实现：读取 `AppsUseLightTheme` 注册表值（只读）、监听 `WM_SETTINGCHANGE`/`ImmersiveColorSet`、
  `DwmSetWindowAttribute(20)` 深色标题栏、`SetWindowTheme` + uxtheme 未文档化序号导出（#135 / #133 / #104）。
  所有未文档化调用均包在 try/catch 中，失败时降级为浅色并记录日志
- 图片身份使用接口返回的 `startdate` 而非本地日期（zh-CN 市场在 UTC 16:00 换图）
- 缓存文件名不含市场代码：市场描述的是「从哪个频道拿到这张图」，不是图片本身的属性。
  身份取 `urlbase` 里的 `OHR.<名称>` 标记（`/th?id=OHR.WhyteCliffP_ZH-CN0573407830` → `WhyteCliffP`），
  它跨市场稳定，于是反复切换市场不会把同一张照片存成好几份
- 切换分辨率后，同一张图的旧尺寸副本会被清掉——但**只在当前分辨率那份确实存在时**才删，
  所以这一步只会去掉多余副本，永远不会删掉某张图的最后一份
- 下载先写 `.tmp`、解码校验通过后再原子改名，避免半截文件被当作有效缓存
- 全局异常钩子（`Application.ThreadException`、`AppDomain.UnhandledException`、`TaskScheduler.UnobservedTaskException`）
  会把完整异常链写入日志并弹出可复制文本的错误对话框
- 单选框 / 复选框为自绘控件：系统绘制的字形在深色背景下会变成「黑底黑点」，自绘后选中态在两种主题下
  都是强调色蓝
- 全球化功能保持启用：曾经开启的 `InvariantGlobalization`（.NET 10 时期）会让 WinForms 在切换输入法
  （`WM_INPUTLANGCHANGE` → `CultureInfo.GetCultureInfo(lcid)`）时直接崩溃

## 免责声明

- 本项目为**非官方**项目，与 Bing 及 Microsoft 没有任何关联、认可或合作关系。
- "Bing"、"Microsoft"、"Windows" 是 Microsoft Corporation 的商标。微软另有一款同名官方产品 Bing Wallpaper，
  与本项目无关，请勿混淆。
- Bing 每日图片的版权归原作者 / 版权方所有，Bing 仅授权其用作个人桌面壁纸。请勿将下载到的图片用于其他用途。
- 本软件按「现状」提供，不作任何担保，使用风险自负。

## 许可证

[GPL-3.0-or-later](LICENSE)（仅涵盖本项目代码，不涵盖任何 Bing 图片内容）。
