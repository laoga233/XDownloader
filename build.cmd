@echo off
setlocal
copy /y "%~dp0vendor\WebView2\Microsoft.Web.WebView2.Core.dll" "%~dp0Microsoft.Web.WebView2.Core.dll" >nul
if errorlevel 1 exit /b 1
copy /y "%~dp0vendor\WebView2\Microsoft.Web.WebView2.WinForms.dll" "%~dp0Microsoft.Web.WebView2.WinForms.dll" >nul
if errorlevel 1 exit /b 1
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /platform:anycpu /optimize+ /win32icon:"%~dp0assets\app.ico" /utf8output /reference:"%~dp0vendor\WebView2\Microsoft.Web.WebView2.Core.dll" /reference:"%~dp0vendor\WebView2\Microsoft.Web.WebView2.WinForms.dll" /resource:"%~dp0assets\Gallery.html",XDownloader.Gallery.html /out:"%~dp0XDownloader.exe" /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Net.Http.dll /reference:System.Web.dll /reference:System.Web.Extensions.dll "%~dp0src\Browser.cs" "%~dp0src\Model.cs" "%~dp0src\Crawler.cs" "%~dp0src\Downloader.cs" "%~dp0src\App.cs" "%~dp0src\Tests.cs" "%~dp0src\History.cs" "%~dp0src\RecordDb.cs" "%~dp0src\Changelog.cs" "%~dp0src\TaskArchive.cs" "%~dp0src\Preview.cs" "%~dp0src\MediaGallery.cs" "%~dp0src\GalleryTests.cs"
exit /b %ERRORLEVEL%
