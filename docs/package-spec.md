# PortableDev v2 规范：包格式与 `package.json`

> 配套文档：[architecture.md](architecture.md)、[spec.md](spec.md)、[index-spec.md](index-spec.md)、[commands.md](commands.md)、[kernel.md](kernel.md)
> 状态：草案。同样遵守「宁多勿少」：**发送方把知道的字段都带上，接收方不认识就忽略**。

---

## 0. 总原则

1. **一个包必有一份 `package.json`**，其余都是可选的。
2. **未知字段忽略**；`x-` 开头的字段永不校验。
3. **不支持的已知能力 → 整个包拒绝**（不是"能装多少装多少"）。
4. 所有路径用**占位符**，不写盘符、不写绝对路径。
5. `schema` 只在破坏性变更时加一。

---

## 1. 包的形态

### 1.1 包文件（`.pdpkg`）

本质是 zip，文件名 `<id>-<version>.pdpkg`：

```
firefox-139.0.pdpkg
  package.json          说明书（必须）
  files\...             内容（可选，见下）
  install.ps1           安装脚本（可选）
  uninstall.ps1         卸载脚本（可选）
```

规则：

- `package.json` 必须存在，且在包的根目录；
- **内容来源二选一或都有**：
  - `files\` 里带本地内容 → 离线可用；
  - `package.json` 里写 `source.url` + `sha256` → 安装时下载；
- 也可以两者都有：优先用本地 `files\`，缺了再下载；
- **包里不带界面**（界面属于扩展，见架构 4.11）。

### 1.2 包目录（开发时）

不加压缩的目录，结构同上。`pd install .\firefox\` 直接能吃。

### 1.3 来源

| 来源 | 命令 | 说明 |
|---|---|---|
| 包文件 | `pd install .\firefox-139.0.pdpkg` | 自带说明书和内容 |
| 包目录 | `pd install .\firefox\` | 开发时用 |
| URL | `pd install https://.../x.pdpkg` | 下载整个包文件 |
| 源（索引） | `pd install firefox` | 从索引找到说明书 |
| Scoop 源 | `pd install firefox --source scoop` | 先转换再装（见第 6 节） |
| 已装目录 | `pd adopt .\mytool` | 收编成本地包 |

---

## 2. 完整示例（把将来要用的字段也写上）

```json
{
  "schema": 1,

  "id": "firefox",
  "version": "139.0",
  "channel": "stable",
  "title": "Firefox",
  "description": "Mozilla Firefox 便携版",
  "tags": ["browser", "gui"],
  "homepage": "https://www.mozilla.org/",
  "license": "MPL-2.0",
  "author": "Mozilla",

  "platform": "win-x64",
  "arch": ["x64"],
  "minKernel": ">=1",

  "requires": [
    { "id": "msvc", "range": ">=14.40", "optional": false }
  ],
  "conflicts": ["firefox-esr"],
  "replaces": [],
  "provides": ["browser"],

  "source": {
    "kind": "msi",
    "url": "https://download.mozilla.org/?product=firefox-msi-latest-ssl&os=win64&lang=zh-CN",
    "sha256": "0000000000000000000000000000000000000000000000000000000000000000",
    "size": 62345216,
    "rename": "firefox-139.0.msi",
    "mirrors": ["https://mirror.example.com/firefox-139.0.msi"],
    "headers": {},
    "auth": "none"
  },

  "files": [],

  "unpack": {
    "kind": "msi",
    "strip": 0,
    "extractDir": "",
    "sanitize": true,
    "maxFiles": 200000,
    "maxBytes": 4294967296
  },

  "script": {
    "entry": "install.ps1",
    "interpreter": "pwsh",
    "on": ["install", "upgrade"],
    "timeout": "10m",
    "network": false,
    "elevated": false
  },

  "path": [],
  "env": { "MOZ_CRASHREPORTER_DISABLE": "1" },

  "registry": [],

  "data": [
    { "kind": "arg", "args": ["-profile", "{{HOME}}\\firefox\\profile"] }
  ],

  "launch": [
    { "name": "Firefox", "exe": "firefox.exe",
      "args": ["-no-remote"],
      "env": { "MOZ_CRASHREPORTER_DISABLE": "1" },
      "cwd": "",
      "console": false,
      "icon": "firefox.exe",
      "runAs": "user",
      "hidden": false }
  ],

  "asserts": [
    { "kind": "command", "run": "firefox.exe --version",
      "expect": "139.0", "match": "contains", "timeout": "20s" },
    { "kind": "exists", "path": "distribution\\policies.json" },
    { "kind": "json", "path": "distribution\\policies.json",
      "pointer": "/policies/DisableAppUpdate", "equals": true }
  ],

  "hashes": {
    "archive": "0000000000000000000000000000000000000000000000000000000000000000",
    "tree": ""
  },

  "update": {
    "kind": "url",
    "url": "https://download.mozilla.org/?product=firefox-msi-latest-ssl&os=win64&lang=zh-CN",
    "versionPattern": "firefox-(?<ver>\\d+\\.\\d+)",
    "check": "sha256-file"
  },

  "uninstall": {
    "keepData": true,
    "runScript": true,
    "removePaths": []
  },

  "persist": [
    { "source": "profile", "target": "{{HOME}}\\firefox\\profile" }
  ],

  "size": { "download": 62345216, "installed": 250000000 },

  "meta": {},
  "x-": {}
}
```

---

## 3. 字段说明

### 3.1 身份

| 字段 | 必填 | 说明 |
|---|---|---|
| `schema` | 是 | 文件格式版本，当前 1 |
| `id` | 是 | 包名，`[a-z][a-z0-9-]*`，全局唯一 |
| `version` | 是 | 版本号；不强制语义化，但数字段按数值比较 |
| `channel` | 否 | `stable` / `beta` / `dev`，默认 `stable` |
| `title` / `description` | 否 | 展示用 |
| `tags` / `homepage` / `license` / `author` | 否 | 元信息 |

**命名空间**：来自源（如 Scoop）的包保留原 id；与已装包冲突时，要么拒绝，
要么用 `--as` 装成别的名字（记录里保留 `origin`）。

### 3.2 兼容与依赖

| 字段 | 说明 |
|---|---|
| `platform` | 默认 `win-x64`；不匹配则拒绝安装 |
| `arch` | 明确列出支持的架构，缺省按 `platform` 推 |
| `minKernel` | 需要的最低内核 **ABI**（整数，如 `>=1`；与 `kernelVersion` 无关） |
| `requires` | 依赖的包及版本范围；按拓扑顺序安装。`id` 可以是虚拟包（见 `provides`）；已装且满足范围则跳过，否则递归安装；冲突/循环报 24；`--no-deps` 跳过 |
| `conflicts` | 与哪些包不能共存：对方已装（双向检查）就报错 24；`--force` 跳过 |
| `replaces` | 声明"我替代谁"（老版本迁移用）。安装时先卸载被替代的已装包（保留用户数据）；本包安装失败则尽力装回旧版本 |
| `provides` | 提供的虚拟包名，如 `browser`。其他包依赖虚拟名时：已装的提供者优先满足；否则按源优先级选；同优先级有多个不同提供者则报错点名安装 |

### 3.3 来源 `source`

`source` 描述**下载到的那个文件是什么**（怎么拿、校验），不描述怎么解。

| 字段 | 说明 |
|---|---|
| `kind` | 下载物的类型：`archive`（通用压缩包）/ `raw`（单个可执行文件）/ `msi` / `zip` / `tar.gz` / `7z` / `none` |
| `url` | 下载地址 |
| `sha256` | **远程来源必须有**；缺失则拒绝 |
| `size` | 字节数，用于预算空间和进度 |
| `rename` | 下载后改成的文件名（有些地址不带扩展名） |
| `mirrors` | 备用地址，主地址失败时依次尝试 |
| `headers` | 额外请求头 |
| `auth` | `none`（暂不支持认证） |

`files\` 里带内容的包可以没有 `source`。

### 3.4 解包 `unpack`

`unpack` 描述**怎么把上面的东西解开**，和 `source.kind` 是两件事：
下载一个 `.msi` 是 `source.kind: "msi"`；要不要用管理式解包、剥几层目录，写在 `unpack` 里。

| 字段 | 默认 | 说明 |
|---|---|---|
| `kind` | 按 `source.kind` 推 | `zip` / `tar.gz` / `7z` / `msi` / `raw` / `none` |
| `strip` | 0 | 剥掉顶层目录的层数 |
| `extractDir` | `""` | 只取压缩包里的某个子目录 |
| `sanitize` | `true` | 拒绝 `..\`、绝对路径、指向盘外的链接 |
| `maxFiles` / `maxBytes` | 20 万 / 4 GB | 解压上限，防炸弹 |

**默认值推导**：`unpack.kind` 缺省时按 `source.kind` 取；`raw` 表示不写盘、
直接当单个文件用（如 `jq.exe`）；`none` 表示内容已经在 `files\` 里，不需要解包。

### 3.5 安装脚本 `script`

| 字段 | 说明 |
|---|---|
| `entry` | 脚本文件，相对包根 |
| `interpreter` | `pwsh`（默认）/ `python` / `cmd` |
| `on` | `install` / `upgrade` / `remove` 的子集，默认 `["install"]` |
| `cpu` | 可选，脚本 Job Object 的 CPU 硬上限（占单核百分比，`40` 或 `"40%"`，1–100）；缺省不限 |
| `elevated` | 可选，`true` 时经提权助手（一次 UAC，SHA-256 绑定请求）执行；**没有请求协议**，只适合简单安装器 |
| `timeout` | 默认 `10m` |
| `network` | 默认 `false`；脚本内要联网须显式声明 |
| `elevated` | 默认 `false`；需要提权须显式声明（会弹 UAC） |

脚本**不能连内核**，只能往 stdout 打印请求，由 `pkgmgr` 代执行（架构 8.2）。
请求必须带 `id`，`pkgmgr` 从脚本的 **stdin** 回一行结果：

```json
// 脚本 → stdout
{"type":"request","id":"1","cmd":"link","args":{"target":"{{HOME}}\\x","source":"x"}}
// pkgmgr → 脚本 stdin
{"id":"1","ok":true,"data":{}}
{"id":"1","ok":false,"error":{"code":"ERR_IO","message":"目标已存在"}}
```

命令名是**裸名**，不带 `pd` 前缀：

```
fetch / unpack / write / copy / move / rm / link
path.add / env.set / reg.set / reg.del / launch / assert
```

不在白名单里的请求 → 回执 `ok:false` + `ERR_UNSUPPORTED`，脚本应停止（不静默忽略）。
写回执有超时（默认 30s）：脚本不读回执就继续干，视为脚本违规，本次安装失败。

**默认策略（三处文档统一按这个）**：

| 包从哪来 | 默认跑不跑脚本 |
|---|---|
| 本地：`adopt`、本地 `.pdpkg`、`--from-dir` | 跑 |
| 网络：索引源、Scoop 源、`https://...pdpkg` | **不跑**，要 `--allow-scripts` 才跑 |
| 显式 `--no-scripts` | 一律不跑 |

`scoop` 源的 `pre_install` / `post_install` 即使加了 `--allow-scripts` 也要先在转换时
打印出来确认，不做"悄悄执行"。

### 3.6 接线

| 字段 | 说明 |
|---|---|
| `path` | 要加进 PATH 的目录（相对安装目录），如 `["."]`、`["bin"]` |
| `env` | 要设的环境变量（全局），值支持占位符 |
| `registry` | 要写的注册表项（见 3.8） |
| `launch` | 生成的启动器（见 3.10） |

### 3.7 数据落点 `data`

统一成一套机制，"数据放哪"只有这一处，按代价从低到高，**能不用联接就不用**：

| kind | 字段 | 适用 | 机器上有没有痕迹 |
|---|---|---|---|
| `arg` | `args` | 程序认 `-profile` / `--user-data-dir` | 无 |
| `env` | `name`、`value` | 程序认环境变量 | 无 |
| `link` | `source`、`target` | 程序只认 Known Folder（如 `%APPDATA%\X`） | 一个联接 |
| `copy` | `source`、`target` | 两者都不认（尽量别用） | 复制出来的文件 |

方向说明：

- `link`：**机器上的路径** 指向 **盘上的目录**（`target` ← `source`），
  适用于程序硬编码去读 `%APPDATA%` 的情况（FlClash）；
- `arg` / `env`：只影响这个程序的运行，不改机器。

`source` 是盘上的路径（用占位符），`target` 是机器上的位置。
`link` 需要一次提权；`arg` / `env` 不需要。

> `link` 和 `persist`（3.12）共用同一套联接原语，不要写两遍。

### 3.8 注册表 `registry`

```json
{ "hive": "HKCU", "key": "Software\\Classes\\.md",
  "name": "", "type": "REG_SZ", "value": "MyMarkdown",
  "when": "install" }
```

- `when`：`install` / `upgrade` / `always`；
- 写之前备份原值（含类型），`clean` 时逐条还原；
- **路径值必须是稳定根**（`C:\Dev\...`），出现盘符直接拒绝该包。

### 3.9 断言 `asserts`

| kind | 字段 | 说明 |
|---|---|---|
| `command` | `run`、`expect`、`match`、`timeout` | 跑命令比对输出；`match`：`contains`（默认）/`equals`/`regex`；`timeout` 默认 20s；命令经 proc 扩展在 Job Object 里执行（`cmd /d /s /c`），输出取 stdout+stderr，非 0 退出即失败 |
| `exists` | `path` | 文件或目录存在 |
| `missing` | `path` | 文件或目录不存在 |
| `json` | `path`、`pointer`、`equals`/`contains` | 检查 JSON 字段 |
| `fileHash` | `path`、`sha256` | 单个文件校验 |
| `x-*` | — | 自定义，未识别则跳过 |

断言失败 → 整个安装回滚（架构 11.1）。安装时断言随记录保存，之后用 `pd selftest` 复跑
（store 里没有 `package.json` 也能测）。

`selftest` 字段（可选）声明 `pd selftest --full` 才跑的完整工作流：

```json
"selftest": [
  { "name": "build hello", "run": "cl hello.c && hello.exe", "expect": "hello", "timeout": "60s" }
]
```

每项字段同 `command` 断言（`run` / `expect` / `match` / `timeout`），`name` 只用于报告；
默认的 `pd selftest` 不跑它，避免日常检查触发重编译。

### 3.10 启动器 `launch`

| 字段 | 默认 | 说明 |
|---|---|---|
| `name` | 必填 | 生成的文件名（不含扩展名） |
| `exe` | 必填 | 相对安装目录的可执行文件 |
| `args` | `[]` | 固定参数，支持占位符 |
| `env` | `{}` | 只对这个程序生效的环境变量 |
| `cwd` | 安装目录 | 工作目录 |
| `console` | `false` | `false` 时用无窗口方式启动 |
| `icon` | `exe` | 图标来源 |
| `runAs` | `user` | `user` / `admin`（admin 需要显式声明） |
| `hidden` | `false` | 不在界面里显示 |

生成物一律用 `%~dp0` 或稳定根，盘符变化不影响。

### 3.11 更新 `update`

| kind | 说明 |
|---|---|
| `none` | 不检查（默认） |
| `url` | 固定地址，用 `versionPattern` 从文件名/内容提版本 |
| `github` | 从 GitHub Releases 查 |
| `scoop` | 从 Scoop bucket 查 |
| `index` | 从我们的索引查 |

字段：`url`、`versionPattern`、`check`（`sha256-file` / `sha256-url`）、`repo`、`app`、`bucket`。
本地自造的包默认 `none`，不参与 `pd outdated`。

### 3.12 卸载与数据

| 字段 | 说明 |
|---|---|
| `uninstall.keepData` | 默认 `true`：卸载时保留用户数据 |
| `uninstall.runScript` | 是否跑 `uninstall.ps1` |
| `uninstall.removePaths` | 额外要删的路径（相对安装目录） |
| `persist` | 数据位置声明：安装目录里的某个路径 **指向** 盘上的持久目录（与 `data.link` 用同一套联接原语，只是机器侧落点不同：`data.link` 在 Known Folder，`persist` 在安装目录内） |

### 3.13 哈希与 store 路径

| 字段 | 说明 |
|---|---|
| `hashes.package` | `.pdpkg` 文件本身的哈希（只有 `files\` 的包靠它） |
| `hashes.archive` | 要下载的那个压缩包的哈希（有 `source` 的包靠它） |
| `hashes.tree` | 解压 + 跑完脚本后的目录指纹（写进记录，用来发现被改过） |

**store 路径用哪个哈希**：

| 包的类型 | 决定路径的哈希 |
|---|---|
| 有 `source.sha256` | `hashes.archive` |
| 只有 `files\`（自带内容） | `hashes.package`（`.pdpkg` 的哈希） |
| 包目录（开发模式） | `hashes.tree`（安装时现算） |

三者都是"内容决定路径"。前两种在安装前就知道，最后一种要先解压、算完才能定路径——
所以它只能在 staging 里算完再 rename，不能提前拼路径。

**路径里只取哈希的前 16 位十六进制**（Windows 260 字符限制），完整哈希写进记录。

远程包必须提供 `archive`；`tree` 一律安装时现算，不要求作者填。

### 3.14 占位符

| 占位符 | 含义 |
|---|---|
| `{{ROOT}}` | 稳定根，如 `C:\Dev` |
| `{{STICK}}` | 盘根真实路径 |
| `{{STORE}}` | `{{STICK}}\store` |
| `{{CACHE}}` | `{{STICK}}\cache` |
| `{{STATE}}` | `{{STICK}}\state` |
| `{{HOME}}` | `{{ROOT}}\home\<id>`，是盘上 `home\<id>` 的联接（所以写进启动器也不带盘符） |
| `{{INSTALL}}` | 本次安装目标目录 |
| `{{ID}}` / `{{VERSION}}` | 包名与版本 |
| `{{PATH:<name>}}` | 某个包的 PATH 目录 |

规则：**写进注册表、启动器、环境变量的路径必须用 `{{ROOT}}`**，不能出现盘符字面量。

---

## 4. 与 Scoop 清单的映射

| Scoop 字段 | 我们的字段 | 说明 |
|---|---|---|
| `version` | `version` | 直接映射 |
| `url` | `source.url` | 直接映射（支持多个时取第一个，其余进 `mirrors`） |
| `hash` | `source.sha256` | 直接映射；`base64:` 前缀的先转换 |
| `extract_dir` | `unpack.extractDir` | 直接映射 |
| `bin` | `path` + `launch` | 数组项拆成目录和启动器 |
| `persist` | `persist` | 直接映射 |
| `depends` | `requires` | 转成对象形式 |
| `env_set` | `env` | 仅当值是普通字符串；含脚本逻辑则拒绝 |
| `env_add_path` | `path` | 直接映射 |
| `shortcuts` | — | **不支持 → 拒绝整包** |
| `pre_install` / `post_install` | `script` | 默认拒绝；加 `--allow-scripts` 才转成脚本 |
| `installer` | — | **不支持 → 拒绝整包** |
| `notes` | `description`/`meta` | 仅作展示 |

转换结果**存成我们的格式再使用**（不是每次现场解析），存在 `state\sources\` 下。

---

## 5. 索引与 `index.json`

源 = 一个 `index.json` + 一批包。先给个形状，细节另开文档：

```json
{
  "schema": 1,
  "name": "core",
  "generated": "2026-11-23T20:00:00+08:00",
  "packages": {
    "firefox": {
      "latest": "139.0",
      "versions": {
        "139.0": { "url": "https://.../firefox-139.0.pdpkg",
                   "sha256": "...", "size": 62345216 }
      }
    }
  }
}
```

`pd install <id>` 时：查索引 → 拿 `url` + `sha256` → 下载包文件 → 校验 → 读 `package.json` → 走安装流程。

---

## 6. 拒绝规则（重要）

遇到下面这些，**整个包拒绝**，并列出是哪一条：

| 情况 | 原因 |
|---|---|
| 远程包没有 `sha256` | 无法确认拿到的是什么 |
| 需要厂商安装器（`installer`） | 会写注册表/服务，破坏可重建性 |
| 需要创建快捷方式 | 属于机器状态，`clean` 收不干净 |
| 脚本要写系统目录 / HKLM 却没声明 `elevated` | 未声明的提权 |
| 路径里出现盘符字面量 | 换机器就废 |
| 声明的 `platform` 不匹配 | 装了也跑不起来 |
| 依赖缺失且无法解析 | 装一半会留残局 |

原则：**宁可装不上，也不装一半。**

### 6.1 离线时怎么判定

`--offline` 只禁止联网，不禁用盘上的内容。逐包判断：

| 情况 | 结论 |
|---|---|
| 有 `files\` | 能装 |
| 有 `source`，且 `cache\blobs\<sha256>` 里已有 | 能装 |
| 有 `source`，缓存里没有 | 拒绝，报 `ERR_OFFLINE_MISS`，给出 `pd fetch <id>@<版本>` |

报错必须带补救命令，不能只说"离线不可用"。

---

## 7. 版本比较

不强制语义化，但比较规则固定：

1. 按 `.`、`-` 拆段；
2. 全是数字的段按数值比；
3. 数字段 > 非数字段；
4. 段数少的，缺的当 0；
5. 相同时按字符串比（保证稳定排序）。

这样 `1.2.10 > 1.2.9`，`3.13.18 > 3.9.0`（不会犯字符串比较的错）。

---

## 8. 还没定的

1. `config\settings.json` 的字段。
2. ~~`index.json` 的完整规范和生成方式~~ → 已定，见 [index-spec.md](index-spec.md)。
3. ~~源之间的优先级与冲突~~ → 已定，见 [index-spec.md](index-spec.md) 第 4 节。
4. 本地包的打包命令（`pd pkg pack`）的产物细节。
5. 安装步骤的 dry-run 输出格式（给人看的计划长什么样）。
