@echo off
setlocal enabledelayedexpansion

set "REPO=1k09-byte/kaliteOS-tool"
set "API=https://api.github.com/repos/%REPO%/releases/latest"

curl -s -A "kaliteOS-installer" "%API%" > "%TEMP%\_rl.json"
if errorlevel 1 (
    echo Failed to fetch release info.
    del "%TEMP%\_rl.json" 2>nul
    exit /b 1
)

set "TAG="
for /f "usebackq tokens=*" %%L in (`findstr /i "tag_name" "%TEMP%\_rl.json"`) do (
    set "LINE=%%L"
)

if defined LINE (
    set "TAG=!LINE:*tag_name":"=!"
    for /f "tokens=1 delims=," %%A in ("!TAG!") do set "TAG=%%A"
    set "TAG=!TAG:~0,-1!"
    set "SHORT=!TAG:v=!"
)

del "%TEMP%\_rl.json" 2>nul

if "!TAG!"=="" (
    echo Could not find tag.
    exit /b 1
)

set "NAME=kaliteConfig-Setup-%SHORT%.exe"
set "URL=https://github.com/%REPO%/releases/download/%TAG%/%NAME%"
set "OUT=%USERPROFILE%\Downloads\%NAME%"

curl -s -L -A "kaliteOS-installer" -o "%OUT%" "%URL%"
if errorlevel 1 (
    echo Download failed.
    exit /b 1
)

start /wait "" "%OUT%"
del "%OUT%" 2>nul
