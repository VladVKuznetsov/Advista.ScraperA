# Deploying to Linux

A plain Windows build only bundles the Windows Playwright driver, so running it on Linux fails with:

```
PlaywrightException: Driver not found: .../.playwright/node/linux-x64/node
```

The Playwright NuGet package ships drivers for every OS, but the build copies only the **build
host's** driver. The fix is to produce a `linux-x64` build.

## 1. Publish for linux-x64

On the dev machine:

```powershell
pwsh scripts/publish-linux.ps1
```

or directly:

```bash
dotnet publish -c Release -r linux-x64 --self-contained false
```

Output: `bin/Release/net9.0/linux-x64/publish/` — it contains `.playwright/node/linux-x64/node`.

(Or simply run `dotnet publish` **on the Linux machine** — it bundles the right driver automatically.)

## 2. Deploy + set up on the Linux box

Copy the publish folder's contents to the server, then from that directory:

```bash
bash setup-linux.sh            # browser only
sudo bash setup-linux.sh --with-deps   # browser + OS libraries (bare server)
```

`setup-linux.sh` restores the driver's execute bit and runs the OS-agnostic browser installer
(`dotnet AgentScraper.dll --install-browsers`). The driver loses `+x` when copied from Windows.

## 3. Config on the server

- Put the real key in `appsettings.json` (gitignored). `appsettings.development.json` is only a
  placeholder template.
- Set **`"Headless": true`** — a server has no display, so headed Chromium can't launch.
