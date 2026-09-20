# TODO2：代码走查待修项

> 来源：2026-09-03 对全项目做的一次 code review。
> 2026-09-06 第二次复核：第 7 条已修复，从列表移除；1~6、8~15 原样保留（编号不变，方便和旧记录对照）。
> 2026-09-06 第三次复核（重点查内存泄漏与热路径性能）：新增 16~22 条。
> 2026-09-20 第四次复核：修正第 1、20 条描述；新增推荐修复批次。
> 行号为记录问题时的源码行号，后续改动可能使其漂移，以方法名为准。

---

## 推荐修复批次

建议总共分 **9 批**，按共同状态机、数据生命周期和资源所有权划分。每批完成后单独构建和验证，
避免一次改动横跨太多控制面。

1. **操作占用与结果反馈**：1、4。两项都由 `MoveToAsync` 的 `_busy` 早退和生命周期引起；统一返回“是否执行”，并在操作结束时接力被合并的刷新。
2. **壁纸应用结果与托盘状态**：2、3。先统一传播 `Apply` 的成败，再让 `UpdateMenuState` 从明确的成功/失败状态决定标题，避免分别修后再次互相覆盖。
3. **设置持久化与强类型选项**：5、14。都集中在 `SettingsForm` 的“控件值 → 配置值 → 落盘”链路，可一起补回滚并消除 `SelectedIndex` 对枚举数值的隐式依赖。
4. **图片身份与文件名匹配**：6、17。先缓存稳定的 `ImageId`，再让锁定图按日期 + 图片身份跨分辨率匹配；同一身份模型一次改完。
5. **网络请求生命周期**：8、9。给选择窗口请求加 in-flight 所有权，并用 `finally` 收口下载临时文件；一起验证取消、失败、重试和窗口关闭路径。
6. **日志可靠性与写入性能**：10、19。先保证异常描述器不会递归失控，再引入进程级 writer、长度计数和可靠关闭；两项共同影响故障路径，不宜拆开验证。
7. **启动探测与平台契约**：11、12、13。复用启动时的可写探测结果，并一次性统一 manifest、项目注释和 README 的 DPI / 最低系统版本表述。
8. **周期性目录与路径工作**：15、21。都在 `TrayContext` 的周期路径上：复用保护文件集合，并把收藏目录扫描移到忙碌守卫之后。
9. **WinForms 热路径与 GDI 资源**：16、18、20、22。统一处理布局测量、绘制资源、待办队列和字体所有权；其中 20 当前影响很低，可作为同批顺手清理，不值得单独改一批。

---

## P0 · 会让功能真的失效

### [ ] 1. 手动切换期间的刷新会滞留，收藏应用期间则可能并发

`UI/TrayContext.cs:1250` `MoveToAsync`

置了 `_busy` 却不排空 `_rerunRequested`，而唯一消费这个标志的 `while` 在 `RefreshAsync:559`，
此刻并没有在跑。切换壁纸时改壁纸地区：INI 写了新市场，却不触发抓取，要等下一次定时器
（最长 168 小时）。`_rerunUserInitiated` 也停在 `true`，下次自动刷新失败会弹一个用户没要的对话框。

`_applyingFavorite` 也没被 `StartRefresh` 检查，但这里不是“吞掉”：刷新会立即启动，与收藏应用并发争夺
桌面。`_applyingFavorite` 又只包住 `ApplyFavoriteCoreAsync`，不覆盖随后 `SetPinned` 和菜单状态更新；即使把它
直接加进守卫，也要确保补跑发生在完整收藏事务结束之后。

改法：让刷新、手动切换和收藏应用共用同一套占用/接力规则；至少应让 `StartRefresh` 在 `_busy` 或
`_applyingFavorite` 时合并请求，并由非刷新操作在完整收尾后接力一次。

### [ ] 2. 「刷新失败，详见日志文件」大多数情况下显示不出来

`UI/TrayContext.cs:625`（catch 里写 `Text`）→ `UI/TrayContext.cs:1503` `UpdateMenuState`

`UpdateMenuState` 只在「有列表、本次会话什么都没应用过」这一条路径上保留 catch 写进去的文案。
最常见的路径没救：本会话已经贴过壁纸时走 `_appliedImage is not null`（1513 行），无条件写回
日期 + 标题；首启无网络走 `_images.Count == 0`（1570 行），变成「尚未获取到壁纸信息」。

改法：加 `_lastRefreshFailed` 状态字段，由 `UpdateMenuState` 统一决定标题。

### [ ] 3. `Apply()` 失败仍被大部分调用点忽略

`WallpaperService.cs:130`（日志级别）
调用点：`UI/TrayContext.cs:371`、`675`、`1422`、`1701`

`ApplyFavoriteCoreAsync`（`TrayContext.cs:1376`）已经检查返回值，说明这个模式已被接受。但
`ApplyIndexAsync:371` 照旧丢弃返回值，紧接着写 `_currentIndex` / `_appliedPath` / `_appliedImage`：
组策略 `NoChangingWallPaper` 生效时，菜单显示一张根本没贴上桌面的图是「当前」，还能对它执行锁定。
另外三处 `WallpaperService.Apply` 也都丢弃返回值。

日志级别没动：`SetWallpaper` 无论成败都是 `Info`，按 AGENTS.md 判据失败应当是 `Error`。

### [ ] 4. 忙碌时点历史磁贴会假报成功

`UI/PickerForm.cs:709-734` ← `UI/TrayContext.cs:1250`

定时刷新正在跑时点一张磁贴：`MoveToAsync` 开头就 `return`，`await` 顺利通过，状态栏打出
「已锁定：…」，桌面纹丝不动。`IsBusy` 属性已经有了，但只用在 shuffle 开关和「取消锁定」菜单项上。

改法：让 `MoveToAsync` 把「有没有真的做事」告诉调用方，或忙碌时同步禁用磁贴。

### [ ] 5. 设置保存失败后内存值不回滚

`UI/SettingsForm.cs:666` `Persist` ← 对比 `UI/TrayContext.cs:762` `SetPinned`

`CheckedChanged` 先改 `_config`，`Save` 抛异常后 `Persist` 弹完框就 `return`——不还原、也不触发
`SettingsChanged`。结果注册表没写、磁盘还是旧值，内存和勾选框却显示新值；此后任何一次 `Persist`
都会把这个从未生效的值顺手写进磁盘。`SetPinned` 在同样的失败上是回滚的，两处应当一致。

---

## P1 · 边角场景下会出错

### [ ] 6. 换分辨率会让仍在窗口内的锁定图「失踪」

`UI/TrayContext.cs:720` `FindImageIndex`

按带分辨率后缀的整个文件名匹配。锁定 UHD 后改成 1080p，`EnsurePinnedAsync` 得到 `index = -1`，
托盘标题退化、选择窗口不再显示「已锁定」。更糟的是文件同时不在时，`EnsurePinnedAsync:697` 会直接
释放锁定并应用今天的图——而那张图其实完全可以重新下载。

改法：改用 日期 + imageId 匹配，`BingImageInfo.TryParseFileName` 已经现成。

### [ ] 8. 壁纸选择窗口可能并发发两次同样的请求

`UI/PickerForm.cs:278-306` `LoadImages`

没有 in-flight 保护。窗口开着时再点一次托盘的「选择日期」，`ShowPicker` 照样调 `LoadImages`，
`_images` 仍空就再发一条链（`FetchWindowAsync` 两页、每页 2/4/8 秒退避重试），两条链
`AdoptImages` / `Populate` 同一个活窗口。

改法：一个 `_fetching` 标志即可，和托盘的 `_busy` 一个思路。

### [ ] 9. `.jpg.tmp` 残留永远清不掉

`BingClient.cs:316`（临时文件名）、`323`（只在重试开头删）

没有 `finally` 兜底。四次重试全失败，或最后一次在 `Paths.MoveOverwrite`（`Paths.cs:180`，
`File.Replace` 的 `UnauthorizedAccessException` 仍未捕获）上失败时，`.jpg.tmp` 就留下了。
而 `WallpaperService.cs:210` 和 `288` 两处清理都只枚举 `*.jpg`，孤儿在受 `KeepDays` 约束的
目录里无限堆积。

改法：`finally` 里兜底删一次；或让清理器把 `*.jpg.tmp` 一并纳入。

### [ ] 10. 崩溃处理器自己可能爆栈

`Logger.cs:100` `Describe`

处理 `AggregateException` 时递归调用 `Describe(item)`，而 `depth` 是方法内的局部变量，每层递归
重新从 0 开始，外面的 `while (depth < 10)` 什么也没拦住。嵌套 `AggregateException` 会一路递归到
栈溢出——而这段代码存在的意义正是「保证崩溃不会无声无息」。

改法：把 `depth` 作为参数传下去（加一个私有重载）。

---

## P2 · 一致性、开销与文档

### [ ] 11. 启动时重复做可写探测

`Program.cs:31`（`LogEnvironment`）/ `Program.cs:55`（`RunGui`）

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

`UI/SettingsForm.cs:271-274`（按 `Enum.GetValues` 顺序填充）、`481` / `522-527`（按下标读写）

只在 `WallpaperFit` 保持 0..5 连续无空洞时才成立，往中间插一个成员就会静默错位，编译器不报错。
同文件的 `_intervalBox`、`_keepDaysBox`、`_shuffleIntervalBox` 都已用 `Choice` 包装携带真实值——
四个里三个用了正确写法，只剩它一个特例。

### [ ] 15. `BuildProtectedFiles()` 连着调用两次

`UI/TrayContext.cs:611` / `615`

两次各新建一个 `List<string>`，各自对同样的两个路径重跑 `Path.GetFullPath`，每个刷新周期都来一遍。
提一个局部变量到两次调用之上即可。（`1726` 行那处是单次调用，没问题。）

---

## P1 · 热路径性能（2026-09-06 新增）

### [ ] 16. `TileGrid` 每量一次行高就问一次屏幕 DC

`UI/TileGrid.cs:170` `TileHeight`（`Font.Height`）、`161` `CellHeight`、`486` `PaintTile`

`System.Drawing.Font.Height` 不是缓存值：每次读都会 `GetDC(NULL)` + 建一个 `Graphics` +
`GdipGetFontHeight` + `ReleaseDC`。而 `TileHeight` 被 `CellHeight`、`GetTileBounds`、
`GetVisibleRange`、`HitTest`、`PaintTile` 层层调用：

- 一次 `OnPaint`：每块磁贴 3 次，8 块可见就是 25 次；
- 每条 `WM_MOUSEMOVE`：`HitTest` 3 次，移过磁贴边界再加 4 次（两次 `InvalidateTile`）。

鼠标在收藏页上划一下，一秒钟上百次屏幕 DC 往返，滚动时更明显——这正是 LTSC 老机器最吃亏的地方。

改法：把 `TileHeight` / `CellHeight` 存进字段，`OnFontChanged` 时作废；或改用 `Control.FontHeight`
（WinForms 自己就是为这个缓存的）。

### [ ] 17. `BingImageInfo.ImageId` 每次读都重新解析一遍

`BingImageInfo.cs:49`

`ImageId` 是表达式属性，每读一次就跑一遍 `ExtractImageId`：`IndexOf` + 两次 `Substring` +
一个 `StringBuilder`。`GetFileName` 又建在它上面，于是 `FindImageIndex`（`TrayContext.cs:720`）
每次线性查找就是 15 次 StringBuilder + 三四十个临时字符串——而这个查找每个 shuffle tick
（`ApplyFavoriteCoreAsync:1384`）都要走一次。`UrlBase` 一旦从 JSON 填好就不再变。

改法：解析一次存字段（`UrlBase` 的 setter 里算，或懒加载缓存）。

### [ ] 18. 磁贴绘制每块都新建 GDI+ 对象

`UI/TileGrid.cs:516`（Pen）、`525`（SolidBrush）、`561`（Pen）、`593-594`（两个 SolidBrush）、
`643`（GraphicsPath）、`657`（Pen）

每块磁贴每次重绘至少一支 `Pen`，带角标的再加两支 `SolidBrush`，锁定角标还要一条 `GraphicsPath`
和一支 `Pen`。收藏夹几百张时快速滚轮，一秒钟就是几千个带终结器的 GDI+ 对象。

改法：调色板只有两套、颜色固定，可以把常用的 Pen/Brush 缓存在控件字段里，`ThemeChanged` 时重建。

### [ ] 19. 每行日志都开关一次文件

`Logger.cs:146`（`new FileInfo`）、`133`（`File.AppendAllText`）

每写一行都先 `new FileInfo(path)` 探一次长度做轮转判断，再 `File.AppendAllText` 打开-写入-关闭，
两次文件系统往返，而且全在全局 `Sync` 锁里。排障时把 `LogLevel` 调到 `Debug`，
`api: image …` 和 `thumbnail: …` 会让缩略图工作线程和 UI 线程排队卡在文件 IO 上。

改法：进程内保留一个打开的 `StreamWriter`（`AutoFlush` 或按批 flush），长度自己累加计数，
不必每行 stat。

### [ ] 20. `ThumbnailStore` 仍从列表头部出队，但实际队列很短

`ThumbnailStore.cs:413` `TakeNext`（`425` 行 `RemoveAt(0)`）

`List<string>` 上 `RemoveAt(0)` 每次都要整体前移，取完 n 项就是 O(n²)。问题仍存在，但性能影响远小于
原描述：当前固定 4 列 × 2 行，预取范围只有可见区上下各一行，待办通常不超过约 16~20 项，缓存命中的
项目还不会入队。因此这是低优先级的确定性小优化，不是高分屏下会放大到上百项的热点。

改法：换 `Queue<string>`，保持整批替换和按屏幕顺序处理的语义不变；或用游标下标避免移动元素。

### [ ] 21. `StepShuffle` 在忙碌判断之前就全量扫目录

`UI/TrayContext.cs:1155`

`_playlist.Sync(Favorites.Scan())` 放在 `_busy` 守卫之前，注释说「便宜到丢掉的一步也付得起」。
但 `Scan()` 是整个 `favorites\` 的目录枚举 + 逐个文件名解析 + 全量排序：收藏几千张时，
每个轮播周期（最短 1 分钟）都要跑一遍，就算这一步最后被丢掉也照跑。

改法：先过 `_busy` / `_applyingFavorite` 守卫再 `Sync`；或让 `Sync` 接受一个懒枚举。

---

## P2 · 资源句柄（2026-09-06 新增）

### [ ] 22. 每开一个窗口漏一个 `Font`

`Theme/ThemeManager.cs:123` `ApplySystemFont`、`UI/ErrorDialog.cs:44`

`SystemFonts.MessageBoxFont` 每次读都返回**新的** `Font`（文档明确要求调用方 Dispose），
而 `ApplySystemFont` 只是 `control.Font = font` 就撒手；`Control.Dispose` 从不释放外部赋给它的
字体。`ErrorDialog` 里的 `new Font(FontFamily.GenericMonospace, 9f)` 同理。

于是每开一次选择窗口 / 设置窗口 / 错误对话框，就多一个只能等终结器回收的 HFONT。
`Font` 有终结器，所以不是永久泄漏，但 GDI 句柄是每进程上限受限的资源，回收时机又不确定——
一个要跑几周的托盘程序不该把它交给 GC 决定。

改法：`ApplySystemFont` 里换字体前 Dispose 掉自己上次设的那个（或全程序共用一个静态 `Font`）；
`ErrorDialog` 用 `using` 管住等宽字体，在 `ShowDialog` 返回后释放。
