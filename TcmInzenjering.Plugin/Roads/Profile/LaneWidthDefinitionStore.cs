using Autodesk.AutoCAD.DatabaseServices;

namespace TcmInzenjering.Plugin.Roads.Profile;

public enum LaneRole
{
    Carriageway = 0,
    Shoulder = 1,
    Other = 2
}

public sealed class LaneWidthPoint
{
    public double Station { get; set; }
    public double Width { get; set; }

    public LaneWidthPoint Clone() => new() { Station = Station, Width = Width };
}

public sealed class LaneWidthLane
{
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public double Width { get; set; } = 3.5;
    public bool IsCarriageway
    {
        get => Role == LaneRole.Carriageway;
        set => Role = value ? LaneRole.Carriageway : LaneRole.Shoulder;
    }

    public LaneRole Role { get; set; } = LaneRole.Carriageway;
    public List<LaneWidthPoint> WidthPoints { get; set; } = [];

    public LaneWidthLane Clone() => new()
    {
        Id = Id,
        Label = Label,
        Width = Width,
        Role = Role,
        WidthPoints = WidthPoints.Select(point => point.Clone()).ToList()
    };

    public double WidthAt(double station)
    {
        if (WidthPoints.Count == 0)
        {
            return Math.Max(0, Width);
        }

        var ordered = WidthPoints.OrderBy(point => point.Station).ToList();
        if (station <= ordered[0].Station)
        {
            return Math.Max(0, ordered[0].Width);
        }

        if (station >= ordered[^1].Station)
        {
            return Math.Max(0, ordered[^1].Width);
        }

        for (var i = 0; i < ordered.Count - 1; i++)
        {
            var a = ordered[i];
            var b = ordered[i + 1];
            if (station < a.Station || station > b.Station)
            {
                continue;
            }

            var span = b.Station - a.Station;
            if (Math.Abs(span) < 1e-9)
            {
                return Math.Max(0, a.Width);
            }

            var t = (station - a.Station) / span;
            return Math.Max(0, a.Width + t * (b.Width - a.Width));
        }

        return Math.Max(0, Width);
    }
}

public sealed class LaneWidthType
{
    public string Name { get; set; } = "Trenutni";
    public List<LaneWidthLane> Left { get; set; } = [];
    public List<LaneWidthLane> Right { get; set; } = [];

    public LaneWidthType Clone() => new()
    {
        Name = Name,
        Left = Left.Select(lane => lane.Clone()).ToList(),
        Right = Right.Select(lane => lane.Clone()).ToList()
    };

    public double LeftCarriagewayWidth =>
        Left.Where(lane => lane.IsCarriageway).Sum(lane => Math.Max(0, lane.Width));

    public double RightCarriagewayWidth =>
        Right.Where(lane => lane.IsCarriageway).Sum(lane => Math.Max(0, lane.Width));
}

public sealed class LaneTypeAssignment
{
    public double StartStation { get; set; }
    public double EndStation { get; set; }
    public string TypeName { get; set; } = string.Empty;

    public LaneTypeAssignment Clone() => new()
    {
        StartStation = StartStation,
        EndStation = EndStation,
        TypeName = TypeName
    };
}

public sealed class LaneWideningSettings
{
    public bool Enabled { get; set; }
    public double DesignSpeedKmh { get; set; } = 60;
    public double ManualDeltaLeft { get; set; }
    public double ManualDeltaRight { get; set; }
    public double TransitionLength { get; set; } = 20;

    public LaneWideningSettings Clone() => new()
    {
        Enabled = Enabled,
        DesignSpeedKmh = DesignSpeedKmh,
        ManualDeltaLeft = ManualDeltaLeft,
        ManualDeltaRight = ManualDeltaRight,
        TransitionLength = TransitionLength
    };
}

public sealed class LaneHatchSettings
{
    public bool Enabled { get; set; }
    public string Pattern { get; set; } = "ANSI31";
    public double Scale { get; set; } = 1.0;
    public double Angle { get; set; }
    public short ColorIndex { get; set; } = 8;

    public LaneHatchSettings Clone() => new()
    {
        Enabled = Enabled,
        Pattern = Pattern,
        Scale = Scale,
        Angle = Angle,
        ColorIndex = ColorIndex
    };
}

public sealed class LaneWidthDefinitionSet
{
    public string ActiveTypeName { get; set; } = "Trenutni";
    public List<LaneWidthType> Types { get; set; } = [];
    public List<LaneTypeAssignment> Assignments { get; set; } = [];
    public LaneWideningSettings Widening { get; set; } = new();
    public LaneHatchSettings Hatch { get; set; } = new();
    public bool DrawBoundaries { get; set; } = true;

    public LaneWidthDefinitionSet Clone() => new()
    {
        ActiveTypeName = ActiveTypeName,
        Types = Types.Select(type => type.Clone()).ToList(),
        Assignments = Assignments.Select(item => item.Clone()).ToList(),
        Widening = Widening.Clone(),
        Hatch = Hatch.Clone(),
        DrawBoundaries = DrawBoundaries
    };
}

/// <summary>
/// Imenovani tipovi poprečnog rasporeda traka vezani za nativnu osu (v3).
/// </summary>
internal static class LaneWidthDefinitionStore
{
    private const string DictionaryName = "TCM_LANE_WIDTH_TYPES";
    private const string KeyPrefix = "LWT_";
    private const string Version = "3";
    private const string Version2 = "2";

    public static bool HasSavedDefinitions(
        Transaction tr,
        Database db,
        string axisName)
    {
        var dictionary = TryGetDictionary(tr, db);
        return dictionary is not null && dictionary.Contains(GetKey(axisName));
    }

    public static LaneWidthDefinitionSet Load(
        Transaction tr,
        Database db,
        string axisName,
        double fallbackLeft = 3.5,
        double fallbackRight = 3.5)
    {
        var dictionary = TryGetDictionary(tr, db);
        if (dictionary is null)
        {
            return CreateDefault(fallbackLeft, fallbackRight);
        }

        var key = GetKey(axisName);
        if (!dictionary.Contains(key))
        {
            return CreateDefault(fallbackLeft, fallbackRight);
        }

        var record = (Xrecord)tr.GetObject(dictionary.GetAt(key), OpenMode.ForRead);
        var values = record.Data?.AsArray();
        if (values is null || values.Length < 4)
        {
            return CreateDefault(fallbackLeft, fallbackRight);
        }

        var version = values[0].Value?.ToString() ?? string.Empty;
        try
        {
            if (string.Equals(version, Version, StringComparison.Ordinal))
            {
                return LoadV3(values);
            }

            if (string.Equals(version, Version2, StringComparison.Ordinal))
            {
                return PromoteV2(values);
            }

            return CreateDefault(fallbackLeft, fallbackRight);
        }
        catch
        {
            return CreateDefault(fallbackLeft, fallbackRight);
        }
    }

    public static void Save(
        Transaction tr,
        Database db,
        string axisName,
        LaneWidthDefinitionSet definitions)
    {
        var normalized = Normalize(definitions);
        var values = new List<TypedValue>
        {
            new((int)DxfCode.Text, Version),
            new((int)DxfCode.Text, normalized.ActiveTypeName),
            new((int)DxfCode.Int32, normalized.Types.Count)
        };

        foreach (var type in normalized.Types)
        {
            values.Add(new TypedValue((int)DxfCode.Text, type.Name));
            AppendLanes(values, type.Left);
            AppendLanes(values, type.Right);
        }

        values.Add(new TypedValue((int)DxfCode.Int32, normalized.Assignments.Count));
        foreach (var assignment in normalized.Assignments)
        {
            values.Add(new TypedValue((int)DxfCode.Real, assignment.StartStation));
            values.Add(new TypedValue((int)DxfCode.Real, assignment.EndStation));
            values.Add(new TypedValue((int)DxfCode.Text, assignment.TypeName));
        }

        values.Add(new TypedValue((int)DxfCode.Int16, normalized.Widening.Enabled ? 1 : 0));
        values.Add(new TypedValue((int)DxfCode.Real, normalized.Widening.DesignSpeedKmh));
        values.Add(new TypedValue((int)DxfCode.Real, normalized.Widening.ManualDeltaLeft));
        values.Add(new TypedValue((int)DxfCode.Real, normalized.Widening.ManualDeltaRight));
        values.Add(new TypedValue((int)DxfCode.Real, normalized.Widening.TransitionLength));

        values.Add(new TypedValue((int)DxfCode.Int16, normalized.Hatch.Enabled ? 1 : 0));
        values.Add(new TypedValue((int)DxfCode.Text, normalized.Hatch.Pattern));
        values.Add(new TypedValue((int)DxfCode.Real, normalized.Hatch.Scale));
        values.Add(new TypedValue((int)DxfCode.Real, normalized.Hatch.Angle));
        values.Add(new TypedValue((int)DxfCode.Int16, normalized.Hatch.ColorIndex));
        values.Add(new TypedValue((int)DxfCode.Int16, normalized.DrawBoundaries ? 1 : 0));

        var dictionary = GetDictionaryForWrite(tr, db);
        var key = GetKey(axisName);
        var buffer = new ResultBuffer(values.ToArray());
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

    public static IReadOnlyList<LaneWidthType> BuiltInTemplates() =>
    [
        CreateTemplate("2×3.50",
            [("TRAKA_L1", 3.5, true)],
            [("TRAKA_D1", 3.5, true)]),
        CreateTemplate("1×3.50 + bankina 1.00",
            [("TRAKA_L1", 3.5, true), ("BANKINA_L", 1.0, false)],
            [("TRAKA_D1", 3.5, true), ("BANKINA_D", 1.0, false)]),
        CreateTemplate("2+2×3.50",
            [("TRAKA_L1", 3.5, true), ("TRAKA_L2", 3.5, true)],
            [("TRAKA_D1", 3.5, true), ("TRAKA_D2", 3.5, true)]),
        CreateTemplate("3.50+1.00+3.50",
            [("TRAKA_L1", 3.5, true), ("RAZDELNA", 1.0, false)],
            [("TRAKA_D1", 3.5, true)])
    ];

    private static LaneWidthDefinitionSet LoadV3(IReadOnlyList<TypedValue> values)
    {
        var index = 1;
        var activeName = ReadText(values, ref index);
        var typeCount = ReadCount(values, ref index);
        var types = new List<LaneWidthType>(typeCount);
        for (var typeIndex = 0; typeIndex < typeCount; typeIndex++)
        {
            var type = new LaneWidthType { Name = ReadText(values, ref index) };
            ReadLanesV3(values, ref index, type.Left, "L");
            ReadLanesV3(values, ref index, type.Right, "R");
            types.Add(type);
        }

        if (types.Count == 0)
        {
            return CreateDefault(3.5, 3.5);
        }

        var result = new LaneWidthDefinitionSet
        {
            ActiveTypeName = types.Any(type =>
                string.Equals(type.Name, activeName, StringComparison.OrdinalIgnoreCase))
                ? activeName
                : types[0].Name,
            Types = types
        };

        if (index >= values.Count)
        {
            return result;
        }

        var assignmentCount = ReadCount(values, ref index);
        for (var i = 0; i < assignmentCount && index + 2 < values.Count; i++)
        {
            result.Assignments.Add(new LaneTypeAssignment
            {
                StartStation = Convert.ToDouble(values[index++].Value),
                EndStation = Convert.ToDouble(values[index++].Value),
                TypeName = ReadText(values, ref index)
            });
        }

        if (index + 4 < values.Count)
        {
            result.Widening = new LaneWideningSettings
            {
                Enabled = Convert.ToInt16(values[index++].Value) != 0,
                DesignSpeedKmh = Convert.ToDouble(values[index++].Value),
                ManualDeltaLeft = Convert.ToDouble(values[index++].Value),
                ManualDeltaRight = Convert.ToDouble(values[index++].Value),
                TransitionLength = Convert.ToDouble(values[index++].Value)
            };
        }

        if (index + 4 < values.Count)
        {
            result.Hatch = new LaneHatchSettings
            {
                Enabled = Convert.ToInt16(values[index++].Value) != 0,
                Pattern = ReadText(values, ref index),
                Scale = Convert.ToDouble(values[index++].Value),
                Angle = Convert.ToDouble(values[index++].Value),
                ColorIndex = Convert.ToInt16(values[index++].Value)
            };
        }

        if (index < values.Count)
        {
            result.DrawBoundaries = Convert.ToInt16(values[index].Value) != 0;
        }

        return result;
    }

    private static LaneWidthDefinitionSet PromoteV2(IReadOnlyList<TypedValue> values)
    {
        var index = 1;
        var activeName = ReadText(values, ref index);
        var typeCount = ReadCount(values, ref index);
        var types = new List<LaneWidthType>(typeCount);
        for (var typeIndex = 0; typeIndex < typeCount; typeIndex++)
        {
            var type = new LaneWidthType { Name = ReadText(values, ref index) };
            ReadLanesV2(values, ref index, type.Left, "L");
            ReadLanesV2(values, ref index, type.Right, "R");
            types.Add(type);
        }

        if (types.Count == 0)
        {
            return CreateDefault(3.5, 3.5);
        }

        return new LaneWidthDefinitionSet
        {
            ActiveTypeName = types.Any(type =>
                string.Equals(type.Name, activeName, StringComparison.OrdinalIgnoreCase))
                ? activeName
                : types[0].Name,
            Types = types
        };
    }

    private static LaneWidthDefinitionSet Normalize(LaneWidthDefinitionSet source)
    {
        var result = new LaneWidthDefinitionSet
        {
            Widening = source.Widening.Clone(),
            Hatch = source.Hatch.Clone(),
            DrawBoundaries = source.DrawBoundaries
        };

        foreach (var sourceType in source.Types)
        {
            var name = sourceType.Name.Trim();
            if (name.Length == 0 ||
                result.Types.Any(type =>
                    string.Equals(type.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var type = sourceType.Clone();
            type.Name = name;
            type.Left = NormalizeLanes(type.Left, "L");
            type.Right = NormalizeLanes(type.Right, "R");
            result.Types.Add(type);
        }

        if (result.Types.Count == 0)
        {
            result = CreateDefault(3.5, 3.5);
            result.Widening = source.Widening.Clone();
            result.Hatch = source.Hatch.Clone();
            result.DrawBoundaries = source.DrawBoundaries;
        }

        result.ActiveTypeName = result.Types.Any(type =>
                string.Equals(type.Name, source.ActiveTypeName, StringComparison.OrdinalIgnoreCase))
            ? source.ActiveTypeName
            : result.Types[0].Name;

        result.Assignments = source.Assignments
            .Where(item =>
                item.EndStation > item.StartStation &&
                result.Types.Any(type =>
                    string.Equals(type.Name, item.TypeName, StringComparison.OrdinalIgnoreCase)))
            .Select(item => item.Clone())
            .OrderBy(item => item.StartStation)
            .ToList();

        result.Widening.DesignSpeedKmh = Math.Max(10, result.Widening.DesignSpeedKmh);
        result.Widening.TransitionLength = Math.Max(0, result.Widening.TransitionLength);
        result.Hatch.Scale = Math.Max(0.01, result.Hatch.Scale);
        if (string.IsNullOrWhiteSpace(result.Hatch.Pattern))
        {
            result.Hatch.Pattern = "ANSI31";
        }

        return result;
    }

    private static List<LaneWidthLane> NormalizeLanes(
        IEnumerable<LaneWidthLane> lanes,
        string sidePrefix)
    {
        var result = new List<LaneWidthLane>();
        var index = 1;
        foreach (var lane in lanes.Where(item => item.Width > 1e-6 || item.WidthPoints.Count > 0))
        {
            var id = string.IsNullOrWhiteSpace(lane.Id)
                ? $"{sidePrefix}{index}"
                : lane.Id.Trim();
            result.Add(new LaneWidthLane
            {
                Id = id,
                Label = string.IsNullOrWhiteSpace(lane.Label) ? $"TRAKA_{id}" : lane.Label.Trim(),
                Width = Math.Max(0, lane.Width),
                Role = lane.Role,
                WidthPoints = lane.WidthPoints
                    .Where(point => point.Width > 1e-9)
                    .Select(point => new LaneWidthPoint
                    {
                        Station = point.Station,
                        Width = Math.Max(0, point.Width)
                    })
                    .OrderBy(point => point.Station)
                    .ToList()
            });
            index++;
        }

        return result;
    }

    private static LaneWidthDefinitionSet CreateDefault(double left, double right) => new()
    {
        ActiveTypeName = "Trenutni",
        Types =
        [
            CreateTemplate("Trenutni",
                [("TRAKA_L1", Math.Max(0.1, left), true)],
                [("TRAKA_D1", Math.Max(0.1, right), true)])
        ]
    };

    private static LaneRole ResolveTemplateRole(string label, bool carriageway)
    {
        if (carriageway)
        {
            return LaneRole.Carriageway;
        }

        if (label.IndexOf("BANKINA", StringComparison.OrdinalIgnoreCase) >= 0 ||
            label.IndexOf("SHOULDER", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return LaneRole.Shoulder;
        }

        if (label.IndexOf("RAZDELNA", StringComparison.OrdinalIgnoreCase) >= 0 ||
            label.IndexOf("MEDIAN", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return LaneRole.Other;
        }

        return LaneRole.Shoulder;
    }

    private static LaneWidthType CreateTemplate(
        string name,
        IEnumerable<(string Label, double Width, bool Carriageway)> left,
        IEnumerable<(string Label, double Width, bool Carriageway)> right)
    {
        var type = new LaneWidthType { Name = name };
        var leftIndex = 1;
        foreach (var item in left)
        {
            type.Left.Add(new LaneWidthLane
            {
                Id = $"L{leftIndex++}",
                Label = item.Label,
                Width = item.Width,
                Role = ResolveTemplateRole(item.Label, item.Carriageway)
            });
        }

        var rightIndex = 1;
        foreach (var item in right)
        {
            type.Right.Add(new LaneWidthLane
            {
                Id = $"R{rightIndex++}",
                Label = item.Label,
                Width = item.Width,
                Role = ResolveTemplateRole(item.Label, item.Carriageway)
            });
        }

        return type;
    }

    private static void AppendLanes(List<TypedValue> values, IReadOnlyCollection<LaneWidthLane> lanes)
    {
        values.Add(new TypedValue((int)DxfCode.Int32, lanes.Count));
        foreach (var lane in lanes)
        {
            values.Add(new TypedValue((int)DxfCode.Text, lane.Id));
            values.Add(new TypedValue((int)DxfCode.Text, lane.Label));
            values.Add(new TypedValue((int)DxfCode.Real, lane.Width));
            values.Add(new TypedValue((int)DxfCode.Int16, (short)lane.Role));
            values.Add(new TypedValue((int)DxfCode.Int32, lane.WidthPoints.Count));
            foreach (var point in lane.WidthPoints)
            {
                values.Add(new TypedValue((int)DxfCode.Real, point.Station));
                values.Add(new TypedValue((int)DxfCode.Real, point.Width));
            }
        }
    }

    private static void ReadLanesV3(
        IReadOnlyList<TypedValue> values,
        ref int index,
        ICollection<LaneWidthLane> target,
        string sidePrefix)
    {
        var count = ReadCount(values, ref index);
        for (var laneIndex = 0; laneIndex < count; laneIndex++)
        {
            var lane = new LaneWidthLane
            {
                Id = ReadText(values, ref index),
                Label = ReadText(values, ref index),
                Width = Convert.ToDouble(values[index++].Value),
                Role = (LaneRole)Convert.ToInt16(values[index++].Value)
            };
            if (string.IsNullOrWhiteSpace(lane.Id))
            {
                lane.Id = $"{sidePrefix}{laneIndex + 1}";
            }

            var pointCount = ReadCount(values, ref index);
            for (var pointIndex = 0; pointIndex < pointCount; pointIndex++)
            {
                lane.WidthPoints.Add(new LaneWidthPoint
                {
                    Station = Convert.ToDouble(values[index++].Value),
                    Width = Convert.ToDouble(values[index++].Value)
                });
            }

            target.Add(lane);
        }
    }

    private static void ReadLanesV2(
        IReadOnlyList<TypedValue> values,
        ref int index,
        ICollection<LaneWidthLane> target,
        string sidePrefix)
    {
        var count = ReadCount(values, ref index);
        for (var laneIndex = 0; laneIndex < count; laneIndex++)
        {
            var label = ReadText(values, ref index);
            var width = Convert.ToDouble(values[index++].Value);
            var carriageway = Convert.ToInt16(values[index++].Value) != 0;
            target.Add(new LaneWidthLane
            {
                Id = $"{sidePrefix}{laneIndex + 1}",
                Label = label,
                Width = width,
                Role = carriageway ? LaneRole.Carriageway : LaneRole.Shoulder
            });
        }
    }

    private static string ReadText(IReadOnlyList<TypedValue> values, ref int index) =>
        values[index++].Value?.ToString() ?? string.Empty;

    private static int ReadCount(IReadOnlyList<TypedValue> values, ref int index) =>
        Math.Max(0, Convert.ToInt32(values[index++].Value));

    private static DBDictionary? TryGetDictionary(Transaction tr, Database db)
    {
        var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
        return nod.Contains(DictionaryName)
            ? (DBDictionary)tr.GetObject(nod.GetAt(DictionaryName), OpenMode.ForRead)
            : null;
    }

    private static DBDictionary GetDictionaryForWrite(Transaction tr, Database db)
    {
        var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
        if (!nod.Contains(DictionaryName))
        {
            nod.UpgradeOpen();
            var dictionary = new DBDictionary();
            nod.SetAt(DictionaryName, dictionary);
            tr.AddNewlyCreatedDBObject(dictionary, true);
            return dictionary;
        }

        return (DBDictionary)tr.GetObject(nod.GetAt(DictionaryName), OpenMode.ForWrite);
    }

    private static string GetKey(string axisName) =>
        KeyPrefix + axisName.Trim().ToUpperInvariant();
}
