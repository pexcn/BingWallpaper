# CLAUDE.md

必应壁纸：Windows 托盘程序，`net48` + WinForms。
**初衷是在 Windows 10 LTSC 上不装任何运行时直接跑起来**，技术选型都由此而来。

**与我对话请用中文。**

## 硬约束

- **`net48`**：LTSC 2021（19044）内置的最新框架版本。只能用 .NET Framework 4.8 有的 API，
  `System.Text.Json`、`PublishSingleFile`、NativeAOT 都不可用（JSON 用 `JavaScriptSerializer`）。
- **零 NuGet 依赖**：只用 BCL + P/Invoke，否则就谈不上免安装。
- **性能优先**：避免热路径上的重复分配与多余拷贝。体积也是设计目标，但不拿性能去换。
- UI 全部由代码构建，无设计器、无 `.Designer.cs`。

## 语言

代码、注释、日志、提交信息用英文；UI 文案、README、TODO 用中文。
提交遵循 Conventional Commits，写清**为什么**这么改。

## 代码风格

全库一致，跟着写即可：

- 显式类型，不用 `var`
- 字符串用 `+` 拼接，不用 `$""` 插值
- file-scoped namespace；类型一律 `internal`，能 `sealed` 就 `sealed`
- 判空用 `is null` / `is not null`
- Allman 大括号；单行转发用表达式体成员
- 不用 `#region`；`Nullable` 已开启，不用 `!` 压制

## 注释

`///` 密度高是刻意的。**解释「为什么」，不复述代码做了什么** —— 为什么选这条路、
放弃了哪个替代方案、什么条件下该改回去。平台怪癖和未文档化行为必须写清缘由，
显而易见的赋值则不需要旁注。

## Win32 互操作

声明集中在 `NativeMethods.cs`（主题相关的在 `Theme/DarkModeNative.cs`），每个注明
Win32 名字和最低 Windows 版本。**未文档化的调用必须包 `try`/`catch`**，失败时降级并记日志。

## 错误处理

捕获并降级，而不是往上抛。每条降级路径都要 `Logger.Warn` / `Logger.Error` 说明发生了什么，
别静默吞掉；需要用户知道的失败走 `ErrorDialog`。

## 构建

CI 带 `-warnaserror`，**任何编译警告都会让构建失败**。

```powershell
dotnet publish src/BingWallpaper/BingWallpaper.csproj -c Release -o publish/BingWallpaper
```
