using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace TcmInzenjering.Plugin.Roads.Profile;

/// <summary>Metapodaci podužnog profila (CGSA Plateia stil).</summary>
internal sealed class ProfileViewData
{
    public string ProfileId { get; init; } = string.Empty;
    public string AxisName { get; init; } = string.Empty;
    public string TableName { get; init; } = string.Empty;
    public string TableType { get; init; } = "TCM_1";
    public Point3d Origin { get; init; }
    public double StartStation { get; init; }
    public double EndStation { get; init; }
    public double BaseElevation { get; init; }
    public double TopElevation { get; init; }
    /// <summary>CGSA horizontalni imenilac (npr. 1000 → 1:1000).</summary>
    public double HorizontalDenom { get; init; } = 1000;
    /// <summary>CGSA vertikalni imenilac (npr. 100 → 1:100).</summary>
    public double VerticalDenom { get; init; } = 100;
    public double StationTickInterval { get; init; } = 25;
    public ProfileTabulationMode TabulationMode { get; init; } = ProfileTabulationMode.CrossAxes;
    public double CrossAxisInterval { get; init; } = 20;
    public int BetweenDivisor { get; init; } = 2;
    public bool DrawVerticals { get; init; } = true;
    public bool DrawTabulation { get; init; } = true;

    public IReadOnlyList<double> CollectTabulationStations() =>
        ProfileTabulation.CollectStations(
            StartStation,
            EndStation,
            TabulationMode,
            StationTickInterval,
            CrossAxisInterval > 1e-6 ? CrossAxisInterval : StationTickInterval,
            BetweenDivisor);

    /// <summary>
    /// Tabeliranje: situacija = master (iste stacionaže i STA brojevi).
    /// </summary>
    public IReadOnlyList<double> CollectTabulationStations(Transaction tr, Database db) =>
        CollectSituationStations(tr, db).Select(s => s.Station).ToList();

    public IReadOnlyList<SituationStation> CollectSituationStations(Transaction tr, Database db) =>
        ProfileTabulation.CollectSituationStations(
            tr,
            db,
            AxisName,
            StartStation,
            EndStation,
            TabulationMode,
            StationTickInterval,
            CrossAxisInterval > 1e-6 ? CrossAxisInterval : StationTickInterval,
            BetweenDivisor);

    /// <summary>
    /// Model: 1 crtezna jedinica ≈ 1 m papira pri H=1000 / V=1000.
    /// x = Δstac × (1000/H); y = Δkota × (1000/V).
    /// </summary>
    public double StationFactor => 1000.0 / Math.Max(HorizontalDenom, 1e-9);

    public double ElevationFactor => 1000.0 / Math.Max(VerticalDenom, 1e-9);

    public double ProfileWidth => Math.Max(1e-6, (EndStation - StartStation) * StationFactor);

    public ProfileTableType ResolveTableType() => ProfileTableTypeCatalog.Resolve(TableType);

    public IReadOnlyList<ProfileBandDefinition> ResolveBands() =>
        ProfileBandDefinition.FromTableType(ResolveTableType());

    public double LabelColumnWidth =>
        Math.Max(5.0, ResolveTableType().LabelColumnWidth);

    /// <summary>Leva ivica podataka (desno od kolone naziva).</summary>
    public double DataOriginX => Origin.X + LabelColumnWidth;

    public double BandsTotalHeight
    {
        get
        {
            var sum = 0.0;
            foreach (var b in ResolveBands())
            {
                sum += b.Height;
            }

            return sum;
        }
    }

    public double GraphHeight =>
        Math.Max(1e-6, (TopElevation - BaseElevation) * ElevationFactor);

    public double GraphBottomY => Origin.Y + BandsTotalHeight;

    public double GraphTopY => GraphBottomY + GraphHeight;

    public double StationToX(double station) =>
        DataOriginX + (station - StartStation) * StationFactor;

    public double ElevationToY(double elevation) =>
        GraphBottomY + (elevation - BaseElevation) * ElevationFactor;

    public Point3d ToProfilePoint(double station, double elevation) =>
        new(StationToX(station), ElevationToY(elevation), 0);

    /// <summary>Y donjeg ruba prve rubrike datog tipa sadržaja (od dna).</summary>
    public double BandBottomY(ProfileBandKind kind)
    {
        var y = Origin.Y;
        var bands = ResolveBands();
        // Stack od dna: poslednja u listi = Origin.Y; prva = najbliža grafiku.
        for (var i = bands.Count - 1; i >= 0; i--)
        {
            if (bands[i].Kind == kind)
            {
                return y;
            }

            y += bands[i].Height;
        }

        return Origin.Y;
    }

    public double BandCenterY(ProfileBandKind kind)
    {
        var bands = ResolveBands();
        var def = bands.FirstOrDefault(b => b.Kind == kind);
        if (def is null)
        {
            return Origin.Y;
        }

        return BandBottomY(kind) + def.Height * 0.5;
    }

    public double BandBottomYAt(int indexFromTop)
    {
        var bands = ResolveBands();
        if (indexFromTop < 0 || indexFromTop >= bands.Count)
        {
            return Origin.Y;
        }

        var y = Origin.Y;
        for (var i = bands.Count - 1; i > indexFromTop; i--)
        {
            y += bands[i].Height;
        }

        return y;
    }

    public double BandTopYAt(int indexFromTop)
    {
        var bands = ResolveBands();
        if (indexFromTop < 0 || indexFromTop >= bands.Count)
        {
            return Origin.Y;
        }

        return BandBottomYAt(indexFromTop) + bands[indexFromTop].Height;
    }

    public double BandCenterYAt(int indexFromTop)
    {
        var bands = ResolveBands();
        if (indexFromTop < 0 || indexFromTop >= bands.Count)
        {
            return Origin.Y;
        }

        return BandBottomYAt(indexFromTop) + bands[indexFromTop].Height * 0.5;
    }

    /// <summary>
    /// Donja ivica plavih vertikalnih linija: vrh prve rubrike
    /// (Kota nivelete / Glavni elementi / Širine) — ne ide do dna cele tabele.
    /// </summary>
    public double TabulationVerticalBottomY()
    {
        var bands = ResolveBands();
        for (var i = 0; i < bands.Count; i++)
        {
            if (bands[i].Kind is ProfileBandKind.GradeElevations
                or ProfileBandKind.MainElements
                or ProfileBandKind.LaneWidths)
            {
                return BandTopYAt(i);
            }
        }

        return Origin.Y;
    }
}

internal static class ProfileXData
{
    public const string RoleView = "PODV";
    public const string RoleTerrain = "PODT";
    public const string RoleTable = "PODTBL";
    public const string RoleGrade = "PODG";

    public static void AttachView(Entity entity, string profileId, string axisName)
    {
        Set(entity, new ResultBuffer(
            new TypedValue((int)DxfCode.ExtendedDataRegAppName, RoadDrawing.RegAppName),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, RoleView),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, profileId),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, axisName)));
    }

    public static void AttachTerrain(Entity entity, string profileId)
    {
        Set(entity, new ResultBuffer(
            new TypedValue((int)DxfCode.ExtendedDataRegAppName, RoadDrawing.RegAppName),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, RoleTerrain),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, profileId)));
    }

    public static void AttachGrade(Entity entity, string profileId)
    {
        Set(entity, new ResultBuffer(
            new TypedValue((int)DxfCode.ExtendedDataRegAppName, RoadDrawing.RegAppName),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, RoleGrade),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, profileId)));
    }

    public static void AttachTable(Entity entity, string profileId)
    {
        Set(entity, new ResultBuffer(
            new TypedValue((int)DxfCode.ExtendedDataRegAppName, RoadDrawing.RegAppName),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, RoleTable),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, profileId)));
    }

    public static bool TryReadView(Entity entity, out string profileId, out string axisName)
    {
        profileId = string.Empty;
        axisName = string.Empty;
        var values = entity.GetXDataForApplication(RoadDrawing.RegAppName);
        if (values is null)
        {
            return false;
        }

        var items = values.AsArray();
        if (items.Length < 4 ||
            items[1].TypeCode != (int)DxfCode.ExtendedDataAsciiString ||
            Convert.ToString(items[1].Value) != RoleView)
        {
            return false;
        }

        profileId = Convert.ToString(items[2].Value) ?? string.Empty;
        axisName = Convert.ToString(items[3].Value) ?? string.Empty;
        return !string.IsNullOrWhiteSpace(profileId);
    }

    public static bool TryReadRole(Entity entity, out string role, out string profileId)
    {
        role = string.Empty;
        profileId = string.Empty;
        var values = entity.GetXDataForApplication(RoadDrawing.RegAppName);
        if (values is null)
        {
            return false;
        }

        var items = values.AsArray();
        for (var i = 0; i < items.Length; i++)
        {
            if (items[i].TypeCode != (int)DxfCode.ExtendedDataAsciiString)
            {
                continue;
            }

            var text = Convert.ToString(items[i].Value) ?? string.Empty;
            if (text is RoleView or RoleTerrain or RoleTable or RoleGrade)
            {
                role = text;
                if (i + 1 < items.Length && items[i + 1].TypeCode == (int)DxfCode.ExtendedDataAsciiString)
                {
                    profileId = Convert.ToString(items[i + 1].Value) ?? string.Empty;
                }

                return true;
            }
        }

        return false;
    }

    private static void Set(Entity entity, ResultBuffer buffer)
    {
        if (!entity.IsWriteEnabled)
        {
            entity.UpgradeOpen();
        }

        entity.XData = buffer;
    }
}

internal static class ProfileViewStore
{
    private const string DictionaryName = "TCM_PODUZNI";
    private const string Prefix = "VIEW_";

    public static void Save(Transaction tr, Database db, ProfileViewData data)
    {
        var dictionary = GetDictionary(tr, db, OpenMode.ForWrite);
        var key = Prefix + data.ProfileId;
        var buffer = new ResultBuffer(
            new TypedValue((int)DxfCode.Text, data.ProfileId),
            new TypedValue((int)DxfCode.Text, data.AxisName),
            new TypedValue((int)DxfCode.Real, data.Origin.X),
            new TypedValue((int)DxfCode.Real, data.Origin.Y),
            new TypedValue((int)DxfCode.Real, data.Origin.Z),
            new TypedValue((int)DxfCode.Real, data.StartStation),
            new TypedValue((int)DxfCode.Real, data.EndStation),
            new TypedValue((int)DxfCode.Real, data.BaseElevation),
            new TypedValue((int)DxfCode.Real, data.HorizontalDenom),
            new TypedValue((int)DxfCode.Real, data.VerticalDenom),
            new TypedValue((int)DxfCode.Real, data.StationTickInterval),
            new TypedValue((int)DxfCode.Real, data.TopElevation),
            new TypedValue((int)DxfCode.Real, data.DrawVerticals ? 1.0 : 0.0),
            new TypedValue((int)DxfCode.Real, data.DrawTabulation ? 1.0 : 0.0),
            new TypedValue((int)DxfCode.Text, data.TableName),
            new TypedValue((int)DxfCode.Real, (double)(int)data.TabulationMode),
            new TypedValue((int)DxfCode.Real, data.CrossAxisInterval),
            new TypedValue((int)DxfCode.Real, data.BetweenDivisor),
            new TypedValue((int)DxfCode.Text, string.IsNullOrWhiteSpace(data.TableType) ? "TCM_1" : data.TableType));

        if (dictionary.Contains(key))
        {
            var existing = (Xrecord)tr.GetObject(dictionary.GetAt(key), OpenMode.ForWrite);
            existing.Data = buffer;
            return;
        }

        var record = new Xrecord { Data = buffer };
        dictionary.SetAt(key, record);
        tr.AddNewlyCreatedDBObject(record, true);
    }

    public static ProfileViewData? Load(Transaction tr, Database db, string profileId)
    {
        var dictionary = GetDictionary(tr, db, OpenMode.ForRead);
        var key = Prefix + profileId;
        if (!dictionary.Contains(key))
        {
            return null;
        }

        var record = (Xrecord)tr.GetObject(dictionary.GetAt(key), OpenMode.ForRead);
        return Parse(record.Data?.AsArray(), profileId);
    }

    public static IReadOnlyList<ProfileViewData> LoadAll(Transaction tr, Database db)
    {
        var result = new List<ProfileViewData>();
        var dictionary = GetDictionary(tr, db, OpenMode.ForRead);
        foreach (System.Collections.DictionaryEntry entry in dictionary)
        {
            if (entry.Key is not string keyText ||
                !keyText.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (entry.Value is not ObjectId recordId || recordId.IsNull)
            {
                continue;
            }

            var record = (Xrecord)tr.GetObject(recordId, OpenMode.ForRead);
            var view = Parse(record.Data?.AsArray(), keyText[Prefix.Length..]);
            if (view is not null)
            {
                result.Add(view);
            }
        }

        return result;
    }

    public static IReadOnlyList<ProfileViewData> LoadAllForAxis(Transaction tr, Database db, string axisName)
    {
        return LoadAll(tr, db)
            .Where(v => string.Equals(v.AxisName, axisName, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static ProfileViewData? Parse(TypedValue[]? data, string fallbackId)
    {
        if (data is null || data.Length < 13)
        {
            return null;
        }

        // Schema ≥15: TopElevation [11], flags…; ≥17 mode/cross/between
        if (data.Length >= 15)
        {
            var tick = Convert.ToDouble(data[10].Value);
            var mode = data.Length > 15
                ? (ProfileTabulationMode)(int)Math.Round(Convert.ToDouble(data[15].Value))
                : ProfileTabulationMode.FixedInterval;
            var cross = data.Length > 16 ? Convert.ToDouble(data[16].Value) : tick;
            var between = data.Length > 17 ? (int)Math.Round(Convert.ToDouble(data[17].Value)) : 2;
            if (between < 1)
            {
                between = 2;
            }

            var tableType = data.Length > 18 && data[18].TypeCode == (int)DxfCode.Text
                ? Convert.ToString(data[18].Value) ?? "TCM_1"
                : "TCM_1";

            return new ProfileViewData
            {
                ProfileId = Convert.ToString(data[0].Value) ?? fallbackId,
                AxisName = Convert.ToString(data[1].Value) ?? string.Empty,
                Origin = new Point3d(
                    Convert.ToDouble(data[2].Value),
                    Convert.ToDouble(data[3].Value),
                    Convert.ToDouble(data[4].Value)),
                StartStation = Convert.ToDouble(data[5].Value),
                EndStation = Convert.ToDouble(data[6].Value),
                BaseElevation = Convert.ToDouble(data[7].Value),
                HorizontalDenom = NormalizeDenom(Convert.ToDouble(data[8].Value)),
                VerticalDenom = NormalizeDenom(Convert.ToDouble(data[9].Value)),
                StationTickInterval = tick,
                TopElevation = Convert.ToDouble(data[11].Value),
                DrawVerticals = Convert.ToDouble(data[12].Value) >= 0.5,
                DrawTabulation = Convert.ToDouble(data[13].Value) >= 0.5,
                TableName = data.Length > 14 && data[14].TypeCode == (int)DxfCode.Text
                    ? Convert.ToString(data[14].Value) ?? string.Empty
                    : string.Empty,
                TabulationMode = mode,
                CrossAxisInterval = cross > 1e-6 ? cross : tick,
                BetweenDivisor = between,
                TableType = string.IsNullOrWhiteSpace(tableType) ? "TCM_1" : tableType
            };
        }

        // Stari format: HorizontalScale/VerticalScale kao "m po jedinici"
        var hOld = Convert.ToDouble(data[8].Value);
        var vOld = Convert.ToDouble(data[9].Value);
        var baseElev = Convert.ToDouble(data[7].Value);
        var gridH = Convert.ToDouble(data[12].Value);
        return new ProfileViewData
        {
            ProfileId = Convert.ToString(data[0].Value) ?? fallbackId,
            AxisName = Convert.ToString(data[1].Value) ?? string.Empty,
            Origin = new Point3d(
                Convert.ToDouble(data[2].Value),
                Convert.ToDouble(data[3].Value),
                Convert.ToDouble(data[4].Value)),
            StartStation = Convert.ToDouble(data[5].Value),
            EndStation = Convert.ToDouble(data[6].Value),
            BaseElevation = baseElev,
            TopElevation = baseElev + gridH * Math.Max(vOld, 1e-9),
            HorizontalDenom = hOld <= 50 ? 1000 : hOld,
            VerticalDenom = vOld <= 20 ? 100 : vOld,
            StationTickInterval = Convert.ToDouble(data[10].Value),
            DrawVerticals = true,
            DrawTabulation = true
        };
    }

    private static double NormalizeDenom(double value) =>
        value < 10 ? 1000 : value;

    private static DBDictionary GetDictionary(Transaction tr, Database db, OpenMode mode)
    {
        var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
        if (!nod.Contains(DictionaryName))
        {
            if (mode != OpenMode.ForWrite)
            {
                return new DBDictionary();
            }

            nod.UpgradeOpen();
            var dictionary = new DBDictionary();
            nod.SetAt(DictionaryName, dictionary);
            tr.AddNewlyCreatedDBObject(dictionary, true);
            return dictionary;
        }

        return (DBDictionary)tr.GetObject(nod.GetAt(DictionaryName), mode);
    }
}
