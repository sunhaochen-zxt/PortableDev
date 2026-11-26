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
- 记录同时标记捕获时的机器（`wiring before.machine`，来自 hello.ok 的 machineId）；
  换机器后 clean/remove 只删本机接线，不把安装机捕获的原值写回本机；
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

`tools/wire-e2e.py`：**43 项**。

| 组 | 断言 |
|---|---|
| 安装带接线的包 | 退出码、`wiringChanges`、PATH 条目、环境变量展开、注册表值、启动器、current 联接 |
| `pd apply` | 退出码 0、`changed: 0`（幂等） |
| `pd clean` | 退出码 0、PATH/环境变量/注册表/启动器/联接全部消失 |
| 再 `apply` | 全部恢复 |
| 真实机器 | 环境变量真的写进用户环境，`clean` 后真的消失 |
| HKCU 走助手 | `tools/registry-e2e.py` 12 项：部署助手到测试盘后，装/clean/remove 的 HKCU 写入与还原全部经过 pd-elevate 的 --no-elevate 模式（本机 `reg.exe` 被策略禁用也能跑） |
| HKCU 走助手 | `tools/registry-e2e.py` 12 项：部署助手到测试盘后，装/clean/remove 的 HKCU 写入与还原全部经过 `pd-elevate --no-elevate`（本机 `reg.exe` 被策略禁用也能跑） |

## 7. 还没做的

| 项 | 说明 |
|---|---|
| 系统 PATH | 仍只写用户 PATH；系统 PATH 需要提权批次 + 广播校验 |
| 提权批次的一致性 | HKLM/盘外路径由 `pd-elevate` 执行；ledger 在改动之后才写（崩在中间会留下无账可撤的改动） |
| 启动器模板 | 现在只生成最小 `.cmd`；图标、无窗口、`runAs` 等字段未处理 |
| 存档点 | 已实现（见 [impl-generation.md](impl-generation.md)）：接线状态随每次 apply 入库 |
| 广播设置变更 | 环境变量用 .NET API 写，系统会广播；PATH 同理。未做显式 `WM_SETTINGCHANGE` 校验 |

## 8. 提权与 HKLM（2026-11-25）

- HKLM 写入、以及稳定根/用户目录之外的 `link`/`copy` 目标，由 `src/PortableDev.Elevate`
  的 `pd-elevate` 助手执行：一次请求文件列出一批 op，结果文件回传每个 op 的 `before`；
  非管理员时助手用 `runas` 自重启（UAC），取消则整批失败；
- **请求完整性**：请求文件落盘后算 SHA-256，经 `--sha256 <hex>` 传给提权实例；提权侧读同一份字节、
  先校验摘要再解析执行（UAC 弹窗等待期间被替换 → 直接拒绝并返回 2）；结果路径通过 `--result` 传入且必须
  落在 `<盘>\state\tmp` 下，结果用独占创建写入（不跟随预置文件/链接）；注册表写入限定在 `HKCU\Software\*` / `HKCU\Environment` 与 `HKLM\SOFTWARE\*` / `HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment`（禁止 `Run`/`Policies`/`IFEO`/`Winlogon`/`Windows Defender`），文件操作拒绝盘符根目录与 Windows 系统目录且不递归跟随重解析点；
- **HKCU 读写也优先走同一助手**（--no-elevate，不需要 UAC）：机器上 `reg.exe` 被策略禁用时仍能读写
  HKCU；只有助手不存在/起不来时才回退 `reg.exe`；
- 部署要保证 `dotnet` 在扩展的 PATH 上（或用自包含发布），并同时放 `pd-elevate.runtimeconfig.json` /
  `pd-elevate.deps.json`（只放 dll 会报 hostpolicy 缺失）；
- **部署**：把构建出的 `pd-elevate.exe`（或 `pd-elevate.dll` + `dotnet`）放到
  `<盘>\app\tools\`；storage 按这个路径查找，找不到时 HKLM/盘外写入返回 `ERR_UNSUPPORTED`；
- **测试**：`state\wire\fake-mode` 下 HKLM/link/copy 全部走 fake 状态，不需要 UAC；
  助手自身支持 `--fake <state.json>` 无 UAC 自测；真实 UAC 用 `tools/elevation-selftest.py` 手动跑；
- 已知限制：提权批次里“机器已改、ledger 尚未写”的窗口存在（崩在中间会留下无账可撤的改动）；
  `script.elevated: true` 的包脚本未实现。
