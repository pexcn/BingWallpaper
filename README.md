# BingWallpaper

一个干净、便携、开源的 Bing 每日壁纸客户端（Windows / WinUI 3 / 托盘常驻）。

> 截图占位：`docs/screenshot-dark.png`（深色模式下的设置界面与托盘菜单）。
> 首个构建产物在真机运行后补充。

---

## 特性

- 常驻托盘，无主窗口，默认每小时检查一次今日壁纸
- 4K（`_UHD.jpg`）/ 1080p（`_1920x1080.jpg`）分辨率由客户端自己决定，不受接口返回值影响
- 支持 14 个常见市场（设置界面为只读下拉框；INI 里可手填任意市场代码，程序会把它补进列表）
- 最近 8 天历史壁纸缩略图网格，一键切换；托盘菜单可直接上一张 / 下一张
- 可固定某一张壁纸，不再随检查间隔自动更换，重启后依旧保持
- 六种填充方式：填充 / 适应 / 拉伸 / 平铺 / 居中 / 跨区
- WinUI 3（Windows App SDK）界面，Fluent 控件、圆角、亚克力质感，深浅色由 `ElementTheme` 统一切换，
  并可跟随系统实时变化；托盘菜单是原生 Win32 菜单，深色模式通过 uxtheme 补齐
- 完全便携：配置、日志、壁纸全部位于程序目录，删除整个文件夹 = 完整卸载
- 除 Windows App SDK 外零第三方依赖，**免安装运行时**：.NET 与 Windows App SDK 都随程序打包

## 系统要求

- Windows 10 2004（build 19041）及以上，64 位；**Windows 10 LTSC 2021（19044）满足要求**
- **无需安装 .NET 运行时，也无需安装 Windows App SDK 运行时**：两者都随程序一起打包

## 安装与使用

1. 从 [Releases](../../releases) 下载 `BingWallpaper-<版本>-win-x64.zip`（约 69 MB，解压后约 166 MB）
2. 解压到一个**有写入权限**的目录（例如 `D:\Tools\BingWallpaper\`，或 U 盘）
   - 放在 `C:\Program Files` 之类不可写的位置时，程序会明确报错退出，不会偷偷改写 `%APPDATA%`
   - 压缩包里的文件要保持在一起：`BingWallpaper.exe` 需要同目录下的运行时文件，单独拷走 exe 无法启动
3. 双击 `BingWallpaper.exe`，托盘出现图标后即开始工作

### 关于体积：WinUI 3 不再是一个单文件小程序

这一点必须说清楚。改用 WinUI 3 之后，「几百 KB 单文件」不再成立：

| 方案 | 体积 | 是否需要安装运行时 |
|---|---|---|
| WinUI 3 + 自包含（本项目） | 166 MB / 449 个文件（压缩包 69 MB） | **否**，.NET 与 Windows App SDK 都在包内 |
| WinUI 3 + 依赖框架 | 数 MB | 是，需装 .NET Desktop Runtime **和** Windows App SDK 运行时 |
| WinForms + .NET Framework 4.8（旧实现） | ~200 KB 单文件 | 否，Windows 10 1903+ 内置 |

Windows 里**没有任何一个版本内置现代 .NET 或 Windows App SDK**，所以 WinUI 3 只能在
「体积」和「免安装」之间二选一。本项目选择免安装：便携性（拷贝即用、删除文件夹即卸载）
是这个项目存在的理由之一，让用户先去装两个运行时不能接受。

项目引用的是 `Microsoft.WindowsAppSDK.WinUI` 组件包而不是 `Microsoft.WindowsAppSDK` 元包：
后者代表「整个 SDK」，会把 AI、ML、Search、Widgets 一并塞进发布目录——光 `onnxruntime.dll`
和 `DirectML.dll` 就是 40 MB。只引用 WinUI 之后是 166 MB，元包则是 222 MB。

换来的是现代控件、原生高 DPI（PerMonitorV2）、系统一致的浅色/深色主题，
以及不再需要为深色模式手写一整套自绘控件。

托盘右键菜单：

```
当前壁纸日期与标题    点击用浏览器打开图片来源（版权链接）
──────────────
上一张 / 下一张      在最近 8 天内前后切换（不改变固定状态）
选择日期…            打开历史壁纸窗口，点选一张即设为壁纸并固定
立即刷新             只更新图片列表，不会解除固定
固定当前壁纸 ✓       勾选后不再随检查间隔自动更换；取消勾选立即回到今日壁纸
──────────────
打开壁纸目录
──────────────
设置…
退出
```

### 目录布局

```
<程序目录>\
├─ BingWallpaper.exe
├─ BingWallpaper.ini        # 配置，纯文本可手改
├─ BingWallpaper.log        # 日志（512KB 轮转，保留一个 BingWallpaper.log.1）
├─ Microsoft.WinUI.dll      # Windows App SDK / .NET 运行时文件（随包附带，勿删）
├─ ...                      # 同上，其余运行时文件
└─ wallpapers\
   └─ 20260818_WhyteCliffP_UHD.jpg
```

程序只写入 `BingWallpaper.ini`、`BingWallpaper.log(.1)` 和 `wallpapers\`，
其余文件是运行时的一部分，升级时整个文件夹一起替换即可（`.ini` 和 `wallpapers\` 记得保留）。

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
PinnedWallpaper=            ; 固定的壁纸文件名，留空 = 跟随检查间隔自动更换
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
| `<程序目录>\wallpapers\*.jpg` | 下载壁纸时 | 按保留天数自动清理；当前使用中的文件和已固定的文件永不删除 |
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

需要 .NET 10 SDK 和 Windows（WinUI 3 的 XAML 编译器只在 Windows 上跑）：

```powershell
dotnet build   src/BingWallpaper/BingWallpaper.csproj -c Release
dotnet publish src/BingWallpaper/BingWallpaper.csproj -c Release -o publish
```

产物是整个 `publish\` 目录，`BingWallpaper.exe` 需要与其中的运行时文件放在一起。

CI（`.github/workflows/build.yml`，`windows-latest`）在每次 push / PR 时构建，
并以 `-warnaserror` 保证零编译警告；打 `v*` 标签时把打包好的 zip 挂到 GitHub Release 上，
同时保留 artifact 上传。

## 技术说明

- C# + **WinUI 3 / Windows App SDK**（`net10.0-windows10.0.19041.0`），未打包（unpackaged）模式运行：
  没有 MSIX、没有安装程序、没有包标识，`WindowsPackageType=None` + `WindowsAppSDKSelfContained` +
  `SelfContained`，运行时全部随程序发布
- 不启用 `PublishTrimmed` / `PublishAot`：XAML 运行时按名字解析类型，裁剪后的构建会在运行时以
  编译期查不出来的方式失败，而体积本来就由两个运行时决定
- 依赖只有一个：`Microsoft.WindowsAppSDK.WinUI`（连带 Base / Foundation / InteractiveExperiences），
  不引元包，因此发布目录里没有 AI / ML / Search / Widgets 的运行时；DWriteCore 也不需要——
  WinUI 链接的是系统自带的 `DWrite.dll`
- 窗口用 XAML 描述、逻辑写在 code-behind 里，数据不走反射绑定：下拉框条目直接是 `ComboBoxItem`
  （取值放在 `Tag` 里），只有历史网格的模板用了编译期绑定 `x:Bind`；三个窗口都能用 Esc 关闭
  （`KeyboardAccelerator`）
- **托盘图标与托盘菜单是纯 Win32**：WinUI 3 既没有托盘图标，也没有能脱离 XAML 窗口弹出的菜单，
  于是自己 `RegisterClassEx` + `Shell_NotifyIcon` + `CreatePopupMenu` / `TrackPopupMenuEx`。
  承载消息的窗口是一个隐藏的**顶层**窗口而不是 message-only 窗口——message-only 窗口收不到广播消息，
  而主题切换正是靠 `WM_SETTINGCHANGE` 广播感知的；同时监听 `TaskbarCreated`，资源管理器重启后自动补回图标
- 深色模式：窗口内部由 `ElementTheme` 统一切换，标题栏用 `DwmSetWindowAttribute(20)`，
  托盘菜单用 uxtheme 未文档化序号导出（`SetPreferredAppMode` #135 / `AllowDarkModeForWindow` #133 /
  `RefreshImmersiveColorPolicyState` #104 / `FlushMenuThemes` #136）。所有未文档化调用均包在 try/catch 中，
  失败时降级为浅色并记录日志
- JSON 用 `System.Text.Json` 的 `JsonDocument` 逐字段读取，不做反射反序列化
- 下载校验不再依赖 `System.Drawing`（现代 .NET 里它已不属于框架）：改为直接读 JPEG/PNG 的标记段，
  确认起始标记、帧头尺寸与结尾标记 `FF D9` / `IEND` 都在，半截文件因此不会被当成有效缓存
- 图标是一个多分辨率 `.ico`（256/128/64/48/32/24/20/16），同一个文件既由 `ApplicationIcon` 编进 exe 的
  Win32 资源（资源管理器、任务栏、Alt+Tab），也由 `LoadImage` 按当前 DPI 取出对应帧给托盘和窗口标题栏使用
- 16/20/24 三帧是从 128 帧重采样生成的，不是逐级均值缩小：均值缩小出来的小帧笔画落在半像素上，
  标题栏和托盘里看着发虚
- 没有主窗口：`DispatcherShutdownMode = OnExplicitShutdown`，否则关掉设置窗口就等于退出程序
- 按 PerMonitorV2 感知（见 `app.manifest`），WinUI 自己处理跨显示器缩放，不再需要手写 DPI 换算
- 图片身份使用接口返回的 `startdate` 而非本地日期（zh-CN 市场在 UTC 16:00 换图）
- 缓存文件名不含市场代码：市场描述的是「从哪个频道拿到这张图」，不是图片本身的属性。
  身份取 `urlbase` 里的 `OHR.<名称>` 标记（`/th?id=OHR.WhyteCliffP_ZH-CN0573407830` → `WhyteCliffP`），
  它跨市场稳定，于是反复切换市场不会把同一张照片存成好几份
- 切换分辨率后，同一张图的旧尺寸副本会被清掉——但**只在当前分辨率那份确实存在时**才删，
  所以这一步只会去掉多余副本，永远不会删掉某张图的最后一份
- 下载先写 `.tmp`、校验通过后再原子改名，避免半截文件被当作有效缓存
- 全局异常钩子（`Application.UnhandledException`、`AppDomain.UnhandledException`、
  `TaskScheduler.UnobservedTaskException`）会把完整异常链写入日志，并弹出可复制文本的错误窗口

## 免责声明

- 本项目为**非官方**项目，与 Bing 及 Microsoft 没有任何关联、认可或合作关系。
- "Bing"、"Microsoft"、"Windows" 是 Microsoft Corporation 的商标。微软另有一款同名官方产品 Bing Wallpaper，
  与本项目无关，请勿混淆。
- Bing 每日图片的版权归原作者 / 版权方所有，Bing 仅授权其用作个人桌面壁纸。请勿将下载到的图片用于其他用途。
- 本软件按「现状」提供，不作任何担保，使用风险自负。

## 许可证

[GPL-3.0-or-later](LICENSE)（仅涵盖本项目代码，不涵盖任何 Bing 图片内容）。
