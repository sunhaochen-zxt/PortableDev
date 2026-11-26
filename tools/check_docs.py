#!/usr/bin/env python3
"""v2 文档一致性检查：链接、命令、错误码、退出码。

用法：python check_docs.py <docs目录>
不修改任何文件，只报告问题。
"""
import json
import re
import sys
from pathlib import Path

# 默认检查仓库里的 docs/（而不是当前目录：从仓库根跑时会把 kernel.md 之类全漏掉）。
DEFAULT_DOCS = Path(__file__).resolve().parent.parent / "docs"
DOCS = Path(sys.argv[1]) if len(sys.argv) > 1 else (DEFAULT_DOCS if DEFAULT_DOCS.is_dir() else Path("."))

# 1. 收集文档
# 审阅/报告类文件不是规范，不参与一致性检查
files = sorted(f for f in DOCS.glob("*.md") if not f.name.startswith("audit-"))
if not files:
    print("没有找到 .md 文件")
    sys.exit(2)

# README 也是规范的一部分（构建/测试/结构说明），一起检查
text = {f.name: f.read_text(encoding="utf-8") for f in files}
readme = DOCS.parent / "README.md"
if readme.is_file() and "README.md" not in text:
    text["README.md"] = readme.read_text(encoding="utf-8")
problems = []

# 2. 链接检查：](xxx.md) 或 ](xxx.md#anchor)
link_re = re.compile(r"\]\(([A-Za-z0-9_\-]+\.md)(#[^)]*)?\)")
for name, body in text.items():
    for m in link_re.finditer(body):
        target = m.group(1)
        if target not in text:
            problems.append(f"[链接] {name}: 指向不存在的文件 {target}")

# 3. 命令检查：`pd xxx` 是否都在 commands.md 里出现
commands_body = text.get("commands.md", "")
declared = set()
for m in re.finditer(r"^\|\s*`pd\s+([a-z][a-z0-9\-]*)", commands_body, re.M):
    declared.add(m.group(1))
# 再收集 pd 后跟多个词的（如 kernel stop），取第一段
# 代码块里允许出现说明性命令，只检查正文中的 `pd xxx`
pd_re = re.compile(r"`pd\s+([a-z][a-z0-9\-]*)")
known_extra = {
    "install", "remove", "upgrade", "rollback", "list", "ls", "info", "search",
    "outdated", "fetch", "gc", "apply", "clean", "doctor", "selftest", "adopt",
    "pkg", "bundle", "source", "index", "kernel", "run", "shell", "config",
    "ext", "ui", "help", "complete", "version", "log", "env", "recover",
    "raw", "rescue", "status",
}
placeholder = {"xxx"}  # 文档里作为占位符出现的，不算真命令
for name, body in text.items():
    for m in pd_re.finditer(body):
        cmd = m.group(1)
        if cmd in placeholder:
            continue
        if cmd not in known_extra and cmd not in declared:
            problems.append(f"[命令] {name}: `pd {cmd}` 不在命令表里")

# 4. 错误码：ERR_XXX 是否都在 spec.md 6.2 声明
spec_body = text.get("spec.md", "")
declared_errs = set(re.findall(r"`(ERR_[A-Z_]+)`", spec_body))
for name, body in text.items():
    for m in re.finditer(r"`(ERR_[A-Z_]+)`", body):
        if m.group(1) not in declared_errs:
            problems.append(f"[错误码] {name}: {m.group(1)} 未在 spec.md 6.2 声明")

# 5. 退出码：commands.md 第 3 节声明的码集合
exit_block = re.search(r"## 3\. 退出码(.*?)## 4\.", commands_body, re.S)
exit_codes = set()
if exit_block:
    for m in re.finditer(r"^\|\s*`(\d+)`", exit_block.group(1), re.M):
        exit_codes.add(m.group(1))
# 检查"失败码"列里出现的数字都在集合里（130 允许）
for m in re.finditer(r"\|\s*([0-9/]+)\s*\|\s*$", commands_body, re.M):
    for code in m.group(1).split("/"):
        if code and code not in exit_codes:
            problems.append(f"[退出码] commands.md: 用了未声明的退出码 {code}")

# 6. 章节引用：见 xxx.md N 节 —— 只检查文件存在，不解析节号
for name, body in text.items():
    for m in re.finditer(r"见 \[?([A-Za-z0-9_\-]+\.md)\]?", body):
        if m.group(1) not in text:
            problems.append(f"[引用] {name}: 提到 {m.group(1)} 但文件不存在")

# 7. 开关闭合：正文里出现的 `--xxx` 必须在 commands.md 里出现
flag_re = re.compile(r"`(--[a-z][a-z0-9-]*)`")
declared_flags = set(m.group(1) for m in flag_re.finditer(commands_body))
# 文档里明确说"没有这种开关"的，不算
# 不是 pd 自己的开关：文档里举的外部程序参数 / 占位符
nonexistent_flags = {"--no-junction", "--user-data-dir", "--x", "--no-incremental"}  # 外部工具开关
for name, body in text.items():
    for m in flag_re.finditer(body):
        flag = m.group(1)
        if flag not in declared_flags and flag not in nonexistent_flags:
            problems.append(f"[开关] {name}: {flag} 未在 commands.md 声明")

# 8. 退出码唯一：第 3 节表里同一个码只能出现一次
if exit_block:
    codes = re.findall(r"^\|\s*`(\d+)`\s*\|", exit_block.group(1), re.M)
    for code in sorted(set(codes)):
        if codes.count(code) > 1:
            problems.append(f"[退出码] commands.md: `{code}` 在退出码表里出现 {codes.count(code)} 次")

# 9. 设置键：SettingCatalog 里的键必须都写进 kernel.md 2.1（反向也不允许有幽灵键）
catalog = DOCS.parent / "src" / "PortableDev.Core" / "Config" / "SettingCatalog.cs"
if catalog.exists():
    code_keys = set(re.findall(r'new\("([A-Za-z][A-Za-z0-9.]*)"', catalog.read_text(encoding="utf-8")))
    settings_block = re.search(r"### 2\.1 字段(.*?)### 2\.2", text.get("kernel.md", ""), re.S)
    doc_keys = set()
    if settings_block:
        doc_keys = set(re.findall(r"^\|\s*`([A-Za-z][A-Za-z0-9.]*)`", settings_block.group(1), re.M))
    for key in sorted(code_keys - doc_keys):
        problems.append(f"[设置] kernel.md 2.1: 代码里的设置键 {key} 未写入文档")
    for key in sorted(doc_keys - code_keys):
        problems.append(f"[设置] kernel.md 2.1: 文档里的设置键 {key} 在代码里不存在")

# 10a. 错误码常量：ErrorCodes.cs 里的每个 ERR_* 都必须写进 spec.md，反之亦然；
#      commands.md 6.3 映射表里的码必须真实存在。
error_codes_file = DOCS.parent / "src" / "PortableDev.Core" / "Protocol" / "ErrorCodes.cs"
if error_codes_file.exists():
    source = error_codes_file.read_text(encoding="utf-8")
    code_values = set(re.findall(r'\"(ERR_[A-Z_]+)\"', source))
    for code in sorted(code_values - declared_errs):
        problems.append(f"[错误码] ErrorCodes.cs: {code} 未在 spec.md 6.2 声明")
    for code in sorted(declared_errs - code_values):
        problems.append(f"[错误码] spec.md 6.2: {code} 在 ErrorCodes.cs 里不存在")
    exit_map = re.search(r"### 6\\.3 退出码与错误码的对应(.*?)(?:\n## |\Z)", commands_body, re.S)
    if exit_map:
        for code in sorted(set(re.findall(r"`(ERR_[A-Z_]+)`", exit_map.group(1)))):
            if code not in code_values:
                problems.append(f"[错误码] commands.md 6.3: {code} 在 ErrorCodes.cs 里不存在")

# 10. 扩展命令：ext.json 注册的命令必须出现在 commands.md（pd <name>）
ext_root = DOCS.parent / "app" / "extensions"
if ext_root.exists():
    extension_commands = set()
    for manifest_path in ext_root.glob("*/ext.json"):
        try:
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            continue
        for command in manifest.get("commands") or []:
            name = command.get("name") if isinstance(command, dict) else None
            if name:
                extension_commands.add(name)
    for name in sorted(extension_commands):
        if f"`pd {name}" not in commands_body:
            problems.append(f"[命令] commands.md: 扩展命令 pd {name} 未写入命令表")

# 11. 命令实现状态：commands.md §4 的命令要么能解析到实现，要么被"实现状态"明确标为未实现
builtin_source = DOCS.parent / "src" / "PortableDev.Kernel" / "Dispatch" / "BuiltinCommands.cs"
implemented = set()
if builtin_source.exists():
    for match in re.finditer(r'Add\("([a-z][a-z0-9 ]*)"', builtin_source.read_text(encoding="utf-8")):
        name = match.group(1).strip()
        implemented.add(name)
        implemented.add(name.split(" ")[0])
if ext_root.exists():
    for manifest_path in ext_root.glob("*/ext.json"):
        try:
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            continue
        for command in manifest.get("commands") or []:
            name = command.get("name") if isinstance(command, dict) else None
            if name:
                implemented.add(name)

declared_unimplemented = set()
unimpl_match = re.search(r"\*\*未实现[^\n]*\*\*[：:](.*?)(?:\n\n|\n#)", commands_body, re.S)
if unimpl_match:
    for token in re.findall(r"`([^`]+)`", unimpl_match.group(1)):
        for word in re.split(r"[^a-z-]+", token):
            if word:
                declared_unimplemented.add(word)

for token in sorted(declared_unimplemented):
    if token in implemented:
        problems.append(f"[命令] commands.md 的'未实现'清单里写着已实现的 pd {token}")

for match in re.finditer(r"^\|\s*`pd(?:\s+([a-z][a-z0-9-]*))?", commands_body, re.M):
    name = match.group(1)
    if not name:
        continue
    if name not in implemented and name not in declared_unimplemented:
        problems.append(f"[命令] commands.md §4: pd {name} 既没有实现，也没有在'实现状态'里标注")

# 输出
print(f"检查 {len(files)} 份文档，共 {sum(len(t.splitlines()) for t in text.values())} 行")
if not problems:
    print("✓ 没有发现不一致")
else:
    seen = set()
    for p in problems:
        if p not in seen:
            seen.add(p)
            print("✗", p)
    print(f"\n共 {len(seen)} 处")
    sys.exit(1)
