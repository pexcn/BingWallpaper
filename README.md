# 必应壁纸

一个干净、便携、开源的 Bing 每日壁纸客户端。

## 截图

<!-- 图片就位后把对应格子换成 ![说明](docs/xxx.png) 即可 -->

|  |  |
| :---: | :---: |
| **设置 · 深色**<br>`docs/settings-dark.png` | **设置 · 浅色**<br>`docs/settings-light.png` |
| **历史壁纸**<br>`docs/history.png` | **托盘菜单**<br>`docs/tray.png` |

> 截图待补：首个构建产物在真机运行后补上。

## 特性

- 常驻托盘，无主窗口，默认每小时检查一次今日壁纸
- 4K / 1080p 分辨率由客户端自己决定，不受接口返回值影响
- 支持 14 个常见市场，也可在配置文件里手填任意市场代码
- 可设置最近 8 天的历史壁纸缩略图网格，一键切换
- 可锁定某一张壁纸，不再随检查间隔自动更换
- 六种填充方式：填充 / 适应 / 拉伸 / 平铺 / 居中 / 跨区
- 手写深色模式，**在 Windows 10 上同样有效**，可跟随系统实时切换
- 完全便携：配置、日志、壁纸全部位于程序目录，删除整个文件夹即可完整卸载
- 零第三方依赖，**单文件几百 KB 的可执行文件，无需安装任何运行时**

## 系统要求

- Windows 10 1903 (build 18362) 及以上，64 位
- **无需安装 .NET 运行时**：程序基于 .NET Framework 4.8，该版本自 Windows 10 1903 起随系统内置

## 安装与使用

1. 从 [Releases](../../releases) 下载 `BingWallpaper.zip` 并解压，得到一个 `BingWallpaper` 文件夹
2. 把这个文件夹放到一个**有写入权限**的位置，例如 `D:\Software\`
3. 启动文件夹里的 `BingWallpaper.exe`，托盘出现图标后即开始工作

### 配置文件

程序目录下的 `BingWallpaper.ini`，纯文本可手改，保存后重启程序生效：

```ini
[General]
Market=zh-CN              ; 市场代码，决定壁纸来自哪个频道；可设置为列出的 14 个常见市场，亦可填写任意代码
Resolution=UHD            ; 下载分辨率: UHD 或 1920x1080
Fit=Fill                  ; 填充方式: Fill / Fit / Stretch / Tile / Center / Span
Theme=System              ; 界面主题: System / Light / Dark
RefreshIntervalHours=1    ; 检查间隔: 1 ~ 168 小时
KeepDays=30               ; 壁纸保留天数: 0 表示永久保留，上限为 3650
RunAtStartup=false        ; 开机自启动: true 或 false
PinnedWallpaper=          ; 锁定的壁纸文件名，留空表示跟随检查间隔自动更换
```

取值非法或超出范围时会被修正到最接近的合法值，并在日志里留下记录，不会因此启动失败。

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

本程序只做一件事：把 Bing 每日图片下载下来，并设为壁纸。

## 技术说明

<details>
<summary>实现细节与取舍，一般使用者不必阅读（点击展开）</summary>

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

</details>

## 许可证

[GPL-3.0-or-later](LICENSE).
