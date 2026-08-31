dotnet publish Flow.Launcher.Plugin.Iconify -c Release -r win-x64 --no-self-contained
if (Test-Path Flow.Launcher.Plugin.Iconify/bin/Iconify.zip) { Remove-Item Flow.Launcher.Plugin.Iconify/bin/Iconify.zip -Force }
# Remove debug symbols to reduce zip from 35MB to ~5MB (not needed at runtime)
Remove-Item Flow.Launcher.Plugin.Iconify/bin/Release/win-x64/publish/*.pdb -Force -ErrorAction SilentlyContinue
Remove-Item Flow.Launcher.Plugin.Iconify/bin/Release/win-x64/publish/*.xml -Force -ErrorAction SilentlyContinue
Compress-Archive -Path Flow.Launcher.Plugin.Iconify/bin/Release/win-x64/publish/* -DestinationPath Flow.Launcher.Plugin.Iconify/bin/Iconify.zip -Force