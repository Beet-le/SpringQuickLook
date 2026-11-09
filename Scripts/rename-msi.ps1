$version = git describe --always --tags --exclude latest

Remove-Item ..\Build\SpringQuickLook-$version.msi -ErrorAction SilentlyContinue
Rename-Item ..\Build\SpringQuickLook.msi SpringQuickLook-$version.msi