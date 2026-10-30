# PortableDev

Portable development-environment manager for Windows. Restores a full toolchain from a
portable drive (USB stick or external disk) onto a machine that gets wiped
(restore-card / Deep Freeze style), in one command.

Status: **core + CLI + WPF GUI complete.** The core test suite is green and
`PortableDev verify` passes every declared check (15/15) on the reference stick.
`apply` is idempotent — a second run reports `nothing to do` — and a failure now rolls
the registry writes back instead of leaving half an environment behind.

## Layout

```
portable-dev/
├─ PortableDev.sln
├─ src/
│  ├─ PortableDev.Core/     pure logic: manifest, path algebra, planner   (no Win32)
│  ├─ PortableDev.Win/      Win32: volume locator, registry, junction
│  ├─ PortableDev.App/      CLI host   -> PortableDev.exe
│  └─ PortableDev.Gui/      WPF shell  -> PortableDev.Gui.exe
├─ tests/
│  └─ PortableDev.Core.Runner/   self-contained assertions (no testhost)
├─ manifest/                manifest.toml + manifest.d/*.toml  (copied to the stick)
└─ scripts/                 bootstrap + publish scripts
```

## Build

The .NET SDK lives on the drive itself, so no machine-wide install is required. Replace
`F:` with whatever drive letter the drive got on this machine:

```powershell
$D = 'F:\PortableDev\tools\dotnet-sdk\8.0.426'
$env:DOTNET_ROOT = $D
$env:PATH = "$D;$env:PATH"
$env:NUGET_PACKAGES = 'F:\PortableDev\cache\nuget'   # cache travels with the drive

dotnet build PortableDev.sln -c Release
```

Run the test suite (a plain console app, deliberately not `dotnet test`,
which is blocked by the testhost process watcher in restricted sessions):

```powershell
dotnet run --project tests\PortableDev.Core.Runner -c Release
# => ==== N passed, 0 failed ====   (exit code 0 when green; N grows with the suite)
```

If MSBuild reports a failure with no error text in a restricted session, it is the
child-node pipe being blocked; build with `-m:1 -nodeReuse:false`.

## Version control

`source\` is a git repository. On NTFS it behaves like any normal checkout. If the tree
lives on exFAT (the classic USB-stick setup), git cannot see file ownership there and
refuses to touch the work tree until the path is marked safe:

```powershell
git -c safe.directory=F:/PortableDev/source status
# or once, for all drives, on each machine:
git config --global --add safe.directory F:/PortableDev/source
```

`.gitignore` keeps `bin/`, `obj/`, `artifacts/` and `state/` out of the repository: the
last one holds registry backups that can contain secrets and belong to one machine.

## CLI

```
PortableDev locate    # detected stick root, volume label, marker
PortableDev doctor    # read-only diagnosis: stick, junction, PATH, tools
PortableDev plan      # what would change; no side effects
PortableDev apply     # execute the plan (backs up first, journals every step)
PortableDev verify    # run every tool's declared [[verify]]
PortableDev clean     # undo apply: junction, home links, env vars, managed PATH
PortableDev gui       # launch the WPF front-end (a sibling PortableDev.Gui.exe)
PortableDev shell     # open a terminal that already has the environment applied
PortableDev update    # ask upstream for newer versions (read-only)
PortableDev update --fetch jq   # download an update without installing it
```

Options: `--json`, `--yes`, `--root <path>`, `--stable <path>`, `--no-junction`,
`--no-elevate`, `--test-registry`, `--strict`, `--adopt-home`, `--no-adopt-home`,
`--no-rollback`, `--fetch`, `--trace`, `--dry-run`.

### Safety switches

| Switch | Effect |
|---|---|
| `--test-registry` | read/write `HKCU\Environment-Test` instead of the live key |
| `--no-junction` | never touch `C:\Dev`; PATH and home links use the stick's real path |
| `--no-elevate` | never prompt for UAC; fail with exit code 8 instead |
| `--root <path>` | skip auto-detection (useful when two sticks are plugged in) |
| `--adopt-home` / `--no-adopt-home` | override `[apply].home_adopt` for one run |
| `--no-rollback` | keep a half-applied environment when a step fails (default: undo) |
| `--trace` | print the computed PATH/env and change nothing |

Two sticks that both carry the marker are refused, not guessed: every command stops
with exit code 4, names each candidate, and asks for `--root`. Silently taking the first
drive letter would rewrite PATH to point at the wrong toolchain.

`apply` always writes a `state\backup\path-<ts>.reg` plus a `.txt` copy of the raw
PATH before the first mutation, and a `state\journal\<tx>.jsonl` trail. If a step fails,
the registry writes already made are undone in reverse order, using the *raw* previous
values so `%VARS%` survive; the journal records every attempted undo. The stable junction
is deliberately not rolled back — it is idempotent and undoing it would need a second UAC
prompt. `--no-rollback` opts out and keeps the partial state, which is occasionally what
you want when debugging a failure.

### `clean` undoes `apply`

`clean` is meant to leave the machine the way `apply` found it, so it reverts all four
things apply changed:

- the stable junction `C:\Dev` (through the same one-shot elevation broker);
- every home link, leaving the data on the stick;
- the environment variables apply set — restored to the value **and registry kind** they
  had, or removed when they did not exist before;
- the managed PATH entries, keeping foreign entries byte-for-byte and keeping any entry
  you added yourself after applying.

The original env values come from the journals apply writes *before* mutating: the
earliest apply that touched a value wins, because that is "before apply". Where no such
record exists (a machine applied before that bookkeeping, or a `--test-registry` run,
which is ignored), `clean` only removes a variable that still carries exactly the value
the current manifest declares, and reports anything it left alone rather than deleting a
value that might be yours. A `Path` value that did not exist before is deleted rather than
set to an empty string. Like `apply`, every mutation is journalled and backed up first.

## Tools on the stick

Fourteen toolchains, each in its own versioned directory under `tools\<id>\<version>`:

| Tool | Version | Notes |
|---|---|---|
| Python | 3.13.17 | python-build-standalone, with pip |
| Node.js | 26.12.1 | npm 11.20.0 |
| Git | 2.56.0.3 | PortableGit |
| CMake | 4.4.4 | |
| Ninja | 1.13.2 | |
| ripgrep | 15.2.0 | |
| jq | 1.8.2 | |
| 7-Zip | 26.05 | standalone `7zr`; zip/tar.gz are unpacked in-process |
| LLVM / clang | 23.1.3 | llvm-mingw (UCRT); add `-static` for standalone exes |
| VSCode | 1.143.1 | portable `data\`, settings in `data\user-data\User` |
| Sublime Text | 4215 | portable `Data\`, settings in `Data\Packages\User`, `subl` on PATH |
| PowerShell | 7.6.8 | portable `pwsh`, self-contained .NET; the built-in 5.1 is untouched |
| fastfetch | 2.70.0 | full package (presets + `lua55.dll`); feeds the GUI **系统状态** page, also on PATH |
| Firefox | 159.0 | official zh-CN MSI unpacked; **launch via `Firefox.cmd`**, profile in `home\firefox\profile` (NSS, so logins travel with the stick); deliberately not on PATH |

Firefox is declared as a tool but has **no `[[path]]` entry**: starting `firefox.exe`
directly would use `%APPDATA%` and stop being portable. `Firefox.cmd` in the stick root
passes `-profile <stick>\home\firefox\profile -no-remote`, and
`distribution\policies.json` turns off self-update (the manifest owns versions), the
default-browser check, and telemetry.

## GUI

`PortableDev.cmd` with no arguments opens the window; any argument goes to the CLI.
Six pages, no framework, no external dependency:

| Page | What it does |
|---|---|
| 概览 / Dashboard | stick, junction target, PATH/env counts, per-check rows, one-click apply; an ambiguous or missing stick is shown with the candidate roots |
| 工具 / Tools | every declared tool, version and location; **全部验证** runs the same checks as `verify` and shows *why* a check failed; **检查更新** queries upstream and offers **升级**, which then downloads, verifies the published SHA256, repoints version/dir in the manifest, runs `apply` and self-checks — no manual follow-up; per-row **安装 / 编辑 / 目录** and a **新增工具** form |
| 变更预览 / Plan | the diff, grouped and colour-coded by add/update/remove, including **家目录联接 / home links**; degraded-mode toggle |
| 体检 / Doctor | the same rules as `PortableDev doctor`, plus `clean` |
| 进度与日志 / Progress | live apply/clean output, step tracker, final verdict |
| 系统状态 / System | runs the stick's `fastfetch` and shows its report verbatim (mono, selectable, copy button) — no parsing, so upgrading the tool or its preset needs no GUI change |

### Updating tools from the network

Each tool can declare where its newer versions live:

```toml
[update]
kind    = 'github'                     # github | node | vscode | sevenzip
repo    = 'BurntSushi/ripgrep'
pattern = 'ripgrep-(?<ver>d+.d+.d+)-x86_64-pc-windows-msvc.zip'
```

The **pattern** is a regex over asset names (GitHub) or the whole index body
(others); it exposes the version as group `ver`, and any other named group
becomes a `{placeholder}` in the optional **url** template. That is enough to
point a tool at a fast mirror while still asking GitHub which version exists:

```toml
url = 'https://registry.npmmirror.com/-/binary/python-build-standalone/{build}/cpython-{ver}+{build}-x86_64-pc-windows-msvc-install_only.tar.gz'
```

Downloads are split across 16 connections with resume, because the GitHub release
CDN throttles per connection (measured: ~43 KB/s single, ~1.25 MB/s at 16). The
archive is unpacked into a **new** version directory and only then is the manifest
version rewritten, so a failed or corrupt download never damages a working tool.

### Download integrity

Every download is verified before it can become a tool:

- When the source publishes a checksum — a `<asset>.sha256` sibling on GitHub, an explicit
  `sha256_url` template, or the `<url>.sha256` convention — it is fetched and compared.
  A mismatch deletes the file and the transfer fails.
- When no checksum exists, the actual SHA256 is computed and recorded. An unknown
  checksum is never treated as a match, and never guessed.
- An archive that already exists is re-verified, not trusted because it is non-empty:
  a truncated package is deleted and fetched again.
- The chunked downloader refuses to join unless every part has exactly the length its
  byte range claims, and it assembles into `*.assembling` before an atomic rename, so the
  destination file is either absent or complete.

The read-only `update` listing pays for none of this: checksums are resolved only when
something is actually downloaded (`update --fetch`, or a GUI upgrade).

### Home links: carrying settings across machines

Tools survive on the stick, but their *state* does not: Code.exe, Sublime, `pwsh` and
`clangd` all keep profiles under `%APPDATA%` or the user profile, and those die with a
restored machine. An environment variable cannot fix that — these programs resolve their
config path from the Win32 known-folder API, not from `HOME`.

So each declared directory is replaced on the machine by a **junction** pointing into the
stick:

```toml
[[home_dir]]
for  = 'VSCode windows and settings'
root = 'APPDATA'          # env var or known folder: APPDATA | LOCALAPPDATA | USERPROFILE
path = 'Code'             # relative to that root
store = 'home/Code'       # optional; defaults to home/<path>
```

- A junction needs **no administrator rights** (unlike a symlink) and is created by `apply`.
- In degraded mode (`--no-junction`) the links point straight at the stick, because there
  is no `C:\Dev` to reach it through.
- A slot that is **already a real directory with data in it is never moved silently**:
  `plan` reports `home-blocked` and `apply` leaves it untouched. Turn on
  `[apply] home_adopt = true` (or pass `--adopt-home` for one run) to move the contents
  onto the stick first. Nothing is deleted on the way.
- `doctor` reports each slot as linked, wrong, or blocked.
- `clean` removes the links and leaves the data on the stick.


The user profile folder itself is deliberately **not** redirected, and neither are
`.nuget` (a package cache) or `AppData\Local\Temp` (multi-GB, and being wiped every boot
is a feature).

### Managing tools from the GUI

The Tools page can add, edit and install toolchains without hand-editing TOML:

- **新增工具** renders a new `manifest.d\<id>.toml` from a short form.
- **编辑** opens the raw TOML so comments and keys the form does not know about survive.
  Saving re-parses the *entire* manifest; on any error the previous file is restored,
  so a typo can never leave the stick unbootable.
- **安装** unpacks a zip / 7z / tar.gz into `tools\<id>\<version>` (auto-stripping a
  single top-level directory), and can rewrite the manifest version in one step.
  It only writes inside the stick; PATH and the registry stay the exclusive business
  of `apply`, so an install can never half-modify the machine.

Three deliberate constraints:

- **The GUI does not implement writes.** `apply` and `clean` shell out to
  `PortableDev.exe`, because that is where the elevation broker lives. One write
  path means the GUI cannot drift into a second, subtly different implementation.
- **Reads go through Core, not through the CLI.** The dashboard and the plan page call
  the same `Planner` the CLI calls, so what is shown is exactly what `plan` would print.
- **No MVVM framework.** `ObservableObject` and `RelayCommand` are about eighty lines; a
  portable tool cannot afford a dependency that has to resolve on a restored machine.
  WPF's `ObservableCollection` is thread-affine, so the contract is that a *refresh* never
  starts on a worker thread: `AppViewModel` marshals each page refresh as a whole, and
  `ProgressViewModel` marshals every streamed CLI line through `Infrastructure\UiThread`.
  The page view models then fill their collections directly — they rely on that contract
  rather than each calling the dispatcher, which is why no `ConfigureAwait(false)` is
  allowed anywhere in that path.

## Design decisions worth knowing

- **One stable root.** Every generated path is `C:\Dev\...`; the drive letter never
  appears in PATH. Only `C:\Dev` is a link (a junction on the system drive). The
  PortableDev tree itself contains **no** symlinks, so exFAT and NTFS both work; NTFS is
  preferable because `doctor` then reports `filesystem-ntfs` as OK and git needs no
  `safe.directory` workaround.
- **Versioned paths, no `current` pointer.** PATH entries resolve to
  `tools\<id>\<version>\...` at apply time, so upgrades are a manifest edit.
- **Never `setx`.** It truncates at 1024 chars and downgrades `REG_EXPAND_SZ` to
  `REG_SZ`, destroying `%USERPROFILE%`. Values are read with
  `DoNotExpandEnvironmentNames` and written back as `ExpandString`.
- **Only managed entries are touched.** Everything under the stable root is removed
  and rebuilt; foreign entries are preserved byte-for-byte, including `%VARS%`.
  A foreign entry that shadows one of our commands produces a warning, never a delete.
- **PATH is written last.** A failure at the junction step leaves PATH untouched, so a
  refused UAC prompt cannot corrupt the environment.
- **Junctions are created by `mklink /J`.** A hand-built `REPARSE_DATA_BUFFER` was
  accepted by the kernel but stored as `\??\Global\...`, and a two-byte length error
  produced `ERROR_INVALID_REPARSE_DATA`. Shelling out to the OS implementation is the
  only way to guarantee identical behaviour.
- **Junction targets are read with `FSCTL_GET_REPARSE_POINT`.** .NET's `LinkTarget` returns
  null for junctions, which made every `apply` believe the link was wrong and prompt for
  UAC again, breaking idempotency.
- **Degraded mode exists.** `--no-junction` produces a working environment that
  breaks if the drive letter changes — used when `C:\` is not writable.
- **A failed apply undoes itself.** Env vars and PATH are restored from their raw
  (unexpanded) previous values, *with the registry kind they had* (`REG_SZ` is not turned
  into `REG_EXPAND_SZ`), in reverse order; a variable or a `Path` value that did not exist
  before is deleted rather than set to an empty string. Home links are put back the way
  they were: one this run created is removed, one it repointed is pointed at its old
  target again, and an empty directory it replaced is recreated. Adopted data stays on the
  stick. The stable junction stays, because removing it needs a second UAC prompt and the
  next apply wants it anyway. `--no-rollback` turns this off.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | ok |
| 1 | failed |
| 2 | bad command line |
| 3 | manifest error |
| 4 | stick not found (ambiguous, or GUI not published) |
| 5 | blocked by a warning |
| 6 | not confirmed |
| 7 | junction failed |
| 8 | elevation declined or unavailable |
| 9 | home link failed |
