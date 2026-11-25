# PortableDev v2 规范：源与 `index.json`

> 配套文档：[architecture.md](architecture.md)、[spec.md](spec.md)、[package-spec.md](package-spec.md)、[commands.md](commands.md)、[kernel.md](kernel.md)
> 状态：草案。同样遵守「宁多勿少」：字段可以多写，接收方忽略不认识的。
> v2 实现进度：`dir` / `http` / `git` 源、缓存、优先级、版本范围、yanked、离线均已实现
> （见 impl-pkgmgr.md）；`scoop` 清单转换、依赖递归安装未实现。

---

## 0. 这一层解决什么

前面定了一件事：**包自带说明书**（`.pdpkg` 里有 `package.json`）。
但"`pd install firefox`"的时候，程序还不知道 `firefox` 在哪、哪个版本、多大、哈希多少。

**索引（index）就是回答这三个问题的目录。** 它只存"哪来的、多大、哈希多少"，
不存包的内容，也不存包的全部元数据——那些在包自己的 `package.json` 里。

---

## 1. 源是什么

一个源 = **一个 `index.json` + 它指向的一批包文件**。

| 源的种类 | `kind` | 实际是什么 |
|---|---|---|
| 内置 | `builtin` | 随程序发布，只读，和程序同版本 |
| 目录 | `dir` | 盘上某个文件夹，里面有 `index.json` 或一堆 `.pdpkg` |
| 静态 HTTP | `http` | 一个 `https://.../index.json` |
| Git 仓库 | `git` | `gh:ScoopInstaller/Main` 这类，取 raw 文件 |
| Scoop bucket | `scoop` | 读 Scoop 清单，先转成我们的格式再用 |

`builtin` 和 `dir` 不需要联网；`http`/`git`/`scoop` 需要联网，但会把结果缓存到盘上。

---

## 2. `index.json`

### 2.1 完整示例（含预留字段）

```json
{
  "schema": 1,
  "name": "core",
  "title": "PortableDev 核心包",
  "description": "",
  "channel": "stable",
  "priority": 100,
  "generated": "2026-11-23T20:00:00+08:00",
  "generator": "pd 2.0.0",
  "homepage": "",
  "signature": "",
  "revoked": [],
  "packages": {
    "firefox": {
      "latest": "139.0",
      "title": "Firefox",
      "tags": ["browser", "gui"],
      "deprecated": false,
      "replaces": [],
      "provides": ["browser"],
      "versions": {
        "139.0": {
          "url": "https://example.com/firefox-139.0.pdpkg",
          "mirrors": ["https://mirror.example.com/firefox-139.0.pdpkg"],
          "sha256": "0000000000000000000000000000000000000000000000000000000000000000",
          "size": 62345216,
          "published": "2026-11-01T12:00:00+08:00",
          "channel": "stable",
          "platform": "win-x64",
          "minKernel": ">=1",
          "requires": [{ "id": "msvc", "range": ">=14.40" }],
          "yanked": false,
          "yankedReason": "",
          "meta": {}
        }
      }
    }
  },
  "x-": {}
}
```

### 2.2 顶层字段

| 字段 | 必填 | 说明 |
|---|---|---|
| `schema` | 是 | 格式版本，当前 1 |
| `name` | 是 | 源名，全局唯一，`[a-z][a-z0-9-]*` |
| `title` / `description` | 否 | 展示用 |
| `channel` | 否 | 这个源默认的渠道：`stable` / `beta` / `dev` |
| `priority` | 否 | 越小越优先，默认 500 |
| `generated` | 否 | 生成时间 |
| `generator` | 否 | 用什么生成的，排查用 |
| `homepage` | 否 | 主页 |
| `signature` | 否 | 预留：整份索引的签名 |
| `revoked` | 否 | 预留：被撤销的包名/版本 |
| `packages` | 是 | 包名 → 包条目 |
| `x-` | 否 | 实验区 |

### 2.3 包条目

| 字段 | 必填 | 说明 |
|---|---|---|
| `latest` | 是 | 默认安装的版本 |
| `title` / `tags` | 否 | 搜索和展示 |
| `deprecated` | 否 | 整个包标记废弃，安装时警告 |
| `replaces` / `provides` | 否 | 虚拟包与替代关系 |
| `versions` | 是 | 版本号 → 版本条目 |

### 2.4 版本条目

| 字段 | 必填 | 说明 |
|---|---|---|
| `url` | 是 | `.pdpkg` 的下载地址 |
| `sha256` | 是 | `.pdpkg` 文件的哈希 |
| `size` | 否 | 字节数，用来预算空间和进度 |
| `mirrors` | 否 | 备用地址 |
| `published` | 否 | 发布时间 |
| `channel` | 否 | 该版本属于哪个渠道 |
| `platform` / `minKernel` | 否 | 兼容性约束（`minKernel` 比的是 ABI 整数），不满足则跳过 |
| `requires` | 否 | 包依赖（会在真正安装时再核对一遍） |
| `yanked` / `yankedReason` | 否 | 已撤回：默认不装，显式 pin 才允许 |
| `meta` | 否 | 任意附加信息 |

**索引里只放"怎么拿到"的信息，不放完整说明书。** 完整说明书在 `.pdpkg` 里的
`package.json`（见 [package-spec.md](package-spec.md)）。这样索引可以很小，也永远落后于包本身。

---

## 3. 源配置 `sources.json`

存在 `config\sources.json`（随盘走）：

```json
{
  "schema": 1,
  "sources": [
    { "name": "local",  "kind": "dir",
      "path": "{{STICK}}\\packages", "priority": 10, "enabled": true },
    { "name": "scoop-main", "kind": "scoop",
      "repo": "ScoopInstaller/Main", "priority": 300,
      "ttl": "24h", "enabled": false },
    { "name": "core", "kind": "builtin", "priority": 100, "enabled": true }
  ],
  "defaultSource": "",
  "allowPrerelease": false,
  "offlineOnly": false,
  "x-": {}
}
```

| 字段 | 说明 |
|---|---|
| `name` | 源名，唯一 |
| `kind` | `builtin` / `dir` / `http` / `git` / `scoop` |
| `path` / `url` / `repo` | 按 kind 选一个 |
| `priority` | 越小越优先；同值时按声明顺序 |
| `enabled` | 关掉的源不参与解析，但缓存保留 |
| `ttl` | 索引多久算过期（`30m` / `24h` / `always` / `never`） |
| `mirrorOf` | 预留：声明自己是某个源的镜像 |
| `headers` | 预留：自定义请求头 |
| `x-` | 实验区 |

---

## 4. 解析顺序（`pd install firefox` 背后发生了什么）

```
1. 收集候选    所有 enabled 的源里，找出提供 firefox 的
2. 过滤        平台不符、minKernel 不满足、yanked、channel 不匹配的剔除
3. 选源        按 priority 从小到大；同优先级按 sources.json 里的顺序
4. 选版本      默认取该源的 latest；带 @版本 时精确匹配；带范围时取最大满足
5. 确认        --source 指定了源就用它；--pin 直接锁定，跳过 3、4
6. 下载        .pdpkg 按 url → mirrors 顺序尝试
7. 校验        比对 sha256；不符就换镜像，全都失败则报错
8. 解包读说明  从 .pdpkg 里读 package.json，再核一遍平台和依赖
9. 走安装流程  见架构第 8 节
```

### 4.1 同名冲突

两个源都提供 `firefox` 时，**不弹窗、不猜**，按固定规则：

1. `--source <name>` 明确指定 → 用它；
2. 否则按 `priority` 小的赢；
3. `priority` 相同 → 按 `sources.json` 里的声明顺序，靠前的赢；
4. `pd doctor` 会把"被遮蔽的候选"列出来，方便排查。

**永远不静默合并两个源的版本列表**——那样会出现"版本号来自 A、哈希来自 B"的怪事。

### 4.2 版本约束

命令行支持：

```
pd install firefox            latest
pd install firefox@139.0      精确
pd install firefox@^139       兼容范围（>=139 <140）
pd install firefox@~139.0     补丁范围（>=139.0 <139.1）
pd install firefox@latest     显式最新
```

范围语法与 `ext.json` 一致：`>=`、`<`、`==`、`^`、`~`，可用空格组合（见 spec.md 2.4）。
不写范围时：默认 `latest`，但**跳过 yanked 和 prerelease**（除非 `allowPrerelease`）。

### 4.3 版本被撤回（yanked）

- 默认不装；
- 已经装了的会在 `pd outdated` 里标出来，提示"该版本已撤回"；
- 显式 `@版本` 时仍然允许，但要求 `--force`。

---

## 5. 本地目录源

`kind: dir` 支持两种形态，优先第一种：

1. **有 `index.json`**：直接用，最快；
2. **没有 `index.json`**：扫描目录里的 `*.pdpkg`，现场生成一个临时索引
   （读每个包的 `package.json` + 算文件哈希）。

第二种是为了"把几个 `.pdpkg` 丢进 `packages\` 就能用"。
包不多时（几十个）扫描很快；多了就建议跑一次 `pd index build`。

---

## 6. 索引的生成

```
pd index build <目录> [--out index.json] [--name <源名>] [--priority 500]
```

做的事情：

1. 扫描 `<目录>` 下所有 `*.pdpkg`；
2. 打开每个包，读 `package.json`，取 `id` / `version` / 平台等；
3. 算 `.pdpkg` 文件的 `sha256` 和大小；
4. 按版本归并，`latest` 取最大版本（跳过 yanked）；
5. 输出 `index.json`。

**不做的**：不下载、不安装、不联网。

内置源（`builtin`）由发布脚本在打包时生成，索引和程序同版本，保证"离线也能装核心包"。

---

## 7. 缓存

```
state\sources\<源名>\index.json      最近一次拉到的索引
state\sources\<源名>\meta.json       拉取时间、ETag、内容哈希
cache\blobs\<sha256>                 下载过的 .pdpkg（也是包文件本体）
```

规则：

| 情况 | 行为 |
|---|---|
| 索引未过期（`ttl` 内） | 直接用缓存 |
| 索引过期 | 后台刷新；网络失败就用旧的，并提示"索引可能过期" |
| `--refresh` | 强制刷新 |
| `--offline` | 只禁止联网，不禁止用盘上的东西（见 7.1） |
| 缓存损坏（哈希不符） | 删掉重拉；离线时报错 |

### 7.1 离线判定（逐包，不是一刀切）

`--offline` 只做一件事：**禁止发起网络请求**。能不能装，按包逐个判断：

| 包的情况 | 离线能不能装 |
|---|---|
| `files\` 里自带内容 | **能**，完全不联网 |
| 需要下载，但 `cache\blobs\<哈希>` 里已有 | **能**，直接用缓存 |
| 需要下载，缓存里没有 | **不能**，报错并给补救命令 |

报错模板（错误信息必须能照着做）：

```
包 firefox 139.0 需要联网下载，当前是 --offline。
补救：
  pd fetch firefox@139.0        # 联网预取，之后可离线安装
  pd install firefox@139.0      # 去掉 --offline
```

`pd fetch` 是纯预取：下载到 `cache\blobs\`，装不装由后面的命令决定。

另外，**索引本身也要在缓存里**才能离线解析包名。索引没有时报错：

```
源 core 的索引缓存不存在，无法离线解析包名。
补救：pd source refresh      # 联网刷新一次
```

---

## 8. 校验链

一条完整的信任链，任何一环断了都不装：

```
索引（可选签名）
  └─ 版本条目：.pdpkg 的 sha256
       └─ 下载 .pdpkg → 比对 sha256
            └─ 解包读 package.json
                 └─ 若有 source.url：下载 → 比对 package.json 里的 sha256
                      └─ 解压 → 算目录指纹 → 写进记录
```

注意两个哈希是**不同东西**：

| 哈希 | 算谁 | 存在哪 |
|---|---|---|
| 外层 | `.pdpkg` 文件本身 | 索引里 |
| 内层 | 包要下载的那个压缩包 | `package.json` 里 |
| 目录指纹 | 解压后的目录 | 安装记录里 |

---

## 9. 搜索

```
pd search firefox            在当前所有源里搜（用缓存，离线可用）
pd search --source scoop jq  只在某个源里搜
pd search --installed        只搜已装的
```

搜索只读缓存索引，不联网。要联网用 `--refresh`。

结果排序：完全匹配 > 前缀匹配 > 包含；同分组按源优先级、再按名字。

---

## 10. 命令

```
pd source list                    看有哪些源、优先级、是否启用、索引新旧
pd source add <name> <url|path|gh:owner/repo> [--kind ...] [--priority N]
pd source remove <name>
pd source enable|disable <name>
pd source refresh [<name>]        强制刷新索引
pd index build <dir>              从目录生成索引
pd index inspect <file|url>       看索引里有什么，不安装
```

---

## 11. 错误

| code | 含义 | `hint` 里给什么 |
|---|---|---|
| `ERR_SOURCE_UNREACHABLE` | 源连不上 | 检查网络/代理，或用 `--offline` |
| `ERR_INDEX_INVALID` | 索引格式不对 | 哪个字段、哪一行 |
| `ERR_PKG_NOT_FOUND` | 所有源里都没有 | 相近的包名建议 |
| `ERR_PKG_AMBIGUOUS` | 多个源都有，需要指定 | 列出候选和 `--source` 用法 |
| `ERR_PKG_YANKED` | 版本已撤回 | 推荐替代版本，`--force` 说明 |
| `ERR_PKG_PLATFORM` | 平台不匹配 | 实际平台 vs 要求的平台 |
| `ERR_HASH` | 哈希不符 | 已试过哪些镜像 |
| `ERR_OFFLINE_MISS` | 离线且缓存里没有 | 先联网刷新一次 |

---

## 12. 预留（现在不做，但结构留好）

| 预留 | 用途 |
|---|---|
| `signature` / `signatures` | 索引签名，防止被中间人改 |
| `revoked` | 全局撤回名单 |
| `mirrorOf` / `mirrors` | 镜像源、CDN 加速 |
| `channel` 多套 | stable / beta 并存 |
| 增量索引 | 只传变化的部分，目前全量 |
| `meta` / `x-` | 埋点、第三方扩展字段 |

---

## 13. 还没定的

1. ~~`config\settings.json` 的字段~~ → 已定，见 [kernel.md](kernel.md) 第 2 节。
2. ~~`pd pkg pack` 的产物细节~~ → 已定，见 [commands.md](commands.md) 5.13。
3. 安装计划（dry-run）给人看的格式。
4. 源之间的包迁移（比如从 scoop 源迁到自己的 core 源）。
