/*
 * vswhere shim for the portable MSVC toolset.
 *
 * Flutter's Windows build runs, verbatim,
 *
 *   %ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe -format json ...
 *
 * (tools\flutter\<ver>\packages\flutter_tools\lib\src\windows\visual_studio.dart says the
 *  path "is not user-controllable", which holds for the sub-path but not for the variable.)
 *
 * The real vswhere enumerates instances registered with the Visual Studio Installer. This
 * toolset is not registered and cannot be without admin rights, so this shim answers the
 * one question Flutter asks: where is the instance, and what version is it. It ignores
 * every argument and always prints the single instance it lives inside.
 *
 * The path is derived from the executable's own location, never hard-coded:
 *   <toolset>\pf86\Microsoft Visual Studio\Installer\vswhere.exe
 *   -> four levels up -> <toolset>
 * so the stick keeps working after a drive-letter change, exactly like setup.cmd does.
 *
 * Flutter then probes the rest itself: <toolset>\VC\Tools\MSVC\* for the toolset
 * (visual_studio.dart:_findMsvcVersion) and <toolset>\Common7\...\CMake\bin\cmake.exe for
 * CMake. Only installationPath and installationVersion are load-bearing here; the other
 * fields exist so the output looks like what vswhere really prints.
 *
 * Build (with the toolset activated):
 *   call scripts\msvc-activate.cmd
 *   cl /nologo /O1 /W3 source\scripts\vswhere-shim.c /Fe:"<stick>\tools\msvc\pf86\Microsoft Visual Studio\Installer\vswhere.exe"
 *
 * Keep MSVC_INSTALL_VERSION in step with the manifest, VC\Auxiliary\Build\
 * Microsoft.VCToolsVersion.default.txt and scripts\msvc-activate.cmd.
 */

#include <windows.h>
#include <stdio.h>
#include <string.h>

#define MSVC_INSTALL_VERSION "17.14.35207.0"

int main(void)
{
    char home[MAX_PATH];
    DWORD len = GetModuleFileNameA(NULL, home, MAX_PATH);
    if (len == 0 || len >= MAX_PATH)
        return 1;

    /* ...\Installer\vswhere.exe -> ...\Installer -> ...\Microsoft Visual Studio
       -> ...\pf86 -> <toolset> */
    for (int i = 0; i < 4; i++)
    {
        char *slash = strrchr(home, '\\');
        if (slash == NULL)
            return 1;
        *slash = '\0';
    }

    /* JSON needs backslashes escaped. */
    char escaped[MAX_PATH * 2];
    size_t out = 0;
    for (size_t i = 0; home[i] != '\0' && out + 2 < sizeof escaped; i++)
    {
        if (home[i] == '\\')
            escaped[out++] = '\\';
        escaped[out++] = home[i];
    }
    escaped[out] = '\0';

    printf("[{\"instanceId\":\"portablemsvc\","
           "\"installationPath\":\"%s\","
           "\"installationVersion\":\"" MSVC_INSTALL_VERSION "\","
           "\"displayName\":\"Portable MSVC (msvc-kit)\","
           "\"isComplete\":true,\"isLaunchable\":true,"
           "\"isRebootRequired\":false,\"isPrerelease\":false,"
           "\"catalog\":{\"productDisplayVersion\":\"" MSVC_INSTALL_VERSION "\"}}]\n",
           escaped);

    return 0;
}
