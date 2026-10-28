@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo Собираю AdminHelper.exe ...
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -p:DebugSymbols=false -o publish
echo.
if exist "publish\AdminHelper.exe" (
  echo Готово: %cd%\publish\AdminHelper.exe
  echo Запускаю программу...
  start "" "%cd%\publish\AdminHelper.exe"
) else (
  echo Сборка не удалась, пришлите текст ошибки выше.
  pause
)
