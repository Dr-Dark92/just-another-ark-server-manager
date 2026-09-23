@echo off
setlocal
cd /d "%~dp0\.."

echo ==============================================
echo JAASM - Standalone ARK Icon Downloader
echo ==============================================
echo.
echo This runs locally only. It is not part of CI.
echo Output: ark-icon-downloads\
echo.

where py >nul 2>&1
if %errorlevel%==0 (
    py -3 tools\ark_icon_downloader.py %*
) else (
    python tools\ark_icon_downloader.py %*
)

set EXITCODE=%errorlevel%
echo.
if %EXITCODE%==0 (
    echo Finished. Upload filenames.txt or the downloaded icon folder when ready.
) else (
    echo Downloader finished with exit code %EXITCODE%.
)
echo.
pause
exit /b %EXITCODE%
