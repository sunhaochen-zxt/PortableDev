# PortableDev v2 规范：扩展声明与消息协议

> 配套文档：[architecture.md](architecture.md)、[commands.md](commands.md)、[kernel.md](kernel.md)、[window.md](window.md)
> 状态：草案，字段可以调整，但**兼容规则**（第 0、2.9、6 节）建议一开始就遵守。

本文定两件事：

1. `ext.json` 有哪些字段、每个字段什么语义；
2. 客户端、内核、扩展之间传什么消息、怎么传。

---

## 0. 总原则：宁多勿少

这条决定了整份文档的写法，先说清楚：

> **发送方把知道的字段都带上；接收方遇到不认识的字段一律忽略。**

理由：多传一个字段的成本是几十字节，而协议后期加字段的成本是"改两端 + 考虑版本兼容 + 老客户端崩溃"。
所以：

1. **结构里预留字段**：现在用不上、但以后大概率要用的，先定义出来；
2. **接收方必须宽容**：不认识的字段、不认识的事件类型、不认识的枚举值，都不能报错；
3. **发送方必须完整**：即使接收方暂时不用，也要把值填上（空值也填）；
4. **`x-` 是实验区**：任何以 `x-` 开头的字段/类型，永远不校验、永远忽略；
5. **`meta` 是公共垃圾桶**：任何一端都能往里塞自己的东西，对方忽略即可。

---

## 1. 命名

- 扩展名：小写，`[a-z][a-z0-9-]*`，全局唯一。
- 能力名：`<扩展名>.<能力>`，如 `storage.write`。
- 命令名：小写，`[a-z][a-z0-9-]*`，全局唯一（别名同样）。
- 事件类型：小写；核心类型固定，自定义用 `x-<名字>`。
- 所有文件第一个字段都是 `schema`（整数），只在**破坏性变更**时加一。

---

## 2. `ext.json`

### 2.1 完整示例（把将来要用的字段也写上）

```json
{
  "schema": 1,

  "name": "pkgmgr",
  "version": "0.1.0",
  "title": "包管理",
  "description": "编排安装、卸载、升级：下载 → 解压 → 收编 → 接线",
  "tags": ["core", "package"],
  "author": "",
  "homepage": "",
  "license": "AGPL-3.0",
  "notes": "",

  "enabled": true,
  "priority": 100,

  "entry": "pkgmgr.ps1",
  "interpreter": "pwsh",
  "entryArgs": ["-NoProfile", "-File"],
  "cwd": "",
  "env": { "PD_PKG_CACHE": "{{CACHE}}" },
  "inheritEnv": false,

  "requiresKernel": ">=1 <3",
  "requiresCli": ">=1",
  "platform": "win-x64",

  "depends": ["storage>=1", "log", "download", "unpack"],

  "lifecycle": {
    "start": "on-demand",
    "startTimeout": "5s",
    "idleTimeout": "5m",
    "heartbeatTimeout": "30s",
    "stopTimeout": "3s",
    "maxConcurrency": 1,
    "restart": "on-crash",
    "maxRestarts": 3,
    "restartBackoff": "2s"
  },

  "healthcheck": { "kind": "ping", "interval": "30s", "timeout": "5s" },

  "permissions": [
    { "kind": "fs", "scope": "staging", "mode": "rw" },
    { "kind": "fs", "scope": "store", "mode": "rw" },
    { "kind": "fs", "scope": "cache", "mode": "rw" },
    { "kind": "fs", "scope": "state", "mode": "rw" },
    { "kind": "registry", "hive": "HKCU", "key": "Software\\Classes" },
    { "kind": "net", "hosts": ["*.mozilla.org", "github.com"] },
    { "kind": "process", "allow": ["msiexec.exe", "7z.exe"] },
    { "kind": "env", "read": true },
    { "kind": "elevation", "allow": true },
    { "kind": "commands", "allow": ["install", "remove", "list"] },
    { "kind": "ui", "allow": true }
  ],

  "provides": ["pkg.install", "pkg.remove"],
  "requires": ["storage.write", "download.http", "unpack.archive"],

  "emits": ["progress", "log", "table", "prompt", "done", "step"],

  "commands": [
    {
      "name": "install",
      "aliases": ["add", "i"],
      "summary": "安装一个包",
      "usage": "pd install <包名|路径|URL> [--dry-run]",
      "group": "package",
      "since": "2.0",
      "hidden": false,
      "mutates": true,
      "args": [
        { "name": "target", "type": "string", "required": true,
          "help": "包名、.pdpkg 路径或 URL", "suggest": "packages" },
        { "name": "--from-dir", "type": "path", "help": "从这个目录找离线包" },
        { "name": "--as", "type": "string", "help": "装成另一个名字" },
        { "name": "--dry-run", "type": "flag", "help": "只显示计划" },
        { "name": "--yes", "type": "flag", "hidden": true }
      ],
      "exitCodes": { "0": "成功", "12": "断言失败", "20": "包不存在" },
      "examples": ["pd install firefox", "pd install ./mytool-1.0.pdpkg"]
    },
    {
      "name": "list",
      "aliases": ["ls"],
      "summary": "列出已装的包",
      "args": [{ "name": "--outdated", "type": "flag" }]
    }
  ],

  "pages": [
    { "id": "packages", "title": "软件包", "entry": "ui/packages.html",
      "icon": "box", "order": 20, "nav": "sidebar", "requires": [] }
  ],

  "settings": {
    "autoUpdate": { "type": "bool", "default": false, "help": "是否自动检查更新" }
  },

  "about": { "homepage": "", "license": "AGPL-3.0", "author": "" }
}
```

### 2.2 身份与元信息

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `schema` | int | 是 | 文件格式版本 |
| `name` | string | 是 | 扩展名，唯一 |
| `version` | string | 是 | 扩展自身版本 |
| `title` | string | 否 | 显示名 |
| `description` | string | 否 | 一句话说明 |
| `tags` | string[] | 否 | 分类/搜索用 |
| `author` / `homepage` / `license` / `notes` | string | 否 | 元信息，不影响行为 |
| `enabled` | bool | 否 | 默认 `true`；`false` 等于临时停用但保留文件 |
| `priority` | int | 否 | 加载顺序与冲突裁决的权重，默认 100，小的先 |

### 2.3 入口与进程

| 字段 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `entry` | string | — | 入口文件，相对扩展目录 |
| `interpreter` | enum | `exe` | `exe` / `pwsh` / `python` / `node` / `dotnet` / `cmd` / `auto` |
| `entryArgs` | string[] | `[]` | 解释器的固定参数，如 `-NoProfile -File` |
| `cwd` | string | 扩展目录 | 进程工作目录，支持占位符 |
| `env` | object | `{}` | 注入的环境变量，支持占位符 |
| `inheritEnv` | bool | `false` | 是否继承内核的环境（默认不继承，避免"服务环境"污染） |

`interpreter: auto` 时按扩展名猜（`.ps1`→pwsh，`.py`→python，`.js`→node，`.dll`→dotnet，`.cmd`/`.bat`→cmd，其他→`exe`）。未知取值在加载时直接拒绝（扩展不加载并记警告），不会静默当 `exe` 跑。

`entryArgs` 的位置：`exe` 是前置（`entryArgs` 在前、入口在后），其余解释器都是入口在前、`entryArgs` 追加在后（`pwsh` 还会在最前面加 `-NoProfile -File`）。

> `dotnet` 用**随盘 dotnet 运行时**跑入口 dll（内核自己的宿主优先），不依赖机器的全局安装。

### 2.4 版本与平台约束

| 字段 | 说明 |
|---|---|
| `requiresKernel` | 内核 **ABI** 范围，如 `>=1 <3`；不满足则不加载。比的是整数 ABI，不是 `kernelVersion` |
| `requiresCli` | 需要的最低客户端版本（可选） |
| `platform` | `win-x64`（缺省）；不匹配则不加载 |

范围语法只支持五种比较符，避免引入版本库：

```
>=1         至少 1
>=1 <3      1 到 3（不含 3）
==2         必须正好 2
^139        兼容范围（>=139 <140）
~139.0      补丁范围（>=139.0 <139.1）
```

`^` / `~` 只对数字段生效；段数不一致时缺的当 0（比较规则见 package-spec.md 第 7 节）。
解析不了 → 视为不匹配。

**ABI 和协议版本是两条线**：

| 名字 | 是什么 | 现在 |
|---|---|---|
| `abi` | 内核 ↔ 扩展的接口版本，只在接口不兼容时 +1 | 1 |
| `v` / `protocol` | 线协议（信封）版本 | 1 |

两者由 `hello.ok` 分别报出（`kernelAbi` 与 `protocolRange`），互不替代。

### 2.5 依赖与能力

| 字段 | 说明 |
|---|---|
| `depends` | 依赖的扩展名，可带范围（`storage>=1`）；拉起时按拓扑顺序 |
| `provides` | 对外提供的能力；字符串，或 `{ "name": "storage.write", "mutates": true }` |
| `requires` | 需要别人提供的能力；缺一个就拒绝加载 |

能力是"扩展之间解耦"的关键：`pkgmgr` 说"我需要 `unpack.archive`"，不关心是谁提供的。
将来把 unpack 拆成 `unzip` 和 `un7z` 两个扩展，`pkgmgr` 一行都不用改。

`mutates` 决定 `call` 进不进独占队列：**字符串写法等价于 `mutates: true`**，
只有显式写 `false` 的只读能力才能并行——宁可排队，也不要并发写。

### 2.6 生命周期

| 字段 | 默认 | 说明 |
|---|---|---|
| `start` | `on-demand` | `on-demand` 用到才起；`eager` 内核启动就起 |
| `startTimeout` | `5s` | 超过算起不来 |
| `idleTimeout` | `5m` | 无在途请求多久后退出（进程回收） |
| `heartbeatTimeout` | `30s` | 多久没有任何消息就发 `ping`；再超时则强杀（见 5.5） |
| `stopTimeout` | `3s` | 请求退出后等多久，超时强杀 |
| `maxConcurrency` | `1` | 同时处理几个请求；超出排队 |
| `restart` | `on-crash` | `never` / `on-crash` / `always` |
| `maxRestarts` | `3` | 连续崩溃几次后放弃，标记为坏 |
| `restartBackoff` | `2s` | 重启间隔（可指数退避） |

**不要**用"总时长"超时；长任务靠进度续命（见 5.5）。

### 2.7 权限

每条是带 `kind` 的对象，**未知 kind 一律忽略**。

| kind | 字段 | 含义 |
|---|---|---|
| `fs` | `scope`、`mode` | `scope`：`store` / `staging` / `cache` / `state` / `ext:<name>`；`mode`：`r` / `rw` |
| `registry` | `hive`、`key` | 要访问的注册表位置 |
| `net` | `hosts` | 允许的主机（支持 `*.example.com`） |
| `process` | `allow` | 允许拉起的外部程序 |
| `env` | `read` / `write` | 能否读取调用方环境、能否设环境变量 |
| `elevation` | `allow` | 是否需要提权能力 |
| `commands` | `allow` | 允许这个扩展（及其页面）调用哪些**别人的**命令；缺省只能调自己的命令与只读命令 |
| `ui` | `allow` | 能否打开窗口/页面；声明了 `pages` 时必须为 `true` |

声明只是"我知道我要干什么"；实际动手仍要向存储扩展要租界（架构 4.4）。

### 2.8 命令

| 字段 | 说明 |
|---|---|
| `name` / `aliases` | 命令名与别名 |
| `summary` / `usage` | 一行说明、用法示例 |
| `group` | 帮助里的分组 |
| `since` / `deprecated` | 版本标记与废弃提示 |
| `hidden` | 不在 `pd help` 里显示 |
| `mutates` | **是否改机器状态**，默认 `false` |
| `parallel` | **是否允许并行**，默认 `false`（即默认独占） |
| `args` | 参数定义 |
| `exitCodes` | 退出码 → 说明 |
| `examples` | 示例数组 |

并发只分两种，**内核不猜**：

| 类别 | 什么算 | 例子 | 并发 |
|---|---|---|---|
| **独占** | 写共享状态（`store`/`state`/`cache`/`config`/`staging`/`current`），或改机器 | `install`、`remove`、`upgrade`、`rollback`、`apply`、`clean`、`gc`、`adopt`、`source add`、`source refresh`、`recover`、`doctor --fix` | 同一时刻只允许一个，其余排队 |
| **只读** | 只读内存和文件，不写共享状态，也不写用户指定路径之外的东西 | `list`、`info`、`search`、`outdated`、`doctor`、`help`、`env` | 可并行 |

**写到用户指定路径不算独占**：`pkg pack --out`、`bundle export --out`、`index build --out`
都是把结果交到用户手里，不碰共享状态，所以并行没问题。

**`fetch` 也不算独占**：它只往 `cache\blobs\` 写自己那一个 blob（先写临时名再原子改名），
不碰 `store` / `state` / `current`；否则 `install.maxConcurrent` 就是一句空话。

- 内置命令的分类由内核硬编码（表在 [commands.md](commands.md) 第 4 节）；
- 扩展命令**默认独占**，只有明确写 `"parallel": true` 的才并行——
  宁可慢，也不要两个命令同时写同一个文件；
- `mutates: true` 但 `parallel: true` 是矛盾声明，加载时直接拒绝。

命令名与别名在**整个内核范围内唯一**；同名冲突时后加载的扩展整个不加载。

参数定义：

| 字段 | 说明 |
|---|---|
| `name` | `--x` 为选项，否则是位置参数 |
| `type` | `string` / `path` / `glob` / `bool` / `flag` / `int` / `float` / `enum` / `list` / `json`；**未知按 `string`** |
| `required` | 必填 |
| `default` | 默认值 |
| `values` | `enum` 候选 |
| `multiple` | 是否可重复（配合 `list`） |
| `suggest` | 补全数据源名（如 `packages`、`files`、`commands`） |
| `help` / `hidden` / `deprecated` | 展示相关 |

这些类型**不发给客户端**（架构 3.8），只用于内核校验、`pd help`、界面画表单。

### 2.9 界面页面

| 字段 | 说明 |
|---|---|
| `id` | 页面标识，唯一 |
| `title` | 显示名 |
| `entry` | HTML 路径（相对扩展目录） |
| `icon` / `order` / `nav` | 图标、排序、出现位置（`sidebar` / `menu` / `hidden`） |
| `requires` | 页面所需能力 |

声明 `pages` 必须同时声明 `{ "kind": "ui", "allow": true }`；页面能调的命令受
`permissions.commands` 约束（见 2.7 与 window.md 3.2）。

### 2.10 设置项（预留）

`settings` 声明扩展自己的配置项，默认值、类型、说明。当前不强制实现，
但先留位置：以后「设置」页面可以直接按这份声明生成表单。

### 2.11 兼容规则（重要）

1. **未知字段一律忽略**，不报错、不警告。
2. `x-` 开头的字段是实验区，永不校验。
3. 枚举值是**开放集合**：旧内核遇到新取值要降级，不能崩。
4. `schema` 只在破坏性变更时加一；新增字段不算。
5. 扩展可以声明比内核新的 `schema`；只要 `requiresKernel` 满足就尝试加载。

---

## 3. 传输层

### 3.1 通道

- Windows 命名管道：`\\.\pipe\portabledev-<stickId>`；
- ACL 限定当前用户；不监听端口，不接受远程；
- 一个客户端一个连接；内核支持多连接。

### 3.2 帧格式

```
[4 字节小端长度 N][N 字节 UTF-8 JSON]
```

- 用长度前缀而不是 NDJSON：JSON 里有换行、转义、编码问题；
- 单帧上限 16 MiB，超过直接断开并记录；
- 大表格分块（见 4.6）。

### 3.3 通用信封

```json
{
  "v": 1,
  "kind": "req",
  "id": "r7",
  "seq": 3,
  "ts": "2026-11-23T21:40:00.123+08:00",
  "trace": "9f2c",
  "parent": "",
  "data": { },
  "meta": { }
}
```

| 字段 | 必填 | 说明 |
|---|---|---|
| `v` | 是 | 协议版本，当前 1 |
| `kind` | 是 | 消息类型 |
| `id` | 视 kind | 请求/任务标识；同一请求的事件共用 |
| `seq` | 否 | 同一 `id` 下递增，用于排序与丢包检测 |
| `ts` | 否 | 发送时间，排查用 |
| `trace` | 否 | 链路追踪 id，跨客户端/内核/扩展 |
| `parent` | 否 | 父任务 id，用于嵌套任务 |
| `data` | 是 | 类型相关的负载 |
| `meta` | 否 | 公共扩展区，任何一端都能塞，对方忽略 |

### 3.4 消息类型

| kind | 方向 | 用途 |
|---|---|---|
| `hello` | 客户端 → 内核 | 握手 |
| `hello.ok` / `hello.err` | 内核 → 客户端 | 协商结果 / 拒绝 |
| `req` | 客户端 → 内核 | 一次命令调用 |
| `evt` | 内核 → 客户端、扩展 → 内核 | 事件流 |
| `event` | 扩展 → 内核 → 客户端 | 扩展主动上报的**主题事件**（非任务事件，见 4.9 / 5.8） |
| `ctl` | 双向 | 取消、心跳、交互回复、租界申请、订阅 |
| `ack` | 双向 | 流量控制回执（预留） |
| `ext.invoke` | 内核 → 扩展 | 派活 |
| `ext.ready` | 扩展 → 内核 | 启动完成 |
| `ext.result` | 扩展 → 内核 | 终态回报（一般用 `evt.done`） |
| `call` / `call.ok` / `call.err` | 扩展 ↔ 扩展（经内核） | 按能力名调用另一个扩展 |
| `x-*` | 任意 | 预留，未知一律忽略 |

---

## 4. 客户端 ↔ 内核

### 4.1 握手

```json
{
  "v": 1, "kind": "hello", "ts": "2026-11-23T21:40:00.100+08:00",
  "data": {
    "protocol": 1,
    "client": "cli",
    "clientVersion": "2.0.0",
    "pid": 4321,
    "session": "s-7f3a",
    "features": ["json", "ansi", "prompt", "progress", "unicode", "link"],
    "ctx": {
      "cwd": "Z:\\work",
      "env": { "HTTP_PROXY": "http://127.0.0.1:7890" },
      "tty": { "cols": 120, "rows": 30, "color": true, "utf8": true },
      "locale": "zh-CN",
      "timezone": "Asia/Shanghai",
      "user": "Administrator",
      "machineId": "b1c2...",
      "interactive": true
    }
  }
}
```

`ctx.env` 传**完整快照**（几 KB 而已）。内核用它做业务判断，不读自己的环境。

内核回复：

```json
{
  "v": 1, "kind": "hello.ok",
  "data": {
    "protocol": 1,
    "protocolRange": [1, 2],
    "kernelVersion": "2.0.0",
    "kernelAbi": 1,
    "stickId": "pd-748bc65148ad",
    "machineId": "m-a5f24fefbd80",
    "root": "Z:\\PortableDev",
    "stableRoot": "C:\\Dev",
    "features": ["json", "prompt", "cancel", "progress", "subscribe", "topics"],
    "bootedAt": "2026-11-23T21:20:00+08:00",
    "limits": { "maxFrame": 16777216, "maxConcurrent": 8 }
  }
}
```

拒绝：

```json
{ "v": 1, "kind": "hello.err",
  "data": { "code": "ERR_PROTOCOL", "message": "内核协议 2，客户端需要 1",
            "hint": "停下来升级 pd", "retryable": false } }
```

### 4.2 发命令

```json
{
  "v": 1, "kind": "req", "id": "r7", "ts": "...",
  "data": {
    "argv": ["install", "firefox", "--dry-run"],
    "opts": {
      "json": false, "yes": false, "quiet": false, "verbose": false,
      "color": true, "offline": false, "dryRun": false, "force": false
    },
    "deadline": "",
    "priority": 0,
    "idempotencyKey": "",
    "labels": { "ui": "cli" },
    "meta": { }
  }
}
```

- `argv` 就是用户敲的那串，**内核不做客户端解析**；
- `opts` 是客户端层面的通用开关，命令自己决定用不用；
- `deadline`/`priority`/`idempotencyKey` 现在可以不填，先留着；
- 未知命令 → `done` + `ERR_UNKNOWN_COMMAND` + "你是不是想敲 install"。

### 4.3 事件

```json
{ "v": 1, "kind": "evt", "id": "r7", "seq": 4, "ts": "...",
  "data": { "type": "progress", "task": "download", "pct": 42, "meta": {} } }
```

下表前五行是**核心事件**（每个客户端都必须会渲染），后面是可选的扩展事件（不认识就跳过）：

| 类型 | 用途 | 关键字段 |
|---|---|---|
| `progress` | 进度 | `task`、`pct` / `current`+`total`、`label`、`rate`、`eta` |
| `log` | 日志 | `level`（debug/info/warn/error）、`msg`、`fields` |
| `table` | 表格 | `title`、`columns`、`rows`、`chunk`、`final`、`key` |
| `prompt` | 交互 | `promptId`、`kind`、`message`、`default`、`options`、`secret`、`timeout` |
| `done` | 终态 | `ok`、`exitCode`、`summary`、`error`、`artifacts`、`metrics` |
| `step`（可选） | 多步任务分组 | `stepId`、`title`、`state`（pending/running/done/failed/skipped） |
| `link`（可选） | 可点击的路径/URL | `text`、`target`、`kind`（file/url） |
| `kv`（可选） | 键值详情面板 | `title`、`items` |
| `tree`（可选） | 树形数据 | `title`、`nodes` |
| `notice`（可选） | 横幅提示 | `level`、`message`、`actions` |
| `ext`（可选） | 扩展状态变化 | `name`、`state`（`starting`/`ready`/`exited`/`crashed`）、`pid`、`reason` |
| `x-*` | 自定义 | 客户端不认识就跳过 |

**关于 `ext` 事件**：

- 内核在扩展启动、握手完成、退出、崩溃时发它；
- 它不属于任何任务，`id` 固定填 `"system"`（不是空串，避免和正常任务混淆）；
- 客户端要收到它，必须在 `hello` 的 `features` 里声明 `"ext-events"`——
  没声明的客户端只收任务事件，不被打扰；
- 也可以在运行时 `ctl` + `{"action":"subscribe","scope":"ext"}` 订阅。

规则：

- 同一 `id` 下 `seq` 严格递增；`done` 必须是最后一条；
- 未知类型忽略，不报错；
- `--json` 模式下把事件原样按行输出（NDJSON），不渲染。

### 4.4 `progress` 细节

```json
{ "type": "progress", "task": "download",
  "label": "下载 firefox", "current": 12, "total": 40, "unit": "MB",
  "pct": 30, "rate": "2.1 MB/s", "eta": "00:13" }
```

`task` 是任务 id，可以有多个并存；客户端按 `task` 分栏显示。
内核可以对同一个 `task` 的 `progress` 做合并（只保留最新），避免刷屏。

### 4.5 `table` 分块

```json
{ "type": "table", "key": "packages", "title": "已安装",
  "columns": [{ "key": "id", "title": "包名", "type": "string", "width": 24 },
              { "key": "version", "title": "版本", "type": "string" }],
  "rows": [["python", "3.13.18"]], "chunk": 0, "total": 3, "final": false }
```

- `columns[].type`：`string` / `number` / `bool` / `date` / `path` / `link`；
- `width`/`align` 只是建议，客户端可覆盖；
- `key` 相同的分块按 `chunk` 累积，`final: true` 时渲染。

### 4.6 `prompt` 与回复

```json
// 内核 → 客户端
{ "type": "prompt", "promptId": "p1", "kind": "confirm",
  "message": "要覆盖已存在的包吗？", "default": false, "timeout": 60 }

// 客户端 → 内核
{ "v": 1, "kind": "ctl", "id": "r7",
  "data": { "action": "prompt_reply", "promptId": "p1", "value": true } }
```

可选的 `code` / `params` 字段（向后兼容）：

```json
{ "type": "prompt", "promptId": "p1", "kind": "confirm",
  "code": "proc.kill.critical", "params": { "pid": 612, "level": 2 },
  "message": "", "default": false, "timeout": 60 }
```

- 认识 `code` 的客户端按 `code` + `params` 自己渲染（**文案属于壳，机制层不产生人类可读文本**）；
- 不认识的客户端回退显示 `message`；`message` 为空时显示通用提问。

- `kind`：`confirm` / `input` / `select` / `multiselect`（开放集合）；
- 客户端非交互（`--json`、管道、无 TTY）时回 `value: null`；
- 超时未回 → 内核取消任务，`done` 带 `ERR_PROMPT_TIMEOUT`。

### 4.7 取消、心跳、订阅

```json
// 取消（Ctrl+C）
{ "v": 1, "kind": "ctl", "id": "r7", "data": { "action": "cancel" } }

// 只停某个任务
{ "v": 1, "kind": "ctl", "id": "r7",
  "data": { "action": "cancel", "scope": "task", "task": "download" } }

// 心跳
{ "v": 1, "kind": "ctl", "data": { "action": "ping", "ts": 1763900000 } }
{ "v": 1, "kind": "ctl", "data": { "action": "pong", "ts": 1763900001 } }

// 订阅另一个请求的事件（界面用：CLI 起的任务，界面也能看进度）
{ "v": 1, "kind": "ctl", "data": { "action": "subscribe", "id": "r7" } }
{ "v": 1, "kind": "ctl", "data": { "action": "unsubscribe", "id": "r7" } }

// 订阅全局扩展事件（壳用）
{ "v": 1, "kind": "ctl", "data": { "action": "subscribe", "scope": "ext" } }
{ "v": 1, "kind": "ctl", "data": { "action": "unsubscribe", "scope": "ext" } }
```

规则：

- 客户端断连 = 取消该连接所有在途请求；
- 取消是协作式的：扩展应尽快停并回 `done`（`ok:false`，`ERR_CANCELLED`）；
- `stopTimeout` 内没停 → 内核杀进程树，代它回 `done`；
- 客户端看门狗（固定 30s，不可配）内无事件且无心跳 → 判定卡死；
- `subscribe` 不带 `scope` 时默认是任务订阅（`id` 必填）；
  带 `"scope":"ext"` 时订阅扩展状态事件（`id` 忽略）；带其它值（如 `"proc"`）时是主题订阅，见 4.9。

### 4.8 并发与串行

- 一个连接可同时发多个 `req`；
- **独占请求由内核全局串行**——判断依据见 2.8：改机器**或写盘**的算独占；
  内置命令的分类由内核硬编码，扩展命令默认独占，只有 `"parallel": true` 的才并行；
- 只读请求可并行；
- 排队中被取消 → 直接回 `ERR_CANCELLED`，不执行；
- 被调用方（`call`）如果是独占能力，同样进同一条串行队列。

---

### 4.9 主题事件订阅

客户端订阅某个主题，内核把扩展上报的 `event` 转发过来（界面实时刷新用，例如进程列表）：

```json
// 客户端 → 内核
{ "v": 1, "kind": "ctl", "id": "r9", "data": { "action": "subscribe", "scope": "proc" } }
// 内核 → 订阅者
{ "v": 1, "kind": "event",
  "data": { "topic": "proc", "event": "proc.started", "ts": "...",
            "payload": { "procId": "p-3f2a", "kind": "script", "pid": 8124 } } }
```

- 主题名由提供方约定（`proc` 由进程扩展提供）；客户端只按名订阅，不认识就忽略；
- 没有订阅者时事件直接丢弃，**不缓存、不重放**；
- 客户端在 `hello` 的 `features` 里声明 `topics` 才收得到；
- 状态：已实现（`proc` 扩展上报 `proc.started` / `proc.exited`，见 [proc.md](proc.md)）。

---

### 4.10 exec 通道（客户端执行）

有些命令的进程必须由**客户端**拉起（`pd run`：stdio 属于终端，内核没有 TTY）。协议给一条
“扩展请求客户端执行”的通道：

```json
// 扩展 → 内核（任务是该扩展的）
{ "v": 1, "kind": "evt", "id": "<扩展任务键>", "seq": 1,
  "data": { "type": "exec", "execId": "e1", "command": "cmd.exe", "args": ["/c", "..."],
            "cwd": "Z:\\...", "env": {}, "detach": false } }
// 内核 → 客户端（id 翻译回客户端 id）；客户端执行完后回执：
{ "v": 1, "kind": "ctl", "id": "<客户端任务 id>",
  "data": { "action": "exec.reply", "execId": "e1", "exitCode": 3 } }
// 内核按任务把回执转回发起扩展（ctl 的 id 是扩展任务键）
```

- 请求走 `evt.type=exec`，回执走 `ctl.action=exec.reply`；内核**只转发**，不执行、不等待；
- 客户端负责：继承自己的 stdio、可选 Job（`detach` 时用 breakaway）、回真实退出码；
- 扩展收到回执后自己决定 `done` 与退出码（`pd run` 把子进程退出码放 `done.metrics.childExitCode`）；
- 状态：已实现（`pkgmgr` 的 `run` + `pd.exe`）。

---

## 5. 内核 ↔ 扩展

形状与客户端一致，区别在 `kind` 和先开口的一方。

### 5.1 扩展接入

扩展进程启动后**主动连内核管道**（管道名从 `PD_PIPE` 取得），第一条消息是 `hello`：

```json
{ "v": 1, "kind": "hello",
  "data": { "protocol": 1, "role": "extension", "name": "pkgmgr",
            "version": "0.0.1", "features": [],
            "invocationId": "i-9f2c81ab" } }
```

- `invocationId` 来自内核注入的 `PD_INVOCATION_ID`；内核只接受与本轮拉起记录一致的连接，
  不一致（或没带）回 `hello.err`（`ERR_PROTOCOL`）后断开——本机其他进程不能冒充扩展；
- 同一扩展已有连接时，重复接入一律拒绝；
- 内核回 `hello.ok`（`protocolRange` / `kernelAbi` / `stickId` / `machineId` / `root` / `limits`）。

扩展随后再发 `ext.ready`：

```json
{ "v": 1, "kind": "ext.ready", "data": { "name": "pkgmgr", "version": "0.1.0",
    "protocol": 1, "capabilities": ["pkg.install"] } }
```

### 5.2 派活

```json
{ "v": 1, "kind": "ext.invoke", "id": "s-1a2b3c4d-r1", "trace": "9f2c",
  "data": {
    "taskKey": "s-1a2b3c4d-r1",
    "cmd": "install",
    "argv": ["firefox", "--dry-run"],
    "ctx": { "cwd": "Z:\\work", "env": { ... }, "tty": { ... } },
    "opts": { "dryRun": true },
    "deadline": "",
    "meta": {}
  } }
```

- `id` / `taskKey` 是**会话唯一、全局唯一且可作文件名**的任务键（`<sessionId>-<客户端id>`，
  形如 `s-1a2b3c4d-r1`）；扩展后续事件都用它当 `id`，内核转发给客户端时再翻译回客户端自己的 id；
- 同一会话里重复的客户端 id 直接拒绝（`ERR_BAD_ARGS`），否则两条任务的 `done` 会串线；
- **派活时不带租界**：内核不碰盘、也不持有任何路径；扩展启动后自己发
  `ctl` + `action: grant.request` 向存储扩展申请（见 5.4）；
- 内核只转发，不缓存、不代持、不续期 grant。

### 5.3 扩展回报

扩展发 `evt`（类型同 4.3），最后一条必须是 `done`。
日志走 `ctl` + `action: log.write`，**发完即忘**，不等回执。

### 5.3.1 日志提供者契约 v1

日志由一个**实现可替换**的扩展提供；内核只依赖下面的契约，不依赖任何实现细节。

**提供者必须**：在 `ext.ready` 声明 `{ "name": "log.ingest", "version": 1 }` 与
`{ "name": "log.query", "version": 1 }`；注册命令 `log`（`mutates:false`、`parallel:true`）；
`lifecycle.start = "eager"`。

**内核必须**：按能力选择提供者（`priority` 最小）；**同优先级目前只记录冲突日志并保留先就绪者**
（严格拒绝全部并回 `ERR_LOG_PROVIDER_CONFLICT` 尚未实现，见 impl-kernel.md 的偏差表）；
没有提供者时 `pd log` 返回 `ERR_LOG_PROVIDER_MISSING`（20）。

**摄入**：`ctl { "action": "log.write", "schema": 1, "records": [ … ] }`，发完即忘。
提供者不得拒收单条记录；队列满时只能丢弃并计数，随后补一条 `log.dropped`。

**记录**（每条 ≤ 64 KiB，超出截断并标 `_truncated`）：

```json
{"ts":"2026-11-24T16:20:01.123+08:00","pri":6,"level":"info","msg":"kernel starting",
 "evt":"kernel.boot","seq":42,"_bootId":"b-3f9a2c71b004","_unit":"kernel","_pid":8780,
 "_invocationId":"","_stickId":"pd-12c2608283c2","_machineId":"m-a5f24fefbd80",
 "_transport":"kernel","trace":"","task":""}
```

- `pri`：error=3、warn=4、info=6、debug=7；
- `_` 前缀是**内核可信字段**；扩展上报同名字段会被剥离并计 `_spoofed`；
- 键名以 `env` / `environment` / `secret` / `token` / `password` / `credential` 开头 → 丢弃并计 `_redacted`；
- `seq` 每 `unit` 单调递增；**cursor** = `<bootId>/<unit>/<seq>`，`--since` 直接接受。

**存储是私有的**：契约**不规定**磁盘格式。换一个实现后，旧日志不保证可读——
这是拿"数据连续性"换"实现可替换性"的有意取舍。提供者不得读写
`state\\logs\\kernel.jsonl` 与 `bootstrap.log`（那是内核兜底）。

**查询**：`log` 命令必须支持 `--tail/--since/--until/--level/--unit/--pkg/--grep/--follow/--json/--disk-usage/--vacuum`，
以 `evt type=log`（`record` 字段为上面的记录）或 `table`（disk-usage）输出；
`--follow` 保持任务直到收到 `ctl cancel`，取消退出码 130。

### 5.3.2 审计流（账本）契约 v1

账本是"装到一半失败了要倒着撤"的撤销记录。它和普通日志共用日志提供者，但**保证等级不同**。

**能力**：提供者在 `ext.ready` 声明 `{ "name": "log.audit", "version": 1 }`。

**写入**（durable）：

```json
// 调用方 → 内核 → 提供者
{ "kind": "call", "id": "a1",
  "data": { "cap": "log.audit", "op": "append",
            "args": { "stream": "audit", "task": "t42", "records": [ ] } } }

// 提供者 → 内核 → 调用方（fsync 成功之后才发）
{ "kind": "call.ok", "id": "a1", "data": { "result": { "count": 2, "lastSeq": 17 } } }
```

- **先落盘再回话**：`call.ok` 必须在这批记录 `fsync` 之后才发出；
- 提供者不得丢审计记录、不得合并、必须保序；写不进去就回 `call.err`，**不许假装成功**；
- 调用方必须等到 `call.ok` 才能继续下一步；拿不到就中止操作——不允许"改了但没记上"。

**条目**：

```json
{ "id": "a1-1", "ts": "...", "task": "t42", "op": "path.add",
  "pkg": "firefox",
  "before": [],
  "after":  ["C:\\Dev\\current\\firefox"],
  "undo":   { "cap": "storage.wire", "op": "path.set",
              "args": { "scope": "user", "value": [] } } }
```

- `undo` 是**写入方给出的撤销指令**，形状固定为 `{cap, op, args}`；
  读取方（回滚扩展）把它当不透明指令执行，不认识业务语义；
- 没有 `undo` 的条目（如 `phase:"begin"`）不可撤销，回滚时跳过并报告；
- `before` 必须区分"原本不存在"（`null`）与"存在但为空"；
- 每条带单调递增的 `seq`（同一提供者内），查询按它排序。

**查询**：

```json
{ "cap": "log.audit", "op": "query", "args": { "task": "t42", "since": "..." } }
// → { "entries": [ ... 按追加顺序 ... ], "oldestTs": "...", "truncated": false }
```

**保留**：`log.auditRetentionDays`（默认 7）。真空只删审计条目；
若某任务的 `begin` 已过期而后续条目还在，`query` 返回 `truncated:true`，
回滚必须**拒绝执行**并说明"账本超出保留期"，不做半撤。

**提供者不可用**：`append` 返回 `ERR_LOG_PROVIDER_MISSING`（20）或 `ERR_IO`；
调用方必须让整个操作失败，不能在没有审计保障的情况下提交。

### 5.4 租界消息

```json
// 扩展 → 内核
{ "v": 1, "kind": "ctl", "id": "t42",
  "data": { "action": "grant.request", "kind": "dir", "hint": "unpack",
            "mode": "rw", "ttl": "10m" } }

// 内核 → 扩展
{ "v": 1, "kind": "ctl", "id": "t42",
  "data": { "action": "grant.reply", "grant": { "id": "g2", "kind": "dir",
            "path": "Z:\\PortableDev\\staging\\t42-b", "mode": "rw",
            "expiresAt": "..." } } }
```

内核只是转发，**决定权在存储扩展**。

转发时内核会补一个 `from` 字段（发起方扩展名），存储扩展据此记账；
`grant.reply` 的 `id` 必须原样带回发起方的任务号，内核按任务号路由回去。
内核按能力 `storage.grant` 找提供者——不按扩展名，换实现不用改内核。

### 5.5 超时与续命

- 扩展每 1–2 秒推一次 `progress` 或心跳；
- `heartbeatTimeout`（默认 30s，和 `idleTimeout` 是两个东西）内没有任何消息 → 内核发 `ping`；
- 再超时 → 强杀，代发 `done`（`ERR_EXT_TIMEOUT`）；
- 不要用总时长超时。

### 5.6 按能力调用另一个扩展

`provides` / `requires` 不是装饰，靠 `call` 落到实际调用：

```json
// pkgmgr → 内核
{ "v": 1, "kind": "call", "id": "c1", "trace": "9f2c",
  "data": { "cap": "storage.write", "op": "commit",
            "args": { "task": "t42", "pkg": "firefox", "version": "139.0" } } }

// 内核 → pkgmgr（成功）
{ "v": 1, "kind": "call.ok", "id": "c1",
  "data": { "result": { "path": "Z:\\PortableDev\\store\\ab12...-firefox-139.0" } } }

// 内核 → pkgmgr（失败）
{ "v": 1, "kind": "call.err", "id": "c1",
  "data": { "error": { "code": "ERR_BUSY", "message": "另一个任务在写" } } }
```

规则：

- 扩展**不按扩展名找对方**，只认能力名（`storage.write`）；换实现不用改调用方；
- 内核转发 `call` 时补 `from`；`call.ok` / `call.err` 按 `call` 的 `id` 路由回发起方，
  同一个 `id` 在途时重复使用会被拒绝（`ERR_BUSY`）；
- 内核按 `provides` 找到提供者，没在跑就拉起，找不到就回 `ERR_EXT_MISSING`；
- 多个扩展提供同一能力 → 按 `priority` 选一个，冲突在启动时就报出来；
- 调用同样受超时、取消、串行化约束（`mutates` 的能力要排队）；
- 被调用方可以再发事件，但事件归属当前 `id`，不另起一条链。

### 5.7 敏感信息

`ctx.env` 传**全量快照**，包括可能含 token 的变量（`GITHUB_TOKEN`、代理口令等）。
这是有意为之：装包经常需要这些变量（私有源、企业代理），过滤掉反而装不上。

现阶段在**传输边界**（客户端 → 内核 → 扩展）**不做打码、不做白名单**。
**日志边界**按 kernel.md 7.1：不主动倾倒整份环境快照，按名记录；敏感值标 `_sensitive:true`，
加密落地后按它处理。风险由两件事兜住：

- 日志扩展以后提供**加密**（预留：`log.encryption` 配置项，先用占位，见《内核》文档）；
- 日志默认落 `state\logs\`，不随包分发、不导出。

如果以后要收紧，加一个配置项（`kernel.envMode: "full" | "proxy-only"`）即可，不改协议。

---

### 5.8 扩展主动事件

扩展除了任务内的 `evt`，还可以发**不属于任何任务**的主题事件：

```json
{ "v": 1, "kind": "event",
  "data": { "topic": "proc", "event": "proc.started",
            "payload": { "procId": "p-3f2a", "kind": "script", "pid": 8124 } } }
```

- 只允许扩展发；内核校验来源是已就绪的扩展，补 `ts`，按 `topic` 转发给订阅者（4.9）；
- 发完即忘，没有回执、没有 `done`，不占并发额度；
- 载荷里**不放人类可读文本**（文案属于壳）；
- 状态：已实现（首个提供方是 `proc` 扩展）。

---

## 6. 错误与退出码

### 6.1 错误对象

```json
{ "type": "done",
  "data": { "ok": false, "exitCode": 12,
    "error": { "code": "ERR_ASSERT",
               "message": "firefox --version 不匹配",
               "hint": "上游可能改了目录结构，检查 install.ps1",
               "details": { "expected": "139.0", "actual": "140.0" },
               "retryable": false },
    "metrics": { "durationMs": 4210, "bytes": 0 } } }
```

| 字段 | 说明 |
|---|---|
| `code` | 稳定英文串，不翻译，可搜索、可断言 |
| `message` | 给人看的一句话（可中文） |
| `hint` | 怎么修（可中文） |
| `details` | 结构化细节，界面可展开 |
| `retryable` | 是否值得重试，客户端可据此给建议 |

### 6.2 稳定错误码

| code | 含义 |
|---|---|
| `ERR_UNKNOWN_COMMAND` | 没有这个命令 |
| `ERR_BAD_ARGS` | 参数不对 |
| `ERR_EXT_MISSING` | 扩展不存在或加载失败 |
| `ERR_UNAUTHORIZED` | 扩展调用了 `ext.json` 的 `requires` 里没有声明的能力 |
| `ERR_UNSUPPORTED` | 脚本请求了白名单外的操作，或包用了不支持的能力 |
| `ERR_EXT_TIMEOUT` | 扩展卡死 |
| `ERR_CANCELLED` | 被取消 |
| `ERR_PROMPT_TIMEOUT` | 交互超时 |
| `ERR_TIMEOUT` | 总时长到点（`--timeout`、`script.timeout`、命令自声明） |
| `ERR_ENV` | 环境不对：找不到盘、盘根不对、不在 PortableDev 盘上 |
| `ERR_AUDIT_EXPIRED` | 账本条目超出 `log.auditRetentionDays`，无法回滚 |
| `ERR_PROTOCOL` | 协议不兼容 |
| `ERR_BUSY` | 另一个改机器的任务在跑 |
| `ERR_ASSERT` | 断言失败 |
| `ERR_SCRIPT` | 包脚本失败（非零退出/违反请求协议） |
| `ERR_PKG_INVALID` | 包说明书不合法（缺字段、路径带盘符等） |
| `ERR_PKG_NOT_FOUND` | 包或版本不存在 |
| `ERR_PKG_AMBIGUOUS` | 多个源都有，需要 `--source` |
| `ERR_PKG_YANKED` | 版本已撤回 |
| `ERR_PKG_PLATFORM` | 平台不匹配（要求 win-x64 却给了别的） |
| `ERR_SOURCE_UNREACHABLE` | 源连不上（网络、代理、地址错） |
| `ERR_OFFLINE_MISS` | 离线且内容不在盘上 |
| `ERR_DEP_UNSATISFIED` | 依赖装不上 |
| `ERR_ELEVATION` | 需要提权但被拒 |
| `ERR_PROC_NOT_FOUND` | 进程不存在或已经退出 |
| `ERR_PROC_PROTECTED` | 受保护进程，即使提权也无法结束 |
| `ERR_INDEX_INVALID` | 索引格式不对 |
| `ERR_IO` / `ERR_NET` / `ERR_HASH` | 常见失败类别 |
| `ERR_INTERNAL` | 兜底 |

### 6.3 退出码

**权威表在 [commands.md](commands.md) 第 3 节**，这里只写约束：

- 命令在 `ext.json` 的 `exitCodes` 里另外声明的，以命令自己为准（但仍落在全局语义里）；
- 内核只保证几件最基本的事：成功 `0`、取消 `130`、参数错误 `2`；
- `pd` 原样透传，不做二次解释；
- 新增退出码必须先改 commands.md 第 3 节，不允许各文档各写一套。

---

## 7. 预留清单（现在就定，避免以后改）

| 位置 | 预留 | 用途 |
|---|---|---|
| 信封 | `ts` / `trace` / `parent` / `meta` | 排查、嵌套任务、私有扩展 |
| `hello` | `features` / `session` / `locale` / `timezone` / `machineId` | 能力协商、多语言、机器识别 |
| `req` | `deadline` / `priority` / `idempotencyKey` / `labels` | 超时、调度、幂等重试、埋点 |
| 事件 | `step` / `link` / `kv` / `tree` / `notice` / `x-*` | 多步任务、可点路径、详情面板、树形、横幅 |
| `progress` | `rate` / `eta` / `unit` | 更好看的进度显示 |
| `done` | `artifacts` / `metrics` / `summary` | 产物列表、耗时统计、摘要 |
| 错误 | `details` / `retryable` | 结构化细节、重试建议 |
| `ext.json` | `settings` / `tags` / `priority` / `enabled` / `healthcheck` | 设置页、搜索、加载顺序、临时停用、健康检查 |
| 命令参数 | `suggest` / `multiple` / `group` / `since` / `deprecated` | 补全、可重复参数、分组、版本标记 |

---

## 8. 为什么这么设计

| 决定 | 理由 |
|---|---|
| 宁多勿少 | 多传字段几乎零成本；事后加字段要改两端、还要兼容 |
| 长度前缀帧而不是 NDJSON | JSON 里有换行会崩；长度前缀最稳 |
| 信封带 `v`/`seq`/`trace` | 版本协商、乱序检测、跨层排查都靠它 |
| 事件类型开放 | 以后加新展示形态不用改协议 |
| 命令参数类型不发客户端 | 少一层 schema 同步；打错由内核报错更自然 |
| 权限是带 `kind` 的对象 | 加新权限种类不改结构 |
| 版本范围只支持五种比较符（`>=` `<` `==` `^` `~`） | 不引依赖，够用 |
| 错误码用稳定英文串 | 日志可搜、测试可断言 |
| 租界由存储扩展发、内核只转发 | 扩展拿不到任意路径；内核也不因此持有任何路径 |
| 断连即取消 + 心跳 | 客户端被强关很常见，不能只靠超时 |

---

## 9. 还没定的

1. ~~`package.json` 的字段~~ → 已定，见 [package-spec.md](package-spec.md)。
2. `config\settings.json` 的字段（稳定根、存档点数、缓存上限、日志级别）。
3. `pd help --json` 的返回结构（预计直接复用 `commands` 定义）。
4. 界面扩展的宿主桥接口（页面怎么调内核）。
5. 租界的续期与回收细节。
