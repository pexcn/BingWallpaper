# BingWallpaper

一个干净、便携、开源的 Bing 每日壁纸客户端（Windows / 免安装 / 托盘常驻）。

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
- 深色模式在 **Windows 10 上同样完整**：界面由 Avalonia 自己绘制，连托盘菜单都是本程序的窗口，
  不受「Win32 菜单永远是浅色」的限制；可跟随系统实时切换
- 完全便携：配置、日志、壁纸全部位于程序目录，删除整个文件夹 = 完整卸载
- .NET 10 **Native AOT** 编译：目标机器**无需安装任何运行时**，进程启动没有 JIT 预热

## 系统要求

- Windows 10 1903（build 18362）及以上，64 位；**Windows 10 LTSC 2021（19044）满足要求**
- **无需安装 .NET 运行时**：程序是 Native AOT 编译出来的原生可执行文件，不依赖 .NET Framework，
  也不依赖 .NET 10 Desktop Runtime

## 安装与使用

1. 从 [Releases](../../releases) 下载 `BingWallpaper-<版本>-win-x64.zip`
2. 解压到一个**有写入权限**的目录（例如 `D:\Tools\BingWallpaper\`，或 U 盘）
   - 放在 `C:\Program Files` 之类不可写的位置时，程序会明确报错退出，不会偷偷改写 `%APPDATA%`
   - 压缩包里除 `BingWallpaper.exe` 外还有三个 dll，是 Avalonia 的渲染库，**必须与 exe 同目录**
3. 双击 `BingWallpaper.exe` 运行，托盘出现图标后即开始工作

### 为什么是 .NET 10 + Native AOT + Avalonia？

三个要求同时成立时，这是唯一走得通的组合：**免安装运行时**、**深色模式在 Win10 上完整可用**、
**代码不用为了迁就 UI 框架而扭曲**。

| 方案 | 体积 | 是否需要安装运行时 | Win10 深色模式 |
|---|---|---|---|
| .NET Framework 4.8 + WinForms | ~200 KB | 否，系统内置 | 只能自绘，且托盘菜单做不到 |
| .NET 10 + Avalonia，依赖框架 | ~2 MB | 是，需装 .NET 10 Runtime | 完整 |
| .NET 10 + Avalonia + Native AOT（本项目） | 34 MB（压缩包约 15 MB） | **否** | **完整** |

代价写在明处：**体积从几百 KB 变成几十 MB**，而且不再是单个文件——Avalonia 的渲染
（Skia / HarfBuzz / ANGLE）是原生库，Native AOT 无法把它们并进 exe，只能放在 exe 旁边。
换来的是一整套自绘控件：深色模式不再靠拦截 uxtheme 的未文档化导出实现，托盘菜单终于也能是深色的，
而且 Win10 与 Win11 表现一致。

实测（v1.0.0，win-x64）：

| 文件 | 大小 | 说明 |
|---|---|---|
| `BingWallpaper.exe` | 17.2 MB | 程序本体，Native AOT 产物 |
| `libSkiaSharp.dll` | 9.4 MB | 渲染 |
| `av_libglesv2.dll` | 5.4 MB | ANGLE，GPU 后端 |
| `libHarfBuzzSharp.dll` | 1.8 MB | 文字排版 |

> 每次 CI 的 `Report published files` 步骤都会打印当次的实际大小。
> 发布包里不含 `.pdb`：ILCompiler 生成的原生符号文件有 77 MB，比其余所有文件加起来还大，
> 单独作为 CI artifact 保留，供需要看崩溃转储的人下载。

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
├─ libSkiaSharp.dll         # Avalonia 的渲染库，随程序分发，不可删
├─ libHarfBuzzSharp.dll     # 同上（文字排版）
├─ av_libglesv2.dll         # 同上（ANGLE，GPU 渲染；缺失时 Avalonia 退回软件渲染）
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
| 安装到 `%LOCALAPPDATA%` 并注册卸载项 | 会 | **不会**，解压即用 |
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

需要 .NET SDK 10。**发布必须在 Windows 上进行**：Native AOT 不支持跨操作系统编译，
而且 ILCompiler 会调用 Visual Studio 的 C++ 链接器（安装「使用 C++ 的桌面开发」工作负载即可）。

```powershell
dotnet build   src/BingWallpaper/BingWallpaper.csproj -c Release
dotnet publish src/BingWallpaper/BingWallpaper.csproj -c Release -o publish
```

产物是 `publish\BingWallpaper.exe` 加三个原生渲染库，整个目录就是可分发的内容。

在 Linux / macOS 上也可以 `dotnet build`（项目开了 `EnableWindowsTargeting`），
用来检查编译错误和 trim / AOT 分析器的告警——项目通过 `IsAotCompatible` 让这些分析器在
普通构建时就运行，而不是等到 publish——但 `dotnet publish` 会在链接阶段停下。

CI（`.github/workflows/build.yml`，`windows-latest`）在每次 push / PR 时构建，
并以 `-warnaserror` 保证零编译警告；打 `v*` 标签时把打包好的 zip 挂到 GitHub Release 上，
同时保留 artifact 上传。

## 技术说明

- C# + [Avalonia](https://avaloniaui.net/) 11（`net10.0-windows`，Native AOT），UI 全部由代码构建，
  没有 XAML 文件、没有设计器。编译期 XAML 在 AOT 下同样可用，不写只是因为这个项目里它只会多出
  一个要对照着看的地方
- 只引用 `Avalonia.Win32` 与 `Avalonia.Skia`，不引用 `Avalonia.Desktop`：后者会把 X11、macOS 后端和
  一个 D-Bus 客户端一起拖进产物。启动时也不用 `UsePlatformDetect()`——它靠反射加载后端，正是 AOT
  解析不了的东西——而是直接写死 `UseWin32().UseSkia()`
- JSON 用 `System.Text.Json` 的 `JsonDocument` 解析：只取那几个字段不需要模型类，于是既不用反射，
  也不用源生成器，AOT 下零配置
- **托盘图标是自己通过 `Shell_NotifyIconW` 挂的**，没有用 Avalonia 的 `TrayIcon`。原因只有一个：
  `TrayIcon` 不暴露右键事件，只能弹 Win32 菜单，而 Win32 菜单由系统绘制，在 Windows 10 上永远是浅色的。
  自己拿到右键，菜单就可以是一个普通的 Avalonia 窗口，跟着主题走
  - 承载图标的是一个真正的顶层窗口而不是 message-only 窗口：`TaskbarCreated`（资源管理器重启后要求
    重新添加图标）是广播消息，message-only 窗口收不到
  - 窗口过程是 `[UnmanagedCallersOnly]` 的静态函数，`NOTIFYICONDATAW` 用定长 `fixed char` 缓冲区，
    整个结构是 blittable 的——这两点都是为了在 AOT 下不需要任何运行期封送
- 托盘菜单窗口的几何是算出来的而不是量出来的：每一行声明自己的高度，于是菜单在出现之前就知道自己
  多大，可以直接摆到最终位置，而不是先画一帧再跳过去。放不下时向上翻、向左翻，都按当前屏幕的
  工作区算，多屏和不同缩放各自成立
- 图标仍然是那个多分辨率 `.ico`（256/128/64/48/32/24/20/16），并且**没有交给 Avalonia 的 `WindowIcon`**：
  那条路只能给它一张位图，再由 Skia 缩放到 shell 要的尺寸，为托盘准备的 16/20/24 三帧就白费了。
  程序自己解析 .ico 目录、挑出匹配当前 DPI 的那一帧、`CreateIconFromResourceEx` 交给 Windows，
  窗口图标走 `WM_SETICON`
  - 16/20/24 三帧是从 128 帧重采样生成的，不是逐级均值缩小：均值缩小出来的小帧笔画落在半像素上，
    标题栏和托盘里看着发虚
- 深色模式收敛成了「选一个 `ThemeVariant`」：Avalonia 自己画每一个像素，不需要 `SetWindowTheme`、
  也不需要 uxtheme 的未文档化序号导出。`ThemeMode.System` 下监听
  `IPlatformSettings.ColorValuesChanged` 跟随系统实时切换；程序自绘的两处（托盘菜单、缩略图磁贴）
  从 `ThemePalette` 取色，颜色值刻意与 Fluent 对齐，两种表面挨在一起不会有接缝
  - 标题栏另外调一次 `DwmSetWindowAttribute`。Avalonia 自己会做，这只是 Win10 上的保险，失败即忽略
- 按 PerMonitorV2 DPI 感知（见 app.manifest）：窗口从 100% 的屏拖到 150% 的屏会重新布局，
  而不是被系统拉伸。所有尺寸都是逻辑像素，代码里没有一处 DPI 换算——原来那个 `DpiScale` 随
  WinForms 一起删掉了
- 界面字体显式指定 `Microsoft YaHei UI, Segoe UI`：Avalonia 的字体来自主题而不是系统，
  Fluent 主题要的 Segoe UI 没有中文字形，而这个程序的界面全是中文
- 图片身份使用接口返回的 `startdate` 而非本地日期（zh-CN 市场在 UTC 16:00 换图）
- 缓存文件名不含市场代码：市场描述的是「从哪个频道拿到这张图」，不是图片本身的属性。
  身份取 `urlbase` 里的 `OHR.<名称>` 标记（`/th?id=OHR.WhyteCliffP_ZH-CN0573407830` → `WhyteCliffP`），
  它跨市场稳定，于是反复切换市场不会把同一张照片存成好几份
- 切换分辨率后，同一张图的旧尺寸副本会被清掉——但**只在当前分辨率那份确实存在时**才删，
  所以这一步只会去掉多余副本，永远不会删掉某张图的最后一份
- 下载先写 `.tmp`、解码校验通过后再原子改名，避免半截文件被当作有效缓存。解码用的就是 Skia
  （经由 Avalonia 的 `Bitmap`），跟后面把缩略图画到屏幕上的是同一个解码器，不会出现「这里能解、
  那里画不出来」
- 全局异常钩子（`AppDomain.UnhandledException`、`TaskScheduler.UnobservedTaskException`）会把完整
  异常链写入日志并弹出可复制文本的错误窗口；UI 还没起来（或起不来）时退回 Win32 消息框
- 全球化功能保持启用：程序要按中文格式化日期，市场代码本身也是 culture 名。ICU 自 Windows 10 1903
  起随系统提供，`InvariantGlobalization` 省下的那点体积不值得

## 免责声明

- 本项目为**非官方**项目，与 Bing 及 Microsoft 没有任何关联、认可或合作关系。
- "Bing"、"Microsoft"、"Windows" 是 Microsoft Corporation 的商标。微软另有一款同名官方产品 Bing Wallpaper，
  与本项目无关，请勿混淆。
- Bing 每日图片的版权归原作者 / 版权方所有，Bing 仅授权其用作个人桌面壁纸。请勿将下载到的图片用于其他用途。
- 本软件按「现状」提供，不作任何担保，使用风险自负。

## 许可证

[GPL-3.0-or-later](LICENSE)（仅涵盖本项目代码，不涵盖任何 Bing 图片内容）。
