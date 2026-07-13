# Distribucija TCM-INZINJERING plugina

## Podržani CAD programi

| Program | Verzije | DLL |
|---------|---------|-----|
| AutoCAD | 2020–2024 | `Contents/net48/TcmInzenjering.Plugin.Legacy.dll` |
| AutoCAD | 2025–2026 | `Contents/net8/TcmInzenjering.Plugin.dll` |
| BricsCAD | 2022+ | `Contents/net48/TcmInzenjering.Plugin.Legacy.dll` |

## Build release instalera (EXE)

```powershell
cd "c:\Users\User\Desktop\AUTOCAD PROGRAMS"
.\scripts\build-release.ps1
```

Rezultat u `dist\`:
- `TCM-INZINJERING-Setup-<verzija>.exe` — installer za korisnike
- `TCM-INZINJERING-<verzija>.zip` — arhiva za GitHub Release
- `update-manifest.json` — manifest za proveru nadogradnje

**Napomena:** Legacy DLL (AutoCAD 2020–2024 / BricsCAD) zahteva build na računaru gde je instaliran AutoCAD 2020–2024.

## Instalacija kod korisnika

1. Preuzmi `TCM-INZINJERING-Setup-x.y.z.exe` sa GitHub Releases
2. Zatvori AutoCAD/BricsCAD
3. Pokreni installer (desni klik → Run as administrator za sve korisnike)
4. Restartuj CAD program

Installer kopira bundle u:
- `%APPDATA%\Autodesk\ApplicationPlugins\`
- `%APPDATA%\Bricsys\ApplicationPlugins\`
- i registruje AutoCAD profile (R23.1–R25.1)

## Provera nadogradnje u programu

U AutoCAD/BricsCAD komandna linija:

```
TCMUPDATE
```

Plugin pri startu automatski proverava da li postoji novija verzija (jednom u 6 sati).

Manifest se čita sa GitHub-a:
`release/update-manifest.json`

Pre objavljivanja izmeni URL repozitorijuma u:
`TcmInzenjering.Plugin/Update/PluginInfo.cs` (trenutno: `mungosthe-cpu/TCM-INZINJERING`)

## GitHub release workflow

1. Poveži projekat sa GitHub repozitorijumom
2. Postavi verziju u `Directory.Build.props`
3. Ažuriraj `release/update-manifest.json`
4. Taguj release:

```powershell
git tag v1.1.0
git push origin v1.1.0
```

GitHub Actions (`.github/workflows/release.yml`) automatski gradi i objavljuje EXE + manifest.

## Dev deploy (lokalno)

```powershell
dotnet build TcmInzenjering.Plugin\TcmInzenjering.Plugin.csproj -c Release /p:DeployPlugin=true
```
