# 外壳实现说明（pd.exe）

> 状态：可用。命令行外壳已实现并通过端到端测试。
> 相关文档：[architecture.md](architecture.md) 3.7 / 3.8、[commands.md](commands.md) 第 1–2 节、[spec.md](spec.md) 3–4 节。

---

## 1. 它是什么

`pd.exe` 是**壳**，不是扩展（架构 3.7）：

| 做 | 不做 |
|---|---|
| 找盘、连管道、必要时拉起内核 | 不写业务逻辑 |
| 解析连接类与呈现类开关 | 不解析业务参数、不校验参数 |
| 渲染事件、处理交互、透传退出码 | 不缓存命令表、不直接碰盘 |

命令表只在核心里（架构 3.8），所以 `pd` 永远不过期。

```
v2/src/PortableDev.Shell/
  Program.cs          入口：解析开关、定位盘、注册 Ctrl+C
  ShellOptions.cs     开关两段式划分
  StickLocator.cs     找盘根、读 stickId 与管道名
  KernelLauncher.cs   没有内核时拉起（PD_KERNEL 可覆盖）
  PipeClient.cs       帧收发（复用 Core 的 FrameCodec / Envelope）
  EventRenderer.cs    文本渲染与 NDJSON 输出
  PdShell.cs          握手、请求、事件循环、prompt、取消、退出码
  StdHandles.cs       防止把自身 stdout 句柄泄漏给内核（见 §6）
```

## 2. 开关划分

壳解析（决定连哪根管道、怎么画）：

| 开关 | 作用 |
|---|---|
| `--root <path>` | 盘根；缺省从当前目录向上找 `state\\stick.json` |
| `--json` | 每个事件原样输出一行 NDJSON |
| `--quiet` / `-q` | 只输出错误和最终结果 |
| `--no-color` | 关闭 ANSI |
| `--trace` | 把收发的帧打到 stderr |
| `--trace-env` | 环境快照写到临时文件（不进 `state\\`） |
| `--yes` / `-y` | 交互一律同意（壳自己答 prompt） |
| `--verbose` / `-v` | 渲染 debug 日志与 `ext` 事件 |
| `--propagate` | 用子进程退出码作为自己的退出码（`pd run` / `pd shell`） |
| `--raw <json>` | 直接把一段 JSON 当请求发出去（救急通道） |
| `--help` / `-h` | 无命令时打印壳用法；带命令时转成 `help <命令>` |

其余 argv **原样**转发给内核（`--offline`、`--timeout`、`--source`、`--dry-run` …）。

## 3. 一次调用的时序

```
解析开关
  → 定位盘根（--root 或向上找 state\stick.json）
  → 算 stickId（state\stick.json；缺失时按卷序列号派生，不写盘）
  → 管道名 = <kernel.pipePrefix>-<stickId>
  → 连接（300ms）；连不上就拉起内核，最多等 15s
  → hello（带 ctx 快照：cwd / env / tty / locale / timezone / user / interactive）
  → hello.ok 校验（协议号、kernelAbi）
  → req（argv 原样 + opts + ctx）
  → 事件循环：渲染 evt，处理 prompt，等 done
  → 退出码 = done.exitCode（--propagate 时取 done.metrics.childExitCode）
```

**内核常驻**：壳只负责拉起，不负责杀掉。`pd kernel stop` 是唯一正常的停止方式。

## 4. 渲染

- 文本模式：`step` → `[..] 标题`、`progress` → TTY 覆盖刷新 / 管道按 10% 打点、
  `table` 对齐输出、`kv` 键值、`log` 按级别加前缀、`done` 打摘要或错误；
- `--json`：每个事件一行，字段就是 `evt.data`（commands.md 6.2），便于 `jq`；
- `--quiet`：只留 warn/error 日志和终态；
- 颜色只在真终端上启用，重定向时自动关闭。

日志提供者会把记录放在 `record` 字段里，渲染器同时认“扁平事件”和“记录”两种形状。

## 5. 交互、取消、退出码

**prompt**：内核把扩展的 `prompt` 事件转给壳；壳按三种情况回 `prompt_reply`：
真终端读一行；`--yes` 直接同意（confirm → true，其余取 default）；
非交互（stdin 被重定向或 `--json`）回 `null`。
回复经内核路由到**发起该任务的那个扩展**（`ExtensionSupervisor.DeliverPromptReply`）。

**取消**：第一次 Ctrl+C 发 `ctl cancel`，内核把终态 `done`（130）发回来；
第二次 Ctrl+C 立刻退出 130。取消只影响当前任务。

**退出码**：

| 情况 | 码 |
|---|---|
| 正常完成 | `done.exitCode` |
| 内核拒绝握手 | 由 `ERR_*` 映射（`ExitCodes.ForError`） |
| 找不到盘 | 4 |
| 内核起不来 / 中途断开 | 4 / 1 |
| `--propagate` | `done.metrics.childExitCode` |

`pd kernel stop` 在内核没跑时直接返回 0（不去拉起一个内核再停掉它）；
`pd kernel status` 在内核没跑时返回 4。

## 6. 踩过的坑（都已修）

1. **句柄泄漏**：壳拉起内核时，若自身 stdout 是管道（`pd version | findstr`），
   内核会继承那个写端，父进程退出后管道仍不关，调用方（脚本/CI）会**永远等不到 EOF**。
   修法：启动时用 `SetHandleInformation` 清掉三个标准句柄的继承位（`StdHandles`）。
   这个 bug 只在“输出被重定向 + 内核由壳拉起”时出现，交互使用永远看不到。
2. **`kernel stop` 触发自动拉起**：连不上就拉内核是给业务命令用的；
   `kernel stop` / `kernel status` 要特殊处理，否则会凭空启动一个守护进程。
3. **`--yes` 被当成命令**：`pd --yes echo ask` 曾把 `--yes` 原样转发成 argv[0]。
   现在 `--yes` / `--verbose` 只在壳内生效，不转发。
4. **日志记录渲染成空行**：提供者发的是 `{type:"log", record:{...}}`，早期只读 `msg`，
   于是每行都是空字符串。现在两种形状都认。

## 7. 测试

`tools/pd-e2e.py`：**29 项**，自己起内核、跑完、收尾：

- 基本：`version`、无参数状态摘要、`--json` 纯 NDJSON 且以 done 收尾、`--quiet` 去掉表格；
- 帮助：壳用法、`help` 从内核取命令表；
- 错误：未知命令 2 + 建议、缺扩展 20、不是盘 4；
- 救急：`--raw` 直接发请求；
- 扩展命令：渲染 step/progress/log/done；
- 交互：非交互回 null、`--yes` 回 true；
- 日志提供者：`log --tail` 有记录；
- 取消：`log --follow` + Ctrl+Break → 退出 130；
- 自动拉起：停内核后用 `PD_KERNEL` 让壳自启，命令成功且内核存活。

运行：

```bash
DN="Z:/PortableDev/tools/dotnet-sdk/8.0.426/dotnet.exe"
"$DN" build src/PortableDev.Shell/pd.csproj
python -u tools/pd-e2e.py .run
```

## 8. 还没做的

| 项 | 说明 |
|---|---|
| `pd run` / `pd shell` 的 `--propagate` 端到端 | 开关与映射已实现，命令本身还等 `pkgmgr`/`storage` 落地 |
| 真终端交互 | CI 只有重定向管道；交互 prompt 需要人工在控制台验一次 |
| 颜色与宽表 | 逻辑已写，重定向下不生效，未做视觉验收 |
| 窗口壳 | `window.exe` 属于另一块（window.md） |
## 补充修复（2026-11-25 第二轮）

- `Ctrl+C` 在**连接/自动拉起阶段**也能取消（第一次按下取消连接，退出码 130）；任务运行中仍走
  `ctl cancel`，第二次按下才强退。
- 表格按**显示宽度**对齐：CJK / 全角字符按 2 列计算，截断与填充不再错位。
