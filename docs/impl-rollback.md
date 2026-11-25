# 回滚扩展实现说明（rollback）

> 状态：可用。读审计流倒序执行 `undo`，进度写回审计流，崩溃可续。
> 相关文档：[architecture.md](architecture.md) 9.2 / 11.1、[spec.md](spec.md) 5.3.2、[impl-storage.md](impl-storage.md)。

---

## 1. 它在整条链上的位置

```
pkgmgr ──(失败)──► rollback 扩展 ──读──► 日志提供者（审计流）
                        │
                        └──执行 undo──► storage 的能力（storage.current / storage.record / 将来的 storage.wire）
```

- **storage** 是唯一执行者：它改指针、改记录、将来改 PATH/注册表；
- **rollback** 是编排者：它只做三件事——查、倒序、调 `undo`；
- 安全边界：`undo` 只允许 `storage.*` 能力（别的 cap 一律拒绝）；`--task` 为空直接 `ERR_BAD_ARGS`
  （空 task 曾会命中全部历史条目、等于全量回滚）；
- 审计真空：provider 按任务留 marker，条目被保留期清掉后回滚明确报 `ERR_AUDIT_EXPIRED`(20)，
  不会把"已过期"误当"没有可撤条目"返回 0；
- **日志提供者** 持有审计流：只有它保证"落盘后才算数"。

rollback 不认识任何业务语义。它看到的每条账都长这样：

```json
{ "id": "...", "ts": "...", "task": "t42", "op": "current.set",
  "before": null, "after": "Z:\\...\\store\\ab12...-demo-1.0.0",
  "undo": { "cap": "storage.current", "op": "clear", "args": { "pkg": "demo" } } }
```

新增一种 op 只要写入方给出对应的 `undo`，**rollback 一行都不用改**。

## 2. 一次回滚的流程

```
1. call log.audit query {task}            ← 拿到该任务全部条目
2. 若 truncated（begin 已被真空）→ ERR_AUDIT_EXPIRED（退出码 20），不半撤
3. 收集已完成的进度条目（op=rollback.step, phase=done）→ 跳过
4. 倒序遍历，挑出有 undo 的条目
5. 逐条：call <undo.cap>/<undo.op> <undo.args>
        成功 → 写一条 rollback.step（durable）→ 继续
        失败 → 停下，报 failed（命令退出码 1；错误里给失败步骤明细）
6. 汇总 { rolledBack, skipped, failed }
```

`--dry-run` 只做第 1–4 步，返回计划不执行。

## 3. 崩溃可续

进度不写本地文件，而是**写回审计流**（`op:"rollback.step", phase:"done", ref:<条目 id>`）。
好处：

- 同一套 durable 保证，不会出现"进度丢了但改动做了"；
- 重跑时按 `ref` 跳过已完成的条目，天然幂等；
- 审计流保留期一到，进度和账本一起过期——语义一致。

## 4. 调用方式

| 入口 | 用法 |
|---|---|
| 命令行 | `pd rollback --task <任务号> [--dry-run]` |
| 能力 | `call { cap:"rollback.run", op:"run", args:{ task, dryRun } }`（pkgmgr 失败时调） |

## 5. 边界与已知限制

| 项 | 现状 |
|---|---|
| 机器层 undo（PATH / 环境变量 / 注册表 / 启动器 / link / home） | 已通过 `storage.wire.*` 与 `storage.home.*` 落地 |
| 存档点回滚（`--to <序号>`） | 已实现（见 [impl-generation.md](impl-generation.md)）；`--task` 与它互斥 |
| 审计流被真空 | 检测到 `truncated` 就拒绝执行，不半撤 |
| 回滚本身失败 | 停下并报告 `failed`；`pd rollback --task` 退出码 1 并列出失败步骤；`pkgmgr` 自动回滚时也会把 partial 写进错误 |
| 安全 | 只执行审计流里的 `{cap,op,args}`；条目 `_unit` 目前固定由 storage 写入，日志提供者不参与伪造 |

## 6. 测试

`tools/storage-e2e.py` 的回滚组（两个日志实现各跑一遍，各 62 项）：

- 装 v2 → 记录/指针都到 v2；
- `--dry-run` 返回计划且**什么都没改**；
- 执行回滚 → 记录回 v1、指针回 v1、store 目录还在；
- 审计流里出现 `rollback.step`；
- 再回滚一次 → `rolledBack: 0`（幂等）。

此外整条链路还覆盖：存储写审计前必须等 `call.ok`（fsync），
日志提供者挂掉时安装会失败而不是"装了没记上"（由 `storage` 的错误路径保证）。
