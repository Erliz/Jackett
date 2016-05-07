cd src && xbuild Jackett.sln /t:Build /p:Configuration=Release /verbosity:minimal
cd ..

rm -rf build.mono
mkdir build.mono

cp -R src/Jackett.Console/bin/Release/ build.mono

cp src/Jackett.Service/bin/Release/JackettService.exe build.mono/JackettService.exe
cp src/Jackett.Service/bin/Release/JackettService.exe.config build.mono/JackettService.exe.config

cp src/Jackett.Tray/bin/Release/JackettTray.exe build.mono/JackettTray.exe
cp src/Jackett.Tray/bin/Release/JackettTray.exe.config build.mono/JackettTray.exe.config

cp src/Jackett.Updater/bin/Release/JackettUpdater.exe build.mono/JackettUpdater.exe
cp src/Jackett.Updater/bin/Release/JackettUpdater.exe.config build.mono/JackettUpdater.exe.config

cp LICENSE build.mono/
cp README.md build.mono/
cp Upstart.config build.mono/

