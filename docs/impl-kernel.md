# 内核实现说明（v2 第一块）

> 状态：可运行。内核 + 日志子系统已落地，带单元测试与端到端测试。
> 相关文档：[architecture.md](architecture.md)、[spec.md](spec.md)、[kernel.md](kernel.md)、[commands.md](commands.md)。

---

## 1. 技术栈与位置

| 项 | 选择 | 原因 |
|---|---|---|
| 运行时 | .NET 8（随盘：`Z:\PortableDev\tools\dotnet-sdk\8.0.426`） | 与 v1 一致，盘上已有 SDK，不需要联网装 |
| 语言 | C# 12 / nullable / 编译告警当错误 | 协议字段多，类型能挡住一半错 |
| 依赖 | 零第三方包 | 减少供应链面，构建可离线 |

```
v2/
  src/PortableDev.Core/      协议、配置、身份、日志、扩展清单（可测试的纯逻辑）
  src/PortableDev.Kernel/    内核：管道、会话、路由、内置命令、扩展监督、恢复
  tests/PortableDev.Core.Runner/  13 个单元测试（控制台断言，零依赖）
  tools/e2e.py               34 项端到端检查（自己拉起内核、装好 echo 假扩展、跑完、收尾）
  tests/fixtures/echo-ext/   测试用假扩展（node，40 行，不是产品代码）
  tools/pd-probe.py          连已经跑着的内核（调试用）
```

## 2. 构建、运行、测试

> **必须加 dotnet build 的 --no-incremental 参数**：这块盘的 mtime 粒度不可靠，增量构建会误判"源码没变"而跳过编译，
> 导致跑的是旧 DLL（本期就因此白白追了一轮"done 不返回"的幽灵 bug）。


```bash
DOTNET="Z:/PortableDev/tools/dotnet-sdk/8.0.426/dotnet.exe"

# 构建
"$DOTNET" build src/PortableDev.Kernel/PortableDev.Kernel.csproj --no-incremental
"$DOTNET" build tests/PortableDev.Core.Runner/PortableDev.Core.Runner.csproj --no-incremental

# 单元测试（21 项）
"$DOTNET" tests/PortableDev.Core.Runner/bin/Debug/net8.0/PortableDev.Core.Runner.dll

# 端到端（51 项无 provider / 61 项带 provider）：自己拉起内核、执行命令、断言、停止
python -u tools/e2e.py .run

# 手工起内核（开发盘 = .run）
"$DOTNET" src/PortableDev.Kernel/bin/Debug/net8.0/win-x64/pd-kernel.dll --root .run
# 初始化一块新盘
"$DOTNET" .../pd-kernel.dll --root .run --init --once
```

## 3. 已实现（对照文档）

| 文档 | 实现 |
|---|---|
| spec 3.1 管道 | `\\\\.\\pipe\\portabledev-<stickId>`，`PipeOptions.CurrentUserOnly`，多连接 |
| spec 3.2 帧 | 4 字节小端长度 + UTF-8 JSON，上限 16 MiB，零长/超限直接断 |
| spec 3.3 信封 | `v/kind/id/seq/ts/trace/parent/data/meta`，未知顶层字段原样保留（`Extras`） |
| spec 4.1 握手 | 协议不兼容 → `hello.err` + `ERR_PROTOCOL`；`hello.ok` 带 `kernelAbi`、`stickId`、`root`、`features`、`limits` |
| spec 4.3/4.7 | `evt` 序号、`done` 收尾；`ctl` 的 ping/pong、cancel、subscribe/unsubscribe、prompt_reply 占位 |
| spec 5.1/5.2 | 扩展以 `hello.role=extension` + `invocationId` 接入（内核校验本轮拉起记录，拒绝冒充/重复连接）；`ext.invoke` 用会话任务键下发；事件按任务键回流并翻译回客户端 id；重复客户端 id 直接拒绝 |
| spec 6.2 | 错误码常量 + `ExitCodes.ForError` 映射（含 `ERR_ENV`、`ERR_TIMEOUT`） |
| kernel.md 2 | `config\\settings.json`：26 个默认项、范围/枚举校验、未知键保留、坏文件 → `.bad` |
| kernel.md 4 | `stickId` 由卷序列号 + 卷标派生，落 `state\\stick.json`；`Local\\` Mutex + 心跳锁文件 |
| kernel.md 5 | 启动恢复：intent 裁决 + staging 清理（逐条容错、按文件名排序）；机器接线由 `pd apply` 或存档点恢复 |
| kernel.md 7 | 日志提供者（`log` 扩展）收/存/查/真空；内核 `kernel.jsonl` 兜底 + 就绪后重放 |
| spec 5.3.1 | 日志提供者契约：能力、`log.write` 批量、记录字段、cursor、命令面；**存储私有** |
| spec 2.8 | 独占/并行：`mutates && !parallel` 进全局单并发队列 |

### 已实现的内置命令

`version`、`help`、`complete`、`kernel status`、`kernel ps`、`kernel ext list`、
`kernel restart`、`kernel stop`、`kernel log`、`log`、`config list|get|set|unset`。
未知命令返回 `ERR_UNKNOWN_COMMAND` + 最近候选提示。

### 扩展监督（已端到端验证）

按需启动、解释器映射（exe/pwsh/python/node/cmd/auto）、`startTimeout` 等待 `ext.ready`、
心跳超时熔断并杀进程树、`on-crash` 退避重启、`ext` 状态事件广播。
环境变量默认不继承，只放行 `SystemRoot/windir/TEMP/TMP/PATH` 与 `PD_*`。

## 4. 日志子系统（本次重点）

- 一行一条 JSON：`ts / level / stream / evt / msg / trace / task + 自定义字段`；
- 按 `log.maxSizeMB` 轮转，保留 `log.keepFiles` 份，写失败降级到 `bootstrap.log`；
- **环境变量硬约束**：`LogRedaction.Validate` 拒绝 `env`、`environment.*`、`secret`、`token`、
  `password`、`credential` 这类字段名；要提环境就用 `EnvFingerprint`，只输出"注入了 N 个变量"。
  这条有单元测试（`logging.redaction-blocks-env`）；
- 内核自己的日志只是**兜底**；日志扩展按架构 4.6 应该接管业务日志，见 §6。

## 5. 测试

```
单元（21 项）：帧往返/零长/超限、信封未知字段/版本、ABI 范围、配置默认值与校验、并发写配置、优先级映射、记录防伪、
             坏配置备份、日志脱敏、日志轮转、命令表冲突与建议、stickId 形状、时长解析
端到端（无 provider 51 项；装 log 提供者后 61 项，两个实现各跑一遍）：握手（含 ABI/stickId/protocolRange）、version、kernel status/ps/ext list、
             help/complete、config get/set/范围拒绝/枚举拒绝/unset、未知命令+建议、
             restart 缺扩展=20、ping/pong、日志尾读、kernel stop 后进程干净退出；
             扩展路径：按需拉起 node 扩展 → ext.ready → ext.invoke → step/progress/done、
             done 必须最后一条、扩展环境看不到宿主 HTTP_PROXY、kernel ps 有 pid、
             崩溃时在途任务立刻拿到 ERR_EXT_TIMEOUT 而不是挂死、on-crash 自动重启、
             不回 done 被看门狗杀进程树、密钥不进日志
```

端到端用 `tools/e2e.py`：它自己 `Popen` 内核、等待管道、断言、最后 `kernel stop` 并确认退出码为 0。
**注意**：直接跑 `pd-kernel.exe` 当后台任务再不管它，会留下持有单实例锁的孤儿进程，
后续内核会以 `ERR_BUSY(10)` 退出——测试要么用 e2e 这种自收尾的方式，要么显式 `kernel stop`。
同理，被 kill 的内核会留下 node 子进程；e2e 的 `kernel stop` 走的是 `StopAllAsync`，会杀整棵进程树。

## 6. 与文档的偏差（都是有意为之，记在这里）

| 偏差 | 现状 | 后续 |
|---|---|---|
| 日志扩展 | 参考实现已发布到 `app/extensions/log`；`log-alt` 作为可替换实现留在 fixtures | 已确认接受"换实现后旧日志不可读" |
| 扩展调用 | 已用 `tests/fixtures/echo-ext` 端到端覆盖（含崩溃/挂起/重启） | 真扩展接入时复用同样的断言 |
| 账本回放 | 启动恢复只裁决 intent 并补记录，不重放审计账本；机器接线由 `pd apply` / 存档点恢复负责 | 设计如此 |
| 单实例 Mutex 名 | 固定 `Local\\portabledev-<stickId>`，不跟 `kernel.pipePrefix` 走 | 换配置时同步前缀（否则同盘同会话无法起第二实例做测试） |
| 看门狗 | 静默判定 + **ping/pong 心跳**：静默过半发 `ctl ping`，扩展回 pong（或任何帧）即续期；仍静默到 `heartbeatTimeout` 才杀进程树（见 §8） | — |
| 日志扩展 | 参考实现已随产品发布到 `app/extensions/log`（`log-alt` 仍是可替换实现） | — |
| 扩展身份 | 扩展 hello 必须带 `PD_INVOCATION_ID`（内核校验本轮拉起记录），重复连接/冒充一律 `hello.err` | — |
| 任务键 | `ext.invoke` 用会话任务键（`s-1a2b3c4d-r1`）下发；同会话重复客户端 id 直接 `ERR_BAD_ARGS` | — |
| 管道前缀选项 | 已生效（覆盖 settings 的 `kernel.pipePrefix`）；单实例 Mutex 名仍固定 | — |
| `pd.exe` 外壳 | 已实现，见 [impl-shell.md](impl-shell.md) | 29 项端到端测试 |
| 目录名 | 与文档一致：`state\\intent\\` | — |

## 7. 下一步建议顺序

1. ~~`log` 扩展~~（已完成，见 impl-shell.md 与 spec 5.3.1）；
2. ~~node 假扩展 + `ext.invoke` 端到端~~（已完成）；
3. ~~`pd.exe`~~（已完成，见 [impl-shell.md](impl-shell.md)）；
4. ~~`storage` 扩展~~（已完成，见 [impl-storage.md](impl-storage.md)）；
5. ~~审计流（`log.audit`）+ `rollback` 扩展~~（已完成，见 [impl-rollback.md](impl-rollback.md) 与 spec 5.3.2）；
6. ~~`pkgmgr` + `download` + `unpack`~~（已完成，见 [impl-pkgmgr.md](impl-pkgmgr.md)）；
7. ~~接线层：`storage.wire.*` + `apply`/`clean`~~（已完成，见 [impl-wire.md](impl-wire.md)）；
8. ~~存档点与 `pd rollback --to`~~（已完成，见 [impl-generation.md](impl-generation.md)）；
9. ~~`pd gc` 接入存档引用~~（已完成）；源与索引；`pd upgrade` / `pd remove`；包脚本；提权与 HKLM。
## 8. 补充修复（2026-11-25 第二轮）

- 能力调用（扩展↔扩展 `call`）有超时：超过提供者 `heartbeatTimeout` 仍未回复，内核回
  `call.err(ERR_TIMEOUT)` 给调用方并清掉 pending，不再永久泄漏；回复必须来自被调用的提供者，
  其他扩展抢答会被丢弃并记 `call.spoof` 警告。
- 启动恢复：intent 文件按文件名排序处理；单个 intent 的 IO/JSON 异常只记 warning 并留给下次启动，
  不会让内核起不来。
- 单连接并发上限：hello.ok 里宣称的 `maxConcurrent` 现在真的生效（默认 8）；超出时第 9 条请求立即回
  `done(exitCode 10, ERR_BUSY)`，不再无限堆在内存里。
- 关停顺序：先停止接收并取消客户端连接（留 150ms 让 `kernel stop` 自己的 done 发出）→ Flush 日志 →
StopAll；`kernel stop --force` 直接杀扩展树，不再等优雅退出；关停期间的新请求立即回 130。
- **客户端断线 ≠ cancel**：客户端进程退出后任务会继续跑到 done（结果落盘），重连后用 `pd list`/`pd log`
  查询；显式取消走 `ctl cancel`（扩展在 await 边界检查并回滚，见 impl-pkgmgr §2.1）。
- 日志提供者冲突：同一 priority 的两个 `log.ingest` 提供者都被拒绝（记 `log.provider` 错误，记录走兜底文件），
  低 priority 者优先接管。
- 日志投递队列：内核到提供者之间是有界队列（8192 条、`DropOldest`）。队列满时丢弃最旧的记录，并用通道的
  `itemDropped` 回调计数（`DropOldest` 下 `TryWrite` 恒返 true，靠返回值计数永远是 0），随后补写一条
  `log.dropped` 警告记录；`Flush()`/`Dispose()` 也会 drain 队列，所以通道按多读者配置，提供者状态字段加锁。
- 内置命令先入命令表：扩展与内置重名时拒绝的是扩展，内置命令不会被静默顶掉。
- 子命令级独占：扩展可以在 `ext.json` 里把 `"source list"` 这类多词命令单独登记，
  内核按**最长前缀**解析（最多 3 段），用该条目的 `mutates`/`parallel` 决定是否串行；
  扩展仍收到去掉首段的 argv，所以旧处理器不用改解析。pkgmgr 的 `source list` 是第一个实例。
- 日志 seq 单调：`KernelLogger` 启动时把 `state\logs\seq-epoch` 加一并原子写回，`_seq` 初值为
  `纪元×1e6`；日志文件不可写时退回 boot 内自增（只保证不崩，不保证跨启动单调）。
- 能力调用的权限强制：扩展的 `ext.json` 里的 `requires` 就是能力白名单，内核在
  `ForwardCallAsync` 转发前校验——未声明（且不是它自己 `provides` 的）能力直接回
  `ERR_UNAUTHORIZED`，只记 `cap.denied` 日志，不打扰提供者；`requires` 支持 `storage.*` 通配，
  内置扩展都按实际调用补全。测试夹具 `cap-probe` 用「声明过的 storage.record 通过 / 未声明的
  storage.uninstall 被拒」双向验证。
- 崩溃恢复的账本阶段：`RecoveryService` 把被 drop 的 task 放进 `RecoveryReport.DroppedTasks`；
  正常启动时 `Supervisor.SetRecoveryRollbackTasks` 通过 `PD_RECOVERY_ROLLBACK` 交给 `rollback` 扩展，
  它在 `ready` 后自动回滚并写 `recovery.rollback` 审计记录；一次性初始化模式（部署/测试用）落盘到
  `state\recovery-orphans.json`，下次启动合并处理（失败不阻塞内核）。
- `pd ext`：`list` 与 `kernel ext list` 同源；`enable/disable` 只改 `ext.json` 的 `enabled`；
  `update` 校验 `ext.json` 名字 → `Supervisor.StopAsync(名)` 停该扩展 → 换目录 → 返回 `restartRequired`
  （注册表启动只扫一次，无热重载、无 rollback）。`pd recover` 现场跑一遍 `RecoveryService`，
  策略只认 `recovery.intentDefault`，`--adopt/--drop/--dry-run` 一律拒绝。
- 内置扩展自愈：内核在 `registry.scan` 之前把 `app\baseline\extensions` 与 `extensions\` 对比，
  补回缺失/版本不一致的内置扩展（记 `ext.restored` 警告），所以删掉 `ui` 的盘仍能开窗；
  手动体检与修复用 `pd doctor --fix`。
- 单实例锁：若 lock.json 的 root 与当前盘根不一致（复制盘），返回明确的 Busy 提示；坏 lock 文件全异常兜底。
- 配置并发：`SettingsStore` 所有读写路径与 `Unset` 均在 `_gate` 锁内执行，`config set/unset` 为独占命令；`AtomicFile` 对 rename 冲突做重试。
- 敏感日志遮蔽：`LogRedaction` 递归丢弃 `env`/`environment` 全量快照，并将命中 `secret`/`token`/`password` 等敏感字段的值替换为 `[REDACTED]` 同时标 `_sensitive: true`（内核与扩展日志统一生效，避免加密未开时明文落盘）。
- 慢客户端与优雅关停：`ClientConnection.SendAsync` 设 5s 写超时（超时即断开慢连接，防止阻塞扩展事件循环），断连前短暂 drain 在途请求；非 `--force` 的 `kernel stop` 等待 `ActiveTasks` 归零（上限 10s）再取消服务器令牌。
- **随盘运行时**：发布目录带 `app\runtime`（.NET 主机 + `shared\Microsoft.NETCore.App` + `Microsoft.WindowsDesktop.App`）
  与 `app\runtime\node\node.exe`，所以换一台没装 .NET/Node 的机器也能跑。解析顺序：`PD_DOTNET`/`PD_NODE`
  覆盖 → 随盘运行时 → `DOTNET_ROOT`/PATH；内核、壳、窗口、扩展用的都是同一份，子进程继承 `DOTNET_ROOT`。
- 能力调用加 30 分钟防泄漏超时；扩展声明 `inheritEnv` 时才继承宿主环境（默认仍隔离）。
- 管道服务预建 16 个 accept 循环（最多 64 实例），消除"所有管道实例都忙"的窗口。
- 协议：Envelope 版本必须为 1；握手完成后的协议错误不再回 hello.err；进程退出事件按进程实例比对，
  旧进程的迟到事件不会清掉新连接。
- 心跳保活：看门狗静默过半发送 `ctl ping`，扩展侧（所有 protocol.js 与 echo 夹具）自动回 pong；
  因此"长时间安静但健康"的任务（大文件下载、慢 IO）不会被误杀；真死/卡死的扩展仍会在
  `heartbeatTimeout` 后被杀（e2e 用 hang 死锁与 6.5s 静默慢任务两个方向验证）。
