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
src/PortableDev.Window      pd-window：窗口壳（WinForms + WebView2；页面来自 ui 扩展）
src/PortableDev.Elevate      pd-elevate：HKLM / HKCU / 盘外路径的提权批次助手
app/extensions/             storage / rollback / log / download / unpack / pkgmgr
                            （pkgmgr 内含 sources.js 索引解析、scripts.js 受限脚本协议）
                            proc（.NET，进程管理；构建时由 PortableDev.ProcExt 复制到 app/extensions/proc）
                            ui（内置页面：概览 / 软件包 / 日志）
tests/PortableDev.Core.Runner  单元测试（21 项）
tests/PortableDev.Proc.Runner  进程原语/管道测试（8 项）
archive/pd-job/                已归档的 pd-job helper（不再构建/部署）
tools/                      e2e 驱动 + 文档一致性检查
docs/                       规范与实现说明（architecture / spec / commands / …）
```

## 构建

要求：Windows 10/11、.NET 8 SDK、Node.js（扩展运行时）、NTFS 盘（junction 与同卷 rename）。

```bash
dotnet build src/PortableDev.Kernel/PortableDev.Kernel.csproj -c Debug --no-incremental
dotnet build src/PortableDev.Shell/pd.csproj -c Debug --no-incremental
dotnet build src/PortableDev.Elevate/PortableDev.Elevate.csproj -c Debug --no-incremental
dotnet build src/PortableDev.Proc/PortableDev.Proc.csproj -c Debug --no-incremental
dotnet build src/PortableDev.ProcExt/PortableDev.ProcExt.csproj -c Debug --no-incremental
dotnet build src/PortableDev.Window/PortableDev.Window.csproj -c Debug --no-incremental
dotnet build tests/PortableDev.Core.Runner/PortableDev.Core.Runner.csproj -c Debug --no-incremental
```

> 这块盘上 mtime 粒度不可靠，必须加 `--no-incremental`，否则增量构建可能跳过编译。

本地试玩（生成一个可双击的测试盘）：

```bash
python tools/deploy-dev.py            # 生成 .run-app/
# 双击 .run-app/启动 PortableDev.cmd 打开窗口壳（自动拉起内核）；命令行用 .run-app/pd.cmd
# 启动器只做一件事：设置 DOTNET_ROOT（随盘 .NET 不在 PATH 上）；机器装了全局 .NET 时可直接双击 pd-window.exe
```

## 测试

套件会自己拉起/停止内核，必须串行跑（先删 `.run/state/lock.json`）：

```bash
dotnet tests/PortableDev.Core.Runner/bin/Debug/net8.0/PortableDev.Core.Runner.dll   # 21
python -u tools/e2e.py .run                                                        # 51（带日志提供者 61）
PD_LOG_FIXTURE=log-ext python -u tools/storage-e2e.py .run                          # 62
PD_LOG_FIXTURE=log-alt python -u tools/storage-e2e.py .run                          # 62（换日志实现）
python -u tools/pkg-e2e.py .run                                                     # 37
python -u tools/pd-e2e.py .run                                                      # 31
python -u tools/wire-e2e.py .run                                                    # 43
python -u tools/generation-e2e.py .run                                              # 29
python -u tools/source-e2e.py .run                                                   # 34
python -u tools/script-e2e.py .run                                                   # 22
python -u tools/manage-e2e.py .run-manage                                            # 93
python -u tools/registry-e2e.py .run-registry                                        # 12（HKCU 走 pd-elevate）
dotnet tests/PortableDev.Proc.Runner/bin/Debug/net8.0/PortableDev.Proc.Runner.dll     # 8（进程原语/管道）
python -u tools/proc-e2e.py                                                          # 28（proc 扩展 + 系统枚举 + pkgmgr 接入）
python -u tools/run-e2e.py                                                           # 19（pd run/shell + exec 通道）
python -u tools/scoop-e2e.py                                                         # 22（scoop 源 + requires 递归）
python -u tools/provides-e2e.py                                                      # 18（虚拟包 + replaces）
python -u tools/window-e2e.py                                                        # 21（窗口壳：内核链路 + WebView2 桥）
python -u tools/runtime-e2e.py                                                       # 11（随盘运行时：清空 DOTNET_ROOT/PATH 后跑 CLI 与窗口）
python tools/elevation-selftest.py                                                  # 手动：UAC 提权自检
python tools/check_docs.py docs                                                     # 文档一致性
```

## 设计文档

从 [docs/architecture.md](docs/architecture.md) 开始；协议见 [docs/spec.md](docs/spec.md)，
命令面/退出码见 [docs/commands.md](docs/commands.md)，进程模型见 [docs/proc.md](docs/proc.md)，
各实现细节在 `docs/impl-*.md`。

## 许可

见仓库根目录 `LICENSE`（AGPL-3.0）。