# MediSearch

MediSearch is a Windows desktop app for quick medicine search. It opens a local backend service, searches configured pharmacy/provider websites, and shows product names, prices, images, screenshots, provider status, and logs in one place.

## Download And Install

For normal users, do not download the source code.

1. Open the latest release:
   `https://github.com/nguyenvantruong-ou/medi-search/releases/latest`
2. Download:
   `MediSearchSetup.exe`
3. Double-click the setup file.
4. Follow the installer and launch `MediSearch`.

The installer puts the app in Program Files, creates a Start Menu shortcut, and can optionally create a desktop shortcut.

Direct latest download link:

```text
https://github.com/nguyenvantruong-ou/medi-search/releases/latest/download/MediSearchSetup.exe
```

The Release also includes `MediSearch.zip` for portable/manual updates. Normal users should use the setup `.exe`.

## Requirements

- Windows x64
- Microsoft Edge or Google Chrome installed
- No .NET installation is required for the released desktop app because it is published self-contained

If Windows reports that .NET is missing, install the .NET 8 Desktop Runtime:

```text
https://dotnet.microsoft.com/download/dotnet/8.0
```

## How To Use

1. Double-click `MediSearch.exe`.
2. Enter a medicine name or active ingredient in the keyword box.
3. Keep the default provider URLs or add a custom provider URL.
4. Click `Search`.

Custom provider URLs can use `{keyword}` as a placeholder:

```text
https://example.com/search?q={keyword}
```

## Auto Update

MediSearch checks the latest GitHub Release on startup by downloading `version.json`.

- If the app is current, it starts normally.
- If a newer supported version exists, the app shows an optional update dialog.
- If the installed version is below the minimum supported version, the app blocks usage until updated.

When updating from inside the app, MediSearch downloads `MediSearch.zip`, shows progress, starts `MediSearch.Updater.exe`, replaces the old files, and restarts the new version.

## Build From Source

Install the .NET 8 SDK, then run:

```powershell
dotnet restore MediSearch.slnx
dotnet build MediSearch.slnx --configuration Release
```

Publish locally:

```powershell
dotnet publish MediSearch/MediSearch.csproj --configuration Release --runtime win-x64 --self-contained true --output artifacts/publish
dotnet publish MedicineQuickSearch/MedicineQuickSearch.csproj --configuration Release --runtime win-x64 --self-contained false --output artifacts/publish/MedicineQuickSearch
dotnet publish MediSearch.Updater/MediSearch.Updater.csproj --configuration Release --runtime win-x64 --self-contained true --output artifacts/updater
```

Copy `artifacts/updater/MediSearch.Updater.exe` into `artifacts/publish`, then use Inno Setup with `installer/MediSearch.iss` to create the installer `.exe`.

## CI/CD

GitHub Actions release workflow:

```text
.github/workflows/release.yml
```

Manual release input examples:

```text
1.0.0
1.0.1
1.1.0
2.0.0
```

The workflow restores dependencies, builds, runs tests if test projects exist, publishes the app, generates `version.json`, creates a Windows installer, creates a GitHub Release, tags it, and uploads both `MediSearchSetup.exe` and `MediSearch.zip`.

## Troubleshooting

If port `5030` is already in use, close MediSearch and stop old `dotnet`, `MediSearch`, or `MedicineQuickSearch` processes from Task Manager.

If browser automation is blocked, allow the app in Windows Defender or antivirus, then run it again.
