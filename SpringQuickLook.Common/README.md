[![GitHub license](https://img.shields.io/github/license/QL-Win/QuickLook.Common)](https://github.com/QL-Win/QuickLook/blob/master/SpringQuickLook.Common/LICENSE) [![NuGet](https://img.shields.io/nuget/v/SpringQuickLook.Common.svg)](https://nuget.org/packages/SpringQuickLook.Common)

# SpringQuickLook.Common

This repository holds the common library of SpringQuickLook. The library is shared among all SpringQuickLook projects.

Repository: https://github.com/QL-Win/QuickLook

## Plugin development update

SpringQuickLook.Common no longer requires submodule-based development for SpringQuickLook plugins.

If your plugin project previously used this repository as a git submodule, remove it with:

```bash
git submodule deinit -f SpringQuickLook.Common
git rm -f SpringQuickLook.Common
```

Then switch to NuGet dependency:

```xml
<PackageReference Include="SpringQuickLook.Common" Version="x.y.z" />
```
