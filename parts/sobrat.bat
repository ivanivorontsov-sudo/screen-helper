@echo off
chcp 65001 >nul
cd /d "%~dp0"
copy /b ScreenHelper.exe.part0+ScreenHelper.exe.part1+ScreenHelper.exe.part2+ScreenHelper.exe.part3 ScreenHelper.exe
if exist ScreenHelper.exe (echo Готово: ScreenHelper.exe собран) else (echo Ошибка: не все части на месте)
pause
