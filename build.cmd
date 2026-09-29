@echo off
setlocal
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /platform:anycpu /optimize+ /win32icon:"%~dp0assets\app.ico" /utf8output /out:"%~dp0XDownloader.exe" /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Net.Http.dll /reference:System.Web.dll /reference:System.Web.Extensions.dll "%~dp0src\Browser.cs" "%~dp0src\Model.cs" "%~dp0src\Crawler.cs" "%~dp0src\Downloader.cs" "%~dp0src\App.cs" "%~dp0src\Tests.cs" "%~dp0src\History.cs" "%~dp0src\RecordDb.cs" "%~dp0src\Changelog.cs"
exit /b %ERRORLEVEL%
