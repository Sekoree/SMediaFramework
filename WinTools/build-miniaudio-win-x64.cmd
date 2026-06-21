@echo off
setlocal

pushd "%~dp0.." >nul
set "ROOT=%CD%"
popd >nul

set "SRC=%~dp0miniaudio-0.11.25"
set "BUILD=%~dp0miniaudio-build-x64"
set "OUT=%ROOT%\WinNatives"
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"

if not exist "%SRC%\miniaudio.c" (
    echo miniaudio.c was not found at "%SRC%\miniaudio.c".
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

cl /nologo /O2 /MD /DMA_DLL /D_CRT_SECURE_NO_WARNINGS /LD "%SRC%\miniaudio.c" ^
    /Fe:"%OUT%\miniaudio.dll" ^
    /Fo:"%BUILD%\miniaudio.obj" ^
    /link /NOLOGO /IMPLIB:"%OUT%\miniaudio.lib" /PDB:"%BUILD%\miniaudio.pdb"

if errorlevel 1 exit /b %errorlevel%

if not exist "%OUT%\miniaudio.dll" (
    echo Build completed, but "%OUT%\miniaudio.dll" was not created.
    exit /b 1
)

echo Built "%OUT%\miniaudio.dll"
echo Intermediate object: "%BUILD%\miniaudio.obj"
exit /b 0
