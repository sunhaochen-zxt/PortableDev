# 接线层实现说明（apply / clean）

> 状态：可用。PATH、用户环境变量、HKCU 注册表、启动器都能写、能查、能撤，全部记账。
> 相关文档：[architecture.md](architecture.md) 7 / 9.2、[commands.md](commands.md) 5.5、[impl-storage.md](impl-storage.md)。

---

## 1. 它是什么

“接线”= 让盘上的内容在机器上真正可用的那层派生状态：

| 接线 | 落点 | 撤销 |
|---|---|---|
| PATH | 用户 PATH（`HKCU\\Environment\\Path`） | 移除条目 |
| 环境变量 | 用户环境变量 | 删回原值或删除 |
| 注册表 | `HKCU` 下的值 | 写回原类型+原值，或删除 |
| 启动器 | 稳定根下的 `.cmd` | 删除文件 / 写回旧内容 |

**只有 storage 执行这些操作**，而且每一次都先写审计条目（带 `undo`）再动手——
所以 `clean` 能用同一套机制撤，`rollback` 扩展也能撤。

## 2. 原语（`storage.wire` 能力）

| 操作 | 参数 | 说明 |
|---|---|---|
| `env.set` / `env.delete` / `env.get` | `name`、`value` | 用户环境变量 |
| `path.add` / `path.remove` / `path.get` | `dir` | 用户 PATH，按分号维护，去重不分大小写 |
| `registry.set` / `registry.delete` / `registry.get` | `hive`、`key`、`name`、`type`、`value` | 只允许 HKCU；HKLM 需要提权，明确拒绝 |
| `launcher.write` / `launcher.remove` | `path`、`content` | 启动器文件 |
| `apply` | `only?` | 按记录重建接线 |
| `clean` | — | 按记录拆掉接线 |

所有写操作都是**幂等**的：目标值已等于现值就不写、不记账（所以重复 `pd apply` 是 0 changes）。

## 3. `pd apply` / `pd clean`

```
apply： 读所有记录 → 每个包：current 联接、path 条目、env、注册表、启动器
        每条改动都先写审计（undo 指回原值）→ 记录 last-apply（给人看）
clean： 读所有记录 → 逐项还原（移除 PATH 条目、删环境变量、删注册表值、删启动器）
        → 删除 current 联接 → 清 last-apply
```

- `apply` 幂等，第二次跑 `changed: 0`；首次 apply 会把机器上的原值（env / registry / launcher 内容、
  PATH 里原本有没有该目录）记进记录的 `wiring.before`；
- `clean` **只拆接线**：包、记录、store、用户数据都不动；有 `wiring.before` 时**还原安装前的值**
  （用户原有的同名环境变量 / 注册表值/启动器不会被删掉）；fake 机模式下捕获的值不会用到真机上；
- 两者都可以 `--dry-run`；
- `clean` 之后 `apply` 能把接线原样建回来（记录里存着声明）。

## 4. 占位符

记录里的接线值支持占位符，`apply` 时展开：

| 占位符 | 展开成 |
|---|---|
| `{{ROOT}}` | 稳定根（默认 `C:\\Dev`） |
| `{{STICK}}` | 盘根 |
| `{{ID}}` / `{{VERSION}}` | 包名 / 版本 |

所以注册表、启动器、环境变量里不会出现盘符（原则 2）。

## 5. 测试模式：fake 机

真实机器测试很危险（会改 PATH/HKCU）。所以：

- `state\\wire\\fake-mode` 文件存在时，storage 把接线写进 `state\\wire\\fake.json`，
  **完全不碰机器**；
- 测试断言假状态的内容，逻辑与真实路径完全同一套代码；
- 另外单独做一次**真实机器冒烟**：只装一个声明了唯一环境变量
  （`PD_TEST_WIRE_MARKER`）的包，验证真实写入与 `clean` 撤销，跑完强制清理。

## 6. 测试

`tools/wire-e2e.py`：**28 项**。

| 组 | 断言 |
|---|---|
| 安装带接线的包 | 退出码、`wiringChanges`、PATH 条目、环境变量展开、注册表值、启动器、current 联接 |
| `pd apply` | 退出码 0、`changed: 0`（幂等） |
| `pd clean` | 退出码 0、PATH/环境变量/注册表/启动器/联接全部消失 |
| 再 `apply` | 全部恢复 |
| 真实机器 | 环境变量真的写进用户环境，`clean` 后真的消失 |

## 7. 还没做的

| 项 | 说明 |
|---|---|
| HKLM 写入 | 需要提权批次，等 `elevation` 流程 |
| 系统 PATH | 同上 |
| 启动器模板 | 现在只生成最小 `.cmd`；图标、无窗口、`runAs` 等字段未处理 |
| 存档点 | 已实现（见 [impl-generation.md](impl-generation.md)）：接线状态随每次 apply 入库 |
| 广播设置变更 | 环境变量用 .NET API 写，系统会广播；PATH 同理。未做显式 `WM_SETTINGCHANGE` 校验 |
