# 存档点实现说明（generations / rollback --to）

> 状态：可用。每次成功接线存一档；`pd rollback --to <序号>` 回到某一档，并再存一档。
> 相关文档：[architecture.md](architecture.md) 9.4、[commands.md](commands.md) 5.3、[impl-wire.md](impl-wire.md)。

---

## 1. 存档点里有什么

`state\\generations\\<序号>-<时间>.json`，只有几 KB：

```json
{
  "schema": 1, "seq": 3, "at": "2026-11-24T20:10:00+08:00",
  "packages": { "pkga": { ...完整记录... }, "pkgb": { ...完整记录... } },
  "wiring": {
    "path": ["C:\\Dev\\current\\pkga\\bin", "C:\\Dev\\current\\pkgb\\bin"],
    "env": { "A_MARK": "A", "B_MARK": "B2" },
    "registry": [ { "hive":"HKCU", "key":"Software\\X", "name":"Y", "type":"REG_SZ", "value":"1" } ],
    "launchers": [ { "path": "C:\\Dev\\pkgb.cmd", "content": "@echo off..." } ]
  }
}
```

**不复制任何内容**：包内容在 `store` 里不可变、按哈希命名，快照只记引用（见 architecture 9.4）。
所以保留几十档的成本几乎为零——真正的磁盘成本是“升级后旧版本还没 `pd gc`”的那段时间。

## 2. 什么时候写

- 每次 `apply`（含安装后的自动接线）**有实际改动**时写一档；
- 没有改动且已有存档时不重复写；
- 保留 `gc.keepGenerations`（默认 50）档，超出删最旧的文件（不删 store）。

## 3. `pd rollback --to <序号>` 做什么

```
1. 读快照，检查每个包的 store 目录还在（不在 → ERR_PKG_NOT_FOUND / 20，不半撤）
2. 记录对齐：快照里没有的包 → 拆接线 + 删记录（记账）
             快照里有但记录不同 → 写回旧记录（记账）
3. apply：重建 current 联接与 PATH / 环境变量 / 注册表 / 启动器（幂等）
4. 再写一档新存档（Nix 风格：回滚也是一次前进，这次回滚本身还能回滚）
```

- **不动 store**：目录一律保留，没用到的交给 `pd gc`；
- 回滚只影响“接线 + 记录”，不是重装；
- `--dry-run` 只报计划；
- `--list` 列所有存档（序号、时间、包含的包）。

## 4. 与 `--task` 的区别

| | `--to <序号>` | `--task <任务号>` |
|---|---|---|
| 依据 | 存档点快照（声明式） | 审计流里该任务的条目（命令式） |
| 恢复范围 | 整机接线 + 记录 | 该任务改过的东西 |
| 能追多久 | 保留多少档就多久 | `log.auditRetentionDays`（默认 7 天） |
| 典型场景 | 用户主动回退 | 安装失败清理 |

## 5. 测试

`tools/generation-e2e.py`：**29 项**（fake 机模式，不碰真实机器）。

- 装 A、B1、B2 三代会生成三档；
- `--list` 数量正确；
- `--to 2`：B 记录回到 1.0.0、`B_MARK` 回到 B1、current 指回 B1、并追加新档；
- `--to 1`：B 的记录 / 环境变量 / current 联接全部消失，A 不受影响，store 目录都还在；
- 序号不存在 → 20；store 被 gc 删掉 → 20；
- `--dry-run` 不改任何东西。

## 6. 还没做的

| 项 | 说明 |
|---|---|
| 存档点包含家目录联接 | 家目录联接还没实现 |
| 跨机器迁移存档点 | 快照里含绝对路径（稳定根），换机器需重算 |
| `pd rollback` 的交互确认 | 现在直接执行；破坏性操作提示还没做 |
