# 包管理实现说明（pkgmgr / download / unpack）

> 状态：可用。三种来源（`.pdpkg`、目录、URL）可装到 store 并建 current 指针；失败自动回滚。
> 相关文档：[commands.md](commands.md) 5.1、[package-spec.md](package-spec.md)、[impl-storage.md](impl-storage.md)、[impl-rollback.md](impl-rollback.md)。

---

## 1. 三个扩展的分工

| 扩展 | 职责 | 不做什么 |
|---|---|---|
| `pkgmgr` | 编排安装流程、读说明书、跑断言、决定失败回滚 | 不直接碰 store/租界（都经 storage） |
| `download` | URL → `cache\\blobs\\<sha256>`，边下边算哈希，镜像依次尝试 | 不安装、不解压 |
| `unpack` | 把压缩包解到指定目录，带路径穿越与大小上限检查 | 不下载、不判断内容 |

三者的连接全靠内核的 `call` 按能力名路由（spec 5.6），互不知道对方是谁。

## 2. 一次安装的流程（已实现部分）

```
pd install <目标>
 1. resolve   判断来源：.pdpkg / 目录 / URL（URL 需要 --sha256）
 2. 读说明书   .pdpkg 先解到租界读 package.json；目录直接读
 3. 预演       --dry-run 在这里返回计划，不碰 store
 4. 取内容     ① 有 files\  → 直接用
              ② 有 source.url → download.fetch → unpack.extract 到租界
              ③ 目录包 → 整个目录先复制进租界（用户目录不在 staging 里）
 5. 文件断言   exists / missing / json / fileHash，对着内容目录检查
 6. 收编       storage.adopt commit：算指纹 → 写审计 → rename 进 store → 写记录
 7. 接线指针   storage.current set（PATH/环境变量/注册表/启动器还没有）
 8. 提交       storage.ledger commit → 清理租界
失败时：调用 rollback.run 撤销该任务的账，然后返回原始错误
```

**路径基准**：相对路径按**调用方 shell 的 cwd** 解析（`req.ctx.cwd`），不是按扩展进程自己的
工作目录——这是 spec 3.5 上下文快照的直接后果。

## 3. 错误与退出码

| 情况 | 错误码 | 退出码 |
|---|---|---|
| 包说明书不合法 / 缺字段 | `ERR_PKG_INVALID` | 3 |
| 下载后哈希不符 | `ERR_HASH` | 3 |
| 找不到包文件 | `ERR_PKG_NOT_FOUND` | 20 |
| 源连不上 / 网络错误 | `ERR_SOURCE_UNREACHABLE`、`ERR_NET` | 15 |
| 离线且缓存没有 | `ERR_OFFLINE_MISS` | 23 |
| 按包名安装（索引未实现） | `ERR_UNSUPPORTED` | 11 |
| 路径穿越、解包不支持的格式 | `ERR_UNSUPPORTED` | 11 |
| 文件断言失败 | `ERR_ASSERT` | 12 |
| 平台不符 | `ERR_PKG_PLATFORM` | 11 |

## 4. 安全边界

- **路径穿越**：解包前先列条目，拒绝绝对路径、盘符、`..`；解包后再统计文件数与字节数，超限即删；
- **哈希**：下载边写边算，不符就删临时文件；本地文件同样校验；
- **租界**：所有解压/复制都在 storage 发的租界里，收编才进 store；
- **失败必回滚**：pkgmgr 在 catch 里调 `rollback.run`，再清理租界。

## 5. 测试

`tools/pkg-e2e.py`：**37 项**，夹具在运行时生成（zip/tar.gz + 本地 HTTP 服务器）。

| 组 | 断言 |
|---|---|
| 预演 | 退出码 0、计划事件、store 不变 |
| `.pdpkg` | 装成功、内容落盘、current 指针、记录、staging 清空 |
| 目录 | 复制进租界后收编成功 |
| 远程内容 | 下载到 blob 缓存、解压、收编 |
| URL 包 | `--sha256` 校验、重复内容复用同一 store 目录 |
| 哈希不符 | `ERR_HASH` + 退出码 3 + 不留 store 目录 |
| 路径穿越 | 拒绝并报告，租界外没有落文件 |
| 断言失败 | `ERR_ASSERT` + 退出码 12 + 不留记录 |
| 未知包名 | `ERR_UNSUPPORTED` + 退出码 11 |
| 日志联动 | `pd log --grep storage.adopt` 能看到记录 |

运行：

```bash
DN="Z:/PortableDev/tools/dotnet-sdk/8.0.426/dotnet.exe"
"$DN" build src/PortableDev.Kernel/PortableDev.Kernel.csproj
python -u tools/pkg-e2e.py .run
```

## 6. 还没做的（按优先级）

| 项 | 说明 |
|---|---|
| 源与索引 | 按包名安装、`@版本`、多源优先级、yanked —— 需要 index-spec 落地 |
| 包脚本 | `script` 段、stdout/stdin 受限协议、Job Object 隔离；目前一律不跑 |
| 更多解包 | `7z` / `msi`（盘上有 7z，接上即可） |
| 激活类断言 | 需要接线完成后才能跑命令类断言 |
| 接线 | PATH / 环境变量 / 注册表 / 启动器 —— 这是下一块（`storage.wire.*` + `apply`/`clean`） |
| 升级 / 卸载 | `pd upgrade`、`pd remove` 还没有；store 不可变，二者主要是挪指针 + 删记录 |
## 补充修复（2026-11-25 第二轮）

- 失败路径不再静默：`storage.ledger commit`、租界清理（`storage.adopt abort`）失败会以
  `log` 事件（warn）报给用户，而不是 `catch(() => {})` 吞掉。
- 恶意归档回归用例：tar 内的 symlink/hardlink 条目会在解包前被拒（`ERR_UNSUPPORTED`），
  并断言没有文件逃出目标目录、没有留下记录。
