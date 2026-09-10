# RamDrive 安装后"警告 + 需管理员运行一次"问题诊断与修复方案

**问题现象（用户描述）**：安装后第一次打开总会弹一个警告（提到"回退/退到某种模式"），以管理员身份运行一次 `RamDrive.exe` 后自动修复。**卸载重装后，直接以管理员方式运行，它仍然报错，还是需要"再以管理员方式运行一次"才行。**

**用户的关键补充（2026-09-10 19:06）**：
> "我不是安装的时候给管理员权限，我是安装完了，我直接以管理员方式运行，它还是会报错，还是要再以管理员方式运行一次才行。不知道它是需要两次呢，还是必须要等它报错之后再运行一次。"

**这一句把问题定性彻底改变了** —— 见 §2.0。答案很明确：**不需要"两次"，也不取决于"是否先报错"；只取决于"WinFsp 驱动有没有重新加载过"。第一次运行负责写配置，第二次运行才真正生效。**

**诊断日期**：2026-09-10
**诊断方式**：源码静态分析（`setup/RamDrive.iss`、`src/RamDrive.Cli/WinFspHostedService.cs`、`Setup.bat`）

---

## 二·零、先直接回答你的疑问

> **"它是需要两次呢，还是必须要等它报错之后再运行一次？"**

**两个都不是。** 真正的规律是：

**WinFsp 的内核驱动只在"驱动加载时"读一次 `MountUseMountmgrFromFSD` 这个注册表值。程序把它写进去之后，必须等驱动重新加载，它才会生效。**

所以：

- **不需要"先报错"**。报错只是"驱动还没读到配置"的表现，不是触发条件。你完全可以第一次运行时无视警告、直接关掉，第二次运行照样能好。
- **"两次"只是表象**。真正需要的是"写配置的那次运行"和"驱动重新加载之后的那次挂载"发生**先后关系**。因为驱动不会自己重新加载，而你又不想重启系统，所以实际上就变成了"多运行一次"。
- **判定方法**：跑第二次之前，如果你手动重启过 WinFsp 驱动（或重启过系统），那么**第一次运行时就已经生效了**，不需要第二次。

**为什么会这样**，看程序自己的代码就明白了（`WinFspHostedService.cs`）。它在同一次运行里做了两件互相矛盾的事：

```csharp
// :66  —— 写入注册表（第一次运行时，因为值不存在，会真的写进去）
EnsureMountMgrFromFSD();

var session = CreateSession(opts);
if (!MountSession(session))   // :69 —— 紧接着就用新配置去挂载
```

而 `EnsureMountMgrFromFSD` 自己的日志文案（`:436-437`）已经把真相写出来了：

```csharp
_logger.LogInformation("Set WinFsp MountUseMountmgrFromFSD=1 in registry. " +
    "Mount Manager will be available from kernel driver on next launch.");   // ← next launch！
```

**代码说"下次启动才可用"，但同一段代码立刻就在本次启动里去用它了。** 这是这个 bug 最直白的证据：**写入时机与生效时机之间存在一次驱动加载的鸿沟，而代码没有跨越它。**

---

## 一、结论先行

这个警告是 **"Mount Manager 挂载失败，回退到 DefineDosDevice"** 那条 `LogWarning`。原文是：

> `Mount Manager mount failed (0x{Status:X8}). Falling back to DefineDosDevice. The drive will work but may be invisible to disk benchmark tools (e.g. ATTO). To fix, run RamDrive.exe once as administrator — it will auto-configure and work without admin afterwards.`

也就是说：**R 盘能用，但它不是通过"挂载管理器"注册的，而是走的退而求其次的 `DefineDosDevice` 老路径。** 后果是 `Get-Volume`、存储 WMI、ATTO 之类的磁盘工具看不到这个盘（上一轮诊断的 §八 也实测确认过这一点）。

**根因有两个，叠加在一起**：

1. **生效时机错位（主因，解释"为什么要运行两次"）**：配置写入之后，WinFsp 驱动已经加载完毕，**它不会重新读配置**。所以本次运行白写，必须等"下一次运行"（严格说是"下一次驱动加载"）才生效。
2. **安装器写入位置可疑（次因，解释"为什么安装没帮上忙"）**：安装器用的注册表视图与读取侧不对称，导致那次写入很可能落在错误位置或压根没生效。详见 §2.2。

**注意两者是独立的**：即使安装器写对了，只要驱动加载时机没对上，仍然会出现"第一次报警告"。安装器那条是"雪上加霜"，不是唯一原因。

---

## 二、根因分析

### 2.1 三条路径的对比

系统里有三个地方在操作 `MountUseMountmgrFromFSD` 这个注册表值：

| # | 位置 | 用的视图/路径 | 写入是否有效 |
|---|---|---|---|
| 1 | `setup/RamDrive.iss:383-387` `ConfigureWinFspMountManager`（安装器） | `HKLM` + `'SOFTWARE\WOW6432Node\WinFsp'` | ⚠️ **可疑**（见 2.2，需实测确认） |
| 2 | `src/RamDrive.Cli/WinFspHostedService.cs:422` `EnsureMountMgrFromFSD`（程序） | `Registry.LocalMachine` + `@"SOFTWARE\WOW6432Node\WinFsp"` | ✅ 正确（见 2.4） |
| 3 | `Setup.bat:82`（绿色版批处理） | `HKLM\SOFTWARE\WOW6432Node\WinFsp` via `reg add` | ✅ 正确（32 位脚本，WOW64 重定向恰好帮了忙） |

> **重要**：这三条路径无论对错，**都只解决"值有没有被写进去"，都不解决"驱动什么时候读到它"**。而后者才是你遇到的那个"要运行两次"的现象的主因（§2.0）。请把这两件事分开看。

### 2.2 安装器那条为什么可疑（次因，需实测）

安装器里写的是：

```pascal
procedure ConfigureWinFspMountManager;
begin
  RegWriteDWordValue(HKLM,
       'SOFTWARE\WOW6432Node\WinFsp',      // ← 手写了 WOW6432Node
       'MountUseMountmgrFromFSD', 1);
end;
```

关键在于 `ArchitecturesInstallIn64BitMode`（`CLAUDE.md:235` 记载 PR #18 为 ARM64 引入了它）。**开启 64 位安装模式后，`HKLM` 常量映射到 64 位注册表视图**，不再有 WOW64 自动重定向。

而**读取侧**（同一个文件、同一个概念）用的是完全不同的写法：

```pascal
// :237  GetWinFspInstallDir —— 读的时候用 HKLM32，且路径里不写 WOW6432Node
Result := RegQueryStringValue(HKLM32, 'SOFTWARE\WinFsp', 'InstallDir', InstallDir)
```

**读用 `HKLM32` + 不手写 `WOW6432Node`；写用 `HKLM` + 手写 `WOW6432Node`。** 两种写法"看起来"指向同一个物理位置，但这种不对称是典型的漂移隐患——只要 Inno 的视图映射行为有一点点不同（例如键不存在时需要新建、或受 UAC 虚拟化影响），写入就会落到 `...\WOW6432Node\WOW6432Node\WinFsp` 这种嵌套错误位置。

> **诚实标注**：仅凭静态阅读，我**无法断定**这条写入在你的机器上究竟落在哪里。§四 给了实地核对命令，**请务必先跑那三条 `reg query`**，在确认落点之后再决定要不要改安装器。
>
> 但有一条推理是确定的：**无论安装器写得多对，都不影响"第一次运行仍会警告"这个现象**——因为驱动加载时机的问题独立存在。所以**修不修安装器，取决于 §四 的核对结果；而"运行两次"的问题，必须改程序才能解决**（§三 方案 B）。

### 2.3 为什么"重装 + 管理员权限"救不了

这是你问题的核心，也是最反直觉的一点：

```powershell
# 用户以为：安装时给了管理员权限 → 应该已经配置好了
# 实际情况：
```

`MountUseMountmgrFromFSD` 是 **WinFsp 内核驱动在加载时读取一次**的配置项。而安装器的执行顺序是（`CLAUDE.md:237` 有详细记载）：

```
PrepareToInstall:
  1. 安装 WinFsp
  2. 停止服务 + 杀进程 + 卸载旧盘      ← 此时 MountUseMountmgrFromFSD 还没写
...
ssPostInstall:
  3. ConfigureWinFspMountManager      ← 才写注册表
  4. 创建并启动服务
```

即使第 3 步写对了，**第 4 步启动的服务仍然用的是"已经加载的旧驱动配置"**。驱动不会因为你改了个注册表值就重新读一遍。

**这与你观察到的现象完全吻合**：

- **第一次运行（管理员）** → `EnsureMountMgrFromFSD()` 发现值不存在 → 写入 → 但驱动已经加载完了，**新值对它无效** → `MountSession` 里 `host.Mount(@"\\.\R:")` 失败 → **警告 + 回退到 DefineDosDevice**。
- **第二次运行（管理员）** → `EnsureMountMgrFromFSD()` 发现值**已存在** → 立即 `return`（什么都不做）→ 但此时**距上次运行已经过去了一段时间，中间 WinFsp 驱动很可能已重载**（比如你期间重启过系统、或服务重启、或驱动按需加载）→ 这次 Mount Manager 成功了。

**所以"第二次能成功"不是因为第二次做了什么**，而是因为**第一次写下的值，在第二次时终于被驱动读到了**。这解释了你困惑的"它到底需要两次，还是必须先报错"——**都不是**，它需要的是"写配置"与"驱动加载"的先后顺序正确。

> **一个能立刻验证这个判断的实测**：在第一次运行报完警告、**不关程序**的情况下，另开一个管理员 PowerShell 执行 `sc.exe stop winfsp; sc.exe start winfsp`（强制驱动重载），然后重启 RamDrive 服务——**你会发现不需要"第二次运行程序"就已经好了**。这能直接证明"多运行一次"只是为了凑出一次驱动重载。

**卸载重装为什么又会复发**：卸载重装通常会重置 WinFsp 驱动状态（驱动被卸载/重装，或注册表值被清掉），于是"必须凑一次驱动重载"的条件重新成立。**"以管理员身份安装"完全没用，因为问题根本不是权限，是驱动加载时机。**

### 2.4 为什么程序自己写就有效

程序里的代码是正确的，因为它用 .NET 的 `Registry.LocalMachine`：

```csharp
using var key = Registry.LocalMachine.OpenSubKey(keyPath, writable: false);
if (key?.GetValue(valueName) is int val && val != 0)
    return; // already set
```

`RamDrive.exe` 是 **AOT 编译的原生 x64 进程**，`Registry.LocalMachine` 在 64 位进程里映射 64 位视图，配合同样手写的 `WOW6432Node` 明文路径，最终落到 WinFsp 读取的位置。**它是"两个错误相互抵消"式的巧合正确**，与安装器是同一种写法但执行上下文不同（安装器是 Inno 的 64 位模式，程序是原生 64 位 .NET）。

---

## 三、修复方案

> **先说优先级**：解决你的问题（"为什么要运行两次"）**必须改程序**，见方案 A。安装器那条（方案 B）是独立的次要项，改不改取决于 §四 的核对结果。

### 方案 A（治本，推荐）：程序侧"写完配置 → 重载驱动 → 重试挂载"

问题出在 `MountSession` 失败后**直接回退**，而没有先尝试"让刚写的配置生效"。正确逻辑是：

```csharp
// WinFspHostedService.cs — 改造后的 MountSession 骨架
private bool MountSession(Session session)
{
    string driveLetter = session.Options.MountPoint.TrimEnd('\\');
    string mountManagerPoint = @"\\.\" + driveLetter;

    var host = new FileSystemHost(session.Adapter);
    int result = host.Mount(mountManagerPoint);
    if (result >= 0) { /* 成功，走 Mount Manager */ return true; }

    // ── 新增：Mount Manager 失败 ──
    // 失败往往是因为 MountUseMountmgrFromFSD 刚写进去、驱动还没读到。
    // 与其直接降级到 DefineDosDevice，不如让驱动重载一次再试。
    if (_mountMgrConfigJustWritten)          // EnsureMountMgrFromFSD 本次是否真的写了新值
    {
        host.Dispose();
        if (TryReloadWinFspDriver())         // sc stop winfsp + sc start winfsp
        {
            host = new FileSystemHost(session.Adapter);
            result = host.Mount(mountManagerPoint);
            if (result >= 0) { /* 重试成功 */ return true; }
            host.Dispose();
        }
    }

    // 仍然失败 → 才回退（保留原有的 DefineDosDevice 逻辑 + 警告）
    host = new FileSystemHost(session.Adapter);
    ...
}
```

配套需要两处小改动：

1. **`EnsureMountMgrFromFSD` 要返回"本次是否写了新值"**，而不只是 `void`。只有"真的刚写了"才值得去重载驱动；如果值本来就在、只是驱动没加载，那是另一回事（这种情况重载也有效，可以一并覆盖）。

```csharp
private bool EnsureMountMgrFromFSD()   // ← 改返回类型
{
    ...
    if (key?.GetValue(valueName) is int val && val != 0)
        return false;                  // 已存在，没写
    ...
    writeKey.SetValue(valueName, 1, RegistryValueKind.DWord);
    return true;                       // 本次写了新值
}
```

2. **新增 `TryReloadWinFspDriver()`**，用 `sc.exe` 或 SCM API 停启 WinFsp 驱动。**注意需要管理员权限**——而 Windows 服务形态下 RamDrive 以 LocalSystem 运行，权限足够；控制台手动运行时才需要用户提权。

```csharp
private bool TryReloadWinFspDriver()
{
    // WinFsp 的驱动服务名通常是 "WinFsp"（内核驱动）。
    // 用 sc.exe 最省事，且不引入额外依赖。
    if (!RunSc("stop winfsp")) return false;
    Thread.Sleep(800);                   // 等驱动真正卸载
    return RunSc("start winfsp");
}
```

**这样改完的效果**：用户**第一次运行就不会看到警告**了——程序自己完成了"写配置 → 重载驱动 → 重试"这一整套。你也不再需要"运行两次"。

### 方案 B（次要）：修正安装器的注册表写入

**仅在 §四 核对确认写入落点错误时才需要做。**

`setup/RamDrive.iss` 的 `ConfigureWinFspMountManager` 改用 `HKLM32`，让 Inno 自己处理视图重定向，不再手写 `WOW6432Node`：

```pascal
procedure ConfigureWinFspMountManager;
begin
  // WinFsp 从 32 位注册表视图读取（HKLM\SOFTWARE\WinFsp，
  // 物理位置 HKLM\SOFTWARE\WOW6432Node\WinFsp）。
  // 用 HKLM32 让 Inno 做正确的视图映射——不要手写 "WOW6432Node"，
  // 那在 ArchitecturesInstallIn64BitMode 下会错位
  // （参照 GetWinFspInstallDir 的读取方式，PR #18 修过同一个坑的读侧）。
  RegWriteDWordValue(HKLM32, 'SOFTWARE\WinFsp', 'MountUseMountmgrFromFSD', 1);
end;
```

这与 `GetWinFspInstallDir` 的读取方式（`HKLM32` + `'SOFTWARE\WinFsp'`，`:237`）形成**对称**——读和写用同一个视图、同一个路径，这是最不容易再次漂移的写法。

**可选加强**：把写入挪到 `PrepareToInstall`（WinFsp 装好之后），并在写入后重载一次 WinFsp 驱动，这样安装完第一次启动就干净了：

```pascal
// PrepareToInstall 中，WinFsp 安装完成之后：
ConfigureWinFspMountManager;
Exec('sc.exe', 'stop winfsp', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
Sleep(1000);
Exec('sc.exe', 'start winfsp', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
```

`ssPostInstall` 里的调用可保留作幂等兜底（重复写无害）。

### 方案 C（治标，至少把文案改对）：修正误导性的警告

当前警告文案（`WinFspHostedService.cs:353-357`）:

> "To fix, run RamDrive.exe once as administrator — **it will auto-configure and work without admin afterwards**."

**这句话是误导的**。它让用户以为"运行一次就永久修好"，但真相是"**要等 WinFsp 驱动重新加载**"（重启系统、或重启 WinFsp 服务、或按方案 A 自动重载）。建议改为：

> "Mount Manager 挂载失败，已回退到 DefineDosDevice（盘可用，但部分磁盘工具看不到）。
> 原因通常是 WinFsp 需要重新加载驱动才能读到新配置。
> 请重启计算机，或执行：`sc stop winfsp && sc start winfsp`，然后重启 RamDrive 服务。"

**修了方案 A 之后，这条文案基本就用不到了**（因为不会再走到回退分支），但作为防御性提示仍然值得改对。

---

## 四、验证步骤（你在本机执行，用于确认根因）

第 1 步，确认安装器到底写到了哪里。**以管理员身份**打开 PowerShell，分别读两个位置：

```powershell
# 正确位置（WinFsp 真正读取的）
Get-ItemProperty 'HKLM:\SOFTWARE\WOW6432Node\WinFsp' -Name MountUseMountmgrFromFSD -ErrorAction SilentlyContinue

# 可能的错误落点
Get-ItemProperty 'HKLM:\SOFTWARE\WOW6432Node\WOW6432Node\WinFsp' -Name MountUseMountmgrFromFSD -ErrorAction SilentlyContinue

# 用 32 位视图读（最权威）
reg query "HKLM\SOFTWARE\WinFsp" /v MountUseMountmgrFromFSD /reg:32
```

**如果只有 `\WOW6432Node\WOW6432Node\` 那条有值** → 完全确认是安装器的路径 bug。

第 2 步，确认驱动重载能否即时修复：

```powershell
# 改好注册表后，重启 WinFsp 驱动
sc.exe stop winfsp
sc.exe start winfsp
# 然后重启 RamDrive 服务，观察是否还出现警告
Restart-Service RamDrive
Get-EventLog -LogName Application -Source RamDrive -Newest 20 | Select-Object TimeGenerated, Message
```

第 3 步，确认挂载是否走的是 Mount Manager：

```powershell
Get-Volume | Where-Object DriveLetter -eq 'R'
# 修复后：应能看到 R 盘
# 未修复：返回空（这正是上一轮诊断 §八 实测到的现象）
```

---

## 五、与你上次提的需求（保数据 + 少复制）的关系

你上次的要求是"改配置时尽量保住原有文件、减少复制"，这一条**已经在当前代码里实现了**，而且做得不错：

- 代码里有一条 `ReloadFastPath`（`WinFspHostedService.cs:203-234`）：当**页大小和容量都没变**时，直接复用原来的文件树和内存页池，只换挂载适配器，**零数据拷贝**。这正是你要的"少复制"。
- 只有容量或页大小变了，才走 `ReloadFullPathAsync` 的"快照 → 重建 → 还原"路径（有数据拷贝，但文件保住了）。

**但我在诊断中发现这条快速路径有一个隐患**（详见另一份报告 `RamDrive-代码质量诊断与优化建议-v2.md` 的 P0-1）：快速重挂载会重建内核卷实例、清空内核缓存，而缓存失效通知机制（`FspFileSystemNotify`）在默认配置下是**关闭**的。这可能导致改配置后短时间内读到旧数据。这一条建议一并修掉。

---

## 六、待办清单

> 优先级已按"你补充的信息"重排。**第 1 项才是解决你问题的那一条。**

| # | 事项 | 位置 | 优先级 |
|---|---|---|---|
| 1 | **程序侧"写完配置 → 重载 WinFsp 驱动 → 重试挂载"**，让第一次运行就干净 | `WinFspHostedService.cs` 的 `MountSession` + `EnsureMountMgrFromFSD` | **最高** |
| 2 | `EnsureMountMgrFromFSD` 改为返回"本次是否写了新值" | `WinFspHostedService.cs:420-444` | **最高** |
| 3 | 修正误导性的警告文案（"运行一次就永久修好"→ 说明需驱动重载） | `WinFspHostedService.cs:353-357` | 中 |
| 4 | 顺带修 P0-1（快速重挂载与通知机制的矛盾） | 见 v2 报告 §三 | **高** |
| 5 | 安装器注册表写入改用 `HKLM32`（**先做 §四 核对**） | `setup/RamDrive.iss:383-387` | 中 |
| 6 | 版本号统一到单一来源（`Directory.Build.props`） | 多处 | 低 |

---

## 七、一句话总结

**你不需要记"要运行两次"。真正的规律是：WinFsp 驱动只在加载时读一次配置，所以"写配置的那次运行"和"驱动重载之后的那次运行"必须分开。** 这个"分开"本该由程序自动完成（写完就重载驱动再重试），但当前代码写完之后**没有重载驱动就立刻挂载**，于是把这一步的成本转嫁给了你——表现为"要多运行一次"。

---

*本诊断基于源码静态分析。沙箱网络无法访问 GitHub（TLS 握手被拦截，已多路径确认），因此**未能修改代码、未能触发工作流、未能产出安装包**。§四 的验证步骤需你在本机执行以最终确认根因。*
