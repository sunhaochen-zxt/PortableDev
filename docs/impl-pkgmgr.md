# 包管理实现说明（pkgmgr / download / unpack / sources）

> 状态：可用。四种来源（`.pdpkg`、目录、URL、**源索引按包名**）都能装到 store 并建 current 指针；
> `pd upgrade` / `pd remove` / `pd list` 已实现；失败自动回滚；包脚本走受限请求协议；
> 家目录/接线/HKLM 由 storage 执行。
> 相关文档：[commands.md](commands.md) 5.1、[package-spec.md](package-spec.md)、[index-spec.md](index-spec.md)、
> [impl-storage.md](impl-storage.md)、[impl-rollback.md](impl-rollback.md)、[impl-wire.md](impl-wire.md)。

---

## 1. 扩展分工

| 扩展/模块 | 职责 | 不做什么 |
|---|---|---|
| `pkgmgr` | 编排安装/升级/卸载/列表、读说明书、跑断言与包脚本、决定失败回滚 | 不直接碰 store/租界（都经 storage） |
| `pkgmgr/sources.js` | `config/sources.json`、索引缓存（`state/sources/<源>`）、版本范围解析 | 不碰 store |
| `pkgmgr/scripts.js` | 受限脚本协议（stdout 请求 / stdin 回执）、超时与进程树清理 | 不直连内核 |
| `download` | URL → blob（带哈希）或任意 URL → 指定路径（`get`，供索引/清单） | 不安装、不解压 |
| `unpack` | 解压到指定目录，拒绝 `..`/绝对路径/链接/超限 | 不下载、不判断内容 |

三者靠内核 `call` 按能力名路由（spec 5.6），互不知道对方是谁。

## 2. 一次安装的流程

```
pd install <目标>
 1. resolve   来源判定：
              URL（如需 --sha256）→ 下载 blob
              本地 .pdpkg / 目录 → 直接用
              包名[@范围] → sources.resolve（dir/http/git 源，优先级+范围+yanked/平台/渠道过滤）
                            → materialize 到 cache\\blobs\\<sha256>
 2. 读说明书   .pdpkg/URL 先解到租界读 package.json；目录直接读
 3. 预演       --dry-run 在解析后返回计划（按名安装不下载 blob）
 4. 解来源     ① files\\ → 直接用 ② source.url → download.fetch → unpack.extract ③ 目录 → 复制进租界
 5. 包脚本     script 段（策略：本地默认跑，网络源需 --allow-scripts，--no-scripts 优先）
 6. 文件断言   exists / missing / json / fileHash
 7. 收编       storage.adopt commit（算指纹 → 写审计 → rename → 写记录）
 8. 家目录/接线 storage.wire apply（env/PATH/HKCU/HKLM/启动器/data.link/data.copy、{{HOME}} 展开）
 9. 提交       storage.ledger commit → 清理租界；失败时 rollback.run 撤销该任务的账
```

**路径基准**：相对路径按**调用方 shell 的 cwd** 解析（`req.ctx.cwd`）。

### 2.1 升级 / 卸载 / 列表

- `pd upgrade`：先 `storage.record.list` → 源解析目标版本（`--to` 可显式降级或同版本重装，否则只升）；
  每个包先 `storage.uninstall` **只摘旧版本**（`skipGeneration:true`，保留 store 目录和家目录数据），
  再复用 `install()` 装新版本；两步共用同一个审计任务 `<任务号>-up-<包>`；
  **uninstall 或 install 任一步失败都会 `rollback.run` 撤销该包**（不再只覆盖 install 失败）。
  安装期开关（`--allow-scripts`/`--no-scripts`/`--force`/`--channel`/`--prerelease`/
  `--refresh`/`--offline`）会透传给内层安装。
- `pd remove`：调 `storage.uninstall`（默认保留家目录；`--purge` 把数据 rename 到 `state/trash`，
  gc 前可用 `pd rollback --task` 恢复），每包审计任务 `<任务号>-rm-<包>`，失败时 rollback 并检查
  部分失败；成功后补 `ledger commit`，metrics 里给每个包带审计任务号 `task`。
- `pd list`：读记录 + 检查 current 指针；`--outdated` 只读本地索引缓存解析最新版，不联网刷新；
  解析失败与“已最新”用 `latestError` 区分。
- 批量语义：一个包失败或名字没装都不影响其他包；最后汇总，有 missing 时退出码 20
  （存在的那部分仍然执行）。`--dry-run` 解析失败按 20；一个包都没装时按名 upgrade → 20；
  remove 的 `--dry-run` 有 missing → 20。
- `--pin`：install 成功后给记录写 `pinned:true`；`upgrade --all` 跳过 pinned，显式点名升级会透传
  `--pin` 保持锁定；`pd list` metrics 里带 `pinned`。
- 缺值的取值开关（`--to`/`--source`/`--as` 等）由 parseFlags 直接报 2，命令层的异常由兜底包装
  转成 done(2)，不会让扩展崩掉。
- `install(message, options)` 支持注入 `taskId` / `io`（默认 `commandIo(message)`），
  这样升级能在同一审计任务里跑完整安装流程并转发事件。

## 3. 源与索引（index-spec）

- `config\sources.json` 随盘走：`{sources:[{name,kind,priority,enabled,path|url|repo,ttl}]}`；
  默认自动加入内置源（`app\sources\<名>\index.json`）和 `<盘>\packages` 目录源；
- 源类型：`dir`（有 index.json 就用，没有就现场扫 `.pdpkg`）、`http`（缓存到
  `state\sources\<名>\index.json` + `meta.json`，TTL 默认 24h）、`git`（`gh:owner/repo` → raw index URL）、
  `scoop`（`gh:owner/bucket` → 下载 bucket zip → 清单转换，见 index-spec 1.1；支持 `branch` / `subdir` / `tarballUrl`）、
  `builtin`（随程序发布，只读）；
- 版本范围：`latest` / 精确版本 / `^` / `~` / `>= < ==` 组合；默认跳过 yanked、预发布
  （除非显式管道/版本，yanked 需 `--force`）、平台不符、minKernel 不满足；
- 多源：priority 小者优先，同优先级按声明顺序；`--source` 锁定单源；
- 离线：`--offline` 只用缓存（索引/blob），缺内容报 `ERR_OFFLINE_MISS`(23)；
- 命令：`pd source list|add|remove|enable|disable|refresh`、`pd index build|inspect`、
  `pd search`（只读缓存）、`pd fetch`（只下不装）。

## 4. 包脚本（package-spec 3.5）

- 入口相对**包根**（archive 的包根、目录包的源目录），cwd 是内容根；
- 策略：`--no-scripts` > `--allow-scripts` > `install.allowScripts`（`local`/`always`/`never`）；
  本地来源默认跑，网络来源默认不跑；
- 协议：stdout 一行一个 `{"type":"request","id","cmd","args"}`，pkgmgr 在 stdin 回
  `{"id","ok","data"|"error"}`；白名单：`write/copy/move/rm/fetch/unpack/assert/link/path.add/env.set/reg.set/reg.del/launch`；
- 一律拒：未知命令、`script.network=false` 时的 fetch、路径逃出内容根、`elevated:true`（见下）；
- 脚本任何请求失败后仍继续并 exit 0 → 安装失败（不静默忽略）；脚本 exit≠0 / 超时（默认 10m）→ 失败；
- 脚本拿不到内核管道（环境里没有 `PD_PIPE`），只能通过 pkgmgr 请求副作用；
- **Job Object 由 `proc` 扩展提供**（`proc.spawn`）：脚本进程放进 Job Object，
  限额来自 settings（`proc.processMemoryMB` 默认 1024、`proc.activeProcesses` 默认 64），
  `KILL_ON_JOB_CLOSE`（脚本退出/被超时杀掉后它派生、还没退出的子孙进程一起结束）；
  stdin/stdout/stderr 用命名管道直连（pkgmgr 建管道、proc 把可继承句柄交给子进程），
  请求协议不变、数据不经过内核；
- 超时由 proc 负责（到点 `TerminateJobObject`）；**没有回退路径**：proc 扩展不可用时装包直接报
  `ERR_EXT_MISSING`（20）。旧的 `pd-job` helper 已归档到 `archive/pd-job/`，不再构建/部署/维护。

## 5. 家目录、data 与提权

- `{{HOME}}` / `{{PERSIST}}` 展开为 `<稳定根>\home\<包>`；apply 前自动建
  `<盘>\home\<包>`（用户数据）并把它接到稳定根（junction）；
- `data.kind=link/copy`：link 建“机器路径 → 盘上目录”的联接；copy 复制一份，ledger 记录 undo；
  目标已存在时拒绝覆盖；
- HKLM、以及稳定根/用户目录之外的目标：storage 调 `pd-elevate`（`app\tools\pd-elevate.exe`）
  做一次提权批次；`state\wire\fake-mode` 下这些操作写 fake 状态，完全不需要提权；
- 已知限制：提权批次里“机器已改、ledger 尚未写”的窗口存在（崩溃可能留下无账可撤的改动）；
  `elevated:true` 的**包脚本**仍未实现（会返回 `ERR_UNSUPPORTED`）。

## 6. 错误与退出码

| 情况 | 错误码 | 退出码 |
|---|---|---|
| 说明书/索引不合法 | `ERR_PKG_INVALID` / `ERR_INDEX_INVALID` | 3 |
| 哈希不符（下载/本地） | `ERR_HASH` | 3 |
| 找不到包/源/版本 | `ERR_PKG_NOT_FOUND` | 20 |
| 显式版本被撤回 | `ERR_PKG_YANKED`（`--force` 可装） | 22 |
| 源连不上 / 网络错误 | `ERR_SOURCE_UNREACHABLE` / `ERR_NET` | 15 |
| 离线且缓存没有 | `ERR_OFFLINE_MISS` | 23 |
| 平台/格式不支持 | `ERR_PKG_PLATFORM` / `ERR_UNSUPPORTED` | 11 |
| 断言失败 | `ERR_ASSERT` | 12 |
| 脚本失败/超时 | `ERR_SCRIPT` / `ERR_TIMEOUT` | 1 / 25 |

## 7. 安全边界

- 解包前拒绝绝对路径/盘符/`..`/链接条目，解包后再查文件数与字节数，超限即删；
- 下载边写边算哈希，不符删临时文件；本地文件同样校验；索引文件走 `download.get` 后立即校验 schema；
- 所有解压/复制都在 storage 发的租界里，收编才进 store；
- 脚本路径必须留在包根/内容根内，未知请求一律 `ok:false`；
- 失败必回滚：pkgmgr catch 里调 `rollback.run`，再清理租界。
- PATH 风险：`pd run`/`pd shell` 会把所有已装包的 `current\` 前置进 PATH，恶意包可用同名 DLL 劫持其他包的程序（DLL 搜索顺序劫持）；缓解靠包来源可信，敏感程序尽量用绝对路径启动。

## 8. 测试

| 套件 | 项数 | 覆盖 |
|---|---|---|
| `tools/pkg-e2e.py` | 37 | 预演、.pdpkg/目录/远程/URL、哈希、穿越、symlink 归档、断言回滚、平台、本地 `--sha256` |
| `tools/source-e2e.py` | 34 | index build/inspect、dir/http 源、优先级、版本范围、yanked、offline、search、disable/remove、坏索引 |
| `tools/scoop-e2e.py` | 22 | scoop 源（zip/exe 转换安装、跳过规则）、requires 递归/冲突/循环/`--no-deps`/`--dry-run` 计划 |
| `tools/provides-e2e.py` | 34 | 虚拟包（已装优先/优先级选择/同优先级报错 21/范围约束）、replaces、conflicts 双向检查、卸载保护/`--force`/`--dry-run` |
| `tools/script-e2e.py` | 22 | 本地/网络脚本策略、--no-scripts、受限请求、env/reg/link、超时、未知命令、fetch 网络开关、内核隔离 |
| `tools/manage-e2e.py` | 93 | install/upgrade/remove/list、`--to` 升降级/重装、`--all`、`--dry-run`、`--purge`/`--keep-data`、升级安装失败回滚、home 包 remove/rollback、partial settings 保留期、任务号 metrics、错码 |
| `tools/proc-e2e.py` | 28 | proc 协议、`proc.spawn` + stdio 代理、list/info/kill、系统枚举/结束、admin 模式 实例、主题事件、真实安装脚本 |
| `tools/run-e2e.py` | 19 | `pd run`：记录解析、launch 声明、exec 通道、stdio 继承、退出码/--propagate、--json、`pd shell` |

运行：

```bash
DN="Z:/PortableDev/tools/dotnet-sdk/8.0.426/dotnet.exe"
"$DN" build src/PortableDev.Kernel/PortableDev.Kernel.csproj --no-incremental
python -u tools/pkg-e2e.py .run
python -u tools/source-e2e.py .run
python -u tools/script-e2e.py .run
python -u tools/manage-e2e.py .run-manage
python -u tools/proc-e2e.py
python -u tools/run-e2e.py
python -u tools/scoop-e2e.py
python -u tools/provides-e2e.py
```

## 8.1 只读与自检命令

| 命令 | 实现要点 |
|---|---|
| `pd doctor` | 只读检查：盘标记、随盘 .NET/Node、`app\baseline\extensions` 对比、记录 vs store、残留 intent、配置解析、稳定根、current 接线解析、PATH/环境变量/注册表接线、家目录联接、受限会话探针；`--deep` 追加 blob 缓存、源冲突（同 id 多源）、文件系统（PowerShell `Get-Volume`，NTFS）；`--fix` 做“从 baseline 覆盖内置扩展 + 删 done/坏 intent + storage `wire apply` 重建机器接线 + 清失效 PATH 条目”，修完重算再出报告（`metrics.fixed/issues/checks`），问题本身不算命令失败 |
| `pd selftest` | 读记录上的 `asserts`（安装时随记录保存，目录安装没有 `package.json` 也能测）；`--full` 追加 `selftest` 工作流；命令类断言经 `proc.spawn` 在 Job Object 里执行，失败 12、`--only` 未装 20 |
- 批量事务：`pd remove/upgrade` 加 `--atomic` 后整批全成功才保留；某个包失败时按 task 反向调用
  `rollback.run` 把已完成的卸载/升级逐条还原，再以首个错误返回（未加 --atomic 仍是旧的逐包语义）。
- 子命令粒度：`source list` 在 `ext.json` 里单独登记为只读并行命令，内核按最长前缀路由；
  pkgmgr 处理器只取 `cmd` 的第一个词选函数，argv 仍是去掉首段的参数（`["list", ...]`），
  所以 `source list` 与其它 `source` 子命令共用同一份解析。
| `pd info` | 只读详情：版本、store 路径、来源、固定状态、PATH/env/registry/launcher 接线、断言清单与已存工作流 |
| `pd outdated` | 对每个已装包调用 `sources().resolve` 比较版本；解析失败的包只计入 `skipped`，不影响退出码 |
| `pd env` | 打印包的 PATH 接线（`<稳定根>\current\<包>\...`）、当前进程 PATH、包声明的环境变量——和 `pd shell --profile` 对照用 |

## 9. 还没做的

| 项 | 说明 |
|---|---|
| scoop 转换 | 已支持 zip/单文件 exe、位数条目（含 32 位回退）、URL `#` 片段、bin/env/extract_dir/depends；仍不支持 persist（明确跳过）、shortcuts、msi、非 sha256 清单 |
| 依赖求解 | `requires` 递归安装（已装满足则跳过；冲突/循环报 24；`--no-deps` 跳过）；虚拟包（已装优先/优先级/同级报错 21）；`replaces` 先卸旧包（失败尽力恢复）；`conflicts` 双向检查报 24；卸载保护（被依赖时 24，`--force` 跳过） |
| 更多解包 | `7z` / `msi` |
| 脚本资源配额 | proc 提供内存/进程数上限，CPU 配额未做 |
| 批量事务 | 升级/卸载是逐包任务（失败只回滚该包），没有“整批全成功才生效”的事务语义 |
