@echo off
setlocal

pushd "%~dp0.." >nul
set "ROOT=%CD%"
popd >nul

set "SRC=%~dp0portaudio-19.7.0"
set "BUILD=%~dp0portaudio-build-x64"
set "OUT=%ROOT%\WinNatives"
set "ASIO_ZIP=%~dp0ASIO-SDK_2.3.4_2025-10-15.zip"
set "ASIO_EXTRACT=%~dp0asiosdk"
set "ASIO_ROOT=%ASIO_EXTRACT%\ASIOSDK"
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"

if not exist "%SRC%\src\common\pa_front.c" (
    echo PortAudio source was not found at "%SRC%".
    exit /b 1
)

if not exist "%ASIO_ZIP%" (
    echo ASIO SDK zip was not found at "%ASIO_ZIP%".
    exit /b 1
)

if not exist "%ASIO_ROOT%\common\asio.h" (
    echo Extracting ASIO SDK to "%ASIO_EXTRACT%"...
    if not exist "%ASIO_EXTRACT%" mkdir "%ASIO_EXTRACT%"
    powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Expand-Archive -LiteralPath '%ASIO_ZIP%' -DestinationPath '%ASIO_EXTRACT%' -Force"
    if errorlevel 1 exit /b %errorlevel%
)

if not exist "%ASIO_ROOT%\common\asio.h" (
    echo ASIO SDK extraction did not create "%ASIO_ROOT%\common\asio.h".
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

set "DEF=%BUILD%\portaudio-mfplayer.def"
> "%DEF%" echo EXPORTS
>> "%DEF%" echo Pa_GetVersion
>> "%DEF%" echo Pa_GetVersionInfo
>> "%DEF%" echo Pa_GetVersionText
>> "%DEF%" echo Pa_GetErrorText
>> "%DEF%" echo Pa_Initialize
>> "%DEF%" echo Pa_Terminate
>> "%DEF%" echo Pa_GetHostApiCount
>> "%DEF%" echo Pa_GetDefaultHostApi
>> "%DEF%" echo Pa_GetHostApiInfo
>> "%DEF%" echo Pa_HostApiTypeIdToHostApiIndex
>> "%DEF%" echo Pa_HostApiDeviceIndexToDeviceIndex
>> "%DEF%" echo Pa_GetLastHostErrorInfo
>> "%DEF%" echo Pa_GetDeviceCount
>> "%DEF%" echo Pa_GetDefaultInputDevice
>> "%DEF%" echo Pa_GetDefaultOutputDevice
>> "%DEF%" echo Pa_GetDeviceInfo
>> "%DEF%" echo Pa_IsFormatSupported
>> "%DEF%" echo Pa_OpenStream
>> "%DEF%" echo Pa_OpenDefaultStream
>> "%DEF%" echo Pa_CloseStream
>> "%DEF%" echo Pa_SetStreamFinishedCallback
>> "%DEF%" echo Pa_StartStream
>> "%DEF%" echo Pa_StopStream
>> "%DEF%" echo Pa_AbortStream
>> "%DEF%" echo Pa_IsStreamStopped
>> "%DEF%" echo Pa_IsStreamActive
>> "%DEF%" echo Pa_GetStreamInfo
>> "%DEF%" echo Pa_GetStreamTime
>> "%DEF%" echo Pa_GetStreamCpuLoad
>> "%DEF%" echo Pa_ReadStream
>> "%DEF%" echo Pa_WriteStream
>> "%DEF%" echo Pa_GetStreamReadAvailable
>> "%DEF%" echo Pa_GetStreamWriteAvailable
>> "%DEF%" echo Pa_GetSampleSize
>> "%DEF%" echo Pa_Sleep
>> "%DEF%" echo PaAsio_GetAvailableBufferSizes
>> "%DEF%" echo PaAsio_ShowControlPanel
>> "%DEF%" echo PaAsio_GetInputChannelName
>> "%DEF%" echo PaAsio_GetOutputChannelName
>> "%DEF%" echo PaAsio_SetStreamSampleRate
>> "%DEF%" echo PaUtil_InitializeX86PlainConverters
>> "%DEF%" echo PaUtil_SetDebugPrintFunction
>> "%DEF%" echo PaWin_SampleFormatToLinearWaveFormatTag
>> "%DEF%" echo PaWin_InitializeWaveFormatEx
>> "%DEF%" echo PaWin_InitializeWaveFormatExtensible
>> "%DEF%" echo PaWin_DefaultChannelMask
>> "%DEF%" echo PaWinMME_GetStreamInputHandleCount
>> "%DEF%" echo PaWinMME_GetStreamInputHandle
>> "%DEF%" echo PaWinMME_GetStreamOutputHandleCount
>> "%DEF%" echo PaWinMME_GetStreamOutputHandle
>> "%DEF%" echo PaWasapi_GetAudioClient
>> "%DEF%" echo PaWasapi_UpdateDeviceList
>> "%DEF%" echo PaWasapi_GetDeviceCurrentFormat
>> "%DEF%" echo PaWasapi_GetDeviceDefaultFormat
>> "%DEF%" echo PaWasapi_GetDeviceMixFormat
>> "%DEF%" echo PaWasapi_GetDeviceRole
>> "%DEF%" echo PaWasapi_ThreadPriorityBoost
>> "%DEF%" echo PaWasapi_ThreadPriorityRevert
>> "%DEF%" echo PaWasapi_GetFramesPerHostBuffer
>> "%DEF%" echo PaWasapi_GetJackCount
>> "%DEF%" echo PaWasapi_GetJackDescription
>> "%DEF%" echo PaWasapi_SetStreamStateHandler
>> "%DEF%" echo PaWasapiWinrt_SetDefaultDeviceId
>> "%DEF%" echo PaWasapiWinrt_PopulateDeviceList
>> "%DEF%" echo PaWasapi_GetIMMDevice

call "%VCVARS%" >nul
if errorlevel 1 exit /b %errorlevel%

set "RSP=%BUILD%\portaudio-cl.rsp"
> "%RSP%" echo /nologo
>> "%RSP%" echo /c
>> "%RSP%" echo /O2
>> "%RSP%" echo /MD
>> "%RSP%" echo /EHsc
>> "%RSP%" echo /DWIN32
>> "%RSP%" echo /D_WIN32
>> "%RSP%" echo /D_WINDOWS
>> "%RSP%" echo /DNDEBUG
>> "%RSP%" echo /D_CRT_SECURE_NO_WARNINGS
>> "%RSP%" echo /DPAWIN_USE_WDMKS_DEVICE_INFO
>> "%RSP%" echo /DPAWIN_USE_DIRECTSOUNDFULLDUPLEXCREATE
>> "%RSP%" echo /DPA_USE_ASIO=1
>> "%RSP%" echo /DPA_USE_DS=1
>> "%RSP%" echo /DPA_USE_WMME=1
>> "%RSP%" echo /DPA_USE_WASAPI=1
>> "%RSP%" echo /DPA_USE_WDMKS=1
>> "%RSP%" echo /DPA_USE_SKELETON=0
>> "%RSP%" echo /I"%SRC%\include"
>> "%RSP%" echo /I"%SRC%\src\common"
>> "%RSP%" echo /I"%SRC%\src\os\win"
>> "%RSP%" echo /I"%SRC%\src\hostapi\asio"
>> "%RSP%" echo /I"%ASIO_ROOT%\common"
>> "%RSP%" echo /I"%ASIO_ROOT%\host"
>> "%RSP%" echo /I"%ASIO_ROOT%\host\pc"
>> "%RSP%" echo "%SRC%\src\common\pa_allocation.c"
>> "%RSP%" echo "%SRC%\src\common\pa_converters.c"
>> "%RSP%" echo "%SRC%\src\common\pa_cpuload.c"
>> "%RSP%" echo "%SRC%\src\common\pa_debugprint.c"
>> "%RSP%" echo "%SRC%\src\common\pa_dither.c"
>> "%RSP%" echo "%SRC%\src\common\pa_front.c"
>> "%RSP%" echo "%SRC%\src\common\pa_process.c"
>> "%RSP%" echo "%SRC%\src\common\pa_ringbuffer.c"
>> "%RSP%" echo "%SRC%\src\common\pa_stream.c"
>> "%RSP%" echo "%SRC%\src\common\pa_trace.c"
>> "%RSP%" echo "%SRC%\src\hostapi\skeleton\pa_hostapi_skeleton.c"
>> "%RSP%" echo "%SRC%\src\os\win\pa_win_hostapis.c"
>> "%RSP%" echo "%SRC%\src\os\win\pa_win_util.c"
>> "%RSP%" echo "%SRC%\src\os\win\pa_win_waveformat.c"
>> "%RSP%" echo "%SRC%\src\os\win\pa_win_wdmks_utils.c"
>> "%RSP%" echo "%SRC%\src\os\win\pa_win_coinitialize.c"
>> "%RSP%" echo "%SRC%\src\os\win\pa_x86_plain_converters.c"
>> "%RSP%" echo "%SRC%\src\hostapi\asio\pa_asio.cpp"
>> "%RSP%" echo "%SRC%\src\hostapi\asio\iasiothiscallresolver.cpp"
>> "%RSP%" echo "%ASIO_ROOT%\common\asio.cpp"
>> "%RSP%" echo "%ASIO_ROOT%\host\pc\asiolist.cpp"
>> "%RSP%" echo "%ASIO_ROOT%\host\asiodrivers.cpp"
>> "%RSP%" echo "%SRC%\src\hostapi\dsound\pa_win_ds.c"
>> "%RSP%" echo "%SRC%\src\hostapi\dsound\pa_win_ds_dynlink.c"
>> "%RSP%" echo "%SRC%\src\hostapi\wmme\pa_win_wmme.c"
>> "%RSP%" echo "%SRC%\src\hostapi\wasapi\pa_win_wasapi.c"
>> "%RSP%" echo "%SRC%\src\hostapi\wdmks\pa_win_wdmks.c"
>> "%RSP%" echo /Fd:"%BUILD%\portaudio.pdb"

pushd "%BUILD%" >nul
cl @"%RSP%"
set "BUILD_RESULT=%ERRORLEVEL%"
popd >nul
if not "%BUILD_RESULT%" == "0" exit /b %BUILD_RESULT%

set "LINK_RSP=%BUILD%\portaudio-link.rsp"
> "%LINK_RSP%" echo /NOLOGO
>> "%LINK_RSP%" echo /DLL
>> "%LINK_RSP%" echo /OUT:"%OUT%\portaudio.dll"
>> "%LINK_RSP%" echo /DEF:"%DEF%"
>> "%LINK_RSP%" echo /IMPLIB:"%OUT%\portaudio.lib"
>> "%LINK_RSP%" echo /PDB:"%BUILD%\portaudio.pdb"
>> "%LINK_RSP%" echo pa_allocation.obj
>> "%LINK_RSP%" echo pa_converters.obj
>> "%LINK_RSP%" echo pa_cpuload.obj
>> "%LINK_RSP%" echo pa_debugprint.obj
>> "%LINK_RSP%" echo pa_dither.obj
>> "%LINK_RSP%" echo pa_front.obj
>> "%LINK_RSP%" echo pa_process.obj
>> "%LINK_RSP%" echo pa_ringbuffer.obj
>> "%LINK_RSP%" echo pa_stream.obj
>> "%LINK_RSP%" echo pa_trace.obj
>> "%LINK_RSP%" echo pa_hostapi_skeleton.obj
>> "%LINK_RSP%" echo pa_win_hostapis.obj
>> "%LINK_RSP%" echo pa_win_util.obj
>> "%LINK_RSP%" echo pa_win_waveformat.obj
>> "%LINK_RSP%" echo pa_win_wdmks_utils.obj
>> "%LINK_RSP%" echo pa_win_coinitialize.obj
>> "%LINK_RSP%" echo pa_x86_plain_converters.obj
>> "%LINK_RSP%" echo pa_asio.obj
>> "%LINK_RSP%" echo iasiothiscallresolver.obj
>> "%LINK_RSP%" echo asio.obj
>> "%LINK_RSP%" echo asiolist.obj
>> "%LINK_RSP%" echo asiodrivers.obj
>> "%LINK_RSP%" echo pa_win_ds.obj
>> "%LINK_RSP%" echo pa_win_ds_dynlink.obj
>> "%LINK_RSP%" echo pa_win_wmme.obj
>> "%LINK_RSP%" echo pa_win_wasapi.obj
>> "%LINK_RSP%" echo pa_win_wdmks.obj
>> "%LINK_RSP%" echo winmm.lib
>> "%LINK_RSP%" echo dsound.lib
>> "%LINK_RSP%" echo ole32.lib
>> "%LINK_RSP%" echo uuid.lib
>> "%LINK_RSP%" echo setupapi.lib
>> "%LINK_RSP%" echo ksuser.lib
>> "%LINK_RSP%" echo advapi32.lib
>> "%LINK_RSP%" echo user32.lib

pushd "%BUILD%" >nul
link @"%LINK_RSP%"
set "BUILD_RESULT=%ERRORLEVEL%"
popd >nul
if not "%BUILD_RESULT%" == "0" exit /b %BUILD_RESULT%

if not exist "%OUT%\portaudio.dll" (
    echo Build completed, but "%OUT%\portaudio.dll" was not created.
    exit /b 1
)

echo Built "%OUT%\portaudio.dll"
echo ASIO SDK: "%ASIO_ROOT%"
echo Intermediate files: "%BUILD%"
exit /b 0
