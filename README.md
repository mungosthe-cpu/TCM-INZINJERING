# TCM-INZINJERING - AutoCAD 2020–2026 Plugin

Plugin koji dodaje **Ribbon tab "TCM-INZINJERING"** u AutoCAD, odmah pored **Featured Apps**.

## Sta dobijas

- Novi tab na Ribbon-u (kao "Kobi Toolkit" na slici)
- Dugmad koja pokrecu komande (`TCMHELLO`, `TCMINFO`, `TCMRIBBON`, teren, situacija…)
- Automatsko ucitavanje pri startu AutoCAD-a
- **Isti feature set** na AutoCAD **2024** (net48 Legacy) i **2026** (net8)

## Preduslovi

1. **AutoCAD 2020–2024** i/ili **AutoCAD 2025–2026**
2. **Visual Studio 2022** sa workload-om ".NET desktop development"
3. **.NET 8 SDK** (za net8 build) + .NET Framework 4.8 targeting pack (za Legacy)

   ```powershell
   winget install Microsoft.DotNet.SDK.8
   ```

## Build

```powershell
cd "c:\Users\User\Desktop\AUTOCAD PROGRAMS"
dotnet build TcmInzenjering.Plugin\TcmInzenjering.Plugin.csproj -c Release /p:DeployPlugin=true
```

Build uvek pravi **oba** DLL-a:
- `Contents\net48\TcmInzenjering.Plugin.Legacy.dll` → AutoCAD/Civil **2020–2024**
- `Contents\net8\TcmInzenjering.Plugin.dll` → AutoCAD/Civil **2025–2026**

## Instalacija u AutoCAD

Kopiraj ceo folder `TcmInzenjering.bundle` u:

```
C:\ProgramData\Autodesk\ApplicationPlugins\
```

Struktura mora biti:

```
C:\ProgramData\Autodesk\ApplicationPlugins\TcmInzenjering.bundle\
  PackageContents.xml
  Contents\
    TcmInzenjering.Plugin.dll
```

Restartuj AutoCAD 2026. Tab **"TCM-INZINJERING"** treba da se pojavi pored **Featured Apps**.

## Rucno ucitavanje (za test)

Ako ne koristis bundle folder:

1. U AutoCAD ukucaj: `NETLOAD`
2. Izaberi: `TcmInzenjering.Plugin.dll`
3. Komanda `TCMRIBBON` osvezava tab ako treba

## Prilagodjavanje

| Sta menjati | Gde |
|-------------|-----|
| Ime taba | `RibbonBuilder.cs` -> `TabTitle` |
| Dugmad i komande | `RibbonBuilder.cs` + `Commands.cs` |
| Ime kompanije / opis | `PackageContents.xml` |

## Napomena o poziciji taba

Plugin trazi tab **Featured Apps** i ubacuje tvoj tab odmah desno od njega.
Ako Featured Apps nije instaliran, tab se dodaje na kraj Ribbon-a.
Poziciju mozes pomeriti i rucno: desni klik na Ribbon -> **Show Tabs**.
