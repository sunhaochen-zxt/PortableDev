# PortableDev v2 规范：进程模型与 `proc` 扩展

> 状态：**基本实现（2026-11-25）**。已落地：进程原语（Job/spawn/kill/detach）、`proc` 扩展协议、
> `proc.spawn/snapshot/kill`、stdio 代理、`pd proc list|info|kill|system`、主题事件（`proc.started/exited`）、
> 系统级枚举与结束（未提权 best-effort + admin 模式 一次性实例），e2e 覆盖 `tools/proc-e2e.py`（28 项）。
> 期 2 的 `pd run`/`pd shell` 已落地（exec 通道，见 spec 4.10）。未做：`--elevated` 的 UAC 自动化测试、
> 受保护进程（PPL）结束、`commandLine` 字段。
> 相关文档：[architecture.md](architecture.md) 3.9 / 18、[spec.md](spec.md) 4.6 / 6.2、
> [commands.md](commands.md) 4.10 / 5.18、[impl-pkgmgr.md](impl-pkgmgr.md)。

---

## 1. 这个模型解决什么

三件事，**不是**"做一个任务管理器"：

1. **可观测**：我们拉起的进程（扩展、包脚本、`pd run`、安装器）在跑什么、为哪个任务跑、跑了多久；
2. **可控**：按任务/树精确结束，不漏孤儿；内核停止时不留残余；
3. **可枚举**：需要时能看整台机器的进程（显式提权），供诊断和将来的界面使用。

## 2. 三类进程

| 类别 | 定义 | 做不做 |
|---|---|---|
| **受管** | 我们拉起并持有树边界的进程（扩展、脚本、run、helper） | ✅ |
| **记账** | 我们拉起但不进树（detached、elevated） | ✅ 只记日志 |
| **外部** | 机器上其它进程 | ✅ 可读枚举；结束需显式提权 |

## 3. 归属

| 角色 | 负责 | 不负责 |
|---|---|---|
| 内核 | 扩展的拉起与监督；`pd kernel ps` 等内核自己的管理命令 | 业务进程、stdio、人类可读文本 |
| `proc` 扩展 | Job Object、限额、超时、整树终止、stdio 代理、脱离、系统级枚举/结束；`pd proc` 命令 | 包脚本策略、机器接线、文案 |
| `pkgmgr` | 包脚本策略（白名单、请求/回执协议） | 进程机制 |
| 壳 `pd.exe` | `pd run` / `pd shell` 拉起（stdio 原生）、**全部人类可读文本与确认交互** | 业务规则 |
| `storage` | 机器接线 + 接线提权 + 账本 | 进程 |

## 4. 受管记录

记录只在内存（不落盘、不进账本——账本管机器改动，不管进程启停）：

| 字段 | 说明 |
|---|---|
| `procId` | 内核/扩展生成，不重复 |
| `kind` | `extension` / `script` / `run` / `helper` / `detached` / `elevated` |
| `pid` + `startTime` | **身份**：所有结束操作都要两者匹配，防 pid 复用 |
| `owner` | 任务号、包名、扩展名、客户端会话 |
| `tree` | 有 Job 的记 job 标识；detached 记 `none` |
| `state` | `starting` / `running` / `exited` / `killed` / `lost` |
| `exit` | 退出码 + 原因（`exited` / `killed` / `timeout` / `cancel` / `crash`） |

内核重启后受管树已随 job 关闭而死，**不需要恢复**；detached/elevated 进程重启后即"失联"（设计接受）。

事件：`proc.started` / `proc.exited` 通过**主题事件通道**上报（[spec.md](spec.md) 4.9 / 5.8），
载荷只有结构化字段（`procId` / `kind` / `pid` / `startTime` / `exit`），没有文案；
客户端订阅 `scope: "proc"` 即可收到。

## 5. 生命周期矩阵

| kind | 谁拉起 | Job | 随谁死 | 超时 | 降权 |
|---|---|---|---|---|---|
| extension | 内核 | 后续再加 | 内核 | startTimeout / idleTimeout | 否 |
| script | pkgmgr 请求 proc | 有（kill-on-close + 限额 + 超时） | 任务 | 脚本超时 | 否（受限协议） |
| run | 壳 | 可选（壳自己的 job） | 壳，除非 detached | 命令超时 | 是 |
| detached | proc / 壳 | **无** | 自己 | — | 跟随发起者 |
| elevated | proc（一次性提权实例） | 无（跨完整性级别管不了） | 自己 | — | 是 |
| helper | 内核 / pkgmgr / proc | 短命 | 调用方 | 固定 | 否 |

## 6. 等级 level

| level | 含义 | 判定 | 结束要求 | 壳的行为 |
|---|---|---|---|---|
| `0` | 普通 | 默认 | 直接结束 | 无提示 |
| `1` | 需要管理员 | 未提权打不开；或跨用户 / 高完整性 | 必须 `--elevated` | 提示"需要管理员权限" |
| `2` | 关键 | `IsProcessCritical` 为真，或命中内置系统名单 | `--elevated` + `--force-critical` | 打印一句通用警告；**不再额外确认**，`--force-critical` 即确认 |

规则：

- **机制层只回 `level` 数字**，不产生人类可读文本；
- 壳按等级给**一句通用文案**（level 2 固定一句"结束它可能导致蓝屏或系统不可用，且不可撤销"），**不做**"进程名 → 后果"映射表；
- 受保护进程（PPL）**不是等级**，是运行期错误 `ERR_PROC_PROTECTED`：提权后照样拒绝，如实报告；
- 硬拒绝：pid 0/4，以及 PD 自己的祖先链（壳 / 内核 / proc）。

## 7. 能力契约

`proc` 是 .NET 原生扩展。两种部署形态都支持：

- **apphost（默认）**：`proc.exe` + `proc.dll` + `deps.json` + `runtimeconfig.json`，`interpreter: exe`；
  内核会给扩展注入 `DOTNET_ROOT`，apphost 因此使用**随盘运行时**，不依赖机器的全局安装；
- **dotnet 解释器**：`entry: proc.dll`、`interpreter: dotnet`，由内核用随盘 dotnet 直接跑 dll（spec 2.3）。

能力只在扩展之间和内核→扩展之间使用，包脚本永远拿不到。

### 7.1 `proc.spawn`

```json
// request
{ "entry": "node.exe", "args": ["install.js"], "cwd": "...", "env": {"PD_ROOT": "..."},
  "stdio": { "stdin": "\\\\pipe\\pd-script-t41-in", "stdout": "\\\\pipe\\pd-script-t41-out" },
  "limits": { "processMemoryBytes": 1073741824, "activeProcesses": 64 },
  "timeoutMs": 120000, "detached": false, "newProcessGroup": true }
// reply
{ "pid": 8124, "startTime": "2026-11-25T18:40:00Z", "tree": "job", "detached": false }
```

- 由 `pkgmgr`（脚本）和内核（`pd run` 的受管模式）调用；
- **创建即入 job**：`CREATE_SUSPENDED` → `AssignProcessToJobObject` → `ResumeThread`，没有竞态窗口；
- `detached: true` 时子进程用 `CREATE_BREAKAWAY_FROM_JOB` 出生，不进树（见 §9）；
- 超时/取消由 `proc` 负责结束整棵树并回报；
- 限额和超时来自 `settings.json`：`proc.processMemoryMB`（默认 1024）、`proc.activeProcesses`（默认 64）、
  `proc.scriptTimeout`（默认 120s）；proc 直接读文件，改后对**新任务**生效，不影响在途任务。

### 7.2 `proc.snapshot`

```json
{ "processes": [ { "procId": "p-3f2a", "kind": "script", "pid": 8124,
                   "startTime": "...", "owner": { "task": "t41", "package": "demo" },
                   "state": "running" } ] }
```

### 7.3 `proc.kill`

```json
{ "pid": 8124, "startTime": "...", "tree": true,
  "scope": "managed", "forceCritical": false }
```

- `scope: "managed"`：任何扩展可调，但**只允许登记表里的 pid**（pkgmgr 取消任务用）；
- `scope: "system"`：**只允许 `proc` 自己的命令处理逻辑**发起（不给任何其它扩展），其他来源一律 `ERR_UNSUPPORTED`；
- 返回 `{ "killed": true, "level": 2, "tree": true }`，或错误码。

### 7.4 `proc.system`

```json
// request
{ "elevated": false, "filter": "node" }
// reply
{ "level": "best-effort", "count": 412, "denied": 15,
  "processes": [ { "pid": 612, "ppid": 500, "name": "csrss.exe", "path": null,
                   "user": null, "commandLine": null,
                   "startTime": "...", "level": 2 } ] }
```

- 字段含 `user`（所属用户）和 `commandLine`（完整命令行）；两者都要读进程内存/令牌，
  拿不到就是 `null` 并计入 `denied`；
- 未提权：best-effort，字段经常为 `null`；
- `elevated: true`：由一次性提权实例完成（见 §10），字段完整。

## 8. stdio 代理

脚本的请求/回执必须回到 `pkgmgr`：

1. `pkgmgr` 创建两条命名管道（`\\pipe\\pd-script-<task>-in` / `-out`）；
2. `proc.spawn` 请求里带管道名；
3. `proc` 连接管道，并把子进程的 stdin/stdout/stderr 指过去；
4. 内容只在 pkgmgr ↔ proc ↔ 子进程 之间流动，**不过内核**。

规则：管道半关闭要处理；任务结束/超时时关闭管道并结束整棵树；脚本协议（[impl-pkgmgr.md](impl-pkgmgr.md)）完全不变。

## 9. 脱离（detach）

- **只能出生时决定**：Windows 没有"把已在 job 里的进程拿出来"的 API；脱离 = `CREATE_BREAKAWAY_FROM_JOB`，且父进程所在的 job 必须设 `BREAKAWAY_OK`；
- 脱离后**不属于受管树**：没有 kill-on-close、不进限额、不随内核/扩展退出；只记一条日志，内核重启后失联；
- 控制台：detached 的控制台程序没有终端，必须新控制台或重定向；同 console 下的 Ctrl+C 需要新进程组隔离；
- 期 1 只实现原语（`proc.spawn` 的 `detached` 参数），不开放用户命令；
- 后续用途：安装器、后台进程；`pd run --detach` 走壳（stdio 原生），交互式程序不用 detach。

## 10. 系统级枚举与结束

两档：

| 档 | 触发 | 能看到 / 能做什么 |
|---|---|---|
| 未提权 | 直接查询 | 同用户普通进程；能结束 level 0 的同用户进程 |
| 提权 | 显式 `--elevated` | 所有用户、命令行、令牌信息；能结束 level 1/2 进程 |

提权用**一次性实例**，不是常驻：

```
proc.exe --admin <请求文件>      # ShellExecuteEx runas，一次 UAC
   ├─ 提升 SeDebugPrivilege
   ├─ 枚举 / 结束（单次请求）
   ├─ 结果写文件
   └─ 退出（不连内核、不常驻）
```

规则：

- 每次操作一次 UAC，UAC 取消 → `ERR_ELEVATION`（8）；
- 请求文件单次消费，放受控临时目录；提权实例只执行请求文件里的一个任务；
- 受保护进程（PPL）即便提权也可能拒绝 → `ERR_PROC_PROTECTED`，如实报告；
- 结束操作写审计日志（pid / name / startTime / level / 是否提权），不进账本；
- 树结束按 ppid 快照"尽力而为"（跨用户进程无法可靠挂 job，不承诺 100%）。

## 11. 命令面

| 命令 | 作用 | 独占 |
|---|---|---|
| `pd proc list` | 受管进程列表（扩展 + 脚本 + run + detached） | 否 |
| `pd proc info` | 一个进程的详情 | 否 |
| `pd proc kill` | 结束进程（受管或系统，system 范围需显式提权） | **是** |
| `pd proc system` | 整机进程枚举（默认 best-effort，提权后完整） | 否 |

命令由 `proc` 扩展注册。**视图不合并**：扩展进程归 `pd kernel ps`（内核的孩子），
本表只覆盖 `proc` 的孩子和系统进程；统一展示留给壳 / 窗口层。`pd proc kill` 不作用于扩展
（杀扩展走 `pd kernel restart/stop`），遇到扩展 pid 按系统进程处理（内核会按重启策略收尾）。

细节与退出码见 [commands.md](commands.md) 4.10 / 5.18。

## 12. 不变式

1. 内核不创建业务进程，只拉扩展；业务进程由 `proc` 或壳拉起。
2. 进程树用 Job Object 表示，杀树 = `TerminateJobObject`，不靠枚举子进程。
3. 出生即归属：先 `CREATE_SUSPENDED`、再入 job、再 resume。
4. 脱离只能出生时决定；detached 进程只记账，PD 不负责回收。
5. 提权只做一次性（每次一次 UAC），不做常驻提权进程。
6. `scope: system` 的能力只给 `proc` 自己的命令处理逻辑；其它扩展和包脚本拿不到。
7. 机制层只产出结构化数据，人类可读文本与确认交互只存在于壳。

## 13. 测试计划（`tools/proc-e2e.py`）

- spawn 成功后拿到 pid/startTime，退出码正确；
- stdio 代理：脚本请求/回执走通（复用 script-e2e 的脚本夹具）；
- 超时 → 整树终止，退出码 25，无残留；
- 脱离：detached 子进程在 job 关闭后仍存活；随后的普通子进程被收尸；
- 限额：进程数 / 内存触顶；
- `proc.snapshot` 与 `pd proc list` 一致；
- 事件：订阅 `scope: "proc"` 后能收到 `proc.started` / `proc.exited`，不订阅收不到；
- 限额配置：改 `proc.activeProcesses` 后新任务受限（在途任务不受影响）；
- `proc.kill` 只接受登记表内的 pid；扩展发起 `scope=system` 被拒（`ERR_UNSUPPORTED`）；
- 等级判定：用假数据单测 + 对测试子进程设置 critical 只验证**识别与拦截**，**不执行 kill**（真杀会触发 bugcheck）；
- 系统枚举：未提权能列出当前进程，拿不到的字段为 `null`；
- 提权路径：UAC 取消时报 8（自动化时用提权助手的不弹 UAC 测试模式，与现有 elevation 自测一致）。

**不测**：真的结束 csrss/lsass 等关键进程、真的结束 PPL 进程。

## 14. 分期

1. **期 1（已完成）**：`PortableDev.Proc` 库（job / spawn / kill / detach + 测试）→ `proc` 扩展（先通协议）→
   `proc.spawn` + stdio 代理 → pkgmgr 接入、`pd-job` 归档 → 主题事件通道（spec 4.9 / 5.8）+
   `proc.started/exited` → 三个 `proc.*` 设置键 → `pd proc list`/`kill`（受管）。
2. **期 2**：`pd run` / `pd shell`（壳拉起、可选 job、detach）+ `pd proc system`（未提权）。
3. **期 3（基础部分已完成）**：未提权 best-effort 枚举、系统级结束、关键进程 `--force-critical` 门禁、
   一次性 admin 模式 实例（UAC 流程代码已就绪，自动化测试只覆盖非交互部分）。
4. **不做**：suspend/resume、常驻提权进程、系统进程管理器 UI、内核线程。
