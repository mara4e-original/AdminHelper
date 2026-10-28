@echo off
chcp 65001 >nul
cd /d "%~dp0"

echo [1/2] Собираю программу со встроенным .NET (первый раз может занять несколько минут)...
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:DebugSymbols=false -o publish-full
if not exist "publish-full\AdminHelper.exe" goto fail_build

set "ISCC="
if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if exist "%LocalAppData%\Programs\Inno Setup 6\ISCC.exe" set "ISCC=%LocalAppData%\Programs\Inno Setup 6\ISCC.exe"
if "%ISCC%"=="" goto no_inno

echo.
echo [2/2] Собираю установщик...
"%ISCC%" setup.iss
if not exist "installer\AdminHelper-Setup.exe" goto fail_setup

echo.
echo Готово: %cd%\installer\AdminHelper-Setup.exe
explorer /select,"%cd%\installer\AdminHelper-Setup.exe"
exit /b 0

:fail_build
echo Не удалось собрать программу, пришлите текст ошибки выше.
pause
exit /b 1

:no_inno
echo.
echo Не найден Inno Setup 6. Установите его с https://jrsoftware.org/isdl.php и запустите этот файл снова.
pause
exit /b 1

:fail_setup
echo Не удалось собрать установщик, пришлите текст ошибки выше.
pause
exit /b 1
