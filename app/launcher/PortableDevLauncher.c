// Tiny GUI-subsystem launcher: starts the bundled dotnet host with the window DLL,
// hides the console window and keeps the WinForms window visible.
#include <windows.h>

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE previous, PWSTR commandLine, int show)
{
    wchar_t exe[MAX_PATH];
    if (GetModuleFileNameW(NULL, exe, MAX_PATH) == 0) return 1;
    wchar_t *slash = wcsrchr(exe, L'\\');
    if (slash == NULL) return 1;
    *slash = 0;

    wchar_t runtime[MAX_PATH], dotnet[MAX_PATH], dll[MAX_PATH], args[MAX_PATH * 3];
    wsprintfW(runtime, L"%s\\app\\runtime", exe);
    wsprintfW(dotnet, L"%s\\dotnet.exe", runtime);
    wsprintfW(dll, L"%s\\app\\pd-window.dll", exe);
    SetEnvironmentVariableW(L"DOTNET_ROOT", runtime);
    wsprintfW(args, L"\"%s\" \"%s\" --root \"%s\"", dotnet, dll, exe);

    STARTUPINFOW si;
    PROCESS_INFORMATION pi;
    ZeroMemory(&si, sizeof(si));
    si.cb = sizeof(si);
    ZeroMemory(&pi, sizeof(pi));
    // CREATE_NO_WINDOW hides the console host; the WinForms window is still created normally.
    if (!CreateProcessW(dotnet, args, NULL, NULL, FALSE, CREATE_NO_WINDOW, NULL, exe, &si, &pi)) {
        return 2;
    }
    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);
    return 0;
}
