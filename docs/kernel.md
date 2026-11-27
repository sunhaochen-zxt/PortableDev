# PortableDev v2 规范：内核、部署与自愈

> 配套文档：[architecture.md](architecture.md)、[spec.md](spec.md)、[commands.md](commands.md)、[window.md](window.md)
> 本文管：**程序怎么部署、配置项有哪些、锁怎么拿、崩溃怎么恢复、扩展怎么更新**。
> 本文不管：业务命令的行为（见 commands.md）。

---

## 1. 部署单元

### 1.0 盘的硬性要求

| 项 | 要求 | 原因 |
|---|---|---|
| 文件系统 | **必须 NTFS** | `current\`、`home\`、`app\current` 都是目录联接（junction）；exFAT / FAT32 不支持 |
| 卷 | 整块盘一个卷 | `staging\` 收编到 `store\` 靠同卷 `rename`；跨卷会退化成复制，原子性也没了 |
| 权限 | 普通用户可读写盘 | 提权只用于机器层（`C:\Dev`、HKLM），写盘本身不该要管理员 |
| 空间 | 至少留 2 倍最大包的余量 | 下载缓存、解压租界、收编后的 store 会同时存在 |

**不做 exFAT / FAT32 的降级路径**：没有联接，`current\` 指针方案就得换成"每台机器自己拼 PATH"，
等于多维护一套实现，收益为零。`pd doctor` 会检查文件系统并明确报错。

### 1.1 程序目录 `app\`

```
v2\app\
  pd.exe                  壳（命令行）
  pd-kernel.exe           内核（常驻服务）
  pd-window.exe           窗口壳
  runtime\                自包含 .NET 运行时
  extensions\             内置扩展（只读基线）
    storage\
    log\
    download\
    unpack\
    pkgmgr\
    (scoop-src\、ui\ 等可选扩展，装了才有)
  baseline.json           内置扩展清单（名字、版本、sha256）
  baseline\               内置扩展的备份副本（自愈用）
```

规则：

- `app\` **只读**：升级只换目录内容，不在这里存任何用户数据；
- 内置扩展带 `baseline.json`，记录每个文件的名字、版本、哈希；
- 用户扩展放在 `v2\extensions\`（盘根），**同名时用户版覆盖内置版**，
  并在 `pd kernel ext list` 里标"被覆盖"。

### 1.2 最小可运行集

| 组成 | 缺了会怎样 |
|---|---|
| `pd.exe` + `pd-kernel.exe` + `runtime\` | 完全跑不起来 |
| `storage` | 内核能起，但任何读写的命令都失败 |
| `log` | 能跑，日志写进兜底文件 |
| `pkgmgr` | 能跑 `kernel *`、`doctor`，装包类命令全不可用 |
| `download` / `unpack` | 只有"装需要下载/解压的包"时才需要 |

### 1.3 拷走 `app\` 能不能用

**不能。** `app\` 只是程序本身；要能用还需要整棵 `v2\` 树（`config` / `state` / `store`）。

单独把 `app\` 拷到别处，内核能启动，但 `doctor` 会报"这里不是一块 PortableDev 盘"，
所有依赖记录的命令都返回 `4`（`ERR_ENV`，环境问题）。

---

## 2. `config\settings.json`

由程序读写，人可以用 `pd config set`（推荐）或直接编辑（需重启内核）。

### 2.1 字段

| 键 | 默认 | 范围 | 谁读 | 改后要重启吗 |
|---|---|---|---|---|
| `schema` | `1` | 整数 | 内核 | — |
| `root.stableRoot` | `C:\Dev` | 绝对路径、不带盘符变量 | 内核、storage | **是** |
| `root.stickRoot` | `""`（自动检测） | 路径或空 | 内核 | 否 |
| `kernel.pipePrefix` | `portabledev` | 字符串 | 内核 | **是** |
| `kernel.logLevel` | `info` | `debug` / `info` / `warn` / `error` | 内核 | **是** |
| `kernel.idleExit` | `0`（不退出） | `0` 或时长（`10m`） | 内核 | **是** |
| `kernel.startTimeout` | `5s` | 1–60s | 内核 | 否 |
| `kernel.envMode` | `full` | `full` / `proxy-only` | 内核 | 否 |
| `install.allowScripts` | `local` | `local` / `always` / `never` | pkgmgr | 否 |
| `install.defaultChannel` | `stable` | `stable` / `beta` / `dev` | pkgmgr | 否 |
| `install.maxConcurrent` | `2` | 1–4 | pkgmgr | 否 |
| `install.verifyAfterInstall` | `true` | bool | pkgmgr | 否 |
| `proc.processMemoryMB` | `1024` | 64–16384 | proc | 否 |
| `proc.activeProcesses` | `64` | 1–1024 | proc | 否 |
| `proc.scriptTimeout` | `120s` | 1–3600s | proc | 否 |
| `gc.keepGenerations` | `50` | 5–500 | 内核 | 否 |
| `gc.keepBlobs` | `true` | bool | 内核 | 否 |
| `gc.stagingMaxAge` | `24h` | 时长 | 内核 | 否 |
| `log.level` | `info` | 同上 | 日志扩展 | **是** |
| `log.maxSizeMB` | `50` | 1–1000 | 日志扩展 | **是** |
| `log.keepFiles` | `10` | 1–100 | 日志扩展 | **是** |
| `log.encryption` | `none` | `none`（预留 `aes`） | 日志扩展 | 否 |
| `log.retentionDays` | `30` | 1–3650 | 日志扩展 | 否 |
| `log.auditRetentionDays` | `7` | 1–3650 | 日志扩展 | 否 |
| `log.batchBytes` | `65536` | 4096–1048576 | 日志扩展 | 否 |
| `log.rateLimit` | `5000` | 0–1000000 | 日志扩展 | 否 |
| `network.timeout` | `30s` | 1–600s | download | 否 |
| `network.retries` | `3` | 0–10 | download | 否 |
| `network.proxy` | `system` | `system` / `none` / URL | download | 否 |
| `elevation.mode` | `on-demand` | `on-demand` | 内核 | 否 |
| `recovery.intentDefault` | `adopt` | `adopt` / `drop` | 内核 | **是** |
| `ui.theme` | `light` | `dark` / `light` / `system` | 窗口壳 | 否 |
| `ui.language` | `zh-CN` | `zh-CN` / `en` | 两个壳 | 否 |

### 2.2 规则

- 键名用 `分段.名字`，未知键**保留但忽略**（向前兼容）；
- `pd config set` 会校验范围，越界直接拒绝；
- **`pd config get` 读的是内核内存里的当前值**（`--file` 才读文件）；
- 手改文件后，内核启动时读一次，之后改文件不生效；`pd config get` 会提示
  "文件已改，重启生效"（比对文件 mtime 与内核上次读取时间）；
- `pd config set` 走内核：校验 → 写文件 → 更新内存 → 返回"是否要重启"；
  所以"立刻生效"的键当场就生效，不需要重启；
- 文件损坏（JSON 解析失败）→ 用默认值启动，并把坏文件改名成 `settings.json.bad`。

> `install.maxConcurrent` 只控制**下载 / 解压**这类非独占阶段的并行度；
> 安装的提交阶段（收编、接线）仍然严格串行，不受这个值影响。

---

## 3. 启动顺序

内核启动严格按这个顺序：

```
1. 读 config\settings.json（坏了就用默认值 + 备份坏文件）
2. 拿锁（见第 4 节）；拿不到就退出，客户端得到 ERR_BUSY
3. 扫 app\extensions 和 v2\extensions，校验 ext.json，构建命令表
   - 冲突的命令名 → 后加载的整个扩展不加载
   - requiresKernel（比的是 ABI 整数，见 spec.md 2.4）不满足 → 跳过并记录
4. 检查最小可运行集（见 1.2）；缺关键扩展 → 记一条致命日志后仍然启动，
   让客户端能拿到明确的错误（而不是连不上管道）
5. 跑崩溃恢复（见第 5 节）
6. 开始监听管道
7. 启动空闲计时（如果配置了 idleExit）
```

---

## 4. 锁与单实例

### 4.1 两层

| 层 | 机制 | 用途 |
|---|---|---|
| 系统层 | Named Mutex `Local\portabledev-<stickId>` | 防止**同一会话**起两个内核 |
| 文件层 | `state\lock.json`（JSON）+ 心跳 | 跨会话/跨用户检测、给人看、`doctor` 用 |

为什么用 `Local\` 而不是 `Global\`：`Global\` 在别的会话先占同名对象时会让内核直接起不来，
而且标准用户在 `Global\` 下创建对象有额外失败可能。**`Local\` 一定拿得到，跨会话靠心跳检测**。

跨会话的情况（同一台机器、两个登录会话插同一块盘）：
`Local\` 会各自成功，此时用 `state\lock.json` 的心跳判断——心跳新鲜就拒绝启动，并报
`ERR_BUSY` + "另一个会话已经在用这块盘（PID xxx）"。这种情况本身很罕见，真实、但不需要 Mutex 兜。

`state\lock.json` 内容：

```json
{ "schema": 1, "pid": 8123, "machineId": "b1c2...",
  "kernelVersion": "2.0.0", "protocol": 1,
  "startedAt": "2026-11-24T08:00:00+08:00",
  "heartbeatAt": "2026-11-24T08:12:31+08:00",
  "state": "running" }
```

### 4.2 stale 判定

内核每 10 秒更新一次 `heartbeatAt`。启动时按这个顺序判断：

| 情况 | 处理 |
|---|---|
| `Local\` Mutex 拿不到 | 同会话已有内核 → 退出，`ERR_BUSY` |
| Mutex 拿到，锁文件不存在 | 正常启动，写锁 |
| Mutex 拿到，锁文件存在且心跳**新鲜**（<60s） | 另一个会话的内核在跑 → 退出，`ERR_BUSY` + 报告 PID |
| Mutex 拿到，锁文件存在但心跳**过期**（≥60s） | 上一个内核异常退出 → **接管**：跑恢复，覆盖锁 |

原则：**`Local\` Mutex 管同会话，锁文件心跳管跨会话**；
心跳过期才允许接管，避免两个会话互相抢。

### 4.3 强停
`pd kernel stop --force`：

1. 告诉内核立刻停（不等任务）；
2. 10 秒内没退 → 按 PID 杀进程树；
3. 删锁文件；
4. 下次启动时由恢复流程清理 staging 残留。

### 4.4 `stickId`：盘的身份证

`\\.\pipe\portabledev-<stickId>` 和 `Local\portabledev-<stickId>` 都用它，所以它必须与盘符无关、在任意机器上稳定、复制盘能被发现。

| 项 | 规则 |
|---|---|
| 存在哪 | `state\stick.json`：`{ "schema": 1, "stickId": "pd-3f9a2c71b004", "source": "volume", "volumeSerial": "1A2B3C4D", "label": "PORTABLEDEV", "createdAt": "..." }` |
| 首次生成 | 内核第一次启动时算：`pd-` + `sha256("portabledev:" + volumeSerial + ":" + label)` 的前 12 位，写进 `state\stick.json`（`source: "volume"`），之后只读 |
| 壳怎么用 | `pd.exe` 先读这个文件；读不到就**按同一公式自己算**，所以首次启动也不会连错管道 |
| 盘符变了 | 不受影响（id 与盘符无关） |
| 复制出来的盘 | 两个盘同 id → 第二个内核发现锁文件里的 `root` 不是自己 → `ERR_BUSY`，并提示跑 `pd doctor --fix` 重新生成本盘 id（随机 12 位，`source: "manual"`，旧 id 记日志） |

`state\stick.json` 是**盘的身份**：不进存档点、不随 `pd clean` 删除；改它等于换了一块盘。

---

## 5. 崩溃恢复

### 5.1 什么时候跑

- 内核启动时自动跑一次（第 3 节第 5 步）；
- 也可以手动 `pd recover`（commands.md 5.14）。

### 5.2 顺序

```
1. 扫 state\intent\*.json，按时间排序
2. 对每条 intent：
   a. 目标目录在 store、记录也在 → 视为已提交，删 intent
   b. 目标目录在、记录不在 → 默认补记录（--adopt），或删目录（--drop）
   c. 目标目录不在、记录在 → 删记录
   d. 都不在 → 删 intent
3. 扫 staging\，删掉**既没有对应 intent、也没有活跃任务**的目录（有 intent 的交给第 2 步）；
   其它残留按 `gc.stagingMaxAge` 在 `pd gc` 时清，不在启动时一刀切
4. 扫账本，找出**有 begin 但没有 done** 的 task：
   把这些 task 的所有接线改动（PATH / 环境变量 / 注册表 / 启动器）
   按写入顺序**倒序**还原到 before 值。
5. 写一条恢复日志
```

账本条目格式见架构 9.2：`begin` / 改动 / `done` 三段，
store 的 intent 和接线的账本条目用**同一个 `task` 号**关联。

### 5.3 恢复失败怎么办

- 单条 intent 处理失败 → 记录，继续下一条，最后汇总；
- 恢复是"尽力而为"，**不会让内核起不来**；
- `pd doctor` 会把没恢复成功的列出来。

---

## 6. 扩展自身更新

扩展就是文件，没有安装器。

```
pd ext list                         名字、版本、来源（内置/用户）、状态、命令数
pd ext enable|disable <名字>        启用/禁用（需重启内核）
pd ext update <名字> <目录|zip>     换一个新版本（需重启内核）
```

行为：

1. `update` 要求源是**绝对路径**，新目录/zip 里的 `ext.json` 名字必须与目标一致；
2. 停掉正在跑的该扩展（只停这一个，不停内核）；
3. 删掉旧目录，新目录就位；
4. 命令返回 `restartRequired: true`，重启内核后新版本生效。

约束：

- **没有热重载**：扩展注册表在启动时只扫描一次，换文件/改 `enabled` 都要重启内核；
- **没有 rollback**：内置扩展有 `app\baseline\extensions` 兜底（内核启动自愈 / `pd doctor --fix` 恢复），
  用户扩展更新失败就再 `pd ext update` 一份能用的；
- 更新内核本身仍然要 `pd kernel stop` 再覆盖（架构 3.3）；
- 用户扩展可以覆盖内置扩展，但 `pd ext list` 会明确标出来。

### 6.1 扩展状态事件

内核在扩展的四个时刻发 `ext` 事件（spec 4.3）：

| 时刻 | `state` |
|---|---|
| 拉起进程 | `starting` |
| 握手 + `ext.ready` 成功 | `ready` |
| 正常退出 | `exited` |
| 崩溃 / 超时被杀 / 握手失败 | `crashed` |

- `id` 固定 `"system"`（不是任务事件）；
- `reason` 在 `crashed` / `exited` 时填（退出码、超时、握手错误）；
- 只发给在 `hello` 里声明了 `features: ["ext-events"]` 的客户端，
  或运行时 `subscribe` + `scope: "ext"` 的客户端；
- 事件是**尽力而为**：客户端连不上或没订阅就当没发生，不重发、不堆积。

---

## 7. 日志

### 7.1 位置与轮转

```
state\logs\providers\<实现名>\   日志提供者自己的存储（**格式实现自定**，不属于契约）
state\logs\kernel.jsonl         内核兜底：没有提供者、或提供者没就绪时写这里
state\logs\bootstrap.log        连兜底都写不了时的最后出口
```

- 日志由 `log` 扩展负责收、存、查、真空（见 spec 5.3.1）；内核只在提供者缺失时兜底；
- 提供者就绪后，内核把本次启动写在 `kernel.jsonl` 里的记录**通过 `log.write` 重放**过去，
  扩展永远不直接读内核文件；
- 记录带 `_bootId` 与 `_invocationId`，一次启动/一次扩展拉起可以精确关联；
- 日志**不进账本、不进存档点、不随包导出**；
- **环境变量按名记录，不倾倒整份快照**：需要时写"哪个键、什么值"；
  整份 `env` 对象传给日志会被内核丢弃并记一条告警。敏感值在记录里标 `_sensitive:true`，
  将来加密落地后按这个标记处理。显式 `--trace-env`（调试开关）仍写独立临时文件，不进 `state\`。
- **审计流（账本）**：日志提供者同时持有可撤销改动的账本，走 `log.audit` 能力的 durable 写入
  （见 spec 5.3.2）；保留期单独配置 `log.auditRetentionDays`（默认 7 天）。
- 相关配置：`log.level`、`log.maxSizeMB`、`log.keepFiles`、`log.retentionDays`、
  `log.auditRetentionDays`、`log.batchBytes`、`log.rateLimit`（后几个供提供者使用，内核透传）。



### 7.2 加密（预留）

`log.encryption` 现在只支持 `none`。以后加 `aes` 时：

- 密钥放 `state\keys\`，用 Windows DPAPI 保护（跟机器绑定）；
- 只加密 `<扩展名>.jsonl`，`bootstrap.log` 保持明文（否则崩溃时没法看）；
- 换机器时密钥失效 → 日志读不出来，但**环境本身不受影响**；
- 加密开关改变后要重启内核。

---

## 8. 验收清单

写完实现后逐条对：

1. 把 `app\extensions\storage` 改名 → 启动后 `pd kernel ext list` 标红，装包命令返回 `ERR_EXT_MISSING`；
2. `pd doctor --fix` → 从 `app\baseline\` 恢复 storage，再跑一次装包成功；
3. 手工写一个 stale 锁（PID 不存在、心跳很旧）→ 内核能接管并记录警告；
4. 在 rename 之后、写记录之前杀进程 → 下次启动 `pd recover` 补出记录；
5. `pd config set gc.keepGenerations 40` → `pd config get` 显示 40，`gc` 按 40 执行；
   手工改文件 → `pd config get` 仍显示旧值并提示"文件已改，重启生效"，
   `pd config get --file` 才显示新值；
6. 把 `settings.json` 改成坏 JSON → 内核用默认值启动，留下 `settings.json.bad`；
7. `pd ext update` 一个握手会失败的扩展 → 自动回退，旧版本仍在跑。
