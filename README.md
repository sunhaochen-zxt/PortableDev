# PortableDev

Portable development-environment manager for Windows. Restores a full toolchain from a
portable drive (USB stick or external disk) onto a machine that gets wiped
(restore-card / Deep Freeze style), in one command.

Status: **core + CLI + WPF GUI complete.** The core test suite is green and
`PortableDev verify` passes every declared check on the reference stick.
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
├─ scripts/                 publish + migration scripts, msvc-activate.cmd
└─ docs/                    long-form notes; docs/msvc.md explains the MSVC toolchain
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
PortableDev shell --profile msvc     # ... plus a named profile's variables
PortableDev update    # ask upstream for newer versions (read-only)
PortableDev update --fetch jq   # download an update without installing it
PortableDev pkg search jq       # find a package in the buckets
PortableDev pkg add fd          # install a package from a Scoop bucket
PortableDev pkg list            # what pkg add installed
PortableDev pkg remove fd       # remove it (persist data survives)
```

`--profile <name>` adds the variables of a `[[profile]]` declared in a tool manifest to
that one terminal. Profiles exist for variables that must not be written to the registry -
`ProgramFiles(x86)`, `EnterpriseWDK` and friends change how unrelated programs behave, so
they are opt-in per shell. `msvc` is the one shipped today; see
`source\docs\msvc.md`.

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

Eighteen toolchains, each in its own versioned directory under `tools\<id>\<version>`:

| Tool | Notes |
|---|---|
| Python | python-build-standalone, with pip |
| Node.js | Node.js with npm |
| Git | PortableGit |
| CMake | |
| Ninja | |
| ripgrep | |
| jq | |
| 7-Zip | full `7z` + `7zr`; zip/tar.gz are unpacked in-process, 7z/NSIS by `7z.exe` |
| LLVM / clang | llvm-mingw (UCRT); add `-static` for standalone exes |
| VSCode | portable `data\`, settings in `data\user-data\User` |
| Sublime Text | portable `Data\`, settings in `Data\Packages\User`, `subl` on PATH |
| PowerShell | portable `pwsh`, self-contained .NET; the built-in 5.1 is untouched |
| fastfetch | full package (presets + `lua55.dll`); feeds the GUI **系统状态** page, also on PATH |
| Firefox | official zh-CN MSI unpacked; **launch via `Firefox.cmd`**, profile in `home\firefox\profile` (NSS, so logins travel with the stick); deliberately not on PATH |
| Tabby | Electron terminal; portable because `data\` sits next to `Tabby.exe` (config, plugins, SSH profiles); **launch via `Tabby.cmd`**, not on PATH |
| FlClash | proxy client; settings in `%APPDATA%\FlClash`, carried by a home link to `home\FlClash`; **launch via `FlClash.cmd`**, not on PATH |
| Flutter | SDK + Dart (with DevTools); web (`flutter run -d chrome`) **and** Windows desktop builds — see [Building Windows desktop apps](#building-windows-desktop-apps); `PUB_CACHE` on the stick, analytics off |
| MSVC + Windows SDK | real `cl.exe`, `link.exe` and the Windows SDK, installed by msvc-kit from Microsoft's own payloads — no Visual Studio installer, no admin, no registry writes; `cl`/`link` on PATH, `INCLUDE`/`LIB` in the environment |

Versions are deliberately not listed here: the manifests under `manifest\manifest.d\` own
them, `PortableDev plan` prints what is actually on the stick, and a version copied into
this file only goes stale (it did, for five tools at once).

Firefox is declared as a tool but has **no `[[path]]` entry**: starting `firefox.exe`
directly would use `%APPDATA%` and stop being portable. `Firefox.cmd` in the stick root
passes `-profile <stick>\home\firefox\profile -no-remote`, and
`distribution\policies.json` turns off self-update (the manifest owns versions), the
default-browser check, and telemetry.

The GUI applications follow the same rule and are started by a `.cmd` in the stick root,
because there is no command of theirs worth putting on PATH. Those three launchers are
**generated from the manifest**, not hand-written:

```toml
[[launch]]
name  = 'Tabby'                 # -> <stick>\Tabby.cmd
exe   = 'Tabby.exe'             # relative to the tool directory
args  = ['-profile', '{{STICK}}home\firefox\profile']   # {{STICK}} renders as %~dp0
env   = { MOZ_CRASHREPORTER_DISABLE = '1' }
```

`apply` writes the file, `plan` shows it as its own group, and a file whose content drifted
(repointed version, missing `-no-remote`) is reported as an update instead of being trusted.
Everything is expressed relative to `%~dp0`, so the launcher keeps working after a
drive-letter change and does not need the `C:\Dev` junction. DSH NEXT is not one of these:
it is a normal install under `<stick>\DSH\` and is started by `<stick>\DSH\启动DSH.cmd`.

- **Tabby** needs no machine-side help at all. Its main process redirects `userData` to
  `<exe dir>\data` as soon as that directory exists, so `tools\tabby\<version>\data` *is*
  the portable mode: config, plugins, hotkeys and SSH profiles live there and the GUI
  upgrade moves the folder into the next version directory. Deleting it silently makes
  the app non-portable again (it would fall back to `%APPDATA%\tabby`).
- **FlClash** has no portable mode of its own: it resolves its data directory from
  the Win32 known-folder API, so `HOME` cannot reach it and profiles always go to
  `%APPDATA%\FlClash`. A `[[home_dir]]` junction carries them instead, which means
  `apply` has to have run once on the machine. Its TUN mode additionally installs a
  Windows *service* (`FlClashHelperService.exe`), which is machine state and cannot
  travel; system-proxy mode needs no service.

### Building Windows desktop apps

`flutter build windows` normally insists on a Visual Studio installation that the VS
Installer knows about. The stick instead carries a portable MSVC toolset, plus a thin
Visual Studio *shape* around it, so the unmodified Flutter SDK builds against it.

One command, from any shell:

```bat
PortableDev.cmd shell --profile msvc
cd /d E:\项目\PiliPlus
flutter build windows --release
```

That opens a terminal with the toolchain in it. If you are already inside a batch file or
a CI step, `call E:\PortableDev\scripts\msvc-activate.cmd` sets the same variables in the
current process instead.

Verified end to end on this machine: a `flutter create` app built in **31.2 s** to
`build\windows\x64\runner\Release\windemo.exe`, and `cl hello.c` compiles, links and runs.
Updating or removing the toolset, and every failure mode we hit with its cause, is in
**`source\docs\msvc.md`** on the stick (in the repo: `docs/msvc.md`).

Plain C/C++ needs no activation at all - `apply` already exports the toolchain:

```bat
cl hello.c
```

What `scripts\msvc-activate.cmd` does, and why each part is needed:

| Item | Why |
|---|---|
| `tools\msvc\setup.cmd` (generated by `msvc-kit setup --portable-root`) | exports `INCLUDE`, `LIB`, `PATH`, `VCToolsInstallDir`, `WindowsSdkDir`, `WindowsSDKVersion` — all relative to `%~dp0` |
| `EnterpriseWDK=True`, `DisableRegistryUse=True` | the escape hatch in `cmVSSetupHelper.cxx`: with both set, CMake takes `VSINSTALLDIR` + `VisualStudioVersion` from the environment and never asks the Visual Studio Installer |
| `VSINSTALLDIR`, `VisualStudioVersion` | the fake instance root and its version |
| `ProgramFiles(x86)=<stick>\tools\msvc\pf86` | Flutter's `_vswherePath` is hard coded to `%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe`; pointing the variable at the stick makes it find our shim there |
| `UseEnv=true` | tells the C++ MSBuild targets to build `INCLUDE`/`LIB` from the environment instead of the registry (otherwise `kernel32.lib` and then `MSVCRTD.lib` go missing) |
| `CheckMSVCComponents=false` | the bundled MSBuild predates the MSVC toolchain in the manifest and would reject the pair with `MSB8052`; the ABI is the same `v143` |

The pieces inside `tools\msvc` that Flutter needs and msvc-kit does not provide:

- `VC\Auxiliary\Build\Microsoft.VCToolsVersion.default.txt` — CMake reads the toolset
  version from this file (`LoadVSInstanceVCToolsetVersion`); without it CMake reports
  "could not find any instance of Visual Studio"
- `Common7\IDE\CommonExtensions\Microsoft\CMake\CMake` — a junction to `tools\cmake\<ver>`,
  because `VisualStudio.cmakePath` is hard coded to that exact path
- `MSBuild\Current\Bin\MSBuild.exe` + `MSBuild\Microsoft\VC\v170` — `cmake --build` uses the
  Visual Studio generator and therefore MSBuild, not Ninja
- `pf86\...\vswhere.exe` — a ~40 line C program that prints one JSON instance; it is
  compiled by the same toolchain it describes

`flutter doctor` reports `Visual Studio` with a `!` because it looks for the Windows SDK in
`HKLM\SOFTWARE\Microsoft\Microsoft SDKs\Windows\v10.0`, which nothing wrote. Builds do not
use that path and succeed regardless.

### DSH NEXT is not managed by the stick

DSH NEXT is an ordinary install under `<stick>\DSH\`, updated by its own updater instead of
by `apply`. Its data directory is chosen inside the app, which stores that choice as an
absolute path in `%APPDATA%\DSH NEXT\home\desktop-next-location.json`.

`<stick>\DSH\启动DSH.cmd` is the entry point: it rewrites that file and
`desktop-next-last-home.json` to the drive it was started from, then launches the app, so
the drive letter can change freely. The data itself lives in `<stick>\DSH数据\`.

Earlier revisions carried a second copy of the app in `tools\dsh` plus a `Migrate-DSH.cmd`
script that moved the install and `%USERPROFILE%\.dsh` there. Both were removed: the app
grew an in-app data-directory setting, so the duplicate could only drift out of date.

## GUI

`PortableDev.cmd` with no arguments opens the window; any argument goes to the CLI.
Seven pages, no framework, no external dependency:

| Page | What it does |
|---|---|
| 概览 / Dashboard | stick, junction target, PATH/env counts, per-check rows, one-click apply; an ambiguous or missing stick is shown with the candidate roots |
| 软件包 / Packages | App-Store style: **推荐** is a curated shelf ranked by GitHub stars, **全部软件** pages through every app in Main and Extras (about 4,200, from the cached index — browsing works offline) with category chips (开发/终端/编辑/系统/网络/媒体/美化/其他, classified from the app id; curated labels win); search covers everything, one click **安装**, installed list with **移除**. Search reads the same Core parser as `pkg search`; install/remove shell out to the CLI, so the GUI keeps its single write path |
| 工具 / Tools | every declared tool, version and location; **全部验证** runs the same checks as `verify` and shows *why* a check failed; **检查更新** queries upstream and offers **升级**, which then downloads, verifies the published SHA256, repoints version/dir in the manifest, runs `apply` and self-checks — no manual follow-up; per-row **安装 / 编辑 / 目录** and a **新增工具** form |
| 变更预览 / Plan | the diff, grouped and colour-coded by add/update/remove, including **家目录联接 / home links**; degraded-mode toggle |
| 体检 / Doctor | the same rules as `PortableDev doctor`, plus `clean` |
| 进度与日志 / Progress | live apply/clean output, step tracker, final verdict |
| 系统状态 / System | runs the stick's `fastfetch` and shows its report verbatim (mono, selectable, copy button) — no parsing, so upgrading the tool or its preset needs no GUI change |

The window lives in the tray: closing it hides it instead of quitting, and the tray menu
offers **显示主窗口 / 检查更新 / 下载可用更新 / 退出**. While hidden it checks for updates
every four hours (`update --json`, read-only) and says so with a balloon; **下载可用更新**
runs `update --fetch`, which downloads and verifies but installs nothing — applying an
upgrade stays a deliberate action on the Tools page. A machine without network simply gets
"检查更新失败" in the balloon and keeps working.

### Updating tools from the network

Each tool can declare where its newer versions live:

```toml
[update]
kind    = 'github'                     # github | node | vscode | sevenzip | scoop
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
CDN throttles per connection (measured: ~43 KB/s single, ~1.25 MB/s at 16). Range
support is discovered with a one-byte ranged GET when HEAD is refused or silent, so a
CDN that answers `405` to HEAD still gets the parallel path. The archive is unpacked
into a **new** version directory and only then is the manifest version rewritten, so a
failed or corrupt download never damages a working tool.

### Installing packages from Scoop buckets

`PortableDev pkg` reads [Scoop](https://scoop.sh) bucket manifests and installs them as
ordinary PortableDev tools:

```powershell
PortableDev pkg search ripgrep            # names from both buckets, with version + summary
PortableDev pkg add fd                    # Main bucket first, then Extras
PortableDev pkg add extras/notepadplusplus
PortableDev pkg add fd --dry-run          # dependencies, urls and scripts; changes nothing
PortableDev pkg list                      # --json for machine-readable output
PortableDev pkg remove fd                 # persist data kept; --purge deletes it too
```

The search list comes from each bucket's GitHub tree (one request per bucket, cached on the
stick for 12 hours and readable offline for up to 30 days); descriptions are fetched only
for the results that are shown. The same page exists in the GUI: **软件包** opens on a
recommended shelf — a hand-curated set of apps ranked by their GitHub star count, cached for
a day because the unauthenticated API allows 60 requests per hour — and has the search box,
one-click **安装**, and the installed list with **移除**.

The bucket supplies **version, url, hash, extract_dir, persist and depends**. The `hash` in
a bucket manifest is a value a human reviewed in a pull request, which makes it a stronger
trust anchor than a `.sha256` fetched later from the same server. Everything else stays
PortableDev's job: the archive is unpacked into `tools\<id>\<version>`, every `persist` path
moves to `tools\<id>\persist` (a junction for directories, a hard link for files, both
written against the `C:\Dev` path so a drive-letter change does not break them), and a
generated `manifest.d\pkg-<id>.toml` makes `apply`, `plan`, `verify` and the GUI work
unchanged.

Deliberately **not** implemented, because each one is Scoop's runtime rather than its data:

| Feature | Behaviour |
|---|---|
| shims, aliases, Start-menu shortcuts | not created; `bin` entries become `[[path]]` directories |
| `installer.script` | refused: those packages write outside their own directory and stop being portable |
| `pre_install` / `post_install` | parsed and reported; executed only with `--allow-scripts`, after their text is printed |
| `env_set` | ignored with a warning |
| `depends` | installed first, in dependency order; a cycle is an error |
| zip / tar.gz / 7z / msi / Inno / NSIS | unpacked (`msiexec /a`, `innounp`, `7z x`); anything else is reported |

A package may not reuse the id of a hand-written tool: two manifests with the same id make
the whole manifest unreadable, so `pkg add 7zip` is refused while `manifest.d\7zip.toml`
exists.

Generated manifests carry `[update] kind = 'scoop'` pointing back at their bucket entry, so
`PortableDev update` and the GUI's 检查更新 see these packages too; upgrading one downloads
the new archive, verifies the bucket hash, honours `extract_dir` and the `#/name.ext` rename,
and rewrites the manifest like any other tool.

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
- A resumed download only reuses parts that provably belong to the same object: the probe
  records the URL, the length and the `ETag` / `Last-Modified` validators in
  `*.parts\partmeta.json`, and a re-uploaded asset (same name, same length, different
  bytes) makes the old parts be discarded instead of spliced into the new file.

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
