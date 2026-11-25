# 存储扩展实现说明（storage）

> 状态：可用。租界、收编、记录、current 指针、gc、账本、崩溃恢复均已跑通端到端。
> 相关文档：[architecture.md](architecture.md) 4.4 / 4.6 / 9 / 11.2、[spec.md](spec.md) 5.4 / 5.6、[kernel.md](kernel.md) 5。

---

## 1. 它是什么

`storage` 是内置扩展里**唯一直接碰盘**的那个（架构 4.6）。内核不碰业务数据，
其它扩展也不许自己往 `store` 里写：它们只能向 storage 要租界，再由 storage 收编。

```
app/extensions/storage/     产品位置（随程序发布）
  ext.json                  能力声明
  index.js                  实现（node，核心扩展允许用脚本，见架构 4.1）
  protocol.js               分帧客户端（和 log / echo 共用同一份形状）
tests/fixtures/storage-probe/  测试驱动：真实走 grant -> adopt -> current -> record
```

## 2. 能力

| 能力 | 调用方式 | 语义 |
|---|---|---|
| `storage.grant` | `ctl` `grant.request` → `grant.reply` | 发一个受控临时目录（租界） |
| `storage.adopt` | `call` op `commit` / `abort` | 收编 staging 目录进 store，或丢弃 |
| `storage.record` | `call` op `get` / `list` | 读包记录 |
| `storage.current` | `call` op `set` / `get` | 挪 current 指针 |
| `storage.gc` | `call` op `run` | 清 staging 与无引用的 store 目录 |
| `storage.ledger` | `call` op `begin` / `append` / `commit` / `list` | 记账 |

内核按**能力名**找提供者（spec 5.6），不认扩展名；回复按 `call` 的 id 或任务 id 路由回发起方。

## 3. 目录布局

```
<root>\
  store\<16位哈希>-<包名>-<版本>\   内容，装好不再改
  current\<包名>                   目录联接 -> store 里某个版本
  staging\<任务号>-<提示>-<序号>\  租界，收编或丢弃后消失
  state\records\<包名>.json        包记录
  state\intent\<任务号>.json       在途意图（崩溃恢复用）
  state\ledger\<yyyy-mm>.jsonl     账本
```

**路径即身份**：目录名里的 16 位十六进制是内容哈希前 16 位（架构 6.1），
完整哈希写进记录。同样的内容重复收编会落到同一个目录，不会存两份。

## 4. 租界（grant）

```
扩展 → 内核   ctl { action:"grant.request", kind:"dir", hint:"unpack", mode:"rw", ttl:"10m" }
内核 → storage ctl（同上，附 from:"pkgmgr"）
storage → 内核 ctl { id:<任务号>, data:{ action:"grant.reply",
                   grant:{ id:"g1", kind:"dir", path:"...\\staging\\t42-unpack-1",
                           mode:"rw", expiresAt:"..." } } }
内核 → 扩展   原样转回
```

- 内核只转发、不缓存、不代持（spec 5.4）；
- 租界目录一定在 `staging\\` 下；storage 在收编时**再校验一次**路径前缀，越界直接拒绝；
- 租界带 `owner` / `expiresAt`：adopt/abort 校验「在这张租界目录内 + 未过期 + 归属同一扩展」，
  gc 先清过期租界再算引用；
- 接线记录在首次 apply 时保存 `wiring.before`（机器原值），clean/回滚据此还原而不是一律删除；
- `log.audit` 的 seq 跨 provider 重启从磁盘恢复（不再归零）；provider 为每个 task 写一个小 marker，
  审计文件被保留期真空后 query 返回 `truncated:true`，从而 `pd rollback` 报 `ERR_AUDIT_EXPIRED`(20)；

## 5. 收编（adopt）

```
1. 校验 stagingPath 在 staging 根之内
2. 算内容哈希（调用方给 contentHash 就用它；否则算目录指纹）
3. 写 intent  state\\intent\\<task>.json { phase:"begin", target }
4. 原子 rename  staging -> store\\<短哈希>-<id>-<ver>
5. 更新 intent  { phase:"renamed" }
6. 写记录       state\\records\\<id>.json（storePath + 两份哈希）
7. 更新 intent  { phase:"done" }，然后删除 intent
```

- **只认 `done` 为提交**（架构 11.2）：中途崩溃时 intent 停在 `begin` / `renamed`，
  下次启动由内核恢复流程裁决（补记录或删目录）；
- 目标已存在 → 直接删 staging、返回 `existed:true`，不重复占空间；
- 目录指纹算法固定（见 §6），所以“同样内容 → 同样路径”可复现。

## 6. 目录指纹

按相对路径排序后，对每个条目喂：

```
文件： "f\0<相对路径>\0<字节数>\0<文件 sha256>\n"
目录： "d\0<相对路径>\n"
```

再整体取 sha256。**只算路径、大小、内容**，不算时间戳与权限——换台机器拷过来指纹不变。

## 7. current 指针

- `current\\<包名>` 是 Windows 目录联接（junction），指向 `store\\...`；
- 升级 = 换一个 store 目录 + 挪联接，PATH 里写的 `current\\<包名>` 不变（架构 7）；
- 替换前先删旧联接（`rmdir`，只删链接不删目标），再 `mklink /J`；
- 删除联接**不能用递归删除**，否则可能把目标内容一起带走——代码里单独走 `removeLink`。

## 8. gc

| 对象 | 规则 |
|---|---|
| 存档文件 | 超过 `--keep` / `gc.keepGenerations` 时删最旧（先裁剪，再算引用） |
| `staging\\*` | 没有对应 intent、也不是活跃租界的目录 → 删 |
| `store\\*` | 不被记录、current 联接、在途 intent、**任何存档点**引用的 → 删 |
| `cache\\blobs\\*` | 只有显式 `--blobs` 才动；删掉没有任何记录/存档引用其 `hashes.archive` 的 blob |

返回 `{ stagingRemoved, storeRemoved, generationsRemoved, blobsRemoved, blobsKept, bytesFreed, dryRun }`；
`--dry-run` 只统计不删除；`--force` 允许在记录损坏时强制清理（默认遇到损坏记录直接拒绝，避免误删）。
命令入口是 `pd gc`（storage 自己注册的命令）。

## 9. 账本（审计流）与回滚

账本不落在 storage 自己目录里，而是通过日志提供者的 `log.audit` 能力写进**审计流**（spec 5.3.2）。
这样只有一份存储、一套保留期，也顺手拿到了“fsync 之后才算数”的保证。

### 9.1 写入

- 改状态**之前**先写审计条目，等 `call.ok` 才继续；拿不到就中止（不允许改了没记上）；
- 条目带 `op / before / after / undo`；`undo` 是 `{cap, op, args}`，由 storage 给出；
- `phase:"begin" / "done"` 由调用方（pkgmgr 或 storage）成对写；
- 保留期 `log.auditRetentionDays`（默认 7 天），由日志提供者真空。

storage 自己会写的条目：

| 操作 | 条目 | undo |
|---|---|---|
| 挪 `current\<包>` | `current.set`（before = 旧目标或 null） | `{cap:"storage.current", op:"set", args:{pkg, storePath}}`，before 为 null 时 `op:"clear"` |
| 写包记录 | `record.write`（before = 旧记录或 null） | `{cap:"storage.record", op:"write"}`，before 为 null 时 `op:"delete"` |

### 9.2 执行撤销的原语

storage 提供两组能力，供 `rollback` 扩展调用：

- `storage.current.set` / `clear`、`storage.record.write` / `delete`；
- `storage.wire.*`（待接线层落地）：`path.set` / `env.set` / `registry.set` / `launcher.remove`，
  全部是“设成这个值”的幂等操作，正向接线和撤销共用。

### 9.3 谁负责编排

回滚本身不在 storage 里：`rollback` 扩展读审计流、倒序执行 `undo`、把进度写回审计流。
storage 只认识“把某个原语执行掉”，不认识任务与批次。

### 9.4 还没覆盖的

机器层原语（PATH / 环境变量 / 注册表 / 启动器）已由接线层（`apply` / `clean`）落地，并在首次
apply 时记录机器原值（`wiring.before`），clean / 回滚 / 存档点恢复都能还原；
存档点回滚是另一套（快照式），已在 `generation` 能力实现。


## 10. 安全边界

- `stagingPath` 必须在 `staging\\` 内，`storePath` 必须在 `store\\` 内——越界一律 `ERR_UNSUPPORTED`；
- 包名/版本必须匹配白名单（`^[a-z][a-z0-9-]{0,63}$` / 版本不含路径分隔符），否则 `ERR_PKG_INVALID`；拼接后还要用 `path.relative` 复验在 `store\` / `records\` / `current\` 内；
- `contentHash` 必须是 64 位十六进制，且 store 名与 `hashes.tree` 始终用**本地重算**的目录指纹（contentHash 只记作 `hashes.archive`）；
- storage 自己不读包脚本、不跑命令，只做文件系统动作；
- HKLM 与稳定根/用户目录之外的目标走 `pd-elevate.exe` 提权批次（`state\wire\fake-mode` 下写 fake 状态）；
- 家目录：`home\<包>` 在盘上，稳定根下自动建 junction；`data.link`/`data.copy` 与普通接线共用
  ledger/rollback，目标已存在时拒绝覆盖。

## 11. 和内核的分工

| 谁 | 干什么 |
|---|---|
| 内核 | 按能力路由 `call`、转发 `grant.request`、把 `grant.reply` 按任务号送回发起方、崩溃恢复时裁决 intent |
| storage | 发租界、收编、写记录、挪指针、gc、记账 |
| 内核恢复 | 只认 intent 的 `done`；`begin`/`renamed` 的按 `recovery.intentDefault` 补记录或删目录 |

## 12. 测试

`tools/storage-e2e.py`：**62 项**，自己起内核、跑完、收尾。

| 组 | 断言 |
|---|---|
| 收编 | 退出码、store 目录名形状、内容落盘、树指纹、记录、current 联接可解析、intent 与 staging 清空 |
| 幂等 | 再装一次同路径、store 里不出现第二份 |
| 丢弃 | abort 后 staging 为空、store 不变 |
| gc | 手工造孤儿 staging/store → 被清掉，真实包保留 |
| 账本 | 条目数、文件按月、begin/done 成对 |
| 回滚 | 装 v2 后回滚该任务：记录回到 v1、指针回到 v1、store 不动、marker 为 done、再回滚是空操作 |
| 日志联动 | storage 的 `log.write` 经内核进入日志提供者 |
| 崩溃恢复 | 造 `phase:"begin"` 的 intent + 目录 → 内核单次运行（只跑恢复）补出记录并清 intent |

运行：

```bash
DN="Z:/PortableDev/tools/dotnet-sdk/8.0.426/dotnet.exe"
"$DN" build src/PortableDev.Kernel/PortableDev.Kernel.csproj
python -u tools/storage-e2e.py .run
```

## 13. 还没做的

| 项 | 说明 |
|---|---|
| 租界续期 | 到期后需重新申请，没有续期接口 |
| restore 断点续跑 | generation.restore 失败后靠账本回滚，不能从中断点继续 |
| 审计全量扫描 | `log.audit query` 仍按月文件全量扫描；任务多了以后需要索引 |
| 租界续期 | 不支持续期（只有 ttl 到期），到期后需重新申请 |
| 扩展卸载时的数据清理 | 等 `pd remove` |
