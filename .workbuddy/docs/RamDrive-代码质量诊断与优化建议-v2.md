# RamDrive 代码质量诊断与优化建议（第二轮 / 复核版）

**诊断对象**：`代码质量诊断与优化建议.zip`（RamDrive，基于 WinFsp 的 C#/.NET 10 内存文件系统）
**代码快照**：`31542ca`（"ci: 触发改为仅手动 workflow_dispatch"），含两个修复提交 `5dc25ec` + `070810e`
**诊断日期**：2026-09-10
**诊断方式**：全量源码静态审查 + 与上一轮报告（`RamDrive-0.4.7-代码质量诊断报告.md`，2026-09-04）的逐条回归比对

> **环境限制（重要，先说结论的可信边界）**
>
> 沙箱仅有 .NET 6.0.301 SDK，项目 `global.json` 锁定 `10.0.103`（`rollForward: latestPatch`），**无法编译、无法运行测试**。尝试联网安装 .NET 10 时发现沙箱网络对 `dotnet.microsoft.com` / `builds.dotnet.microsoft.com` / `nuget.org` / `github.com` 的出站 TLS 全部被拦截（`SSL_ERROR_SYSCALL`），国内镜像站可达但均不提供 .NET SDK 的 tar.gz 直链。
>
> 因此本报告是**静态审查结论**，不含真机实测。凡我确认的缺陷均给出了可复现的代码路径与推演序列；凡我**无法确认**的（需要运行时才能定性的）我都明确标注了。请勿把本文的 P0 当作"已复现"，它们是"已定位、待验证"。

---

## 一、复核结论（先行）

上一轮报告列出的 **3 个 P0 / 5 个 P1 中，5 个已修复且修复质量良好，1 个部分修复，2 个未修**。同时修复过程本身**引入了一个 P0 级新问题**（重挂载路径与通知机制的交互）。

| 上轮编号 | 问题 | 本轮状态 | 证据 |
|---|---|---|---|
| P0-1 | `Rent()` 无预留检查 → 承诺超卖、`FreeBytes` 为负 | ✅ **已修复** | `PagePool` 引入单一 `_committedCount` 闸门；`Reserve`/`Rent`/`RentBatch`/`Return`/`ReturnBatch`/`Unreserve` 全部走它 |
| P0-2 | `OverwriteFile` 先截断后检查容量 | ✅ **已修复** | `WinFspRamAdapter.cs:334` 容量检查已前移到 `SetLength(0)`（`:341`）之前 |
| P0-3 | `Move` 可移动进自身子树（大小写绕过） | ✅ **已修复** | `RamFileSystem.cs:140-144` 沿 `Parent` 链上溯；参考实现 `MemfsReferenceFs:399-403` 同步 |
| ~~P0-4~~ | 删除/替换与打开句柄共存 | ➖ 维持降级 | 上轮已实测未复现，本轮无新增证据 |
| P1-1 | `Move` 允许文件替换空目录 | ✅ **已修复** | `RamFileSystem.cs:159-164` 对**任何**目录目标返回 false |
| P1-2 | `SetFileSize(alloc)` 不截断逻辑大小 | ✅ **已修复** | `WinFspRamAdapter.cs:497-503` 补上 `SetLength(newSize)` |
| P1-3 | 预留回收失败导致 `_reservedCount` 为负 | ✅ **已修复** | `PagedFileContent.cs:160-161` 仅在 `Reserve` 成功时才加回文件级计数 |
| P1-4 | `SetFileSecurity` 无畸形 SD 防护 | ✅ **已修复** | `WinFspRamAdapter.cs:546-556` try/catch → `STATUS_INVALID_SECURITY_DESCRIPTOR`；并新增 `ExceptionHandler`（`:725-729`） |
| P1-5 | `Cleanup(Delete)` 无视删除结果就发通知 | ✅ **已修复** | `WinFspRamAdapter.cs:641-646` 改为 `if (_fs.Delete(...))` 门控 |
| **NEW P0** | **快速重挂载复用旧树，但通知机制默认关闭 → 内核缓存静默失效** | ❌ **新发现，未修复** | 见 §三 P0-1 |

**修复质量评价**：这批修复不是"打补丁"，而是找到了正确的抽象——把 `rented + reserved` 合并成单一原子 `committed` 是教科书式的一体化重构，还顺带配了 4 个回归测试（`PagePoolReservationRegressionTests`，含 500 次混合操作的不变式断言）和 Debug 断言。`Move` 的祖先检查、参考实现的 LOCKSTEP 同步、`OverwriteFile` 的顺序调整也都做对了。

**但修复引入了新问题**：`070810e` 新增的"快速重挂载路径"（复用旧树 + 旧页池，只换适配器）设计精巧，却与 `EnableNotifications` 默认为 `false` 这一事实**叠加**成为系统性风险——详见 P0-1。

**综合评分（我的判断）**：

| 维度 | 上轮 | 本轮 | 变化依据 |
|---|---|---|---|
| 功能正确性 | 6.5 | **8.0** | 3 个 P0 全部修掉；但新增 1 个重挂载缓存风险 |
| 并发设计 | 7.5 | **9.0** | `committed` 单闸门是本质修复；但空闲栈页仍不计入 `allocated` 记账 |
| 安全性 | 7 | **8.0** | 畸形 SD 防护 + ExceptionHandler 已补；根 DACL 仍硬编码 |
| 性能设计 | 8.5 | **8.0** | 通知默认关闭使 `Notify` 调用点成为**死代码**，与文档宣称矛盾 |
| 可维护性 | 8.5 | **8.5** | 注释质量依然极高；但文档与代码出现 3 处实质性漂移 |
| 测试 | 8 | **8.5** | 新增 8 个精准回归测试；`PreAllocate` 场景已覆盖 |
| 构建与发布 | 8 | **9.0** | release.yml 已补齐集成测试 + 差分腿，修复了上轮的"门禁倒挂" |
| **综合** | **7.5** | **8.7** | 已达"可放心用于生产"的临界点，差 P0-1 与文档同步 |

---

## 二、修复验证细节（逐条）

### ✅ P0-1 预留超卖 — 修复正确且完整

核心是 `PagePool` 现在只有一个容量真相来源：

```csharp
private long _committedCount; // rented + reserved — the single capacity gate

public long FreeBytes => (_maxPages - CommittedCount) * _pageSize;   // 恒非负
```

所有 6 条消耗/归还容量的路径都纳入了同一个原子域：

| 方法 | 对 `_committedCount` 的操作 | 位置 |
|---|---|---|
| `Reserve` | CAS `+count` | `:95-107` |
| `Unreserve` | `-count` + Debug 断言非负 | `:113-119` |
| `Rent` | `TryClaimCommitted(1)`；分配失败回滚 `-1` | `:128, :144` |
| `RentBatch` | 先 CAS 认领 `claim`，再退还 `shortfall` | `:161-184` |
| `Return` | `-1` | `:198` |
| `ReturnBatch` | `-count` | `:218` |

回归测试覆盖了三种上轮实测的触发形态（空闲栈非空 + 全量预留、`PreAllocate=true` + 全量预留、`RentBatch` 部分授予），并有一个 500 次随机混合操作的不变式循环断言 `RentedCount + ReservedCount == CommittedCount ≤ maxPages`。**这组测试设计得当，能挡住回归。**

> **遗留观察（非缺陷）**：`AllocateNewPageIfUnderCapacity` 仍用 `_allocatedCount + _reservedCount >= _maxPages` 作为物理分配闸门。由于 `_reservedCount` 与 `_committedCount` 现在是两个独立的原子变量，二者之间存在瞬时窗口——一个页可能既被 `Reserve` 计入 `_reservedCount`（阻止物理分配），又在 `Unreserve` 后仍被 `committed` 认领。这不会造成容量超卖（`committed` 是唯一闸门），但会让**物理分配比预期保守**：极端时序下可能出现"`FreeBytes > 0` 但新页分配失败"的场景。上轮 P3 已提及"每竞争者最多多分配 1 页"，现应补充反向的一页保守。建议把 `_allocatedCount` 的闸门也改为对 `committed` 的单调比较（`allocated >= committed` 时才允许物理分配），彻底消除双计数器口径差。

### ✅ P0-2 / P1-1 / P1-2 / P1-5 — 修复正确

四处都是小改动、大收益，且每个都配了针对性测试（`WinFspRamAdapterFailurePathTests`、`RamFileSystemMoveTests`）。

`OverwriteFile` 的修复值得单独表扬——它把容量检查放到了正确的位置，并且**语义选择是自洽的**：

```csharp
// 检查在前
if (allocationSize > 0 && (long)allocationSize > _fs.FreeBytes)
    return V(FsResult.Error(NtStatus.DiskFull));
long sizeBefore = node.Content.Length;
node.Content.SetLength(0);   // 只有确认放得下才截断
```

> **一个可以再推敲的语义问题**：`allocationSize` 在 WinFsp 契约里是**提示**（hint），不是承诺。当前实现把"提示放不下"升级为**硬失败**。对"复制大文件到接近满的盘"这一场景，NTFS 的行为是"先截断成功、写阶段才报满"，返回值是 `STATUS_DISK_FULL` 但原数据已按 NTFS 语义丢失。现在的实现比 NTFS **更安全**（原数据保住），但会在某些应用里表现为"覆盖写被提前拒绝而 NTFS 会允许"。这是自觉的取舍，建议在 `docs/` 里显式记录该偏差，避免未来被差分测试当成 bug 修掉。

### ✅ P1-3 — 修复正确，但暴露了更深的设计问题

```csharp
// PagedFileContent.cs:160-161
if (unreservedForAlloc > 0 && _pool.Reserve(unreservedForAlloc))
    Interlocked.Add(ref _reservedPages, unreservedForAlloc);
```

只在池子确实接受时才加回文件级计数，堵住了"向池子 `Unreserve` 从未预留过的额度"的路径。**修复是对的。**

但它同时也说明：**文件级 `_reservedPages` 与池级 `_reservedCount` 的同步是靠"尽力而为"维持的**，没有不变式守护。写入失败后文件处于"预留丢失但 `_length` 未变"的中间态，注释里承认了这点（"a later SetLength re-reserves"）。建议加一条 Debug 断言：写入返回 -1 后 `_reservedPages` 与 `_length` 推算的应有预留量一致，或至少记录一条 Warning 日志作为不变式被打破的信号（与 `FallbackSdFor` 的一次性告警模式保持一致）。

### ✅ P1-4 — 修复正确，且顺带补了 `ExceptionHandler`

这一条修得比上轮建议的更完整：不仅 `SetFileSecurity` 加了防护，还在 `WinFspRamAdapter` 里实现了 `ExceptionHandler`（`:725-729`），并新增了 `STATUS_UNSUCCESSFUL` 常量。这是上轮报告明确要求但当时未做的部分。✅

---

## 三、本轮问题清单（按严重度）

> 严重度定义：**P0** 数据丢失 / 结构损坏 / 记账错误；**P1** 与 NTFS 语义或自有 oracle 分歧、可导致应用错误；**P2** 健壮性/边界/卫生；**P3** 风格与长期维护。

### 🔴 P0-1（新发现）快速重挂载路径 + 通知默认关闭 = 内核缓存静默失配

**位置**：`src/RamDrive.Cli/WinFspHostedService.cs:203-234`（`ReloadFastPath`）× `src/RamDrive.Core/Configuration/RamDriveOptions.cs:52`（`EnableNotifications` 默认 `false`）× `src/RamDrive.Core/FileSystem/WinFspRamAdapter.cs:755-757`（`Notify` 的早退）

**事实链**：

1. `ReloadFastPath` 在**页布局未变**（`PageSizeKb`、`CapacityMb` 相同）时被选中——这覆盖了 README 宣传的绝大多数常见编辑（`MountPoint`、`VolumeLabel`、`EnableKernelCache`、`FileInfoTimeoutMs`、`InitialDirectories`）。
2. 该路径的做法是：**保留旧 `RamFileSystem` 与旧 `PagePool`**，只重建 `WinFspRamAdapter` 和一个新的 `FileSystemHost`，然后 `old.Host.Dispose()` → `MountSession(pending)`。
3. 重建适配器意味着**所有 `FileNode` 对象被原样保留**——包括它们的 `SecurityDescriptor`、`Content`、`IndexNumber`。
4. 而 `Notify()` 的第一行就是 `if (!_options.EnableNotifications) return;`，**生产默认值为 `false`**。
5. 因此：`README.md` / `CLAUDE.md` / `WinFspRamAdapter` 类注释中反复描述、并配有完整"通知矩阵"表格的 **`FspFileSystemNotify` 机制，在默认配置下从不执行**。文档描述的是（默认关闭的）可选机制，但措辞让它读起来像保障措施。

**为什么叠加后变成 P0**：重挂载会创建一个**全新的 `FileSystemHost`**。旧 host 的 `Dispose()` 会让内核丢弃该卷的全部 `FileInfo` 缓存与 `Cc` 缓存——这一步本身是对的。问题在于**重建后适配器的"通知矩阵"承诺仍然是空的**：一旦新 host 挂载完成，后续所有 `Create` / `Move` / `Cleanup(Delete)` / `SetFileSize` 仍不发通知。而 `FileInfoTimeoutMs` 的**生产默认是 1000ms**，不是集成测试夹具里钉住的 `uint.MaxValue`。

**具体失效场景**：集成测试用 `FileInfoTimeoutMs = uint.MaxValue`（永久缓存）+ 通知开启的假设来"抓回归"，**但生产默认是 1000ms + 通知关闭**。也就是说：**CI 的门禁强度与生产的实际配置之间存在参数错配**。在 1000ms 窗口内：
- `Create` 后立刻 `Open`（leveldb / SQLite / Chromium 的探测-再创建-再打开三段式）可能命中**负缓存**；
- `Move` 后立刻 `Read` 新路径可能读到 0 字节——这正是 `fix-leveldb-cache-coherency` 修复过的 bug；
- `Delete` 后立刻 `Create` 同名文件可能拿到旧 `FileNode` 的陈旧元数据。

挂载点变更这类"无害"编辑，会**清空内核缓存然后重新进入一个不主动失效缓存的状态**（依赖 1000ms 超时兜底 + 回调返回值）。这是正确的兜底，但它把上轮报告肯定的"通知矩阵是防线"降级为"文档承诺的防线实际不存在"。

**文档矛盾的具体出处（原文摘录）**：

`CLAUDE.md:271` 在配置表里这样描述 `FileInfoTimeoutMs`：

> "The adapter **invalidates the cache explicitly via `FspFileSystemNotify` on every path-mutating callback** (see `WinFspRamAdapter.cs` notification matrix); this timeout is defence in depth."

这句话把 `FspFileSystemNotify` 描述为**一个始终在生效的机制**，而 `FileInfoTimeoutMs` 只是"额外保险"。但代码事实是：`Notify()` 的第一行在 `EnableNotifications=false`（默认）时直接 `return`，**通知从不发出**，`FileInfoTimeoutMs` 才是唯一的防线。措辞与实现**完全相反**。

`WinFspRamAdapter.cs` 的类注释（`:21-28`）和其中的"缓存失效矩阵"表格（`:29-39`）也存在同样问题：表格列出的 8 条失效规则，在默认配置下**一条都不会执行**。而 `RamDriveOptions.cs:52` 的注释（"Default `false`. ... Notifications are redundant in that case"）是**唯一准确的描述**——三处文档中只有一处说了实话。

`README.md:75-77` 的 "Reload on config change" 段落则完全**没有提及**本轮新增的快速重挂载路径，仍在描述"capture → snapshot → restore"的旧流程，读者无法得知"编辑配置会走一条不拷贝数据但有其他副作用（见 P1-3）的路径"。

**为什么这是本轮最该先修的项**：它是**文档-代码-测试三者口径不一致**的典型，而这种不一致在文件系统里最容易演变成"只在用户机器上偶发"的幽灵 bug。

**修复建议（三选一，建议 B）**：

- **方案 A（最小改动）**：把 `EnableNotifications` 默认改为 `true`。代价是每次元数据变更多一次内核 IOCTL（已用线程池异步派发，见 `:760` 的 `UnsafeQueueUserWorkItem`，对热路径影响可控）。这最符合文档现状。
- **方案 B（推荐）**：把文档改回真实情况——在 `RamDriveOptions.EnableNotifications` 与 `WinFspRamAdapter` 类注释里明确写："**默认关闭；缓存一致性依赖每个回调返回正确的 `FspFileInfo` + `FileInfoTimeoutMs` 兜底。通知矩阵是可选的加固手段，仅在排查缓存一致性回归时开启。**" 同时**把集成测试夹具的 `FileInfoTimeoutMs = uint.MaxValue` 改为跑两遍**（一遍 `uint.MaxValue` + 通知开启作为加固模式的回归门禁，一遍 `1000` + 通知关闭模拟生产默认），让 CI 真实覆盖生产参数组合。
- **方案 C**：仅在 `ReloadFastPath` 里强制 `EnableNotifications = true`（重挂载后临时加固）。治标，不推荐。

**无论选哪个，都必须做的**：把 `README.md` §Configuration 表格与 `CLAUDE.md` 里"通知矩阵"的措辞与实际默认值对齐。目前文档让读者以为通知在生效，这是一个会造成误判的文档缺陷。

---

### 🟠 P1-1 重挂载失败路径的"孤儿会话"与静默内存泄漏

**位置**：`WinFspHostedService.cs:203-234`

```csharp
private bool ReloadFastPath(Session old, RamDriveOptions opts)
{
    var newAdapter = new WinFspRamAdapter(old.Fs, Options.Create(opts), ...);
    var pending = new Session { Pool = old.Pool, Fs = old.Fs, Adapter = newAdapter, Options = opts };

    try { old.Host?.Dispose(); }        // ← 旧 host 已销毁
    catch (Exception ex) { _logger.LogWarning(...); }   // ← 异常被吞

    if (!MountSession(pending))
    {
        _logger.LogError("Fast reload failed — new mount did not succeed");
        return false;                    // ← 交给调用方走快照路径
    }
    ...
}
```

**问题**：`old.Host.Dispose()` 抛异常时只记 `LogWarning` 就继续。此时旧 host 可能**处于半销毁状态**（卷已从 Mount Manager 摘除但 `FileSystemHost` 对象未完全释放），而 `MountSession(pending)` 会尝试挂载**同一个盘符**。若旧挂载点尚未释放干净，`host.Mount(mountManagerPoint)` 会失败，于是返回 `false` → 调用方 `ReloadAsync` 继续走 `ReloadFullPathAsync(old, opts)`。

`ReloadFullPathAsync` 里：

```csharp
var snapshot = old.Fs.CreateSnapshot();     // 捕获状态
fresh = CreateSession(opts);                // 建新会话
var restoreError = fresh.Fs.RestoreSnapshot(snapshot);
...
try { old.Host?.Dispose(); old.Fs.Dispose(); old.Pool.Dispose(); }   // 再次 dispose 旧会话
```

**此处有两个隐患**：

1. `old.Fs` 与 `old.Pool` 在 `ReloadFastPath` 里已经被**部分**处置过（`old.Host.Dispose()` 可能连带影响了 `old.Fs` 的生命周期），现在再次 `Dispose()`。`RamFileSystem.Dispose()` 是 `lock + _root.Dispose()`（`:329-335`），`FileNode.Dispose` **没有幂等保护**——`PagedFileContent.Dispose` 有 `_disposed` 卫兵（`:389`），但 `FileNode.Dispose` 会**无条件递归**并重复调用 `Content.Dispose()`。重复调用是安全的（有卫兵），但**并发/重复的递归遍历是 O(树大小) 的浪费**。
2. 更严重的：`ReloadFastPath` 已经构造了 `pending` 会话并在 `MountSession` 里失败，但 `pending` **没有被 dispose**。它的 `Adapter` 持有 `old.Fs` 的引用，而 `old.Fs` 随后会在 `ReloadFullPathAsync` 被 dispose。这不会造成泄漏（适配器不持有非托管资源），但会让日志诊断混乱。

**影响**：低概率（需要 `Dispose` 抛异常 + 挂载失败同时发生），但属于"错误处理路径本身没有正确性论证"的类型。在一个以"每个失败路径都 fail-safe"为卖点的重载机制里，这条路径的论证是不完整的。

**修复建议**：
- `old.Host.Dispose()` 的异常应当**升级为中止重载**，而不是继续尝试复用已部分销毁的 session（因为此时"旧会话仍然完好"的前提已不成立）。
- `ReloadFastPath` 返回 `false` 时，显式把 `pending` 与 `newAdapter` 的生命周期讲清楚（写注释说明为什么不需要 dispose，或干脆 dispose 掉适配器引用）。
- `FileNode.Dispose` 加 `_disposed` 卫兵（与 `PagedFileContent` 一致），使重复/递归 dispose 幂等且 O(1) 短路。

---

### 🟠 P1-2 `FileInfoTimeoutMs` 的 `uint.MaxValue` 特殊值与 0 的语义在重载后不一致

**位置**：`WinFspRamAdapter.cs:119`、`RamDriveOptions.cs:28-36`

```csharp
host.FileInfoTimeout = _options.EnableKernelCache ? _options.FileInfoTimeoutMs : 0u;
```

`EnableKernelCache=false` 强制 `0`（无缓存）——这是上轮报告认可的"backout switch"。**但 `FileInfoTimeoutMs` 的 JSDoc 明确列出 `uint.MaxValue`（4294967295）= 缓存永久有效**，注释还说"集成测试夹具钉住这个值"。

**问题**：把"永久缓存"和"1 秒缓存"混在同一个 `uint` 配置项里，用魔法值区分语义，且**没有任何校验**。`FilePathTimeoutMs = 4294967295` 在生产环境中会让缓存**永不失效**，此时唯一的正确性保障就是"通知矩阵"——而通知默认关闭（P0-1）。**两个默认值叠加会得到一个理论上有正确性风险的配置组合**，虽然需要用户主动改配置才会触发，但缺少防护。

**修复建议**：在 `RamDriveOptions.Validate()` 里加交叉校验——
```csharp
if (EnableKernelCache && !EnableNotifications && FileInfoTimeoutMs == uint.MaxValue)
    errors.Add("FileInfoTimeoutMs=uint.MaxValue (永久缓存) 需要 EnableNotifications=true，" +
               "否则缓存永不失效且没有主动失效机制");
```
或者把 `FileInfoTimeoutMs` 的语义收紧为"仅接受 0 或 [100, 60000]"，把"永久"变成一个独立的布尔开关。

---

### 🟠 P1-3 `DisabledNotifications` 与 `ReloadFastPath` 的"内核缓存清空"是隐性行为

接 P0-1。`ReloadFastPath` 在重建 `FileSystemHost` 时，**内核会丢弃该卷所有缓存的 `FileInfo` 与 `Cc` 页面**（新 host = 新卷实例）。这意味着：

- 编辑一次 `appsettings.jsonc`（哪怕只改 `VolumeLabel`）会**清空整个内核文件数据缓存**，随后所有读操作回落到用户态 `ReadFile` 回调。
- 对于一个靠 `EnableKernelCache` 把读吞吐从 ~3 GB/s 提到 ~9.5 GB/s 的产品（README 原文），**一次配置编辑会造成一次可感知的性能跌落**，直到缓存重新预热。
- 更微妙的是：`ReloadFastPath` 的注释只宣称"零数据拷贝"，**没有提及"会清空内核缓存"**。这是一个"看起来无副作用、实际有性能副作用"的操作，属于文档盲区。

**修复建议**：在 `ReloadFastPath` 的方法注释和 README 的 "Reload on config change" 段落里加一句："快速重挂载会重建内核卷实例，因此内核文件数据缓存会被清空并需要预热；仅修改 `VolumeLabel` 等纯元数据项时会观察到一次短暂的读性能下降。"

---

### 🟡 P2 健壮性与边界（择要）

1. **空闲栈页不计入 `_allocatedCount` 的记账口径**（`PagePool.cs:31-36`）。类注释明确写了"free stack 上的页已经分配但未计入 committed"，这是**有意设计**且正确（预留需要用未来的物理页来兑现）。但 `_allocatedCount` 的语义因此在文档里是"总分配过的页"而非"当前持有的物理页"，`Dispose` 靠 `_allPages` 独立栈来释放（`:226-229`）。三套计数器（`_allocatedCount` / `_committedCount` / `_allPages`）口径不同，建议加一张注释表说明各自语义，否则未来维护者极易误用。

2. **`FileNode` 可变性仍然过宽**（`FileNode.cs:16-32`）：`Name`、`Attributes`、三个时间戳全是 `public set`；`Children` 字典**直接外露**给调用方。上轮 P3 已提，本轮仍未收窄。这直接是 P0-3 那类结构 bug 的温床——`Move` 里的祖先检查之所以需要手工写，本质上就是因为"树的边可以被任意代码改"。建议至少把 `Children` 改为 `internal` 只读视图，把 setter 收窄为 `internal`。

3. **`DirectoryNode.Validate` 与 `WindowsNameRules.IsValid` 的规则集已经漂移**（对照阅读）：
   - `WindowsNameRules` 的保留名判断会**先截断扩展名再比较**（`:32-33`，`CON.txt` 会被拒绝）；
   - `DirectoryNode.IsReservedName` **不截断扩展名**（`:92-96`），因此配置里写 `"CON.txt": {}` 会**通过校验**，然后在 `CreateDirectoriesRecursive` 里被 `fs.CreateDirectory` 用 `WindowsNameRules` 拒绝——静默少建一个目录。
   - 两者都注释说"keep the rule sets in sync"（`WindowsNameRules.cs:6-7`），但**靠注释同步等于没同步**。上轮 P3 提过"提取到共享常量类 + 加一致性单元测试"，本轮应作为 P2 处理。
   - 另：`DirectoryNode.Validate` 的深度检查在 `depth >= MaxDepth` 时 `return`，但**它返回前不校验当前层的名字**——超深配置会被报"过深"但内部的非法名不会被发现。可接受（先修深度），但值得记一笔。

4. **`ReadDirectory` 的排序比较器与 NTFS 不一致**（`RamFileSystem.cs:197`、`WinFspRamAdapter.cs:714`）。仍用 `StringComparer.OrdinalIgnoreCase`，而 NTFS 用 NLS 大写表。该项目**已经有** `MemfsFileNameComparer`（`src/RamDrive.Diagnostics.MemfsReference/MemfsFileNameComparer.cs`）实现了与参考实现一致的比较器，但生产路径没用它。非 ASCII 文件名（中文、扩展 Latin）的枚举顺序会与 NTFS 不同，`marker` 分页在极端命名下可能重复/遗漏条目。建议统一到 `MemfsFileNameComparer`，并补一个非 ASCII 目录枚举的差分测试。

5. **`ListDirectory` 在全局结构锁内做全量 `OrderBy`**（`RamFileSystem.cs:188-200`）。大目录（数万项）下单次枚举是 `O(n log n)` 且**阻塞所有结构操作**（`Create` / `Delete` / `Move` / `FindNode`）。`ReadDirectory` 还是分页的，意味着一次分页遍历会对同一目录反复排序。建议加"脏标记 + 缓存排序结果"，或至少把排序移到锁外（拿到快照后排序）。

6. **`RamFileSystem.FindNode` / `ListDirectory` 全部走全局锁**，而 `WinFspRamAdapter` 在多处直接调用它们（`:167`、`:258`、`:280`、`:309`、`:683`、`:697`）。`ReadDirectory` 每次调用都进一次全局锁；`CreateFile` 失败路径还会额外再 `FindNode` 一次（`:258`、`:280`）——即"创建失败时多一次全局锁往返"。热路径上这是不必要的争用。

7. **`MakeFileInfo` 的 `AllocationSize` 仍是"实际分配页 × 页大小"**（`WinFspRamAdapter.cs:776`）。上轮 P2 提过：`SetLength` 纯扩展后 `AllocationSize=0 < FileSize`。本项目自己判断稀疏文件合法因而不算缺陷，但**内核 `Cc` 与部分应用假设 `Allocation ≥ Size`**。`EnableKernelCache=true` 是默认开启的，建议实测一下是否存在应用因此异常；若要保守，取 `max(allocated, roundup(FileSize, pageSize))`。

8. **`SetFileAttributes(fileAttributes == 0)` 仍不清属性**（`WinFspRamAdapter.cs:462`）：

   ```csharp
   if (fileAttributes != unchecked((uint)(-1)) && fileAttributes != 0)
       node.Attributes = (FileAttributes)fileAttributes;
   ```

   参考实现 `MemfsReferenceFs` 的 `SetFileAttributes` 对 `0` 是**覆盖写**（与 `WinFsp` 契约一致：`0` 表示"设置为无属性"）。这是一个**可被差分测试抓到的语义漂移**，只是当前测试没构造这个用例。同理 `CreateFile` 里 `file.Attributes = (FileAttributes)fileAttributes | FileAttributes.Archive;`（`:295`）——上轮说"`OverwriteFile` 未像参考实现那样无条件 `| Archive`"，本轮看 `OverwriteFile`（`:343-346`）确实也没补 Archive。建议对齐并补差分用例。

9. **`Unmounted` 已清空 `_host`**（`:146`）——上轮 P2-7 已修。✅ 但注意：`ReloadFastPath` 重建适配器时，**旧适配器的 `_host` 仍指向已 dispose 的 host**（`old.Host.Dispose()` 不会触发 `Unmounted` 回调把 `_host` 置空，取决于 WinFsp.Native 的实现）。若旧适配器在 dispose 后仍被某个线程持有并调用 `Notify`，可能对已停止的分发器发 IOCTL。这一条在默认 `EnableNotifications=false` 时不会触发，但一旦采纳 P0-1 的方案 A（默认开启），它就变成真问题。**两条修复要一起做。**

10. **`FsTracer` 的 `[Conditional]` 用法正确**（`RamDrive.Core.csproj:59-64` 仅在 Debug 定义 `TRACE_FS`），生产零开销。✅ 继续保持。

11. **`RamDrive.Cli.Diag` 与主程序共用 `RamDriveOptions`**，但 `DiagHostedService`（`:13-23`）持有的 `IFileSystem` 是**单例注入**且在 `ExecuteAsync` 里直接 `new FileSystemHost(_fs)`。若该项目将来也需要重载，会重复踩 P0-1 的坑。建议在诊断宿主里显式注释"此处不支持重载，原因见 WinFspHostedService"。

### 🟡 P2 构建、发布与卫生

12. **`release.yml` 已补齐差分腿与集成测试** ✅（本轮 diff 确认），修掉了上轮"发布门禁弱于 PR 门禁"的倒挂。但新增的差分步骤 `--filter "FullyQualifiedName!~ChaosTests"` 只排除混沌测试，**保留了 TortureTests（约 45s）**——如果这是有意为之，建议在注释里写明预期时长，避免未来 CI 超时被误删。

13. **版本号漂移仍未收敛**：`Directory.Build.props` = `1.0.0-dev`，`RamDrive.iss` = `0.0.0-dev`，`Setup.bat` 下载的 WinFsp 版本与 `release.yml` 的版本需要人工保持同步（`CLAUDE.md:236` 已警告"Keep ... in sync"）。**靠注释同步=没同步**，建议把 WinFsp 版本提为单一源（如 `Directory.Build.props` 的一个属性），由 CI 和 iss 共同读取。

14. **仍无 `.editorconfig` / Roslyn 分析器 / `TreatWarningsAsErrors`**。对一个含 `unsafe`、`NativeMemory`、手写无锁协议的文件系统，这是最大的"可维护性杠杆"——一条 `EnableNETAnalyzers` + `AnalysisLevel latest-recommended` 就能免费抓到大量边界失误。上轮已提，本轮仍未做，建议排入下个迭代。

15. **`repro_chrome.js` 含开发者本机路径**（`C:\Users\HuYao\...`）——上轮 P2-11 已提，本轮仍在发行 zip 内。建议移入 `docs/` 并匿名化，或加 `.gitattributes` `export-ignore`。

### 🔵 P3 风格与长期维护（摘要）

- **`docs/` 与 `tla/` 的口径**：`README.md:81` 的 TLA+ 链接写的是 `lamport.azurewebsted.net`（拼写错误，应为 `azurewebsites.net`），且该域名早已迁移。属于小瑕疵但影响专业观感。
- **`RamFileSystem.cs:40` 的注释仍写 "Dokan convention"**——Dokan 早已不是该项目的挂载后端。上轮提过，仍在。
- **`ENABLE_NOTIFICATIONS` 的"矩阵"表格**（`WinFspRamAdapter.cs:29-39`）与真实默认值矛盾（见 P0-1），建议重写为"当 `EnableNotifications=true` 时的失效矩阵"。
- **`PagedFileContent` 的 `unsafe` 块内混用 `NativeMemory.Clear` 与 `Buffer.BlockCopy` 风格**，可统一。
- **`TortureTests` / `ChaosTests` 的数据校验只覆盖 SHA-256 与字节比对，不覆盖元数据**（时间戳、属性、`AllocationSize`）。这正是 P2-8 类语义漂移测不出来的原因。建议给混沌测试增加"元数据快照比对"（同一操作序列在参考实现上跑一遍，比对 `FspFileInfo`）。

---

## 四、修复路线图（建议顺序）

| 批次 | 内容 | 预估 |
|---|---|---|
| **1（立即）** | **P0-1**：在"改文档"与"改默认值"之间做决策并落地（推荐方案 B）；补 CI 双参数组合（`uint.MaxValue`+通知开 / `1000`+通知关）。同时修 P2-9 的旧适配器 `_host` 悬垂 | 半天 |
| **2（本迭代）** | **P1-1**：重载失败路径的正确性论证 + `FileNode.Dispose` 幂等卫兵；**P1-2**：`uint.MaxValue` 与 `EnableNotifications` 的交叉校验 | 1 天 |
| **3（本迭代）** | **P1-3**：文档化重挂载的清缓存副作用；**P2-3**：`DirectoryNode` / `WindowsNameRules` 规则集统一 + 一致性单测 | 1 天 |
| **4（下迭代）** | **P2-4/5/6**：比较器统一到 `MemfsFileNameComparer`、目录枚举排序缓存、失败路径减少全局锁往返 | 2–3 天 |
| **5（持续）** | **P2-8**：`SetFileAttributes(0)` / `OverwriteFile` 的 Archive 语义对齐 + 差分用例；**P2-14**：分析器 + `TreatWarningsAsErrors`；**P2-13**：版本源统一；**P3**：文档口径清理 | 持续 |

---

## 五、建议补充的回归测试（对应本轮缺口）

1. **重挂载后的缓存一致性**：`ReloadFastPath` 成功后，立即对既有文件做"改名 → 按新名读取"，断言读到完整数据（覆盖 P0-1 的通知缺失路径）。**注意**：这条测试要分别在 `EnableNotifications=true/false` 两种配置下各跑一次，才能暴露参数错配。
2. **重挂载 + `FileInfoTimeoutMs=1000`（生产默认）**：现有夹具钉的是 `uint.MaxValue`，应加一个用生产默认值的集成测试变体。
3. **`ReloadFastPath` 失败回退**：mock 一个 `MountSession` 返回 `false`，断言树与页池未被破坏、旧会话已被正确处置（覆盖 P1-1）。
4. **`FileNode.Dispose` 幂等**：对同一节点连续 `Dispose()` 两次，断言不抛、不重复递归（覆盖 P1-1）。
5. **非 ASCII 目录枚举顺序**：创建含中文/带重音字符的目录项，断言枚举顺序与 `MemfsReferenceFs` 一致（覆盖 P2-4）。
6. **`SetFileAttributes(0)`**：断言与参考实现一致地清空属性（覆盖 P2-8）。
7. **`DirectoryNode` 与 `WindowsNameRules` 规则一致性**：对一组名字，断言两处校验结果相同（覆盖 P2-3）。
8. **`uint.MaxValue` + `EnableNotifications=false` 的配置**应被 `Validate()` 拒绝（覆盖 P1-2）。

---

## 六、值得肯定的实践（本轮新增）

- **修复的抽象层级正确**：`committed` 单闸门不是"在 `Rent()` 里加个检查"，而是把容量记账收敛为单一真相来源——这是从根上消除这类 bug 的做法。
- **修复自带回归测试且测试设计得好**：`PagePoolReservationRegressionTests` 里的 500 次混合操作不变式循环，比单点断言强得多。
- **LOCKSTEP 约定被真正执行**：`Move` 的祖先检查在 `MemfsReferenceFs` 里同步实现了（`:399-403`），并用 `NtStatus.ObjectNameInvalid` 对齐了生产实现的返回。这是上轮报告点名要求的事，做到了。
- **`ExceptionHandler` 补齐**：上轮建议但当时未做，本轮补上了，且返回保守的 `STATUS_UNSUCCESSFUL`。
- **发布门禁倒挂已修**：`release.yml` 现在跑单元 + 集成 + 差分，与 PR 门禁对齐。
- **注释质量依然罕见地高**：每个修复都写清了"为什么"和"历史 bug 的因果链"，`PagedFileContent.Write` 的三阶段注释、`PagePool` 的记账不变式注释都是可长期维护的资产。

---

## 附：证据索引（本轮新增/变更条目）

| 结论 | 文件 | 行号 |
|---|---|---|
| `committed` 单闸门定义 | `src/RamDrive.Core/Memory/PagePool.cs` | 36, 44-56 |
| `Rent` 走 committed 闸门 | 同上 | 125-146 |
| `RentBatch` 部分授予 + 退还 shortfall | 同上 | 153-186 |
| 物理分配闸门仍用 `_reservedCount` | 同上 | 252-269 |
| P1-3 修复（仅成功时加回） | `src/RamDrive.Core/Memory/PagedFileContent.cs` | 158-162 |
| P0-2 修复（检查前移） | `src/RamDrive.Core/FileSystem/WinFspRamAdapter.cs` | 334-341 |
| P1-2 修复（alloc 缩小时截断） | 同上 | 497-503 |
| P1-4 修复（畸形 SD 防护） | 同上 | 546-556 |
| `ExceptionHandler` 补齐 | 同上 | 725-729 |
| P1-5 修复（Delete 返回值门控） | 同上 | 641-646 |
| **P0-1 通知早退** | 同上 | 755-757 |
| **P0-1 通知默认值** | `src/RamDrive.Core/Configuration/RamDriveOptions.cs` | 52 |
| **P0-1 通知配置文件值** | `src/RamDrive.Cli/appsettings.jsonc` | 44 |
| **P0-1 快速重挂载路径** | `src/RamDrive.Cli/WinFspHostedService.cs` | 203-234 |
| **P1-1 重载失败回退** | 同上 | 213-227, 268-289 |
| **P1-1 `FileNode.Dispose` 无卫兵** | `src/RamDrive.Core/FileSystem/FileNode.cs` | 83-92 |
| P0-3 修复（祖先上溯） | `src/RamDrive.Core/FileSystem/RamFileSystem.cs` | 140-144 |
| P1-1 修复（拒绝目录替换） | 同上 | 159-164 |
| P2-4 枚举排序比较器 | 同上 | 197 |
| P2-5 全局锁内 OrderBy | 同上 | 188-200 |
| P2-3 配置校验规则漂移 | `src/RamDrive.Core/Configuration/DirectoryNode.cs` | 47-77, 90-97 |
| P2-3 名称规则（含扩展名截断） | `src/RamDrive.Core/FileSystem/WindowsNameRules.cs` | 29-37 |
| P2-8 `SetFileAttributes(0)` 分歧 | `src/RamDrive.Core/FileSystem/WinFspRamAdapter.cs` | 462-463 |
| 参考实现 truncate 语义 | `src/RamDrive.Diagnostics.MemfsReference/MemfsReferenceFs.cs` | 335-360 |
| 参考实现目录替换拒绝 | 同上 | 397-403 |
| 参考实现 RefCount | 同上 | 690 |
| 差分测试夹具（钉 `uint.MaxValue`） | `tests/RamDrive.IntegrationTests/RamDriveFixture.cs` | 44 |

*本报告为第二轮复核，基于提交 `31542ca` 的静态审查；沙箱无 .NET 10 且外网被拦截，**未能编译与运行测试**。凡标注"新发现"的条目均为代码阅读定位 + 逻辑推演，未做真机验证；建议在有 .NET 10 环境的机器上按 §五 的回归测试逐条确认后再动手修。*
