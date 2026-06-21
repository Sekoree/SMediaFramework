@echo off
setlocal

pushd "%~dp0.." >nul
set "ROOT=%CD%"
popd >nul

set "SRC=%~dp0portmidi-2.0.8"
set "BUILD=%~dp0portmidi-build-x64"
set "OUT=%ROOT%\WinNatives"
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"

if not exist "%SRC%\pm_common\portmidi.c" (
    echo portmidi.c was not found at "%SRC%\pm_common\portmidi.c".
    exit /b 1
)

if not exist "%VSWHERE%" (
    echo Visual Studio vswhere.exe was not found at "%VSWHERE%".
    echo Install Visual Studio Build Tools with the MSVC C++ toolchain.
    exit /b 1
)

for /f "usebackq tokens=*" %%I in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do (
    set "VSINSTALL=%%I"
)

if not defined VSINSTALL (
    echo Visual Studio Build Tools with the MSVC x64 toolchain were not found.
    exit /b 1
)

set "VCVARS=%VSINSTALL%\VC\Auxiliary\Build\vcvars64.bat"
if not exist "%VCVARS%" (
    echo vcvars64.bat was not found at "%VCVARS%".
    exit /b 1
)

if not exist "%BUILD%" mkdir "%BUILD%"
if not exist "%OUT%" mkdir "%OUT%"

call "%VCVARS%" >nul
if errorlevel 1 exit /b %errorlevel%

set "RSP=%BUILD%\portmidi-cl.rsp"
> "%RSP%" echo /nologo
>> "%RSP%" echo /c
>> "%RSP%" echo /O2
>> "%RSP%" echo /MD
>> "%RSP%" echo /DWIN32
>> "%RSP%" echo /D_WINDOWS
>> "%RSP%" echo /D_WINDLL
>> "%RSP%" echo /D_CRT_SECURE_NO_WARNINGS
>> "%RSP%" echo /I"%SRC%\pm_common"
>> "%RSP%" echo /I"%SRC%\porttime"
>> "%RSP%" echo /I"%SRC%\pm_win"
>> "%RSP%" echo "%SRC%\pm_common\portmidi.c"
>> "%RSP%" echo "%SRC%\pm_common\pmutil.c"
>> "%RSP%" echo "%SRC%\porttime\porttime.c"
>> "%RSP%" echo "%SRC%\porttime\ptwinmm.c"
>> "%RSP%" echo "%SRC%\pm_win\pmwin.c"
>> "%RSP%" echo "%SRC%\pm_win\pmwinmm.c"
>> "%RSP%" echo /Fd:"%BUILD%\portmidi.pdb"

pushd "%BUILD%" >nul
cl @"%RSP%"
set "BUILD_RESULT=%ERRORLEVEL%"
popd >nul
if not "%BUILD_RESULT%" == "0" exit /b %BUILD_RESULT%

set "LINK_RSP=%BUILD%\portmidi-link.rsp"
> "%LINK_RSP%" echo /NOLOGO
>> "%LINK_RSP%" echo /DLL
>> "%LINK_RSP%" echo /OUT:"%OUT%\portmidi.dll"
>> "%LINK_RSP%" echo /IMPLIB:"%OUT%\portmidi.lib"
>> "%LINK_RSP%" echo /PDB:"%BUILD%\portmidi.pdb"
>> "%LINK_RSP%" echo portmidi.obj
>> "%LINK_RSP%" echo pmutil.obj
>> "%LINK_RSP%" echo porttime.obj
>> "%LINK_RSP%" echo ptwinmm.obj
>> "%LINK_RSP%" echo pmwin.obj
>> "%LINK_RSP%" echo pmwinmm.obj
>> "%LINK_RSP%" echo winmm.lib

pushd "%BUILD%" >nul
link @"%LINK_RSP%"
set "BUILD_RESULT=%ERRORLEVEL%"
popd >nul
if not "%BUILD_RESULT%" == "0" exit /b %BUILD_RESULT%

if not exist "%OUT%\portmidi.dll" (
    echo Build completed, but "%OUT%\portmidi.dll" was not created.
    exit /b 1
)

echo Built "%OUT%\portmidi.dll"
echo Intermediate files: "%BUILD%"
exit /b 0
