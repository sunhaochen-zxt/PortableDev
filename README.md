# PortableDev

Portable development-environment manager for Windows. Restores a full toolchain from a
portable drive (USB stick or external disk) onto a machine that gets wiped
(restore-card / Deep Freeze style), in one command.

Status: **core + CLI + WPF GUI complete.** The core test suite is green and
`PortableDev verify` passes every declared check (30/30) on the reference stick.
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

Nineteen toolchains, each in its own versioned directory under `tools\<id>\<version>`:

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
| VSCode | 1.144.0 | portable `data\`, settings in `data\user-data\User` |
| Sublime Text | 4215 | portable `Data\`, settings in `Data\Packages\User`, `subl` on PATH |
| PowerShell | 7.6.8 | portable `pwsh`, self-contained .NET; the built-in 5.1 is untouched |
| fastfetch | 2.70.0 | full package (presets + `lua55.dll`); feeds the GUI **系统状态** page, also on PATH |
| Firefox | 159.0 | official zh-CN MSI unpacked; **launch via `Firefox.cmd`**, profile in `home\firefox\profile` (NSS, so logins travel with the stick); deliberately not on PATH |
| Tabby | 1.0.238 | Electron terminal; portable because `data\` sits next to `Tabby.exe` (config, plugins, SSH profiles); **launch via `Tabby.cmd`**, not on PATH |
| FlClash | 0.9.2 | proxy client; settings in `%APPDATA%\FlClash`, carried by a home link to `home\FlClash`; **launch via `FlClash.cmd`**, not on PATH |
| Flutter | 3.47.9 | SDK + Dart 3.13.7 (DevTools 2.60.0); web (`flutter run -d chrome`) **and** Windows desktop builds — see [Building Windows desktop apps](#building-windows-desktop-apps); `PUB_CACHE` on the stick, analytics off |
| MSVC + Windows SDK | 14.44.35207 / 10.0.22621.0 | real `cl.exe`, `link.exe` and the Windows SDK, installed by msvc-kit from Microsoft's own payloads — no Visual Studio installer, no admin, no registry writes; `cl`/`link` on PATH, `INCLUDE`/`LIB` in the environment |

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
drive-letter change and does not need the `C:\Dev` junction. `DSH.cmd` stays hand-written:
it has a conditional fallback (an empty `home\.dsh` makes it start from the machine profile
and print a hint), which is logic, not data.

- **Tabby** needs no machine-side help at all. Its main process redirects `userData` to
  `<exe dir>\data` as soon as that directory exists, so `tools\tabby\<version>\data` *is*
  the portable mode: config, plugins, hotkeys and SSH profiles live there and the GUI
  upgrade moves the folder into the next version directory. Deleting it silently makes
  the app non-portable again (it would fall back to `%APPDATA%\tabby`).
- **FlClash 0.9.2** has no portable mode of its own: it resolves its data directory from
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
| `CheckMSVCComponents=false` | MSBuild 17.3 predates MSVC 14.44 and would reject the pair with `MSB8052`; the ABI is the same `v143` |

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

### Moving DSH NEXT itself onto the stick

The desktop app this stick is assembled with can live on the stick like any other tool.
`Migrate-DSH.cmd` (a wrapper for `scripts\migrate-dsh.ps1`) does it in one go, and it has
to run **with DSH NEXT closed**: the app and its session database are locked while it runs,
so the script refuses instead of moving files under a live process.

1. finds the unpacked install (`DSH-*-Portable` on any drive) or takes `-Source <folder>`
2. moves it to `tools\dsh\<version>` — a rename when the stick and the install share a
   volume, otherwise a copy that is verified by file count and then leaves the original
   parked as `<install>.migrated-<stamp>` (never deleted: a recursive delete of a
   half-locked install is how a test run once ate 53 files of a live app)
3. copies `%USERPROFILE%\.dsh` — the conversations in `sessions\`, the profiles, the
   storages and `.credentials.yaml` — to `home\.dsh`, checks every file for presence and
   length, and parks the old folder as `.dsh.portabledev-old-<stamp>`
4. records `home\.dsh` as the app's last data directory, so the first start is not
   reported as a data-directory change
5. writes `manifest\manifest.d\dsh.toml`, and takes back out any `[[home_dir]]` for
   `~\.dsh` that an older run of the script added
6. writes `DSH.cmd`, which sets `DSH_DESKTOP_NEXT_HOME` and `DSH_HOME` to
   `<stick>\home\.dsh`, then runs `apply`

**Why an environment variable and not a home link.** DSH's main process validates its data
directory with `lstat()` and throws `Expected a real directory` for *any* reparse point, so
a junction at `~\.dsh` kills the app at startup before a window appears — the first version
of this script did exactly that. `DSH_DESKTOP_NEXT_HOME` is the app's own documented
override (its settings page shows which source the directory came from), it is read before
the `~/.dsh` default, and the launcher resolves `%~dp0` at launch time, so a new drive
letter does not matter.

The launcher also refuses to open an empty stick home while the machine profile still has
conversations: it starts with the profile and tells you to run the migration, instead of
showing an empty app.

`-DryRun` prints the whole plan without touching anything; `-NoApply` stops before the
registry; `-KeepOldData` leaves the real `.dsh` in place (the launcher keeps using it until
the stick home has conversations).

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
