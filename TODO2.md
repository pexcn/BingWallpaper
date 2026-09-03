# TODO2：代码走查待修项

> 来源：2026-09-03 对全项目做的一次 code review，共 15 条，行号已逐条对照源码核实。
> 尚未动手修改任何文件。按「用户能不能感知」排序，修完把方框打勾即可。

---

## P0 · 会让功能真的失效

### [ ] 1. 手动切换壁纸期间的设置改动会被吞掉

`UI/TrayContext.cs:594` `MoveToAsync`

`MoveToAsync` 置了 `_busy` 却没有像 `RefreshAsync` 那样排空 `_rerunRequested`：
唯一消费这个标志的 `while` 循环在 `RefreshAsync` 里，而此刻它并没有在跑。

复现：在壁纸选择窗口点一张图（UHD 正在下载，`_busy = true`），随即打开设置改壁纸地区。
`OnSettingsChanged` → `StartRefresh(userInitiated: true)` 看到 `_busy`，置位后直接返回。
结果 INI 已经写了新市场，却不会触发任何抓取，要等下一次定时器（最长 168 小时）——
正是当初引入 `_rerunRequested` 要避免的「INI 和桌面对不上」。
附带地，`_rerunUserInitiated` 停留在 `true`，那次自动刷新失败还会弹一个用户没要的 `ErrorDialog`。

改法：把 rerun 的排空循环提取成一处，`MoveToAsync` 和 `RefreshAsync` 共用；
或让 `MoveToAsync` 结束时检查一次标志并接力。

### [ ] 2. 「刷新失败，详见日志文件」永远显示不出来

`UI/TrayContext.cs:356`（设置）→ `UI/TrayContext.cs:365`（`finally` 里 `UpdateMenuState()`）

catch 里刚写进 `_titleItem.Text` 的提示，紧接着被 `finally` 中的 `UpdateMenuState()` 无条件覆盖：
首次启动无网络时走 `_images.Count == 0` 分支变成「尚未获取到壁纸信息」；
之前应用过壁纸则走 `_appliedImage is not null` 分支变回日期 + 标题。
所有现实路径下这条提示都是死代码，用户不会知道刷新失败了。

改法：加一个 `_lastRefreshFailed` 之类的状态字段，由 `UpdateMenuState` 统一决定标题，
而不是在 catch 里直接写 `Text`。

### [ ] 3. `Apply()` 失败被三个调用点集体忽略

`WallpaperService.cs:21`（返回 `bool`）、`WallpaperService.cs:64`（日志级别）
调用点：`UI/TrayContext.cs:223`、`399`、`783`

组策略 `NoChangingWallPaper` 生效、或文件在 `File.Exists` 与 `SystemParametersInfoW` 之间被删掉时，
`Apply` 返回 `false`，但日志是 `Info`（`wallpaper: applied ok=False ... lasterror=5`），
三处调用又都丢弃返回值。于是 `ApplyIndexAsync` 照样设置 `_appliedPath` / `_appliedImage` /
`_currentIndex`，菜单显示这张图是「当前」，还能对一张根本没贴上桌面的图执行锁定。

按 AGENTS.md 的级别判据，这属于「功能失败，用户可感知」，应当是 `Error`；
调用点至少要在失败时不更新已应用状态。

### [ ] 4. 忙碌时点历史磁贴会假报成功

`UI/HistoryForm.cs:453` ← `UI/TrayContext.cs:596`

`_busy` 时只有托盘菜单项变灰，壁纸选择窗口里的磁贴仍然可点。
定时刷新正好在窗口开着时触发，用户点一张图：`MoveToAsync` 在开头就 `return`，
返回一个已完成的 Task，`ApplyAsync` 里的 `await` 顺利通过，状态栏打出「已锁定：2026-09-01 · …」，
而桌面纹丝不动、也没有锁定。

改法：让 `MoveToAsync` 把「有没有真的做事」告诉调用方（返回 `bool` 或抛忙碌异常），
或者忙碌时同步禁用磁贴。

### [ ] 5. 设置保存失败后内存值不回滚

`UI/SettingsForm.cs:630` `Persist` ← 对比 `UI/TrayContext.cs:502` `SetPinned`

INI 被同步工具或编辑器占用时，`CheckedChanged` 已经先把 `_config.RunAtStartup` 改成了 `true`，
`_config.Save` 抛异常，`Persist` 记完日志弹完框就 `return`——不还原、也不触发 `SettingsChanged`。
结果：注册表 Run 项没写（功能没生效），磁盘上还是 `false`，但内存配置和勾选框都显示 `true`；
此后任何一次 `Persist`（改别的设置）都会把这个从未生效的值顺手写进磁盘。

`SetPinned` 在同样的失败上是会还原旧值的，两处行为应当一致。

---

## P1 · 边角场景下会出错

### [ ] 6. 换分辨率会让仍在窗口内的锁定图「失踪」

`UI/TrayContext.cs:444` `FindImageIndex`

匹配用的是 `GetFileName(_config.Resolution)`，也就是带分辨率后缀的完整文件名。
锁定 `20260901_Foo_UHD.jpg` 后把分辨率改成 1080p，`EnsurePinnedAsync` 得到 `index = -1`，
于是 `_currentIndex = -1`、`_appliedImage = null`：托盘标题退化成「[2026-09-01] 的壁纸」，
选择窗口里那张图不再显示「已锁定」徽章。更糟的是，如果该 UHD 文件恰好不在
（把程序目录拷到另一台机器、没带 `wallpapers\`），`UI/TrayContext.cs:421` 的 `index < 0`
分支会直接释放锁定并应用今天的图——而那张锁定的图其实完全可以重新下载。

改法：改用 日期 + imageId 匹配，`BingImageInfo.TryParseFileName` 已经现成。

### [ ] 7. 单实例守卫可能半残并泄漏 mutex

`Program.cs:143-152`

`_instanceMutex = mutex` 在 `new EventWaitHandle(...)` 之前赋值。若 `Global\` 的 Mutex 建成功、
事件却抛异常（跨会话已存在对象的 `UnauthorizedAccessException`，或 Global 命名空间被拒），
catch 记完日志进入 `Local\` 那轮，把 `_instanceMutex` 覆盖掉——
Global mutex 既没释放也没 dispose，被整个进程生命周期持有。

之后第二个实例：看到 Global mutex 存在（`createdNew == false`），调用 `SignalRunningInstance`
去开一个并不存在的事件，`TryOpenExisting` 返回 `false`，于是在 `Program.cs:82` 静默退出——
托盘图标不出现、设置窗口不弹、双击 exe 毫无反应。

改法：事件创建失败时先 `ReleaseMutex` + `Dispose` 再进下一轮前缀。

### [ ] 8. 壁纸选择窗口可能并发发两次同样的请求

`UI/HistoryForm.cs:162` `LoadImages`

没有 in-flight 保护。首轮刷新失败（无网络）后 `_images` 为空且 `_busy` 已复位，
「选择日期」仍然可点：第一次打开触发 `FetchAsync`（带 2/4/8 秒退避重试），
用户关掉再点一次，窗口对象被复用、`images.Count == 0` 依旧成立，于是第二条请求链启动。
两条链各自 `AdoptImages` / `Populate` 同一个窗口，谁后到谁说了算。

改法：一个 `_fetching` 标志即可，和托盘的 `_busy` 一个思路。

### [ ] 9. `.jpg.tmp` 残留永远清不掉

`BingClient.cs:204`（临时文件名）、`211` / `241`（只在重试开头删）

`DeleteQuietly` 只在**下一次尝试开始时**执行。四次重试全失败（连接反复中断），
或最后一次在 `Paths.MoveOverwrite`（`Paths.cs:113`，`File.Replace` 的
`UnauthorizedAccessException` 没有被捕获）上失败时，`20260901_Foo_UHD.jpg.tmp` 就留在
`wallpapers\` 里了。而 `WallpaperService.cs:113` 和 `191` 两处清理都只枚举 `*.jpg`，
这个孤儿于是在一个本该受 `KeepDays` 约束的目录里无限堆积。

改法：`finally` 里兜底删一次临时文件；或让清理器把 `*.jpg.tmp` 一并纳入。

### [ ] 10. 崩溃处理器自己可能爆栈

`Logger.cs:94-101` `Describe`

处理 `AggregateException` 时递归调用 `Describe(item)`，而 `depth` 是方法内的局部变量，
每层递归重新从 0 开始——外面那句 `while (current is not null && depth < 10)` 什么也没拦住。
`Task.WhenAll` 很容易造出嵌套的 `AggregateException`，
`TaskScheduler.UnobservedTaskException` 把它交给 `OnUnobservedTaskException` 后就会一路递归到栈溢出。
偏偏这段代码存在的意义正是「保证崩溃不会无声无息」。

改法：把 `depth` 作为参数传下去（加一个私有重载）。

---

## P2 · 一致性、开销与文档

### [ ] 11. 启动时重复做可写探测

`Program.cs:34` / `Program.cs:58`

`Paths.IsBaseDirectoryWritable` 跑两遍，每遍都是 建文件 + 写一个字节 + 删除
（`Paths.cs:64`）。`Program.cs:58` 的结果现成，直接传给 `LogEnvironment` 即可，
省掉启动路径上一次多余的磁盘往返。

### [ ] 12. manifest 注释写的 DPI 模型是错的

`src/BingWallpaper/app.manifest:40`

注释说依赖 `AutoScaleMode.Font`，但所有窗体设的都是 `AutoScaleMode.Dpi`
（`UI/SettingsForm.cs:108`、`UI/HistoryForm.cs:56`），`UI/SettingsForm.cs:105` 的注释还明确说明
字体缩放因为不可靠被否掉了，README 的「技术细节」写的也是 Dpi。
唯独声明 DPI 感知的这个文件还留着旧说法，谁照着它推理 DPI 行为谁就从错误的模型出发。

### [ ] 13. README 承诺的最低系统版本与暗色标题栏的实现前提矛盾

`README.md:28` ↔ `src/BingWallpaper/Theme/DarkModeNative.cs:38-41`

README 写「Windows 10 1903 (build 18362) 及以上」，而 `DWMWA_USE_IMMERSIVE_DARK_MODE = 20`
从 build 19041 才有，代码注释自己写的是「最低支持 19044，因此不需要属性 19 的兼容分支」。
1903/1909（18362/18363）用户在深色模式下每个窗口都是浅色标题栏，
只有一行 Debug 级的 `dwmsetwindowattribute hresult=0x…` 能解释。
两个说法必须改一个：要么抬高 README 的门槛，要么补上属性 19 的降级分支。

### [ ] 14. 填充方式下拉框直接把 `SelectedIndex` 当枚举值用

`UI/SettingsForm.cs:271`（按 `Enum.GetValues` 顺序填充）、`464` / `498` / `503`（按下标读写）

只在 `WallpaperFit`（`AppConfig.cs:18`）保持 0..5 连续无空洞时才成立。
给任何成员写上显式值、或往中间插一个，都会静默地把「填充」映射成「平铺」，编译器不会报错。
同一个文件里的 `_intervalBox`、`_keepDaysBox` 已经用 `Choice` 包装携带真实值，正是为了防这个。

### [ ] 15. `BuildProtectedFiles()` 连着调用两次

`UI/TrayContext.cs:342` / `346`

两次各新建一个 `List<string>`，各自驱动 `WallpaperService.BuildProtectedSet`
对同样的两个路径重跑 `Path.GetFullPath`，每个刷新周期都来一遍。
提一个局部变量到两次调用之上即可。
