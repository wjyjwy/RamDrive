# RamDrive 完整诊断报告（第三轮 · 本地真机版）

**诊断日期**：2026-09-10
**诊断方式**：**真机编译 + 真机跑测试**（不再只是静态阅读）+ 全量源码审查 + 前两轮结论逐条复核
**代码快照**：工作区 `R:\-\RamDrive`（含前两轮修复后的状态）
**对照文档**：`RamDrive-安装警告问题诊断.md`（第一轮）、`RamDrive-代码质量诊断与优化建议-v2.md`（第二轮）

---

## 零、先说结论（给你看的）

| 你最关心的事 | 结论 |
|---|---|
| **软件有严重问题吗？** | 核心（内存页池、并发写入、稀疏文件、安全描述符继承）**是健康的**。单元测试 94/94 全绿，磁盘上的数据完整性没有发现缺陷。 |
| **"装完要运行两次才不报警告"** | 根因确认：**安装器把配置写在 WinFsp 驱动已经加载之后**，驱动读不到。已修复（把写配置挪到装驱动之前）。**不需要"运行两次"了。** |
| **改配置会不会丢内存盘里的文件？** | 代码里的"快速重挂载"路径**已经做到零拷贝保数据**，这条是你上次要求的结果，还在。本次没有削弱它。 |
| **诊断过程会不会弄坏内存盘？** | 全程没碰。所有编译产物都输出到 `C:\rd-diag`，内存盘占用始终是 17–18MB / 2048MB。**我没动被服务监控的配置文件**（它在 `C:\Program Files\RamDrive\`，不在源码里）。 |
| **修了什么？** | 改了 15 个文件：1 个安装器顺序修复（治你那个警告）、1 个崩溃防护、3 个配置校验/一致性修复、1 个测试保护失效修复、6 处文档纠错。全部通过编译与单元测试。 |

**一句话**：核心没问题，但"安装体验"和"文档/测试的口径"有一批真实的坑。你实际遇到的那个警告，根因找到了，也修了。

---

## 一、本次与前两轮的根本差别

前两轮的诊断都在云端沙箱里做的，**连 .NET 都装不上**，所以结论全是"静态推理 + 待验证"。这一轮在本机，我先把工具链打通，然后真的编译、真的跑测试、真的挂载卷做对照实验。因此本报告里：

- ✅ 标 **【实测】** 的是我跑出来的事实；
- ⚠️ 标 **【推理】** 的是仍未被真机证实的；
- ❌ 标 **【证伪】** 的是我复核后发现前两轮**说错了**的。

**打通工具链时踩到的两个真问题**（都不是 RamDrive 的代码问题，但会挡住你）：

1. **`global.json` 把 SDK 版本锁死 → 任何本地/CI 构建都会失败。**
   它要求 `10.0.103` 且 `rollForward: latestPatch`（只接受 10.0.1xx 补丁），而机器上装的是 `10.0.401`（feature band 4xx），于是 `dotnet build` 直接报"A compatible .NET SDK was not found"。
   → **已修**：`rollForward` 改为 `latestFeature`（接受任意 10.0.x）。
   ⚠️ 顺带说：CI（`.github/workflows/ci.yml`）用的是 `dotnet-version: "10.0.x"`，装的也是最新的 10.0.4xx，所以**CI 的构建腿大概率同样是坏的**——只是 CI 是手动触发（`workflow_dispatch`），一直没跑，没人发现。

2. **沙箱把环境变量剥掉了 → NuGet 全机器失效。**
   报错 `error : Value cannot be null. (Parameter 'path1')`，连一个全新的控制台项目都还原不了。查下来是 NuGet 在 Windows 上要解析"机器级设置目录"时用 `PROGRAMFILES`（还有 `APPDATA` / `LOCALAPPDATA` / `ProgramData`），而这些变量当时在 shell 里全是空的 → `Path.Combine(null, …)` 抛异常。
   → **根因（21:40 实测确认）**：这是 **WorkBuddy 的安全沙箱**造成的——它启动子进程时把这批变量剥掉了。**关掉沙箱后这些变量自然都在，`dotnet` 不需要任何包装脚本就能直接用**（详见 §十二）。
   → 诊断期间我用了个包装脚本 `.workbuddy/bin/dn.sh` 补这批变量；**现在已不需要**，保留仅为留痕。

---

## 二、环境与"不动内存盘"的安全策略

### 2.1 环境事实（全部实测）

| 项 | 值 |
|---|---|
| `R:` | 2048MB 的内存盘，已用 17–18MB（1%） |
| 工作区 | `R:\-\RamDrive` —— **工作区本身就在内存盘里** |
| 运行中的服务 | `RamDrive.exe` PID 2688，会话 `Services`（LocalSystem） |
| 服务名 | `RamDrive RAM Disk` |
| WinFsp | 已安装（`C:\Program Files (x86)\WinFsp\bin`） |
| .NET SDK | 仅 `10.0.401` |
| 已安装程序 | `C:\Program Files\RamDrive\`（exe 日期 **9-6**，比仓库代码旧） |
| **服务实际读取的配置** | **`C:\Program Files\RamDrive\appsettings.jsonc`** ← 关键 |
| 内存/CPU | 32GB 内存，空闲约 16GB |

### 2.2 你担心的两件事，我是这么处理的

**（1）"别把内存盘挤掉" —— 这里我一开始理解错了，已纠正**
- 我最初把这条理解成"不要把编译产物放进内存盘"，于是把产物重定向到了 `C:\rd-diag`。**这是误解，方向反了。**
- 你的真实意思：**工作就应该在内存盘上做**（软件开发写操作频繁，内存盘快）；你担心的是两件**具体**的事——① 测试时**重复挂载服务正占用的那个盘**，会不会把它挤掉、连工作区一起丢；② 测试**改动服务监控的配置文件**，触发它自动重载。
- 已改为**直接在 `R:` 上构建与测试**。实测代价很小：整个解决方案 Debug 产出约 **75MB**，`R:` 从 18MB → **96MB（占 2048MB 的 4.7%）**；增量构建约 **5 秒**，全量约 18 秒；`.gitignore` 已含 `bin/`、`obj/`，不污染 git。C 盘那份重复产物已删除。
- 关于 ①：查过 `MountSession` 的实现——它**只负责挂载，从不卸载任何别的卷**。所以拿空闲盘符做测试，客观上不存在"把服务占用的盘挤掉"的代码路径。我全程也确实只在 `S:`–`Z:` 这些空闲盘符上做实验；事后核对 `fsutil fsinfo drives` 只剩 `C:` 和 `R:`，`R:` 读写正常。
- 集成测试挂载的是临时盘符，数据在测试进程自己的内存里，**不占 `R:`**。

**（2）"别碰到它监控的配置文件，你一测试它可能被改，而它会监控那个配置文件"**
- 这是本轮最容易出事的地方，结论是**不会发生**：服务通过 `UseContentRoot(AppContext.BaseDirectory)` + `AddJsonFile("appsettings.jsonc", reloadOnChange: true)` 读的是**它自己 exe 旁边那份**，也就是 `C:\Program Files\RamDrive\appsettings.jsonc`。
- 源码里的 `src/RamDrive.Cli/appsettings.jsonc` 跟它**是两个不同的文件**。我在源码里改配置、编译、跑测试，**不会触发服务的重载**。
- 我全程**只读**过 `C:\Program Files\RamDrive\appsettings.jsonc`，从未写入。
- 一个副作用我要点出来：为了做对照实验，我用**已安装的旧版 exe**临时挂载过 `W:`（它读那份被监控的配置，只读不写）。已确认它没有改动配置、`EnsureMountMgrFromFSD` 也因为是幂等的而直接返回。

> **给你的操作建议**：以后你想改内存盘配置，**改 `C:\Program Files\RamDrive\appsettings.jsonc`**（开始菜单里有"编辑配置"快捷方式），那才会生效。改源码里那份只影响源码编译出来的程序。

---

## 三、真机测试结果

### 3.1 单元测试：**105 / 105 全通过**【实测】

```
已通过! - 失败: 0，通过: 94，已跳过: 0，总计: 94，持续时间: 46 ms
```

覆盖：`PagePool` 记账不变式、`PagedFileContent` 三阶段写入与稀疏语义、`RamFileSystem` 的移动/删除/快照恢复、配置校验、SD 继承、失败路径。**其中 4 条是我本次新增的**（见 §六）。

### 3.2 集成测试：沙箱开着时 6/48（42 个假失败），**关掉沙箱后 48/48 全绿**【实测】

失败项**全部**是同一个错：`System.UnauthorizedAccessException: Access to the path 'Z:\…' is denied`，抛在 `Directory.CreateDirectory`。通过的那 6 项恰好是**只读**的（读安全描述符、校验 SDDL 常量）。

我做了完整的对照实验来定性，结论是**这个失败是本会话环境的产物，不是 RamDrive 的代码缺陷**：

| 实验 | 结果 | 说明 |
|---|---|---|
| 在**正在运行的 `R:`**（服务提供的卷）上 `mkdir` + 写文件 | ✅ 成功 | WinFsp + ACL 继承在这台机器上是好的 |
| `icacls R:\-` | ✅ `Everyone:(OI)(CI)(F)` | 根安全描述符与继承正确 |
| 用 **PowerShell 的 .NET API** 在**同一个新挂载卷 `X:`** 上 `Directory.CreateDirectory` / `File.WriteAllText` / `FileStream` | ✅ **全部成功**，`Get-Acl` 正常 | 同一卷、同一 .NET，换个启动方式就好了 |
| 用**当前源码编译的** exe 挂载新卷后写文件 | ❌ 被拒 | |
| 用**已安装的 9-6 旧版** exe 挂载新卷后写文件 | ❌ 同样被拒 | **新旧二进制行为一致 → 不是代码回归** |
| 关内核缓存（`FileInfoTimeoutMs=0`）后写文件 | ❌ 仍被拒 | 与缓存无关 |
| 开通知（`EnableNotifications=true`）后写文件 | ❌ 仍被拒 | 与通知无关 |
| **关掉沙箱**后跑集成测试 | ❌ 仍失败 | 与沙箱无关 |
| 新挂载后逐秒重试写文件，持续 45 秒 | ❌ 45 秒内一直失败；但建目录一直成功 | 现象稳定可复现 |

**判断（21:45 已实测确证）**：根因就是 **WorkBuddy 的安全沙箱**。关掉沙箱后，同样的命令、同样的会话：

```
已通过! - 失败: 0，通过: 48，已跳过: 0，总计: 48，持续时间: 1 m 23 s
```

——**48/48 全绿**。所以这不是"某个模糊的受限会话"，而是沙箱在子进程层做的事（剥离环境变量 + 拦截新挂载卷上的创建操作）造成的。

**对你的实际影响**：
- 不影响你用 `R:`，也不影响产品本身——**你自己在普通控制台（连管理员都不需要）跑也全绿**。
- 沙箱关闭后，这批假失败**不会再出现**。诊断期间我用了包装脚本补环境变量，**现在也不需要了**（见 §十二）。

**我要更正之前的一句含糊说法**：我先前说"关沙箱也没用"——那是我用的**单条命令的临时绕过开关**，它只放开"命令校验"，不动子进程层；**只有设置里的全局沙箱开关**才真正解决。这一点我之前判断错了，以本节实测为准。

---

## 四、前两轮结论的真机复核（逐条）

这是本轮的重要产出：**前两轮的结论不能照单全收**。

### ✅ 4.1 第一轮"安装后要运行两次"——主因确认

第一轮说：`MountUseMountmgrFromFSD` 这个注册表值由 **WinFsp 内核驱动在加载时读一次**，而安装器是**先装 WinFsp（驱动加载）→ 后来才写注册表**，所以第一次启动的服务读不到，要等一次驱动重载（= 你"再运行一次"）。

复核（`setup/RamDrive.iss`）：

```
PrepareToInstall:  844-845  InstallWinFsp          ← 驱动在这里加载
ssPostInstall:     861-864  ConfigureWinFspMountManager  ← 注册表在这里才写
```

**顺序确实是反的，主因成立。** 而且这条还有一个"反过来更好修"的性质：把写注册表挪到装驱动之前，就彻底不需要重载驱动了（见 §六）。

### ❌ 4.2 第一轮"安装器注册表视图不对称"——**证伪**

第一轮把"安装器写的 `HKLM + 'SOFTWARE\WOW6432Node\WinFsp'`"当成可疑的"次因"，理由是读侧用的是 `HKLM32 + 'SOFTWARE\WinFsp'`，两者不对称。

**这条是误报。** 在 Inno 的 64 位安装模式下：

- 写侧：`HKLM`（64 位视图） + `SOFTWARE\WOW6432Node\WinFsp` → 物理键 `HKLM\SOFTWARE\WOW6432Node\WinFsp`
- 读侧：`HKLM32`（32 位视图） + `SOFTWARE\WinFsp` → 物理键同样是 `HKLM\SOFTWARE\WOW6432Node\WinFsp`

**`HKLM(64) + WOW6432Node\X` 与 `HKLM32 + X` 指向同一个物理位置。** 所以安装器的写入落点是**对的**，不需要改。（写法和读法风格不统一只是观感问题，不是 bug。）

### ⚠️ 4.3 第一轮"方案 A：程序内重载 WinFsp 驱动再重试挂载"——**有风险，不建议**

第一轮推荐的修复是：挂载失败后，程序自己 `sc stop winfsp` + `sc start winfsp`，再重试。

**这个方案我认为危险，理由是本项目的特殊性**：RamDrive 的内存盘**本身就是 WinFsp 驱动提供的**。在一个正在用 `R:` 当工作盘的进程里停掉 WinFsp 驱动，等于**把自己的卷连同上面的数据一起撤掉**（而且 `R:` 上就住着你的工作区）。此外驱动上有已挂载卷时 `sc stop` 通常会失败。

**我采用了更安全的替代**：把"写注册表"提前到"驱动加载之前"（§六-修复1）。既不重载驱动，也不碰正在运行的卷。

### ✅ 4.4 第二轮 P0-1"通知机制默认关闭"——存在，但**定性要改**

第二轮说这是个 P0（数据丢失级）。复核后：**现象是真的，但严重度判断偏了，而且两处事实说错了。**

**真实的部分**：
- `WinFspRamAdapter.Notify()` 第一行是 `if (!_options.EnableNotifications) return;`（`:757`），而 `RamDriveOptions.EnableNotifications` 默认 `false`。所以**默认情况下通知矩阵一次都不会执行**。
- 项目自己的规格 `openspec/specs/cache-invalidation/spec.md` 明确要求：**"每个改动路径的回调 MUST 发送 `FspFileSystemNotify`"**，而且没有"除非 `EnableNotifications=false`"的例外条款。`file-info-timeout-config` 规格还写"正确性完全依赖于通知矩阵"。
- `docs/leveldb-cache-coherency-postmortem.md` 记载：修 Chrome/leveldb 那个 bug 的**主要手段就是补上这些 Notify 调用**。
- 结论：**代码违反了自己的规格**，而且规格里被当作"主要正确性机制"的东西默认是关的。这确实是个真问题——但它的性质是**"规格/文档/测试与代码三者口径不一致 + 一层保护网没启用"**，不是"数据正在被损坏"。

**第二轮说错的两处**：
1. 它说"集成测试夹具用 `FileInfoTimeoutMs=uint.MaxValue` **+ 通知开启**的假设来抓回归"。**错**，夹具（`RamDriveFixture.cs:36-46`）**并没有**设 `EnableNotifications`，也就是**通知也是关的**。
   → 这反而暴露了一个更尖锐的问题：**夹具钉死 `uint.MaxValue` 的意图是"让漏发通知必然导致测试失败"，但因为通知被关，`Notify` 是空操作，漏发和不漏发**完全无法区分**——这个"回归门禁"从来没生效过。**（我已修，见 §六-修复5）
2. 它说生产默认参数组合"没有测试覆盖"。**不全对**：`tests/RamDrive.IntegrationTests/LevelDbReproTests.cs` 里已经有 `LevelDbDefaultTimeoutTests`，专门用生产默认 `FileInfoTimeoutMs=1000` 复现 leveldb 序列。这一点上第二轮的判断过重了。

**关于严重度**：我不同意把它定为"数据丢失级 P0"。因为在生产默认（`1000ms`）下，即使某条通知漏发，内核缓存的陈旧条目最多存活 1 秒后自行过期，兜底是存在的。真正危险的是**"永久缓存 + 通知关闭"这个组合**——那个组合我已经做成**启动即拒绝**（§六-修复5）。

### ❌ 4.5 第二轮 P1-1"`FileNode.Dispose` 重复递归遍历是 O(树大小) 的浪费"——**证伪**

`FileNode.Dispose` 的实现是：

```csharp
foreach (var child in Children.Values) child.Dispose();
Children.Clear();          // ← 关键
```

**第一次调用末尾就 `Clear()` 了**，所以第二次调用遍历的是空字典，本来就是 O(1)。第二轮的"重复递归 O(树大小)"不成立。（我仍然加了一个显式 `_disposed` 卫兵，理由是防御未来的改动，不是修复现存缺陷——见 §六-修复4。）

### ❌ 4.6 第二轮 P2-14"至今没有 Roslyn 分析器"——**证伪**

`dotnet build` 实测产出 **16–18 条分析器警告**，全部来自内置的 .NET 分析器与 xUnit 分析器，例如：

```
warning CA1416: 可在所有平台上访问此调用站点。"RamDriveFixture.Root" 仅在 'windows' 上受支持。
warning xUnit1031: Test methods should not use blocking task operations, as they can cause deadlocks.
```

**分析器是开着的**（.NET 5+ 默认 `EnableNETAnalyzers=true`）。第二轮里**对的只有一半**：`TreatWarningsAsErrors` 确实没开。

---

## 五、新发现（本轮第一次提出）

### 5.1 🔴 畸形 `MountPoint` 会让程序**原生崩溃**（可复现）【实测】

**复现**：把配置里的挂载点写成带正斜杠/斜杠组合（例如 `S://`，或在 JSON 里写了非预期格式），启动即崩溃：

```
Fatal error.
0xC0000005
   at WinFsp.Native.Interop.FspApi.<FspApi>g____PInvoke|8_0(IntPtr, UInt16*, IntPtr)
   at WinFsp.Native.WinFspFileSystem.Mount(String, UInt32, Boolean, UInt32)
   at RamDrive.Cli.WinFspHostedService.MountSession(Session)
```

**成因**：`MountSession` 只做 `MountPoint.TrimEnd('\\')`，不做格式校验，然后把结果拼进 `\\.\` 前缀交给原生 API。`\\.\S://` 这种非法挂载点会让 `FspFileSystemSetMountPointEx` 访问越界。（`CLAUDE.md` 已记载过 `"R:\"` 尾巴反斜杠会崩，这是同一族问题的另一个入口。）

**影响**：用户配置写错格式 → **进程直接崩**，而不是给出一条"挂载点格式非法"的提示。

**本次状态**：**未修**（属于需要加输入校验的中等改动）。建议在 `RamDriveOptions.Validate()` 里加一条：`MountPoint` 必须匹配 `^[A-Za-z]:(\\)?$` 或 `^\\\\\.\\[A-Za-z]:$`，否则拒绝启动。这条我会放进你的待办（§七-1）。

### 5.2 集成测试的"缓存一致性门禁"是空转的【实测】

见 §四-4.4 第 1 点。**已修**：夹具现在会真的开启通知，漏发 `Notify` 才可能被测试抓到。

### 5.3 CI 回归门禁长期处于"手动挡"

`.github/workflows/ci.yml` 只监听 `workflow_dispatch`（注释解释了原因：fork 环境会自动 push 分支，怕误触发）。**后果**：除非有人手动点 "Run workflow"，**代码改动不会跑任何测试**。上面 4.1 的安装器 bug、以及第二轮那批修复，实际上都没经过 CI 验证。

### 5.4 文档与现实的多处漂移（除 §四-4.4 已列）

| 位置 | 问题 |
|---|---|
| `README.md:81` | TLA+ 链接域名写成 `lamport.azurewebsted.net`（应为 `azurewebsites.net`，且该域名早已迁移） |
| `RamFileSystem.cs:40` | 注释还写 "Dokan convention"（早已不是 Dokan 后端）——**已修** |
| `README.md` "Reload on config change" | 只描述"快照→重建→还原"的旧流程，完全没提新增的"快速重挂载"零拷贝路径，也没提它会**清空内核缓存**（改一次配置会有一次短暂读性能回落） |
| `CLAUDE.md` 配置表 | 缺 `EnableNotifications` 行；且 `FileInfoTimeoutMs` 那行**声称"每次都通过 `FspFileSystemNotify` 主动失效缓存"**，与默认关闭的事实相反——**已修** |
| `repro_chrome.js` | 含开发者本机路径 `C:\Users\HuYao\…`，且仍在发行物里 |
| 版本号 | `Directory.Build.props` = `1.0.0-dev`，`RamDrive.iss` = `0.0.0-dev`，WinFsp 版本号在 `.iss` / `release.yml` 各写一份（靠注释保持同步 = 没有同步） |
| 无 `.editorconfig` / `TreatWarningsAsErrors` | 对一个含 `unsafe` + 手写无锁协议的项目，这是最大的可维护性杠杆 |

### 5.5 `DirectoryNode` 与 `WindowsNameRules` 的保留名校验漂移【实测，已修】

- `WindowsNameRules.IsReservedBaseName` 会**先按第一个 `.` 截断**再比较 → `CON.txt` 被拒。
- `DirectoryNode.IsReservedName` **不按 `.` 截断** → 配置里写 `"CON.txt": {}` **能通过校验**，然后被 `RamFileSystem.CreateDirectory` 用 `WindowsNameRules` 拒掉 → **静默少建一个目录**。

两处注释都写着"keep the rule sets in sync"——但**靠注释同步等于没同步**。**已修**（并加了回归测试）。

### 5.6 已确认良好的部分（不必动）

- `PagePool` 的 `_committedCount` 单闸门记账（第一轮 P0-1 的修复）——设计正确，单元测试覆盖到位。
- `PagedFileContent.Write` 的三阶段写入（读锁扫描 → 锁外批量取页 → 写锁 memcpy）——注释与实现一致，是正确的写锁最小化。
- 根安全描述符的 `OICI` 处理 + SD 继承 —— 我们的对照实验在真机上证实 `icacls` 显示的继承 ACL 正确。
- 快照/恢复（`CreateSnapshot`/`RestoreSnapshot`）与 `ReloadFastPath` 的**零拷贝**设计——符合你"保数据、少复制"的要求。
- `FsTracer` 用 `[Conditional]` + csproj 条件常量，生产零开销。

---

## 六、本次实施的修复（逐条）

> 原则：**只做我能推理清楚、且不会破坏"保数据"承诺的改动**；行为变更一律最小化并记录。
> 全部改动 = **15 个文件，+144 / −11 行**；构建 **0 错误**，单元测试 **94/94**。

### 修复 1｜安装器：写注册表提前到 WinFsp 驱动加载之前 —— **治你那个"要运行两次"**
**文件**：`setup/RamDrive.iss`
**改动**：`PrepareToInstall` 里，在 `InstallWinFsp` **之前**先调用 `ConfigureWinFspMountManager`；`ssPostInstall` 里那次保留作幂等兜底（覆盖"用户没选派 WinFsp 组件"的路径）。
**为什么**：WinFsp 驱动在 `DriverEntry` 只读一次该值。写在驱动加载前，新驱动一上来就看到 `1`，**第一次启动就直接走 Mount Manager，不再有警告，也不再需要"再运行一次"**。
**风险**：低。只影响全新安装/重装 WinFsp 的路径。
**验证状态**：⚠️ **未在真机验证**（本机没有 Inno Setup，也没以管理员跑安装器）。逻辑等价于"把已有调用前移"，Pascal 语法无新构造。

### 修复 2｜把误导性的警告文案改成真实原因
**文件**：`src/RamDrive.Cli/WinFspHostedService.cs`（`MountSession`）
**改动**：原文案说 "run RamDrive.exe once as administrator — it will auto-configure and work without admin afterwards"（暗示"跑一次就永久好了"）。改为说明**真正原因是 WinFsp 驱动需要重载**，并给出正确操作（重启系统，或 `sc stop winfsp` + `sc start winfsp`）。
**为什么**：原文案是本项目最大的"误导性文档"，它会让人（包括前两轮的部分推理）往"权限"方向找原因。
**验证状态**：✅ 编译通过。

### 修复 3｜`DirectoryNode` 保留名校验与 `WindowsNameRules` 对齐
**文件**：`src/RamDrive.Core/Configuration/DirectoryNode.cs`
**改动**：`IsReservedName` 增加"按第一个 `.` 截断"，与 `WindowsNameRules.IsReservedBaseName` 完全一致；补注释说明必须同步的理由。
**为什么**：消除 §5.5 的静默少建目录。
**验证状态**：✅ 新增单元测试 `ReservedNameWithExtension_ReportsError` 通过。

### 修复 4｜`FileNode.Dispose` 加显式幂等卫兵
**文件**：`src/RamDrive.Core/FileSystem/FileNode.cs`
**改动**：加 `_disposed` 卫兵 + 说明。
**为什么**：防御性。**注意**：第二轮的"重复递归 O(树大小)"是错的（`Children.Clear()` 已保证 O(1)），所以这不是修复现存缺陷，而是把不变式写死、防止未来有人删掉 `Clear()` 时静默退化。
**验证状态**：✅ 编译 + 既有测试通过。

### 修复 5｜堵住"永久缓存 + 通知关闭"这个危险组合；测试门禁真正生效；**并把通知默认值定为 `true`**
**文件**：`src/RamDrive.Core/Configuration/RamDriveOptions.cs`、`tests/RamDrive.IntegrationTests/RamDriveFixture.cs`、`tests/RamDrive.IntegrationTests/InitialDirectoriesSdTests.cs`、`src/RamDrive.Cli/appsettings.jsonc`、`src/RamDrive.Cli.Diag/appsettings.jsonc`、`CLAUDE.md`、`README.md`
**改动**：
1. `Validate()` 新增规则：`EnableKernelCache=true` + `EnableNotifications=false` + `FileInfoTimeoutMs=uint.MaxValue` → **拒绝启动**并给出可读原因。（与项目既有风格一致：`PagePool` 对非法配置就是"拒绝启动而不是稍后崩"。）
2. 集成测试的两个夹具补上 `EnableNotifications = true`——它们的意图本来就是"让漏发通知必然失败"，现在才真正成立。
3. **`EnableNotifications` 默认值 `false` → `true`**（含两份 `appsettings.jsonc`、`CLAUDE.md`、`README.md`）。
**为什么默认改为 `true`（第一性原理）**：`MoveFile` 的返回类型是 NTSTATUS、`Cleanup` 是 void、`CanDelete` 是 NTSTATUS——**这三个回调都没有 `FspFileInfo` 可返回**。对它们来说，"通知"不是冗余加固，而是内核**唯一**能得知"某条缓存已失效"的途径。而 leveldb/Chrome 的故障恰好就是"改名替换 + 立刻重读"，微秒级的重读根本等不到 `FileInfoTimeoutMs`（默认 1000ms）兜底。所以"回调返回的 FileInfo 已足够"这个理由**不覆盖改名/删除**，默认关闭是个错误选择。这也是 `openspec/specs/cache-invalidation` 本来的要求。
**代价与退路**：开启后每次元数据变更多一次内核 IOCTL（通过线程池派发，不在 I/O 线程上）。⚠️ **本环境无法测量吞吐影响**。如果将来在拆包、`npm install` 这类元数据密集场景感到变慢，把它改回 `false` 即可（一行）。
**注意**：**你已安装的服务读的是它自己目录下的 `C:\Program Files\RamDrive\appsettings.jsonc`，那份仍显式写着 `false`** —— 想让改动生效，需要改那一行或重装。
**验证状态**：✅ 新增 3 条单元测试通过；✅ 已核对运行中的服务配置不会被新校验规则卡住；✅ R 盘构建 0 错误、单元测试 94/94。

### 修复 6｜解锁构建（`global.json`）
**文件**：`global.json` —— `rollForward: latestPatch` → `latestFeature`。
**为什么**：§一-1。不改则本机与 CI 都构建不了。
**验证状态**：✅ 实测构建通过。

### 修复 7｜文档纠错
**文件**：`CLAUDE.md`、`README.md`、`src/RamDrive.Cli/appsettings.jsonc`、`src/RamDrive.Cli.Diag/appsettings.jsonc`
**改动**：
- `CLAUDE.md` 配置表新增 `EnableNotifications` 行；把 `FileInfoTimeoutMs` 行改成与实现一致的描述（澄清"通知默认关闭"）；新增一段"配置不变式"和一段对 `specs/cache-invalidation` 的说明（指明规格与实现的差异及其和解方式）。
- `README.md` 配置示例补上 `FileInfoTimeoutMs` / `EnableNotifications`。
- 两份 `appsettings.jsonc` 的注释补充"`uint.MaxValue` 必须与 `EnableNotifications=true` 同时使用，启动时校验"。
- 顺手修掉 `RamFileSystem.cs` 里过时的 "Dokan convention" 注释。
**验证状态**：✅ 编译通过。

---

## 七、未修但建议做的（按优先级）

> **更新（21:00）**：用户要求这些"由我自行判断并实施"。其中 **#1、#4、#5、#6、#8、#9、#10、#12 已完成**（详见 §十）；**#6 比较器统一、#8 AllocationSize、#3 的 CI 触发策略、#11 的 CI 侧版本来源** 仍保留为待办，原因见 §十末表。

| # | 事项 | 位置 | 优先级 | 为什么值得做 |
|---|---|---|---|---|
| 1 | **`MountPoint` 输入校验**，非法格式拒绝启动而不是崩溃 | `RamDriveOptions.Validate()` + `WinFspHostedService.MountSession` | **高** | §5.1，一个可复现的原生崩溃 |
| 2 | ~~决定 `EnableNotifications` 的生产默认值~~ **已决：改为 `true`**（见 §六-修复5） | `RamDriveOptions` + 两份 `appsettings.jsonc` | ~~高（需你拍板）~~ **已完成** | 理由：`MoveFile`/`Cleanup`/`CanDelete` 都不返回 `FspFileInfo`，通知是内核唯一能得知缓存失效的途径；leveldb/Chrome 的"改名+立刻重读"等不到 1 秒超时兜底。代价是每次元数据变更多一次内核 IOCTL（无法在本环境测量）。**记得：已安装服务读的是它自己那份配置，仍显式写着 `false`。** |
| 3 | 把 CI 改成**至少对 tag / 手动发布**必跑，并修 CI 的 SDK 解析 | `ci.yml` | **高** | §5.3，回归门禁长期空转 |
| 4 | 本地跑集成测试的**正确姿势**写进 README | `README.md` / `CLAUDE.md` | 中 | §3.2，否则你或别人本地看到 42 个红会误判 |
| 5 | `SetFileAttributes(0)` 语义对齐参考实现（`0` 应表示"清空属性"） | `WinFspRamAdapter.cs:462` | 中 | 与 `MemfsReferenceFs` 的差分 oracle 存在语义分歧，属"可被差分测试抓到"的漂移 |
| 6 | 目录枚举比较器统一到 `MemfsFileNameComparer` | `RamFileSystem.cs:197`、`WinFspRamAdapter.cs:714` | 中 | 非 ASCII 文件名（中文）的枚举顺序与 NTFS 不同，`marker` 分页极端情况下可能重复/遗漏 |
| 7 | `ListDirectory` 的全量 `OrderBy` 移出全局结构锁（或加脏标记缓存） | `RamFileSystem.cs:188-200` | 中 | 大目录枚举会阻塞所有结构操作 |
| 8 | `MakeFileInfo` 的 `AllocationSize` 保守取 `max(allocated, roundup(FileSize, pageSize))` | `WinFspRamAdapter.cs:776` | 低 | 纯扩展后 `AllocationSize < FileSize`，内核 `Cc` 与部分应用假设相反 |
| 9 | 加快捷方式/文档：明确"改配置就改 `C:\Program Files\RamDrive\appsettings.jsonc`" | 文档 | 低 | 避免改错文件却不生效 |
| 10 | `README.md` 修 TLA+ 域名拼写；`repro_chrome.js` 匿名化或移出发行物 | 文档/打包 | 低 | 观感与隐私 |
| 11 | 统一版本号单一来源（`Directory.Build.props` 同时供 `.iss` 与 `release.yml` 读取） | 构建 | 低 | §5.4 |
| 12 | 加 `.editorconfig` + `TreatWarningsAsErrors`（可分批放开） | 构建 | 低 | 免费抓边界失误 |

---

## 八、专门回应你提的两件事

### 8.1 "我改配置时，尽量保住内存盘里的文件，减少复制"

**这条已经在代码里，而且实现得是对的**，我复核后确认：

- `WinFspHostedService.ReloadFastPath`（`:203-234`）：当**页大小与容量都没变**时（覆盖了绝大多数常见编辑：改盘符、卷标、开关内核缓存、改 `InitialDirectories`），**直接复用原来的文件树和内存页池，只换适配器和挂载句柄 → 零数据拷贝**。这就是你要的"少复制"。
- 只有容量或页大小变了，才走 `ReloadFullPathAsync`：快照 → 建新会话 → 还原 → 切换。有拷贝，但**文件保住了**，而且每一步都"失败即中止、旧卷不动"。
- **最坏情况是安全的**：新配置非法、或快照装不进新容量 → 中止重载，**正在运行的卷完全不受影响**。

**你需要注意的两个真实边界**（不是缺陷，是设计事实）：

1. **把容量改小到装不下现有文件时，重载会中止**（旧卷保持不变）。这不是 bug，是保护。想改小容量，得先删掉一些文件。
2. **快速重挂载会重建内核卷实例 → 内核文件数据缓存被清空，需要重新预热**。也就是说：改一次配置后，短时间内的读性能会掉一档（这个产品靠内核缓存把读吞吐从 ~3GB/s 提到 ~9.5GB/s）。**这一点现在的文档完全没写**（§5.4），建议补上，否则你会觉得"改个卷标怎么突然变慢了"。

### 8.2 以后你自己怎么安全地测

```powershell
# 1) 想改内存盘配置 → 只改这个文件（服务监控的是它）
notepad "C:\Program Files\RamDrive\appsettings.jsonc"

# 2) 想手动验证挂载状态
Get-Volume -DriveLetter R            # 修复后应能看到 R:
Get-EventLog -LogName Application -Source RamDrive -Newest 20 | Select TimeGenerated, Message

# 3) 万一又出现"要运行两次"的警告，先确认驱动有没有重载过；重载是这句：
sc.exe stop winfsp ; sc.exe start winfsp      # 需要管理员；会短暂断开所有 WinFsp 卷
```

**别做的事**：
- 别在源码目录里跑 `dotnet run --project src/RamDrive.Cli`（它会尝试挂 `R:`，与正在运行的服务冲突）。
- 别把源码里的 `appsettings.jsonc` 当成服务的配置去改（它不生效）。
- 本地跑集成测试要在**正常启动的、最好是管理员**的命令行里跑（§3.2）。

---

## 九、附录

### 9.1 本次实测命令（可复现）

```bash
# 环境修复包装（沙箱缺 PROGRAMFILES → NuGet 崩）
C:\rd-probe\dn.sh = env APPDATA/LOCALAPPDATA/PROGRAMFILES/PROGRAMFILES(X86)/ProgramData/USERPROFILE ... dotnet "$@"

# 构建（直接在 R 盘上，产物约 75MB；增量约 5s，全量约 18s）
bash /c/rd-probe/dn.sh build RamDrive.slnx -c Debug
# → 0 错误

# 单元测试
bash /c/rd-probe/dn.sh test tests/RamDrive.Core.Tests/RamDrive.Core.Tests.csproj -c Debug --no-build
# → 94/94 通过

# 集成测试（本会话环境受限，见 §3.2）
bash /c/rd-probe/dn.sh test tests/RamDrive.IntegrationTests/RamDrive.IntegrationTests.csproj -c Debug --no-build
# → 48 项：6 通过 / 42 失败（全部 ACCESS_DENIED，环境产物）
```

### 9.2 本次改动清单

```
 M CLAUDE.md                                            # 配置表 + 不变式说明
 M README.md                                            # 配置示例补两项
 M global.json                                          # 解锁 SDK 版本
 M setup/RamDrive.iss                                   # 修复1：写注册表提前
 M src/RamDrive.Cli/WinFspHostedService.cs              # 修复2：警告文案
 M src/RamDrive.Cli/appsettings.jsonc                   # 修复7：注释
 M src/RamDrive.Cli.Diag/appsettings.jsonc              # 修复7：注释
 M src/RamDrive.Core/Configuration/DirectoryNode.cs     # 修复3：保留名对齐
 M src/RamDrive.Core/Configuration/RamDriveOptions.cs   # 修复5：危险组合校验
 M src/RamDrive.Core/FileSystem/FileNode.cs             # 修复4：幂等卫兵
 M src/RamDrive.Core/FileSystem/RamFileSystem.cs        # 修复7：过时注释
 M tests/RamDrive.Core.Tests/DirectoryNodeValidationTests.cs    # +1 测试
 M tests/RamDrive.Core.Tests/RamDriveOptionsValidationTests.cs  # +3 测试
 M tests/RamDrive.IntegrationTests/InitialDirectoriesSdTests.cs # 修复5：开启通知
 M tests/RamDrive.IntegrationTests/RamDriveFixture.cs           # 修复5：开启通知
```

### 9.3 事实性修正汇总（前两轮 → 本轮）

| 前两轮的说法 | 本轮 |
|---|---|
| 安装器注册表视图不对称、写入落点可疑（v1 次因） | ❌ **证伪**：`HKLM+WOW6432Node` 与 `HKLM32` 同一物理键 |
| 建议程序内重载 WinFsp 驱动再重试（v1 方案 A） | ⚠️ **有风险**：会撤掉 `R:` 自己；改用"提前写注册表" |
| P0-1 是数据丢失级 P0 | ✅ 存在但**重新定性**：规格/文档/测试口径冲突 + 保护网未启用 |
| 集成夹具"钉 uint.MaxValue + 通知开启" | ❌ **错**：夹具没开通知（这才是真问题） |
| 生产默认参数组合无测试覆盖 | ⚠️ **不全对**：`LevelDbDefaultTimeoutTests` 已覆盖 |
| `FileNode.Dispose` 重复递归 O(树大小) | ❌ **证伪**：`Children.Clear()` 已使其 O(1) |
| "至今没有 Roslyn 分析器" | ❌ **证伪**：分析器已开（16–18 条警告）；对的是没有 `TreatWarningsAsErrors` |

---

*本报告基于 2026-09-10 本机实测：.NET SDK 10.0.401 + WinFsp 2.x + 运行中的 RamDrive 服务（PID 2688）。构建与单元测试为真机结果；集成测试受会话环境限制未能有效执行（原因与证据见 §3.2）；安装器改动未能真机验证（无 Inno Setup + 需管理员）。所有"未验证"项均已明确标注。*

---

## 十、第二批修复（按"你自己看着办"执行）

用户 2026-09-10 21:00 明确：**§七 里的建议由我自行判断并实施**（不是只做我请他拍板的那一条）。以下是第二批，全部通过构建（0 错误）与单元测试（**105/105**，其中本批新增 11 条）。

### ✅ 已做

| # | 事项 | 文件 | 关键点 |
|---|---|---|---|
| 1 | **挂载点格式校验** | `RamDriveOptions.cs`、`WinFspHostedService.cs` | 只接受 `R:` / `R:\`。**刻意拒绝 `\\.\R:`** —— 宿主会自己再前缀一次 `\\.\`，变成 `\\.\\.\R:` 反而必崩。启动前先 `Validate()` 并打印可读错误，不再让畸形值进原生调用。新增 11 条测试（4 合法 + 7 非法） |
| 2 | **`SetFileAttributes(0)` 语义对齐** | `WinFspRamAdapter.cs` | 参考实现（LOCKSTEP oracle）会**应用** `0`（清空属性），生产实现原来跳过 → 差分测试本应抓到的漂移 |
| 3 | **`ListDirectory` 排序移出全局结构锁** | `RamFileSystem.cs` | 锁内只做快照拷贝，排序在锁外。大目录分页枚举不再反复持锁排序（原为每页 O(n log n)）。返回仍是同一时刻的一致快照，语义不变 |
| 4 | **`src/` 开启"警告即错误"** | 新增 `src/Directory.Build.props` | 实测 `src/` 本就零警告（16 条警告全在 `tests/`）。豁免 IL2026/IL2104/IL3050/IL3053 以免 AOT 发布误伤 |
| 5 | **`.editorconfig`** | 新增 | 只放格式化规则，不提升 IDE 规则严重度（否则会和 #4 联动把构建打红） |
| 6 | **README：集成测试跑法 + 配置真实位置** | `README.md` | 写明"要在正常启动（建议管理员）的控制台跑，否则会出现约 42 个假失败"；写明服务读的是**自己目录下**那份 `appsettings.jsonc` |
| 7 | **文档小修** | `README.md`、`repro_chrome.js` | TLA+ 域名拼写 `azurewebsted`→`azurewebsites`；匿化开发者本机路径，改为读 `CHROME_EXE` / `LOCALAPPDATA` |
| 8 | **安装包默认版本对齐** | `setup/RamDrive.iss` | `MyAppVersion` 由 `0.0.0-dev` 对齐到 `Directory.Build.props` 的 `1.0.0-dev` |

### ⏸ 未做（及原因，等你一句话就能做）

| 事项 | 为什么先不做 |
|---|---|
| 目录枚举比较器统一到 `MemfsFileNameComparer`（§七#6） | 需要**非 ASCII 文件名 + marker 分页**的集成测试来验证顺序与去重，本环境跑不了集成测试。改错会让中文名目录枚举出现重复/遗漏 |
| `AllocationSize` 改为保守取值（§七#8） | 会改变对外上报的元数据，可能影响依赖 `AllocationSize ≥ FileSize` 的应用，属"必须真机实测"的改动 |
| CI 触发策略（§七#3 剩余部分） | `.github/workflows/ci.yml` 的注释说明自动触发是**刻意关掉**的（fork 环境自身的 push 会误触发）。擅自打开只会给你制造噪音。SDK 解析那条已由 `global.json` 修好。**建议**改成"打 `release-*` tag 时必跑"——既不误触发、又保留门禁，但这是发布流程策略，我等你确认再动 |
| 版本 / WinFsp 版本的"单一来源"（§七#11） | 真正的单一来源需要 CI 从 `Directory.Build.props` 读取再传给 ISCC；`.iss` 默认值已对齐，CI 那半边属于发布流程改动 |

---

## 十一、关于"重新出一个安装包"

### 11.1 本机编不出来（实测结论）

| 必需组件 | 状态 |
|---|---|
| Inno Setup（`ISCC.exe`） | ❌ 未安装 |
| Visual Studio / MSVC C++ 工具链（AOT 链接器） | ❌ `vswhere` 返回空 |
| Chocolatey | ❌ 未安装 |

我还真的试了一次原生 AOT 发布，结果是：

```
error : Cross-OS native compilation is not supported.
```

——SDK 误判了宿主操作系统（和本次会话里 NuGet/path1、集成测试那一族问题同源）。**所以这个环境既编不出 setup.exe，也编不出 AOT 的 exe。**

### 11.2 可行路径

**路线 A（推荐）：用你仓库的 CI 出包。**
远程仓库是 `https://github.com/wjyjwy/RamDrive`。在 GitHub 的 `Actions → CI → Run workflow` 手动触发，它会产出两个产物：

- `RamDrive-x64-installer` → `RamDrive-…-setup.exe`
- `RamDrive-x64-portable` → `RamDrive.exe` + `appsettings.jsonc`

CI 跑在 `windows-latest`，MSVC 与 Inno Setup 都有，正是为这个准备的。触发链路本身已经修好（`global.json`）。

**路线 B（最省事 + 不动安装器）：只替换两个文件。**
用 CI 产出的 `RamDrive.exe` 和 `appsettings.jsonc` 覆盖 `C:\Program Files\RamDrive\` 下的同名文件。

## 十二、WorkBuddy 两个设置的选择（实测结论）

### 12.1 "在 Windows 上提供 Git 和 Bash Shell 的类 Unix 命令行环境" —— **保持开启**

这是我的 `bash` 工具赖以工作的基础。关掉它，我只能退回 PowerShell——而**实测这个环境里的 PowerShell 工具无法调用外部程序**（`Get-Command dotnet` 返回空，用绝对路径调也毫无输出），等于把"编译 + 跑测试"的能力整个砍掉。**这也是本次诊断能成立的前提。**

> 顺带：你记不清当初为什么关它不是没道理——这一项在个别场景下会被认为"多开一个 shell 环境不安全"。但在本项目的场景里，它是纯收益。

### 12.2 "关闭沙箱" —— 实测对比（同一个会话、同样的命令）

| 观测点 | 沙箱**开启** | 沙箱**关闭** |
|---|---|---|
| `PROGRAMFILES` / `APPDATA` / `LOCALAPPDATA` / `ProgramData` | ❌ 全部为空 | ✅ 全部齐全 |
| `dotnet` 裸跑（NuGet 解析） | ❌ `Value cannot be null (Parameter 'path1')` | ✅ 正常 |
| `sc.exe` / `reg.exe` | ❌ 被安全策略拦 | ✅ 可用 |
| `cmd /c` | ❌ 被拦 | ❌ **仍被拦**（属独立的"防绕过"规则，与沙箱开关无关） |
| **集成测试** | ❌ 6/48（42 个 `ACCESS_DENIED` 假失败） | ✅ **48/48 全绿** |
| 需要包装脚本补环境变量吗 | 需要 | **不需要** |

**结论**：
- **沙箱就是那 42 个假失败的确切根因**，同时也是"环境变量消失 → NuGet 报错"的根因。关掉之后两者一起消失。
- 因此：**要么保持沙箱关闭**（本地能跑通全部测试），**要么接受"沙箱开着时集成测试会出约 42 个红色假失败"**，靠 CI 或自己的控制台来跑测试。两条路都行，取决于你更在意"本地能一键验证"还是"环境更受限"。
- 我已经把"沙箱开着时的假失败"写进了 `README.md` 的测试章节，免得以后你或别人再被它迷惑。

#### 我先前的一个错误更正
我在诊断中途说过"关掉沙箱也没用"——那指的是我用**单条命令的临时绕过开关**（它只放开"命令校验"，不动子进程层）。**只有设置里的全局沙箱开关**才真正解决问题。以本节实测为准。

---

## 十三、⚠️ 更新程序前必须备份（原因很硬）

**RamDrive 的数据全在内存里。停掉服务（或重启机器）= R 盘内容全部消失。** 这不是 bug，是内存盘的固有性质（README 也写明了"the volume lives entirely in RAM"）。

（本节由原 §11.3 独立而成，因为"更新前先备份"对所有更新路径都适用。）

所以要更新 `RamDrive.exe`，就**绕不开**"停服务"，也就**绕不开**"R 盘被清空"：

> **你现在的这个工作区 `R:\-\RamDrive` 本身就在内存盘上。停服务/重装会把它一起抹掉。**

动手前请把至少这些拷到硬盘（C:/D:）：
1. **整个 `R:\-\RamDrive` 项目文件夹**（源码 + 你的诊断文档）；
2. R 盘上任何你要留的其它数据。

你原计划里已经写了"先保存有用的东西"，完全正确——只是要特别记住**项目文件夹自己也在 R 盘上**。

---

## 十四、23:00 追加：僵尸盘事故、更正与最终决定

### 14.1 僵尸 `Z:` 事故的真实严重度（比我写的严重）
我先前把那个僵尸挂载描述成"访问它才会卡"。用户实测反馈更严重：

- **重启 RamDrive 服务完全无效**；
- **重启系统时卡在转圈**，最后只能**强制重启**；
- 强制重启后一切正常。

→ **僵尸 WinFsp 卷会连系统重启都拖住。** 因此差分测试腿（`RAMDRIVE_DIFF=1`）被列为**硬禁令**：除非能接受"可能要强制重启"，否则不许在本机跑。已写入项目长期记忆。

### 14.2 更正：沙箱还限制了 PowerShell 工具（我之前的说法错了）
我先前断言"这个环境里 PowerShell 工具调不动外部程序"，并据此一直只用 bash。**关掉沙箱后实测完全正常**：

```
Get-Command dotnet => True      dotnet --version => 10.0.401
whoami             => ike-pc\ike       fsutil          => 驱动器: C:\ R:\
```

→ 那条限制同样是**沙箱**造成的。更正后的三条沙箱影响：① 剥环境变量（NuGet 报错）；② 拦截新挂载卷上的创建（42 个假失败）；③ 让 PowerShell 工具无法调用外部程序。

### 14.3 重启 + 网盘往返后的完整性核对（通过）
用户重启前把工作区备份到网盘（部分文件报"被占用/传输失败"），恢复到 `R:\-\RamDrive` 后核对：

- `git status` 与重启前**完全一致**（17 个 `M` + 5 个 `??`）；
- `.workbuddy/`（memory / bin / logs / publish）完好，含 37MB 的 `RamDrive.exe` 与配套 `appsettings.jsonc`；
- **不补任何环境变量**直接 `dotnet build` → 0 错误；单元测试 **105/105 通过**；
- 盘符只剩 `C:` `R:`（僵尸已清）。

那些"被占用"的文件是**当时卡死的测试进程持有的句柄**，不是源码；上面的测试结果（含我新增的 14 条测试）也反证了改动全都还在。

### 14.4 最终决定（用户 23:08 明确）
| 事项 | 决定 |
|---|---|
| CI 触发方式 | **保持仅手动 `workflow_dispatch`，不添加任何自动触发**（用户明确要求）。SDK 解析那条已由 `global.json` 修好，无需再动工作流触发器。 |
| 目录枚举比较器统一到 `MemfsFileNameComparer` | **暂不做。** 它只影响"枚举顺序是否与 NTFS 一致"，而 marker 分页用的比较器与排序一致、本身自洽；真正能验证它的是差分腿（已禁用）。投入产出比低、验证路径缺失。 |
| `AllocationSize` 对齐参考实现 | **暂不做。** 更进一步：**这条建议对本项目其实存疑**——参考实现把整个文件实体化成字节数组，所以"向上取整到分配单元"合理；而本项目是**稀疏文件**（只为写过的页分配内存），把 `AllocationSize` 报成"按逻辑大小取整"反而是**虚报占用**，会破坏稀疏语义与容量口径。要做也只能做成 `max(实际分配, 0)`，那就等于现状。 |
| 出包 | 走 GitHub CI（见 §14.5）。 |

### 14.5 出包进展（23:10）
- 改动已**提交并推送**到 `origin/trae/agent-yYwbHt`：commit `76f3d97`（推送耗时 **11 秒**）。
  > 附带发现：git 里**并没有配置代理**（local/global 的 `http.proxy` 均为空），当前直连 GitHub 就很快（`github.com` 2.3 秒、`ls-remote` 7 秒）。所以那个"代理"要么没生效、要么是别的工具在代理。
- 已通过 API 触发 CI：`https://github.com/wjyjwy/RamDrive/actions/runs/34493913607`
- 预期产物：`RamDrive-x64-installer`（setup.exe）与 `RamDrive-x64-portable`（exe + 配置）。
- **观察点**：CI 里有一条差分测试步骤（`RAMDRIVE_DIFF=1`）。它在本机（**沙箱开着**时）挂死过并留下僵尸卷，但**不能断定是产品问题还是沙箱问题**——CI 是正常环境，这次运行正好能给出答案。若 CI 上它也挂，就需要修它或给它加超时保护。


