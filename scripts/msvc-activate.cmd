@echo off
REM ---------------------------------------------------------------------------
REM  Activate the portable MSVC toolchain (cl.exe + Windows SDK) for this shell.
REM
REM    call "E:\PortableDev\scripts\msvc-activate.cmd"
REM
REM  Everything below is derived from the stick root, so the stick can move.
REM ---------------------------------------------------------------------------

set "MSVC_ROOT=%~dp0..\tools\msvc"
for %%i in ("%MSVC_ROOT%") do set "MSVC_ROOT=%%~fi"

if not exist "%MSVC_ROOT%\setup.cmd" (
  echo [ERR] %MSVC_ROOT%\setup.cmd not found. Re-run msvc-kit setup.
  exit /b 1
)

call "%MSVC_ROOT%\setup.cmd" || exit /b 2

REM --- Let CMake's Visual Studio generator accept this non-registered instance.
set "EnterpriseWDK=True"
set "DisableRegistryUse=True"
set "VSINSTALLDIR=%MSVC_ROOT%"
set "VisualStudioVersion=17.14.35207.0"

REM --- Let Flutter's vswhere lookup find our shim.
set "ProgramFiles(x86)=%MSVC_ROOT%\pf86"

REM --- Tell the C++ MSBuild targets to trust the environment (INCLUDE/LIB).
set "UseEnv=true"
set "CheckMSVCComponents=false"
set "WindowsSdkDir=%MSVC_ROOT%\Windows Kits\10"
set "WindowsSDKVersion=10.0.22621.0"

exit /b 0
