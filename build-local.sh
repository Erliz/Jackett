./build.sh
cp -R build.mono/ ../jackett.build
cd ../jackett.build
mono --debug JackettConsole.exe
cd ../Jackett
