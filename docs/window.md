# PortableDev v2 规范：窗口壳与页面桥

> 配套文档：[architecture.md](architecture.md)、[spec.md](spec.md)、[commands.md](commands.md)、[kernel.md](kernel.md)
> 本文管：**窗口从哪加载页面、页面怎么调命令、事件怎么到页面、出错怎么显示、页面之间怎么隔离**。
> 本文不管：具体页面长什么样（那是 `ui` 扩展的事）。
>
> **实现状态（2026-11-25）**：窗口壳 `src/PortableDev.Window`（`pd-window.exe`）+ 内置 `ui` 扩展已落地。
> 与本文的差异记在第 11 节。

---

## 1. 窗口壳的定位

窗口壳（`window.exe`）是**纯容器**，和 `pd.exe` 平级：

| 它做 | 它不做 |
|---|---|
| 开窗口、加载扩展提供的页面 | 不写业务逻辑 |
| 把页面的调用转给内核 | 不自己判断"能不能装" |
| 把内核的事件转发给页面 | 不缓存业务数据 |
| 处理主题、缩放、多页签 | 不提供内置页面（没装 `ui` 扩展就是空窗） |

它和命令行用的是**同一套命令、同一套事件、同一套权限**（架构 3.7），
所以"命令行能做的，界面也能做；界面能做的，命令行也能做"。

---

## 2. 页面从哪来

### 2.1 自定义协议，不用 `file://`

WebView2 里一律用**虚拟主机映射**：

```
https://pd.local/<扩展名>/<页面路径>
```

- 壳启动时把每个 `ui` 扩展的目录映射成 `https://pd.local/<扩展名>/`；
- 理由：`file://` 下 `fetch`、ES module、CORS、Service Worker 全是坑，
  虚拟主机让页面像普通网站一样工作；
- 页面的相对路径直接可用（`./assets/app.js`）。

### 2.2 页面声明

页面在扩展的 `ext.json` 里声明（spec 2.9）：

```json
"pages": [
  { "id": "packages", "title": "软件包", "entry": "ui/packages.html",
    "icon": "box", "order": 20, "nav": "sidebar", "requires": [] }
]
```

| 字段 | 说明 |
|---|---|
| `id` | 页面标识，扩展内唯一 |
| `title` | 显示名 |
| `entry` | HTML 路径，相对扩展目录 |
| `icon` | 图标名（壳自带一套图标） |
| `order` | 排序 |
| `nav` | `sidebar` / `menu` / `hidden` |

### 2.3 页面怎么被发现

- 内核启动时扫描扩展，汇总所有 `pages`；
- 壳连上内核后调用 **`pd ui pages --json`** 拿清单（commands.md 4.9）——
  这是一条普通的内核命令，不需要给壳开专用通道；
- 内核没在跑 → 壳先拉起内核，再取清单；
- 一个页面都没有 → 空窗，并显示一行提示"没有安装界面扩展"（可关）。

---

## 3. 宿主桥

### 3.1 页面看到的东西

壳往每个页面注入一个全局对象：

```js
window.pd = {
  version: "2.0.0",
  extension: "ui-packages",   // 这个页面属于哪个扩展
  theme: "dark",
  locale: "zh-CN",

  // 执行一条命令，返回任务 id
  exec(argv, opts) -> Promise<taskId>,

  // 订阅事件（按任务 id，或全局）
  on(event, handler) -> unsubscribe,
  off(event, handler),

  // 取消
  cancel(taskId) -> Promise<void>,

  // 回应 prompt（交互）；不调用就让内核超时失败
  reply(promptId, value) -> Promise<void>,

  // 读配置（只读）
  config() -> Promise<object>,

  // 当前环境摘要（盘、根、版本）
  env() -> Promise<object>,

  // 跳到另一个页面（同页签）
  navigate(pageId, params) -> Promise<void>
}
```

`navigate` 是 v2 唯一的页面跳转方式：同页签替换当前页面，不做新窗口、不做嵌套页签。
`params` 作为查询串传给目标页面；跨扩展跳转用 `扩展名:页面id`。

`reply` 是**必须**的：命令行有 stdin 自然完成交互，页面没有它就只能等超时。
`value` 的类型跟着 `prompt.kind` 走（`confirm` → 布尔，`input` → 字符串，
`select` → 选项值，`multiselect` → 数组）；取消交互用 `reply(promptId, null)`。

### 3.2 `exec` 的语义

```js
const id = await pd.exec(["install", "firefox"]);
pd.on("evt", (e) => {
  if (e.id !== id) return;
  if (e.type === "progress") renderProgress(e);
  if (e.type === "done") finish(e);
});
```

- `argv` 和命令行**完全一样**（`["install", "firefox", "--dry-run"]`）；
- 返回的是任务 id，事件按 `id` 推送（和 `pd --json` 同一条流）；
- **页面的权限 = 它所属扩展的权限**：扩展没声明 `{ "kind": "ui", "allow": true }` 就调不了；
- **能调哪些命令**：默认只有本扩展 `commands` 里声明的命令 + 全局只读命令
  （`list` / `info` / `search` / `outdated` / `doctor` / `help` / `env` / `version` / `config get` / `log` / `ui pages`）；
  要调别人的命令，扩展必须写 `{ "kind": "commands", "allow": ["install"] }`，没授权就 `exec` reject（`ERR_UNSUPPORTED`）；
- **关标签页 / 切页 / 页面崩溃都不取消任务**——任务归内核管，只有显式 `cancel` 才停；
- 页面卸载后，壳把它的订阅清掉；任务照常跑完，结果留在日志里。

### 3.3 和命令行的区别

| | 命令行 | 页面 |
|---|---|---|
| 命令 | 直接给 `argv` | 同一套 `argv` |
| 输出 | 渲染到终端 / NDJSON | 事件流 → 页面自己画 |
| 交互（prompt） | 终端问答 | 页面弹窗，回 `prompt_reply` |
| 取消 | `Ctrl+C` | `pd.cancel(taskId)` |

**不新增"业务命令"**。页面需要什么，就调已有命令；缺命令说明命令面要补，而不是给界面开小灶。

唯一的例外是 `pd ui pages`：它是**内核自己的清单命令**（commands.md 4.9），
不是给界面专用的——命令行敲 `pd ui pages` 一样能用，只是通常没人这么干。

---

## 4. 事件

### 4.1 壳转发的规则

壳只做转发，不改内容：

```
内核 → 壳 → 页面（原样 JSON）
```

- 页面按 `type` 自己决定怎么渲染；
- 不认识的事件类型直接忽略（spec 4.3 的开放集合）；
- `--json` 那套 NDJSON 和这里的事件是同一份数据。

### 4.2 订阅与过滤

```js
pd.on("evt", handler)                 // 所有任务
pd.on("evt:r7", handler)              // 单个任务
pd.on("ext", handler)                 // 扩展状态变化（spec 4.3 的 `ext` 事件）
```

壳内部维护"任务 → 订阅者"的映射；没人订阅的任务事件直接丢弃（不排队、不堆积）。

---

## 5. 出错怎么显示

| 层 | 表现 |
|---|---|
| 桥调用失败（参数错、没权限） | `exec` 的 Promise reject，页面自己 catch |
| 命令失败（`done.ok = false`） | 事件里带 `error`，页面在结果区显示 |
| 内核连不上 | 壳显示一条顶部横幅："内核未运行，正在拉起…"，重试 3 次后停下并给"重启内核"按钮 |
| 扩展加载失败 | 页面清单里那一项不出现；`pd doctor` 能看到原因 |
| 页面自己崩了 | 壳给一个"重新加载"按钮，不影响其它页面 |

原则：**错误必须能追到原因**——显示 `error.code` 和 `hint`，不要只显示"出错了"。

---

## 6. 隔离与安全

页面是网页，按网页的规矩来：

| 措施 | 说明 |
|---|---|
| 一个扩展一个源 | `https://pd.local/<扩展名>/`，扩展之间不能直接读对方的文件 |
| CSP | 默认 `default-src 'self'; connect-src 'self'; img-src 'self' data:`，不允许外链脚本 |
| 禁用远程内容 | 页面里的 `<script src="https://...">` 一律被 CSP 拦掉 |
| 无 Node 集成 | 页面只有 `window.pd` 一个出口，没有 `require`、没有文件系统 |
| 桥在主线程外 | 大表格不阻塞渲染 |
| 权限跟随扩展 | `ui` 扩展的 `permissions` 决定它能调什么 |

**不做的事**：不做页面之间的 RPC、不做页面访问磁盘、不做 `postMessage` 到别的源。

---

## 7. 主题、语言、DPI

| 项 | 做法 |
|---|---|
| 主题 | 壳把 `ui.theme` 解析成 `dark` / `light`，注入 `pd.theme` 和 `<html data-theme>`；页面只用 CSS 变量 |
| 语言 | `ui.language` 注入 `pd.locale`，页面自己决定翻译 |
| DPI | WebView2 跟随系统缩放；壳不额外缩放 |
| 键盘 | 壳提供 `Ctrl+1..9` 切页、`Ctrl+R` 重载、`F5`；页面自己处理其余快捷键 |

---

## 8. 没有 `ui` 扩展时

- 窗口能开，显示"未安装界面扩展"提示 + "恢复"按钮（调用 `pd doctor --fix`，从 `app\baseline\` 恢复内置扩展）；
- 命令行完全不受影响；
- 这是有意的：**界面是可选组件**（架构 3.7）。

`ui` 是**内置扩展**：随程序发布在 `app\extensions\ui`（只读基线，见 kernel.md 1.1），
不是包、也不通过 `pd install` 安装——包永远不带界面（架构 5.1）。
"没有界面"只有两种情况：`ui` 文件损坏/缺失（按钮调 `pd doctor --fix` 从 `app\baseline\` 恢复），
或它被禁用（把 `enabled` 改回 `true`）。
用户额外的界面扩展放在 `v2\extensions\`，用 `pd ext update <名> <目录|zip>` 安装，页签并列出现。

---

## 9. 验收清单

1. 只装内核和 `pkgmgr`、不装 `ui` → 开窗显示提示，命令行正常；
2. 装一个最小 `ui` 扩展（一个页面）→ 能打开、能调 `pd.exec(["list"])` 拿到数据；
3. 页面调 `install` → 进度条动、结束后显示摘要；
4. 页面调一个没权限的命令 → Promise reject，提示 `ERR_UNSUPPORTED`；
5. 关掉页面标签 → 任务不中断；
6. 内核被 `pd kernel stop` → 壳显示横幅并能重启内核；
7. 页面里写 `<script src="https://example.com/x.js">` → 被 CSP 拦下；
8. 两个 `ui` 扩展各有页面 → 侧边栏都出现，互不干扰。

---

## 10. 实现状态与差异（2026-11-25）

已实现：

- Windows 壳 `src/PortableDev.Window`（WinForms + WebView2 控件，`pd-window.exe`）：
  侧边栏列出所有扩展声明的页面，右侧加载页面；
- 页面来源：`pd ui pages --json`（内核内置命令，扫描 `ext.json` 的 `pages`；非法声明会被拒绝并记日志：
  entry 只允许扩展目录内的相对路径（禁 `/`、`\\` 开头、盘符、`..`、NUL），id 在扩展内唯一，`nav` 只认
  `sidebar`/`menu`/`hidden`，其余回退为 `sidebar`）；
- 虚拟主机：`https://pd.local/<扩展名>/<entry>`，映射到扩展目录（命令行 --extensions 指定，默认探测）；
  WebView2 用 `--proxy-server=direct://` 绕过系统代理——假域名走代理会固定卡几秒才回落直连；
- 宿主桥 `window.pd`：`exec` / `on` / `off` / `cancel` / `reply` / `env` / `navigate`，另有便捷方法
  `run(argv)`（exec + 收集事件直到 done）；`config()` 尚未实现；
- 事件按任务转发（`evt` / `evt:<id>`），prompt 带 `promptId`，页面用 `pd.reply` 回；
- 内置 `ui` 扩展：概览 / 软件包 / 日志三个页面，只通过 `window.pd` 调命令；
- 稳定性：窗口在 `Shown` 后才初始化；WebView2 初始化失败重试 3 次（每次换新的用户数据目录，
  旧实例占用不再以 `0x800700AA` 卡死）；失败写 `<盘根>/state/logs/window-error.log`（HResult + 堆栈 + 重试号）；
  同一盘根同时只开一个窗口（互斥量，第二个直接退出）；关窗释放内核连接；`--root` 会去掉尾部分隔符与多余引号；
- 测试：`tools/window-e2e.py`——无头 self-test 验证内核链路与页面清单；`--smoke <文件>`
  启动真实 WebView2、加载首页并断言 `window.pd` 注入成功；另有真实 apphost 部署、并发双窗口、
  无 ui 扩展、非法 page entry 四组用例；
- 部署：`tools/deploy-dev.py` 生成 `.run-app/`（app 二进制 + 扩展 + 已初始化的盘根 + 启动器），
  窗口壳在管道不存在时会用 `DOTNET_ROOT/dotnet.exe` 自动拉起内核，双击启动器即可用。

差异 / 未做：

- WebView2 SDK 通过 `vendor/nuget/` 里的 `Microsoft.Web.WebView2` 包离线构建（`NuGet.config` 只用本地源），
  没有把运行时打进盘；机器上需要装 WebView2 Runtime（Win10/11 一般随 Edge 已有）；
- 主题/语言从 `config/settings.json` 注入 `pd.theme` / `pd.locale`；图标集、快捷键、多页签还没做；
- CSP 目前靠页面自身约束（虚拟主机映射不能加响应头），没有强制注入；
- 不支持 `pd.config()`、托盘通知、多窗口（按第 10 节仍待定）。

## 11. 还没定的

1. ~~页面之间的导航~~ → 已定：`pd.navigate`，同页签，不做新窗口（见 3.1）。
2. 通知（安装完成后要不要系统托盘提示）。
3. 图标集（壳自带哪些图标、扩展能不能自带）。
4. 多窗口（v2 不做；`navigate` 已覆盖基本需求）。
