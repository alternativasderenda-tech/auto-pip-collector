@echo off
title Instalador - Auto-Pip Collector
echo.
echo   Instalando o Auto-Pip Collector...
echo   (vai pedir permissao de administrador - clique SIM)
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0instalar.ps1"
