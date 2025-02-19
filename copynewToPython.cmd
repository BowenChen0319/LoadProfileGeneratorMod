REM get the path to the current directory
set "srcdirectory=%~dp0"
set "vsdirectory=D:\Program Files\Microsoft Visual Studio\2022\Community\Msbuild\Current\Bin"

REM copy V:\Dropbox\LPG\WpfApplication1\profilegenerator-latest.db3 c:\Main\Git-Repositories\LoadProfileGenerator\WpfApplication1\profilegenerator-latest.db3

cd /D %srcdirectory%
%srcdirectory%\VersionIncreaser\bin\Debug\versionincreaser.exe

cd /D %srcdirectory%\SimulationEngine
rmdir /S /Q %srcdirectory%\SimulationEngine\bin
"%vsdirectory%\msbuild.exe" SimulationEngine.csproj -t:rebuild -v:m

cd /D %srcdirectory%\WpfApplication1
rmdir /S/Q %srcdirectory%\WpfApplication1\bin
"%vsdirectory%\msbuild.exe" LoadProfileGenerator.csproj -t:rebuild  -v:m

cd /D %srcdirectory%\SimEngine2
rmdir /S /Q %srcdirectory%\SimEngine2\bin
dotnet publish simengine2.csproj --configuration Release --self-contained true --runtime win-x64 --verbosity quiet -f net8.0-windows
dotnet publish simengine2.csproj --configuration Release --self-contained true --runtime linux-x64 --verbosity quiet -f net8.0

cd /D %srcdirectory%\ReleaseMaker
"%vsdirectory%\msbuild.exe" ReleaseMaker.csproj -t:rebuild  -v:m

cd /D %srcdirectory%\ReleaseMaker\bin\Debug\net8.0-windows
releasemaker
pause


REM create pylpg
set "releasedirectory=C:\LPGReleaseMakerResults\LPGReleases\releases10.10"
set "pylpgdirectory=C:\LPGPythonBindings\pylpg\"

cd /D %releasedirectory%\windows
simulationengine cpy
xcopy lpgdata.py %pylpgdirectory%
xcopy lpgpythonbindings.py %pylpgdirectory%
pause