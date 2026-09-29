# TODO2：代码走查待修项

> 来源：2026-09-03 对全项目做的一次 code review。
> 2026-09-06 第二次复核：第 7 条已修复，从列表移除；1~6、8~15 原样保留（编号不变，方便和旧记录对照）。
> 2026-09-06 第三次复核（重点查内存泄漏与热路径性能）：新增 16~22 条。
> 2026-09-20 第四次复核：修正第 1、20 条描述；新增推荐修复批次。
> 2026-09-28：第一批（1、4）已修复，从列表移除；其余编号保持不变。
> 2026-09-28：壁纸应用结果与托盘状态（2、3）已修复，从列表移除。
> 2026-09-28：WinForms 热路径与 GDI 资源（16、18、20、22）已修复，从列表移除。
> 行号为记录问题时的源码行号，后续改动可能使其漂移，以方法名为准。

---

## 推荐修复批次

剩余建议分 **6 批**，按共同状态机、数据生命周期和资源所有权划分。每批完成后单独构建和验证，
避免一次改动横跨太多控制面。

1. **设置持久化与强类型选项**：5、14。都集中在 `SettingsForm` 的“控件值 → 配置值 → 落盘”链路，可一起补回滚并消除 `SelectedIndex` 对枚举数值的隐式依赖。
2. **图片身份与文件名匹配**：6、17。先缓存稳定的 `ImageId`，再让锁定图按日期 + 图片身份跨分辨率匹配；同一身份模型一次改完。
3. **网络请求生命周期**：8、9。给选择窗口请求加 in-flight 所有权，并用 `finally` 收口下载临时文件；一起验证取消、失败、重试和窗口关闭路径。
4. **日志可靠性与写入性能**：10、19。先保证异常描述器不会递归失控，再引入进程级 writer、长度计数和可靠关闭；两项共同影响故障路径，不宜拆开验证。
5. **启动探测与平台契约**：11、12、13。复用启动时的可写探测结果，并一次性统一 manifest、项目注释和 README 的 DPI / 最低系统版本表述。
6. **周期性目录与路径工作**：15、21。都在 `TrayContext` 的周期路径上：复用保护文件集合，并把收藏目录扫描移到忙碌守卫之后。

---

## P0 · 会让功能真的失效

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

### [ ] 17. `BingImageInfo.ImageId` 每次读都重新解析一遍

`BingImageInfo.cs:49`

`ImageId` 是表达式属性，每读一次就跑一遍 `ExtractImageId`：`IndexOf` + 两次 `Substring` +
一个 `StringBuilder`。`GetFileName` 又建在它上面，于是 `FindImageIndex`（`TrayContext.cs:720`）
每次线性查找就是 15 次 StringBuilder + 三四十个临时字符串——而这个查找每个 shuffle tick
（`ApplyFavoriteCoreAsync:1384`）都要走一次。`UrlBase` 一旦从 JSON 填好就不再变。

改法：解析一次存字段（`UrlBase` 的 setter 里算，或懒加载缓存）。

### [ ] 19. 每行日志都开关一次文件

`Logger.cs:146`（`new FileInfo`）、`133`（`File.AppendAllText`）

每写一行都先 `new FileInfo(path)` 探一次长度做轮转判断，再 `File.AppendAllText` 打开-写入-关闭，
两次文件系统往返，而且全在全局 `Sync` 锁里。排障时把 `LogLevel` 调到 `Debug`，
`api: image …` 和 `thumbnail: …` 会让缩略图工作线程和 UI 线程排队卡在文件 IO 上。

改法：进程内保留一个打开的 `StreamWriter`（`AutoFlush` 或按批 flush），长度自己累加计数，
不必每行 stat。

### [ ] 21. `StepShuffle` 在忙碌判断之前就全量扫目录

`UI/TrayContext.cs:1155`

`_playlist.Sync(Favorites.Scan())` 放在 `_busy` 守卫之前，注释说「便宜到丢掉的一步也付得起」。
但 `Scan()` 是整个 `favorites\` 的目录枚举 + 逐个文件名解析 + 全量排序：收藏几千张时，
每个轮播周期（最短 1 分钟）都要跑一遍，就算这一步最后被丢掉也照跑。

改法：先过 `_busy` / `_applyingFavorite` 守卫再 `Sync`；或让 `Sync` 接受一个懒枚举。


