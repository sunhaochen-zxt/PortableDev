# PortableDev v2

Windows 便携盘上的“开发环境随身走”工具：一块 NTFS 盘插到任何机器上，内核按需拉起扩展，
把包内容、接线（PATH / 环境变量 / HKCU / 启动器）和账本/存档点管起来，拔盘即走。

V2 是一次微内核重写（V1 在仓库 `main`）：内核只做协议与调度，磁盘操作、下载、解包、装包、
回滚都由扩展提供；store 内容寻址且不可变，所有改机器动作先写审计账本再执行。

## 结构

```
src/PortableDev.Core        协议帧/信封、配置、身份、日志契约、扩展清单
src/PortableDev.Kernel      pd-kernel：管道、派发、监督器、恢复、单实例锁
src/PortableDev.Shell       pd.exe：参数、连接、事件渲染、退出码
app/extensions/             storage / rollback / log / download / unpack / pkgmgr
tests/PortableDev.Core.Runner  单元测试（18 项）
tools/                      e2e 驱动 + 文档一致性检查
docs/                       规范与实现说明（architecture / spec / commands / …）
```

## 构建

要求：Windows 10/11、.NET 8 SDK、Node.js（扩展运行时）、NTFS 盘（junction 与同卷 rename）。

```bash
dotnet build src/PortableDev.Kernel/PortableDev.Kernel.csproj -c Debug --no-incremental
dotnet build src/PortableDev.Shell/pd.csproj -c Debug --no-incremental
dotnet build tests/PortableDev.Core.Runner/PortableDev.Core.Runner.csproj -c Debug --no-incremental
```

> 这块盘上 mtime 粒度不可靠，必须加 `--no-incremental`，否则增量构建可能跳过编译。

## 测试

套件会自己拉起/停止内核，必须串行跑（先删 `.run/state/lock.json`）：

```bash
dotnet tests/PortableDev.Core.Runner/bin/Debug/net8.0/PortableDev.Core.Runner.dll   # 18
python -u tools/e2e.py .run                                                        # 51（带日志提供者 61）
PD_LOG_FIXTURE=log-ext python -u tools/storage-e2e.py .run                          # 60
python -u tools/pkg-e2e.py .run                                                     # 37
python -u tools/pd-e2e.py .run                                                      # 29
python -u tools/wire-e2e.py .run                                                    # 28
python -u tools/generation-e2e.py .run                                              # 26+3
python tools/check_docs.py docs                                                     # 文档一致性
```

## 设计文档

从 [docs/architecture.md](docs/architecture.md) 开始；协议见 [docs/spec.md](docs/spec.md)，
命令面/退出码见 [docs/commands.md](docs/commands.md)，各实现细节在 `docs/impl-*.md`。

## 许可

见仓库根目录 `LICENSE`（AGPL-3.0）。
