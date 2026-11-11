@echo off
setlocal
rem Unity ignores folders ending in ~, including live and rotated connector logs.
pushd "%~dp0Connector~"
if errorlevel 1 exit /b 1
"%~dp0Connector~\tiktok_connector_udp.exe" %*
set "connectorExit=%ERRORLEVEL%"
popd
exit /b %connectorExit%