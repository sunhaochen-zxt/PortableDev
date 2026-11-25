# PortableDev v2 规范：命令面

> 配套文档：[architecture.md](architecture.md)、[spec.md](spec.md)、[package-spec.md](package-spec.md)、[index-spec.md](index-spec.md)、[kernel.md](kernel.md)、[window.md](window.md)
> 本文管：**有哪些命令、每个命令改不改机器、离线能不能用、失败是什么退出码、输出长什么样**。
> 本文不管：命令内部怎么实现（那是各扩展的事）。

---

## 1. 通用形式

```
pd [全局开关] <命令> [参数...]
```

- `pd` 只解析**连接 / 呈现 / 调试类**开关（清单见第 2 节标注"壳"的开关），其余 argv **原样**
  转给内核，自己不解析业务参数（架构 3.8）；
- 每条命令最多带一个"主体"，其余是开关；
- 所有命令都支持 `--json`（输出 NDJSON 事件流）和 `--yes`（不交互）。

---

## 2. 全局开关

| 开关 | 作用 | 影响退出码吗 |
|---|---|---|
| `--json` | 输出结构化事件，不渲染 | 否 |
| `--yes` / `-y` | 所有交互默认"同意" | 否 |
| `--quiet` / `-q` | 只输出错误和最终结果 | 否 |
| `--verbose` / `-v` | 输出调试级日志 | 否 |
| `--offline` | **禁止联网**（不是禁止离线安装，见 4.2） | 会导致 `23` |
| `--refresh` | 强制刷新索引缓存 | 否 |
| `--no-color` | 关闭颜色 | 否 |
| `--root <path>` | 指定盘根（多块盘时用）；缺省按当前目录所在盘找 `state\stick.json` | 4 |
| `--timeout <dur>` | 单条命令的总超时 | 25 |
| `--trace` | 打印详细步骤（调试用） | 否 |
| `--trace-env` | 调试：环境变量写进独立临时文件，**不进 `state\`**（见 kernel.md 7.1） | 否 |
| `--help` / `-h` | 打印用法（等价 `pd help <命令>`） | 否 |
| `--raw` | 把紧跟的 JSON 参数当作一个原始请求发出（救急通道，架构 3.7） | 否 |
| `--propagate` | 用子进程退出码作为 `pd` 的退出码（`pd run` / `pd shell`） | 是 |

> **壳解析**：`--root` / `--json` / `--quiet` / `--no-color` / `--trace` / `--trace-env` / `--yes` /
> `--verbose` / `--propagate` / `--raw` / `--help` / `--`（连接、渲染、救急通道必须先由壳决定）；
> **内核解析**：`--offline` / `--refresh` / `--timeout` 等业务开关（随命令转发）。
> 两段式划分见第 1 节。
> 壳为了找管道只多读一个文件：`<root>\state\stick.json`（见 kernel.md 4.4）。

---

## 3. 退出码

全局统一，命令自己可以在 `ext.json` 里补充。

| 码 | 含义 | 典型场景 |
|---|---|---|
| `0` | 成功 | — |
| `1` | 内部错误 | 未分类的失败 |
| `2` | 参数或命令错误 | 打错命令、参数缺失 |
| `3` | 数据格式错误 | 包格式、索引格式、记录损坏 |
| `4` | 环境问题 | 找不到盘、盘根冲突 |
| `8` | 权限问题 | 需要提权但被拒 |
| `10` | 冲突/忙 | 另一个改机器的任务在跑 |
| `11` | 被拒绝 | 安全策略拒绝（不支持的能力） |
| `12` | 断言失败 | 装完检查不过 |
| `15` | 网络失败 | 源连不上、超时、代理错 |
| `20` | 找不到 | 包、版本、源、存档点不存在 |
| `21` | 需要指定 | 多个源提供同名包且未指定 |
| `22` | 已撤回 | 版本 yanked |
| `23` | 离线缺内容 | `--offline` 且缓存里没有 |
| `24` | 依赖未满足 | 依赖装不上 |
| `25` | 超时 | `--timeout` 到点、扩展无响应 |
| `130` | 被取消 | `Ctrl+C` / 断连 |

规则：

- **成功只有 `0`**，其余都算失败；
- 批量操作只要有一个失败，整体返回**第一个失败的退出码**；
- 退出码和错误码（`ERR_*`）分开：退出码给 shell 用，错误码给程序用。

---

## 4. 命令总表

「独占」= **写共享状态**（`store` / `state` / `cache` / `config` / `staging` / `current`），
或改机器（PATH、环境变量、注册表、联接）。
写到**用户指定路径**（`--out`、`packages\`、导出文件）不算独占——那不会破坏别人的状态。
独占命令同一时刻只跑一个，其余排队；只读命令才并行。
「离线」= 盘上内容齐全时能不能用。

> **实现状态（2026-11-25）**：本节是目标命令面。已注册命令：`version` / `help` / `complete` /
> `kernel status|ps|ext list|restart|stop|log` / `config list|get|set|unset` / `log` /
> `install` / `remove` / `upgrade` / `list` / `rollback` / `apply` / `clean` / `gc` /
> `source` / `index` / `search` / `fetch`。
>
> **未实现（2026-11-25）**：`run` / `shell` / `ext` / `doctor` / `recover` / `outdated` /
> `info` / `ui` / `env` / `selftest` / `adopt` / `pkg` / `bundle`。敲了会得到 `ERR_UNKNOWN_COMMAND`（2）。

### 4.1 信息类

| 命令 | 作用 | 独占 | 离线 | 主要参数 | 失败码 |
|---|---|---|---|---|---|
| `pd` | 无参数时显示状态摘要 | 否 | 能 | — | — |
| `pd version` | 版本信息 | 否 | 能 | `--short` | — |
| `pd help [命令]` | 帮助 | 否 | 能 | `--json` | 2 |
| `pd complete <前缀>` | 补全候选 | 否 | 能 | `--json` | 2 |
| `pd log` | 查日志（由日志提供者实现） | 否 | 能 | `--tail` `--since` `--until` `--level` `--unit` `--pkg` `--grep` `--follow` `--json` `--disk-usage` `--vacuum` | 2/20 |
| `pd env` | 看托管的环境变量与 PATH | 否 | 能 | `--diff` | 4 |

### 4.2 包管理

| 命令 | 作用 | 独占 | 离线 | 主要参数 | 失败码 |
|---|---|---|---|---|---|
| `pd install <目标>` | 安装 | **是** | 见 5.1 | `--from-dir` `--as` `--dry-run` `--force` `--source` `--allow-scripts` `--no-scripts` | 2/3/4/8/10/11/12/15/20/21/22/23/24/25 |
| `pd remove <包>` | 卸载 | **是** | 能 | `--purge` `--keep-data` `--dry-run` | 2/3/4/8/10/20 |
| `pd upgrade [包...]` | 升级 | **是** | 需缓存 | `--all` `--dry-run` `--to <版本>` | 2/4/8/10/11/12/15/20/22/23 |
| `pd rollback [目标]` | 回滚到某个存档点 | **是** | 能 | `--to <序号>` `--list` `--dry-run` | 2/3/4/8/10/20 |
| `pd list` / `ls` | 已装列表 | 否 | 能 | `--outdated` `--json` | — |
| `pd info <包>` | 包详情 | 否 | 能* | `--versions` `--json` | 20 |
| `pd search <词>` | 搜索源 | 否 | 能* | `--source` `--limit` `--installed` | 2/15 |
| `pd outdated` | 有更新的包 | 否 | 需缓存 | `--json` | 15 |
| `pd fetch <包>` | 只下载不安装（预取） | 否（只写自己那个 blob） | 不能 | `@版本` `--from-dir` `--force` | 15/20/23/25 |
| `pd gc` | 清理无用版本和缓存 | **是** | 能 | `--keep <n>` `--blobs` `--dry-run` `--force` | 2/3/10 |

> `能*`：能查到**本地已知**的信息（已装的、缓存过的源）。没刷新过索引时，
> 只显示本地记录里有的，并在输出里注明"索引未刷新，可能不全"。

### 4.3 环境（机器层）

| 命令 | 作用 | 独占 | 离线 | 主要参数 | 失败码 |
|---|---|---|---|---|---|
| `pd apply` | 重建接线（PATH/环境变量/联接/启动器） | **是** | 能 | `--dry-run` `--force` | 3/4/8 |
| `pd clean` | 撤销 apply 改过的东西 | **是** | 能 | `--dry-run` `--keep-data` | 3/8 |
| `pd doctor` | 体检 | 否 | 能 | `--deep` `--json` | 3/4 |
| `pd doctor --fix` | 体检并修能修的 | **是**（改机器） | 能 | `--deep` `--json` | 3/4/8/10 |
| `pd selftest` | 环境自检 | 否 | 能 | `--full` `--only <包>` | 12/20 |
| `pd adopt <目录>` | 把现有工具收编成包 | **是** | 能 | `--id` `--version` `--exe` `--copy` `--dry-run` | 2/3/4/8/10/20 |
| `pd pkg pack <包>` | 把已装的包打回 `.pdpkg` | 否 | 能 | `--out` `--with-files` | 3/20 |

### 4.4 源与索引

| 命令 | 作用 | 独占 | 离线 | 主要参数 | 失败码 |
|---|---|---|---|---|---|
| `pd source list` | 看源 | 否 | 能 | `--json` | — |
| `pd source add <名> <地址>` | 加源 | **是**（写 `sources.json`） | 不能 | `--kind` `--priority` `--ttl` | 2/3/15 |
| `pd source remove <名>` | 删源 | **是** | 能 | — | 20 |
| `pd source enable/disable <名>` | 启停源 | **是** | 能 | — | 20 |
| `pd source refresh [名]` | 刷新索引 | **是**（写索引缓存） | 不能 | `--all` | 3/15/20 |
| `pd index build <目录>` | 从目录生成索引 | 否（写 `--out` 指定文件） | 能 | `--out` `--name` `--priority` | 2/3 |
| `pd index inspect <文件>` | 看索引内容 | 否 | 能 | `--json` | 3 |

### 4.5 内核

| 命令 | 作用 | 独占 | 离线 | 主要参数 | 失败码 |
|---|---|---|---|---|---|
| `pd kernel status` | 内核是否在跑、连不连得上 | 否 | 能 | `--json` | 4 |
| `pd kernel ps` | 各扩展进程 | 否 | 能 | `--json` | — |
| `pd kernel ext list` | 装了哪些扩展 | 否 | 能 | `--json` | — |
| `pd kernel restart <名>` | 重启扩展 | 否 | 能 | — | 20 |
| `pd kernel stop` | 停止内核 | 否 | 能 | `--force` | 4 |
| `pd kernel log` | 内核自己的兜底日志 | 否 | 能 | `--tail <n>` | — |
| `pd recover` | 处理上次崩溃留下的 intent | **是** | 能 | `--adopt` `--drop` `--dry-run` | 3/4/10 |

### 4.6 运行与外壳

| 命令 | 作用 | 独占 | 离线 | 主要参数 | 失败码 |
|---|---|---|---|---|---|
| `pd run <包> [参数...]` | 用该包的环境启动程序 | 否 | 能 | `--` 分隔 `--propagate` | 20/1 |
| `pd shell` | 开一个环境齐备的终端 | 否 | 能 | `--profile msvc` `cmd` `--propagate` | 4 |
| `pd bundle export` | 导出环境清单 | 否 | 能 | `--out` `--lock` | 3 |
| `pd bundle install <文件>` | 按清单装 | **是** | 见 5.1 | `--from-dir` `--dry-run` | 同 install |

### 4.7 配置

| 命令 | 作用 | 独占 | 离线 | 主要参数 | 失败码 |
|---|---|---|---|---|---|
| `pd config list` | 列出所有配置项和当前值 | 否 | 能 | `--json` | — |
| `pd config get <键>` | 读一个配置 | 否 | 能 | `--file` | 2/20 |
| `pd config set <键> <值>` | 改一个配置 | **是**（写 `config\settings.json`） | 能 | `--dry-run` | 2/3/10 |
| `pd config unset <键>` | 恢复默认值 | **是** | 能 | — | 2/20 |

### 4.8 扩展

| 命令 | 作用 | 独占 | 离线 | 主要参数 | 失败码 |
|---|---|---|---|---|---|
| `pd ext list` | 列出已装扩展（版本、来源、是否被覆盖） | 否 | 能 | `--json` | — |
| `pd ext update <名> <目录\|zip>` | 换一个新版本（失败自动回退） | **是**（写 `extensions\`） | 能 | `--force` | 2/3/10/20 |
| `pd ext rollback <名>` | 回退到上一版 | **是** | 能 | — | 20 |

### 4.9 界面

| 命令 | 作用 | 独占 | 离线 | 主要参数 | 失败码 |
|---|---|---|---|---|---|
| `pd ui pages` | 列出所有页面（给窗口壳用） | 否 | 能 | `--json` | 3 |

---

## 5. 每条命令的细节

### 5.1 `pd install`

```
pd install <包名 | .pdpkg 路径 | 目录 | URL> [开关]
```

| 开关 | 作用 |
|---|---|
| `--from-dir <目录>` | 从这个目录找离线包 |
| `--as <名字>` | 装成另一个名字 |
| `--dry-run` | 只显示计划，什么都不改 |
| `--force` | 允许装已撤回版本（yanked）、覆盖同名冲突 |
| `--allow-scripts` | 允许跑包脚本（默认不允许从网络源来的包跑） |
| `--no-scripts` | 一律不跑脚本（优先于 `--allow-scripts` 与配置 `install.allowScripts`） |
| `--source <源>` | 指定从哪个源解析 |
| `--pin` | 预留：当前解析后本来就把精确版本交给安装，没有额外的漂移判定 |
| `--sha256` | 直接给 URL 包的哈希（URL 来源必需；按名从索引安装时由索引提供） |

**输出**：

- 文本：一段计划 + 进度 + 结果摘要；
- `--json`：`step` / `progress` / `log` / `done` 事件。

**失败语义**：任何一步失败 → 只回滚这个包，其他包不受影响（批量时）。
失败时 `pkgmgr` 会调用 `rollback` 扩展撤销该任务的账（见 spec 5.3.2）。

**当前实现范围**（其余见 [impl-pkgmgr.md](impl-pkgmgr.md)）：

- 支持：`.pdpkg`、包目录、URL 直链（需 `--sha256`）、**按包名从源索引解析**（`name@版本范围`、
  多源优先级、yanked、离线缓存、`--source`/`--refresh`/`--offline`；`--pin` 为预留）；
- 支持：包脚本（受限请求协议；本地默认跑、网络源需 `--allow-scripts`，`--no-scripts` 优先）、
  PATH/环境变量/HKCU/HKLM/启动器接线、`data.link`/`data.copy`、`{{HOME}}` 家目录联接；
- 支持：`pd upgrade` / `pd remove` / `pd list` 生命周期命令（见 5.2）；
- 未支持：`scoop` 源、依赖递归安装、`7z`/`msi` 解包、命令类断言、`elevated:true` 的包脚本；
- 断言目前只跑文件类（`exists`/`missing`/`json`/`fileHash`）。

### 5.2 `pd upgrade` / `pd remove` / `pd list`

三条命令都基于“记录 + 存档点”工作：**每个包一个审计任务**，批量时某包失败只回滚该包，
其他包继续，最后汇总（退出码取首个错误的映射）。

#### 升级

```
pd upgrade [包...] [--all] [--to <版本>] [--dry-run]
```

1. 读已装记录（`--all` 选全部，否则按包名；名字没装过 → 20；一个包都没装时按名升级也 → 20）；
2. 从源索引解析目标版本：不给 `--to` 时取最新且**只升不降**，没有更新就跳过（打印 “up to date”）；
   `--to` 是显式目标，可升可降、**同版本也会重装**；`--to` 必须给出包名（不能与 `--all` 同用）；
3. 真跑：先 `storage.uninstall` **只摘旧版本**——拆接线、清 current、删记录，
   但**不删 store 目录、不删家目录、不写存档点**；
4. 再复用 `pd install` 的完整流程装新版本（staging → 脚本 → 断言 → 收编 → 接线）；
   安装期的 `--allow-scripts` / `--no-scripts` / `--force` / `--channel` / `--prerelease` /
   `--refresh` / `--offline` 会透传给这次安装；
5. 成功后写一个存档点；**uninstall 或 install 任一步失败**都会 `rollback.run` 撤销该包
   （含把刚做的 uninstall 一起撤回），其他包继续。

下载/解包/断言/脚本任何一步失败都只影响该包。旧版本的 store 目录不会被立即删：
它还可能被保留中的存档点引用，`pd gc` 只有在没有任何记录/保留存档点引用它时才删。
家目录数据升级后继续使用。`--dry-run` 只打印 包/当前/目标/动作 计划；解析失败或目标版本
不存在 → 20（dry-run 也一样），记录不动。

#### 卸载

```
pd remove <包>... [--purge] [--keep-data] [--dry-run]
```

- 默认（= `--keep-data`）：拆机器层接线、清 current、删记录；**盘上 `home\<包>` 保留**，
  重装时接着用；
- `--purge`：把 `home\<包>` 原子 rename 到 `state\trash\<任务>-<包>-<时间>` 再删记录；
  在 gc 清掉 trash 之前，`pd rollback --task <任务号>` 可以把它原样 rename 回来；gc 之后不可恢复；
  与 `--keep-data` 同时给 → 2；
- store 目录不立即删（留给 `pd gc` 判断是否还有别的引用）；
- 包不存在 → 20；部分不存在时其余照常卸载，汇总里标 `not installed: ...`；
  `--dry-run` 且全部不存在 → 20；
- `--dry-run` 只打印 包/版本/家目录/动作 计划；每个包成功后写存档点，失败时 `rollback.run`
  撤回该包；metrics 里给每个包带上审计任务号（`task`），可直接用于 `pd rollback --task`。

#### 列表

```
pd list [--outdated] [--json]
```

- 表：包 / 版本 / 最新 / current（`current\<包>` 指针是否存在）；
- `--outdated` 用**本地索引缓存**解析每个包的最新版（不联网刷新；缓存缺失显示 `?`），
  并统计 outdated 数；
- `--json` 的 metrics：`{packages:[{package,version,latest,latestError,current,storePath}],outdated}`；
  `latestError` 非空表示解析失败（表里显示 `?`），与“已是最新”区分。

### 5.3 `pd rollback`

```
pd rollback [--to <序号> | --task <任务号>] [--list] [--dry-run]
```

- `--list` 列存档点（序号、时间、包含的版本）；
- 不带 `--to` 时回到上一档；
- 只挪 `current` 指针 + 还原环境变量/注册表/启动器，**不动 store**；
- `--task`：按任务撤销。倒序执行该任务审计条目里的 `undo`，已做过的步骤再跑会跳过；
  不限于失败的事务（成功的任务也能撤），但**不动 store**，孤儿目录交给 `pd gc`；
  由 `rollback` 扩展执行（见 spec 5.3.2）；与 `--to` 互斥；
- `--to`：回到某个存档点（整机接线快照）。按快照改写记录与接线，**store 不动**，并追加一档新存档；
  实现见 [impl-generation.md](impl-generation.md)；
- 账本条目超出保留期（`log.auditRetentionDays`）→ `20`，明确说"账本已过期"，不半撤；
- 序号不存在 → `20`；目标版本已被 `gc` 删掉 → `20`，`--list` 里会标出来；
- 回滚是"回到那一档的接线"，不是"重装"：失败只影响接线，不改 store。

### 5.4 `pd gc`

```
pd gc [--keep <n>] [--blobs] [--dry-run] [--force]
```

"被引用"的定义：

| 类型 | 什么时候算被引用 |
|---|---|
| `store\` 里的版本 | 被某个存档点引用、被 `current` 指向，或被在途 intent 的 target 指向 |
| 已装记录 | 只引用它**当前**指向的那个版本；历史版本不因记录而保留 |
| `cache\blobs` 里的文件 | 被任何已装包或任何存档点的 `hashes.archive` 引用 |
| `staging\` | 只有当前有任务在跑或持有租界的才留，其余一律删 |
| 存档文件 | 超过 `--keep`（或配置 `gc.keepGenerations`）时删最旧；先裁剪再算引用 |

默认保留最近 **N=50** 档（可配，见 `config\settings.json`，也可用 `--keep` 临时指定）。
`--dry-run` 只统计不删除；`--keep` 同时裁剪存档文件本身；`--force` 在记录损坏（gc 默认拒绝执行）时
仍强制清理；命令由 storage 扩展注册。
实现见 [impl-storage.md](impl-storage.md) §8。
`--dry-run` 只列不删。

`--blobs` 的语义：**额外清理缓存里的 blob**（默认不动缓存，只清 `store\` 和 `staging\`）。
不带 `--blobs` 时，blob 只在"没有任何已装包引用它"时才可能被清（默认行为是保留，
因为它就是离线安装的本钱）。

### 5.5 `pd apply` / `pd clean`

- `apply`：按记录重建 PATH、环境变量、`current` 联接、启动器（家目录联接待做）；
  幂等，第二次跑什么都不做（`changed: 0`）；实现见 [impl-wire.md](impl-wire.md)；
- `clean`：只拆"机器层接线"，**不卸载任何包**。包、记录、store、用户数据都不动；
  `clean` 之后 `apply` 能把接线原样建回来；
- 想连包一起清掉，先 `pd remove <包>`，最后再 `pd clean`；
- 两者都先算计划，再一次性执行；提权合并成一次。

### 5.6 `pd doctor`

体检项：

| 检查 | 说明 |
|---|---|
| 盘 | 标记、**文件系统（必须 NTFS）**、空间 |
| 稳定根 | 联接是否存在、是否指对 |
| PATH / 环境变量 | 是否有失效条目、是否被外部改过 |
| 记录 vs store | 有没有"有记录没目录"或反之（intent 残留） |
| 扩展 | 是否齐全、ABI 是否匹配、是否坏掉 |
| 缓存 | 索引是否过期、blob 是否完整 |
| 冲突 | 被遮蔽的源候选 |

`--fix` 只修能自动修的（重建联接、清理 intent 残留、删坏缓存），
不能自动修的列出来不动手。

### 5.7 `pd selftest`

- 默认：各包 `--version` 比对 + 关键断言；
- `--full`：跑真实工作流（`cl hello.c`、`flutter build windows`、`python -c ...`）；
- `--only <包>`：只测指定包；
- 失败返回 `12`。

### 5.8 `pd adopt`

```
pd adopt <目录> [--id <包名>] [--version <版本>] [--exe <相对路径>] [--copy] [--dry-run]
```

1. 扫目录，猜入口和 PATH 目录；
2. 算目录指纹 → 决定 store 路径；
3. `rename` 旧目录进 store（同卷，秒级；失败自动移回）；
4. 生成包记录（`origin: local`）；
5. 建 `current` 联接；
6. 生成启动器（可选）；
7. 断言通过后才允许 `pd gc` 清掉旧目录。

`--copy` 时改为复制，不动原目录（安全但占空间）。
`--version` 不写时用 `0.0.1`——目录本身没有版本信息，要升级就显式给。
失败时 `rename` 移回原位、删记录，环境不变。

### 5.9 `pd fetch`

```
pd fetch <包>[@版本] [--from-dir <目录>]
```

只下载到 `cache\blobs\`，不安装、不改机器。
用途：离线前预取、批量准备。

### 5.10 `pd bundle`

```
pd bundle export [--out env.bundle] [--lock]
pd bundle install <env.bundle> [--from-dir <目录>] [--dry-run]
```

`env.bundle` 是纯文本，一行一个包：

```
# 注释
source core
python@3.13.18
node@22.11.0
local/mytool@0.1.0
```

`install` 等价于"按顺序 install"，失败按单包回滚。

### 5.11 `pd kernel stop`

- 停止接新任务 → 等现有任务结束 → 退出；
- `--force` 立即杀（租界垃圾由下次启动清理）；
- 用途：升级程序本身（Windows 不允许覆盖运行中的 exe）。

### 5.12 `pd run` / `pd shell`

- `run`：按包的 `launch` / `data` 声明设好环境变量和工作目录，再启动程序；
  **子进程一律用普通用户令牌**（即使父进程是管理员）；
- **`run` 的退出码属于 `pd` 自己的空间**（见第 3 节）：启动失败 `20`/`1`，被启动程序非 0 → `1`；
- 被启动程序的真实退出码放 `done.metrics.childExitCode`（`--json` 可见）；需要让 shell 拿到它，
  显式加 `--propagate`（此时退出码 = 子进程退出码，`pd` 不再附加语义）；
- `shell`：开一个终端，PATH 指向稳定根，可选 `--profile msvc` 附加变量；
- `shell` 同理：默认退出码属于 `pd` 的空间；加 `--propagate` 才是终端里那条命令的退出码。

### 5.13 `pd pkg pack`

```
pd pkg pack <包> [--out <路径>] [--with-files]
```

- 把**已安装**的包重新打成 `.pdpkg`：说明来自记录，内容来自 `store\`；
- `--with-files` 才把内容打进包里（否则只出说明书，装的时候还得下载）；
- 产物默认放 `packages\<id>-<version>.pdpkg`；
- 纯本地包（`origin: local`）没有源，只能靠这个命令变成可分发的东西。

### 5.14 `pd recover`

```
pd recover [--adopt] [--drop] [--dry-run]
```

处理上次崩溃留下的 intent（架构 11.2）：

- 默认行为是内核启动时**自动**跑一遍，只在有残留时提示；
- `--adopt`：目录在、记录没有 → 补记录；
- `--drop`：目录在、记录没有 → 删目录；
- 两种都不带时按配置里的默认值（默认 `adopt`）。

### 5.15 `pd log --follow`

- `log` 由日志提供者实现（见 spec 5.3.1）；提供者缺失时返回 `ERR_LOG_PROVIDER_MISSING`（20），
  提示改用 `pd kernel log`；
- `--follow` 是长任务，一直输出直到 `Ctrl+C`；
- 取消（`Ctrl+C` / 断连）只停止跟随，**不影响任何正在跑的安装**；
- 退出码：取消返回 `130`（和其他取消一致，见第 3 节）。

### 5.16 `pd config`

```
pd config list
pd config get <键>
pd config set <键> <值>
pd config unset <键>
```

- 读写 `config\settings.json`（字段见 [kernel.md](kernel.md) 第 2 节）；
- **`get` 读内核内存里的当前值**，加 `--file` 才直接读文件；
- `set` 走内核：校验 → 写文件 → 更新内存 → 返回"是否要重启"；
  标了"不需重启"的键当场生效；
- 手改文件后，`get` 会提示"文件已改，重启生效"（比对 mtime）；
- 取值越界直接拒绝，不写进去。

> 这解决了"人能改的只有包名列表"和"settings.json 要人改"之间的矛盾：
> 人可以用命令改，也可以手改，但命令是推荐路径（会校验）。

### 5.17 `pd ext`

```
pd ext list
pd ext update <名字> <目录|zip>
pd ext rollback <名字>
```

- `update`：校验新扩展的 `ext.json` → 停旧进程 → 旧目录改名 `.prev` → 新目录就位 →
  拉起并握手；握手失败**自动回退**并报错；
- 只停这一个扩展，**不停内核**；
- `list` 会标明来源（内置 / 用户）和"是否覆盖了内置版"；
- 细节见 [kernel.md](kernel.md) 第 6 节。

---

## 6. 输出约定

### 6.1 文本模式

- 进度用一行覆盖刷新（TTY）或每 10% 打一行（非 TTY）；
- 日志按级别加前缀（`!` 警告、`x` 错误）；
- 结束时打印一行摘要。

### 6.2 `--json` 模式

每行一个事件（NDJSON），字段遵循 [spec.md](spec.md) 4.3：

```json
{"type":"step","stepId":"download","state":"running"}
{"type":"progress","task":"download","pct":42}
{"type":"log","level":"info","msg":"解压完成"}
{"type":"done","ok":true,"exitCode":0,"summary":"已安装 firefox 139.0","metrics":{"durationMs":18320}}
```

失败时：

```json
{"type":"done","ok":false,"exitCode":12,
 "error":{"code":"ERR_ASSERT","message":"firefox --version 不匹配","hint":"..."}}
```

### 6.3 退出码与错误码的对应

| 退出码 | 常见 `ERR_*` |
|---|---|
| 1 | `ERR_IO`、`ERR_INTERNAL` |
| 2 | `ERR_BAD_ARGS`、`ERR_UNKNOWN_COMMAND` |
| 3 | `ERR_INDEX_INVALID`、`ERR_PKG_INVALID`、`ERR_HASH` |
| 4 | `ERR_ENV`、`ERR_PROTOCOL` |
| 8 | `ERR_ELEVATION` |
| 10 | `ERR_BUSY` |
| 11 | `ERR_UNSUPPORTED`、`ERR_PKG_PLATFORM` |
| 12 | `ERR_ASSERT` |
| 15 | `ERR_SOURCE_UNREACHABLE`、`ERR_NET` |
| 20 | `ERR_PKG_NOT_FOUND`、`ERR_EXT_MISSING` |
| 21 | `ERR_PKG_AMBIGUOUS` |
| 22 | `ERR_PKG_YANKED` |
| 23 | `ERR_OFFLINE_MISS` |
| 24 | `ERR_DEP_UNSATISFIED` |
| 25 | `ERR_TIMEOUT`、`ERR_EXT_TIMEOUT`、`ERR_PROMPT_TIMEOUT` |
| 130 | `ERR_CANCELLED` |

---

## 7. 帮助与补全

```
pd help                  命令列表（名字 + 一句话）
pd help install          参数、开关、示例、退出码
pd help --json           结构化，给界面画表单用
pd complete ins          候选列表
pd complete --json ins   结构化候选
```

数据来源：内核的命令表（架构 3.8）。`pd` 不缓存。

---

## 8. 这份文档的验收

写完实现后，逐条对：

1. 文档里出现的每个 `pd xxx` 都能在第 4 节查到；
2. 每条命令的"改机 / 离线 / 失败码"三列都不空；
3. 每个退出码都能在第 3 节找到；
4. 每个 `ERR_*` 都能在 [spec.md](spec.md) 6.2 找到；
5. 每个"需要缓存"的命令都有对应的预取方式。
