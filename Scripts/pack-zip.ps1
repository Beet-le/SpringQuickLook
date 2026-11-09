$version = git describe --always --tags --exclude latest

Start-Sleep -s 1

Write-Output "This file makes SpringQuickLook portable." >> ..\Build\Package\portable.lock

Remove-Item ..\Build\SpringQuickLook-$version.zip -ErrorAction SilentlyContinue
Remove-Item -Recurse ..\Build\Package\SpringQuickLook.WoW64HookHelper.exe -ErrorAction SilentlyContinue
Compress-Archive ..\Build\Package\* ..\Build\SpringQuickLook-$version.zip

Remove-Item ..\Build\Package\portable.lock