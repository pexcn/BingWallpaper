# TODO2：代码走查待修项

> 来源：2026-09-03 对全项目做的一次 code review，共 15 条。
> 2026-09-06 按当前源码重新核实：第 7 条已随重构消失，第 2、3 条部分修复，其余 12 条原样。
> 行号为 2026-09-06 的现行行号。编号保持不变，方便和旧记录对照。

---

## P0 · 会让功能真的失效

### [ ] 1. 手动切换壁纸期间的设置改动会被吞掉

`UI/TrayContext.cs:1251` `MoveToAsync`

置了 `_busy` 却不排空 `_rerunRequested`，而唯一消费这个标志的 `while` 在 `RefreshAsync:560`，
此刻并没有在跑。切换壁纸时改壁纸地区：INI 写了新市场，却不触发抓取，要等下一次定时器
（最长 168 小时）。`_rerunUserInitiated` 也停在 `true`，下次自动刷新失败会弹一个用户没要的对话框。

新增的 `_applyingFavorite` 同样没被 `StartRefresh` 检查，是第二条同样吞掉设置改动的路径。

改法：把排空循环提取成一处共用，或让 `MoveToAsync` 结束时接力一次。

### [ ] 2. 「刷新失败，详见日志文件」大多数情况下显示不出来 —— 部分修复

`UI/TrayContext.cs:625`（catch 里写 `Text`）→ `UI/TrayContext.cs:1504` `UpdateMenuState`

`UpdateMenuState` 现在会保留 catch 写进去的文案，但只在「有列表、本次会话什么都没应用过」这一条
路径上生效。最常见的路径没救：本会话已经贴过壁纸时走 `_appliedImage is not null`（1514 行），
无条件写回日期 + 标题；首启无网络走 `_images.Count == 0`（1573 行），变成「尚未获取到壁纸信息」。

改法（原提案未落地）：加 `_lastRefreshFailed` 状态字段，由 `UpdateMenuState` 统一决定标题。

### [ ] 3. `Apply()` 失败仍被大部分调用点忽略 —— 部分修复

`WallpaperService.cs:126`（日志级别）
调用点：`UI/TrayContext.cs:372`、`676`、`1423`、`1702`

新增的 `ApplyFavoriteCoreAsync`（`TrayContext.cs:1366`）已经检查返回值，失败就不更新状态——
说明这个模式已被接受。但 `ApplyIndexAsync:372` 照旧丢弃返回值，紧接着写 `_currentIndex` /
`_appliedPath` / `_appliedImage`：组策略 `NoChangingWallPaper` 生效时，菜单显示一张根本没贴上
桌面的图是「当前」，还能对它执行锁定。另外三处 `WallpaperService.Apply` 也都丢弃返回值。

日志级别没动：`SetWallpaper` 无论成败都是 `Info`，按 AGENTS.md 判据失败应当是 `Error`。

### [ ] 4. 忙碌时点历史磁贴会假报成功

`UI/PickerForm.cs:706-729` ← `UI/TrayContext.cs:1251`

定时刷新正在跑时点一张磁贴：`MoveToAsync` 开头就 `return`，`await` 顺利通过，状态栏打出
「已锁定：…」，桌面纹丝不动。`IsBusy` 属性已经有了，但只用在 shuffle 开关和「取消锁定」菜单项上。

改法：让 `MoveToAsync` 把「有没有真的做事」告诉调用方，或忙碌时同步禁用磁贴。

### [ ] 5. 设置保存失败后内存值不回滚

`UI/SettingsForm.cs:666` `Persist` ← 对比 `UI/TrayContext.cs:789` `SetPinned`

`CheckedChanged` 先改 `_config`，`Save` 抛异常后 `Persist` 弹完框就 `return`——不还原、也不触发
`SettingsChanged`。结果注册表没写、磁盘还是旧值，内存和勾选框却显示新值；此后任何一次 `Persist`
都会把这个从未生效的值顺手写进磁盘。`SetPinned` 在同样的失败上是回滚的，两处应当一致。

---

## P1 · 边角场景下会出错

### [ ] 6. 换分辨率会让仍在窗口内的锁定图「失踪」

`UI/TrayContext.cs:721` `FindImageIndex`

按带分辨率后缀的整个文件名匹配。锁定 UHD 后改成 1080p，`EnsurePinnedAsync` 得到 `index = -1`，
托盘标题退化、选择窗口不再显示「已锁定」。更糟的是文件同时不在时，`EnsurePinnedAsync:698` 会直接
释放锁定并应用今天的图——而那张图其实完全可以重新下载。

改法：改用 日期 + imageId 匹配，`BingImageInfo.TryParseFileName` 已经现成。

### [x] 7. ~~单实例守卫可能半残并泄漏 mutex~~ —— 已修复

`869eb70` 把 `EventWaitHandle` 整个删掉了，第二个实例只写一行日志退出，泄漏路径不复存在。

### [ ] 8. 壁纸选择窗口可能并发发两次同样的请求

`UI/PickerForm.cs:273-303` `LoadImages`

没有 in-flight 保护。窗口关闭现在会 Dispose，所以不再是「重开复用对象」；但窗口开着时再点一次
托盘的「选择日期」，`ShowPicker` 照样调 `LoadImages`，`_images` 仍空就再发一条链
（`FetchAsync` 带 2/4/8 秒退避重试），两条链 `AdoptImages` / `Populate` 同一个活窗口。

改法：一个 `_fetching` 标志即可，和托盘的 `_busy` 一个思路。

### [ ] 9. `.jpg.tmp` 残留永远清不掉

`BingClient.cs:204`（临时文件名）、`211` / `241`（只在重试开头删）

没有 `finally` 兜底。四次重试全失败，或最后一次在 `Paths.MoveOverwrite`（`Paths.cs:186`，
`File.Replace` 的 `UnauthorizedAccessException` 仍未捕获）上失败时，`.jpg.tmp` 就留下了。
而 `WallpaperService.cs:210` 和 `288` 两处清理都只枚举 `*.jpg`，孤儿在受 `KeepDays` 约束的
目录里无限堆积。

改法：`finally` 里兜底删一次；或让清理器把 `*.jpg.tmp` 一并纳入。

### [ ] 10. 崩溃处理器自己可能爆栈

`Logger.cs:94-101` `Describe`

处理 `AggregateException` 时递归调用 `Describe(item)`，而 `depth` 是方法内的局部变量，每层递归
重新从 0 开始，外面的 `while (depth < 10)` 什么也没拦住。嵌套 `AggregateException` 会一路递归到
栈溢出——而这段代码存在的意义正是「保证崩溃不会无声无息」。

改法：把 `depth` 作为参数传下去（加一个私有重载）。

---

## P2 · 一致性、开销与文档

### [ ] 11. 启动时重复做可写探测

`Program.cs:30`（`LogEnvironment`）/ `Program.cs:55`（`RunGui`）

`Paths.IsBaseDirectoryWritable` 跑两遍，每遍都是 建文件 + 写一个字节 + 删除。
`RunGui` 的结果现成，直接传给 `LogEnvironment` 即可。

### [ ] 12. manifest 注释写的 DPI 模型是错的

`src/BingWallpaper/app.manifest:40`

注释说依赖 `AutoScaleMode.Font`，但四个窗体加 `ErrorDialog` 全是 `AutoScaleMode.Dpi`，
README 写的也是 Dpi。唯独声明 DPI 感知的这个文件还留着旧说法。

### [ ] 13. README 承诺的最低系统版本与暗色标题栏的实现前提矛盾

`README.md:30` ↔ `Theme/DarkModeNative.cs:38-41`

README 写「Windows 10 1903 (build 18362) 及以上」，而 `DWMWA_USE_IMMERSIVE_DARK_MODE = 20`
从 19041 才有，代码注释自己写的是「最低支持 19044，因此不需要属性 19 的兼容分支」。
1903/1909 用户在深色模式下每个窗口都是浅色标题栏。两个说法必须改一个。

### [ ] 14. 填充方式下拉框直接把 `SelectedIndex` 当枚举值用

`UI/SettingsForm.cs:273`（按 `Enum.GetValues` 顺序填充）、`481` / `522-527`（按下标读写）

只在 `WallpaperFit` 保持 0..5 连续无空洞时才成立，往中间插一个成员就会静默错位，编译器不报错。
同文件的 `_intervalBox`、`_keepDaysBox`、`_shuffleIntervalBox` 都已用 `Choice` 包装携带真实值——
四个里三个用了正确写法，只剩它一个特例。

### [ ] 15. `BuildProtectedFiles()` 连着调用两次

`UI/TrayContext.cs:611` / `615`

两次各新建一个 `List<string>`，各自对同样的两个路径重跑 `Path.GetFullPath`，每个刷新周期都来一遍。
提一个局部变量到两次调用之上即可。（`1727` 行那处是单次调用，没问题。）
