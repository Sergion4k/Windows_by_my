@echo off
setlocal
cd /d "%~dp0"

echo Сборка Vindows (один exe)...
dotnet publish Vindows\Vindows.csproj -c Release
if errorlevel 1 (
    echo Ошибка сборки.
    exit /b 1
)

echo.
echo Готово:
echo   %~dp0dist\Vindows.exe
echo.
echo Можно копировать на другой ПК — .NET устанавливать не нужно.
