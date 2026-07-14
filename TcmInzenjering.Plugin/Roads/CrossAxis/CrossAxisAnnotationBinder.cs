using System.Text.RegularExpressions;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace TcmInzenjering.Plugin.Roads.CrossAxis;

internal static class CrossAxisAnnotationBinder
{
    // Bilo koji prefiks: "STA 7", "DR 21", "Pera 3"
    private static readonly Regex StaLabelRegex = new(
        @"^\s*(?<prefix>\S+)\s+(?<num>\d+)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Projektni format: "STA 7 0-120.00", "DR 21 0-400.00"
    private static readonly Regex CombinedStaRegex = new(
        @"^\s*(?<prefix>\S+)\s+(?<num>\d+)\s+(?<chain>\S.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ChainageRegex = new(
        @"^\s*-?\d+[+\-]\d+(?:[.,]\d+)?\s*$",
        RegexOptions.Compiled);

    public static bool TryParseStaNumber(string? text, out int number) =>
        TryParseCombined(text, out number, out _) || TryParsePureSta(text, out number);

    public static bool TryParseCombined(string? text, out int number, out string chainage) =>
        TryParseCombined(text, out _, out number, out chainage);

    public static bool TryParseCombined(string? text, out string prefix, out int number, out string chainage)
    {
        prefix = string.Empty;
        number = 0;
        chainage = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var match = CombinedStaRegex.Match(text.Trim());
        if (!match.Success || !int.TryParse(match.Groups["num"].Value, out number) || number <= 0)
        {
            return false;
        }

        prefix = match.Groups["prefix"].Value.Trim();
        chainage = match.Groups["chain"].Value.Trim();
        return prefix.Length > 0 && chainage.Length > 0;
    }

    public static bool TryParsePureSta(string? text, out int number)
    {
        number = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var match = StaLabelRegex.Match(text.Trim());
        return match.Success && int.TryParse(match.Groups["num"].Value, out number) && number > 0;
    }

    public static bool IsChainageText(string? text) =>
        !string.IsNullOrWhiteSpace(text) && ChainageRegex.IsMatch(text.Trim());

    public static bool TryGetTextContent(Transaction tr, Entity entity, out string text, out Point3d position)
    {
        text = string.Empty;
        position = Point3d.Origin;
        switch (entity)
        {
            case DBText dbText:
                text = dbText.TextString ?? string.Empty;
                position = GetDbTextAnchor(dbText);
                return true;
            case MText mText:
                text = mText.Contents ?? string.Empty;
                text = Regex.Replace(text, @"\\[A-Za-z][^;]*;", string.Empty);
                text = text.Replace("\\P", " ").Replace("{", string.Empty).Replace("}", string.Empty);
                position = mText.Location;
                return true;
            default:
                return false;
        }
    }

    public static bool TryGetTextContent(Entity entity, out string text, out Point3d position)
    {
        text = string.Empty;
        position = Point3d.Origin;
        if (entity.Database is null)
        {
            return false;
        }

        using var tr = entity.Database.TransactionManager.StartOpenCloseTransaction();
        var ok = TryGetTextContent(tr, entity, out text, out position);
        tr.Commit();
        return ok;
    }

    public static bool TrySetTextPosition(Entity entity, Point3d position)
    {
        switch (entity)
        {
            case DBText dbText:
                if (!dbText.IsWriteEnabled)
                {
                    dbText.UpgradeOpen();
                }

                if (UsesAlignmentPoint(dbText))
                {
                    dbText.AlignmentPoint = position;
                    if (dbText.Database is not null)
                    {
                        dbText.AdjustAlignment(dbText.Database);
                    }
                }
                else
                {
                    dbText.Position = position;
                }

                return true;
            case MText mText:
                if (!mText.IsWriteEnabled)
                {
                    mText.UpgradeOpen();
                }

                mText.Location = position;
                return true;
            default:
                return false;
        }
    }

    private static bool UsesAlignmentPoint(DBText dbText) =>
        dbText.HorizontalMode != TextHorizontalMode.TextLeft ||
        dbText.VerticalMode != TextVerticalMode.TextBase;

    private static Point3d GetDbTextAnchor(DBText dbText) =>
        UsesAlignmentPoint(dbText) ? dbText.AlignmentPoint : dbText.Position;

    public static int? FindNearestStaNumber(
        Transaction tr,
        Database db,
        Point3d origin,
        double searchRadius = 100.0)
    {
        var bestNumber = (int?)null;
        var bestDistance = double.MaxValue;

        foreach (var (_, text, position) in EnumerateTexts(tr, db))
        {
            if (!TryParseStaNumber(text, out var number))
            {
                continue;
            }

            var distance = origin.DistanceTo(position);
            if (distance <= searchRadius && distance < bestDistance)
            {
                bestDistance = distance;
                bestNumber = number;
            }
        }

        return bestNumber;
    }

    public static (Entity? Label, Entity? Station) FindOrBindAnnotations(
        Transaction tr,
        Database db,
        int axisNumber,
        Point3d origin,
        string? parentRoadAxisName = null,
        double searchRadius = 100.0)
    {
        RoadDrawing.EnsureRegApp(tr, db);

        Entity? boundLabel = null;
        Entity? boundStation = null;
        Entity? candidateLabel = null;
        Entity? candidateStation = null;
        Entity? candidateCombined = null;
        var bestLabelDistance = double.MaxValue;
        var bestStationDistance = double.MaxValue;
        var bestCombinedDistance = double.MaxValue;

        foreach (var (entity, text, position) in EnumerateTexts(tr, db))
        {
            if (CrossAxisXData.TryReadCrossAnnotation(entity, out var role, out var linkedNumber) &&
                linkedNumber == axisNumber)
            {
                if (role == CrossAxisXData.RoleCrossLabel)
                {
                    boundLabel = entity;
                }
                else if (role == CrossAxisXData.RoleCrossStation)
                {
                    boundStation = entity;
                }

                continue;
            }

            // Glavne oznake "Samo stacionaza" (npr. STA 0-300.00) nikad ne razdvajati.
            // Projektni format "STA 7 0-120.00" i dalje sme u CXLB/CXST split.
            if (RoadXData.TryReadStationLabel(entity, out _, out var stationRole, out _) &&
                stationRole == RoadXData.RoleText &&
                !TryParseCombined(text, out _, out _))
            {
                continue;
            }

            var distance = origin.DistanceTo(position);
            if (distance > searchRadius)
            {
                continue;
            }

            if (TryParseCombined(text, out var combinedNumber, out _) &&
                combinedNumber == axisNumber &&
                distance < bestCombinedDistance)
            {
                bestCombinedDistance = distance;
                candidateCombined = entity;
                continue;
            }

            if (TryParsePureSta(text, out var staNumber) &&
                staNumber == axisNumber &&
                distance < bestLabelDistance)
            {
                if (CrossAxisXData.TryReadCrossAnnotation(entity, out _, out var other) && other != axisNumber)
                {
                    continue;
                }

                bestLabelDistance = distance;
                candidateLabel = entity;
            }
            else if (IsChainageText(text) && distance < bestStationDistance)
            {
                if (CrossAxisXData.TryReadCrossAnnotation(entity, out _, out var other) && other != axisNumber)
                {
                    continue;
                }

                bestStationDistance = distance;
                candidateStation = entity;
            }
        }

        if (boundLabel is not null || boundStation is not null)
        {
            // Ne razdvajaj RoleText — to je glavna stacionaza putne ose.
            if (boundLabel is not null &&
                RoadXData.TryReadStationLabel(boundLabel, out _, out var boundRole, out _) &&
                boundRole == RoadXData.RoleText)
            {
                return (boundLabel, boundStation);
            }

            if (boundLabel is not null &&
                TryGetTextContent(boundLabel, out var boundText, out _) &&
                TryParseCombined(boundText, out _, out _) &&
                !(RoadXData.TryReadStationLabel(boundLabel, out _, out var roleChk, out _) &&
                  roleChk == RoadXData.RoleText))
            {
                return SplitCombinedText(tr, db, boundLabel, axisNumber, parentRoadAxisName);
            }

            var label = boundLabel ?? Bind(candidateLabel, CrossAxisXData.RoleCrossLabel, axisNumber, parentRoadAxisName);
            var station = boundStation ?? Bind(candidateStation, CrossAxisXData.RoleCrossStation, axisNumber, parentRoadAxisName);
            return (label, station);
        }

        if (candidateCombined is not null)
        {
            // RoleText (OSA 17 0-320.00) nikad ne splitovati ovde.
            if (RoadXData.TryReadStationLabel(candidateCombined, out _, out var combinedRole, out _) &&
                combinedRole == RoadXData.RoleText)
            {
                return (null, null);
            }

            return SplitCombinedText(tr, db, candidateCombined, axisNumber, parentRoadAxisName);
        }

        return (
            Bind(candidateLabel, CrossAxisXData.RoleCrossLabel, axisNumber, parentRoadAxisName),
            Bind(candidateStation, CrossAxisXData.RoleCrossStation, axisNumber, parentRoadAxisName));
    }

    public static bool TrySyncAxisNumberFromNearbyLabel(
        Transaction tr,
        Database db,
        Entity axisEntity,
        ISet<int> usedNumbers,
        out int number)
    {
        number = 0;
        if (!CrossAxisGeometry.TryGetFrame(axisEntity, out var origin, out _, out _))
        {
            return CrossAxisXData.TryReadCrossAxis(axisEntity, out number);
        }

        if (CrossAxisXData.TryReadCrossAxis(axisEntity, out number))
        {
            var nearby = FindNearestStaNumber(tr, db, origin);
            if (nearby is int detected &&
                detected != number &&
                !usedNumbers.Contains(detected))
            {
                if (!axisEntity.IsWriteEnabled)
                {
                    axisEntity.UpgradeOpen();
                }

                CrossAxisXData.AttachCrossAxis(axisEntity, detected);
                usedNumbers.Remove(number);
                usedNumbers.Add(detected);
                number = detected;
            }

            return true;
        }

        return false;
    }

    public static (Entity Label, Entity Station) SplitCombinedTextPublic(
        Transaction tr,
        Database db,
        Entity combined,
        int axisNumber,
        string? parentRoadAxisName) =>
        SplitCombinedText(tr, db, combined, axisNumber, parentRoadAxisName);

    private static (Entity Label, Entity Station) SplitCombinedText(
        Transaction tr,
        Database db,
        Entity combined,
        int axisNumber,
        string? parentRoadAxisName)
    {
        if (!TryGetTextContent(combined, out var text, out var position) ||
            !TryParseCombined(text, out var prefix, out var number, out var chainage))
        {
            if (!combined.IsWriteEnabled)
            {
                combined.UpgradeOpen();
            }

            CrossAxisXData.AttachCrossAnnotation(
                combined,
                CrossAxisXData.RoleCrossLabel,
                axisNumber,
                parentRoadAxisName);
            var stationFallback = combined is DBText dbFallback
                ? CreateTextClone(tr, db, dbFallback, "0+000.00", position)
                : CreatePlainText(tr, db, "0+000.00", position, 2.5);
            CrossAxisXData.AttachCrossAnnotation(
                stationFallback,
                CrossAxisXData.RoleCrossStation,
                axisNumber,
                parentRoadAxisName);
            return (combined, stationFallback);
        }

        var labelText = string.IsNullOrWhiteSpace(prefix)
            ? number.ToString()
            : $"{prefix.Trim()} {number}";
        Entity label;
        Entity station;

        if (combined is DBText dbText)
        {
            if (!dbText.IsWriteEnabled)
            {
                dbText.UpgradeOpen();
            }

            dbText.TextString = labelText;
            CrossAxisXData.AttachCrossAnnotation(
                dbText,
                CrossAxisXData.RoleCrossLabel,
                axisNumber,
                parentRoadAxisName);
            label = dbText;
            station = CreateTextClone(tr, db, dbText, chainage, position);
        }
        else if (combined is MText mText)
        {
            if (!mText.IsWriteEnabled)
            {
                mText.UpgradeOpen();
            }

            mText.Contents = labelText;
            CrossAxisXData.AttachCrossAnnotation(
                mText,
                CrossAxisXData.RoleCrossLabel,
                axisNumber,
                parentRoadAxisName);
            label = mText;
            station = CreateMTextClone(tr, db, mText, chainage, position);
        }
        else
        {
            label = CreatePlainText(tr, db, labelText, position, 2.5);
            CrossAxisXData.AttachCrossAnnotation(
                label,
                CrossAxisXData.RoleCrossLabel,
                axisNumber,
                parentRoadAxisName);
            station = CreatePlainText(tr, db, chainage, position, 2.5);
            combined.UpgradeOpen();
            combined.Erase();
        }

        CrossAxisXData.AttachCrossAnnotation(
            station,
            CrossAxisXData.RoleCrossStation,
            axisNumber,
            parentRoadAxisName);
        return (label, station);
    }

    private static DBText CreateTextClone(
        Transaction tr,
        Database db,
        DBText source,
        string contents,
        Point3d position)
    {
        var modelSpace = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db),
            OpenMode.ForWrite);

        var clone = new DBText
        {
            Position = position,
            Height = source.Height,
            Rotation = source.Rotation,
            TextString = contents,
            Layer = source.Layer,
            Color = source.Color,
            TextStyleId = source.TextStyleId
        };
        modelSpace.AppendEntity(clone);
        tr.AddNewlyCreatedDBObject(clone, true);
        return clone;
    }

    private static MText CreateMTextClone(
        Transaction tr,
        Database db,
        MText source,
        string contents,
        Point3d position)
    {
        var modelSpace = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db),
            OpenMode.ForWrite);

        var clone = new MText
        {
            Location = position,
            Contents = contents,
            TextHeight = source.TextHeight,
            Rotation = source.Rotation,
            Layer = source.Layer,
            Color = source.Color,
            TextStyleId = source.TextStyleId
        };
        modelSpace.AppendEntity(clone);
        tr.AddNewlyCreatedDBObject(clone, true);
        return clone;
    }

    private static DBText CreatePlainText(
        Transaction tr,
        Database db,
        string contents,
        Point3d position,
        double height)
    {
        var modelSpace = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db),
            OpenMode.ForWrite);

        var text = new DBText
        {
            Position = position,
            Height = height,
            TextString = contents,
            Layer = CrossAxisScanner.LayerName
        };
        modelSpace.AppendEntity(text);
        tr.AddNewlyCreatedDBObject(text, true);
        return text;
    }

    private static Entity? Bind(Entity? entity, string role, int axisNumber, string? parentRoadAxisName)
    {
        if (entity is null)
        {
            return null;
        }

        if (!entity.IsWriteEnabled)
        {
            entity.UpgradeOpen();
        }

        CrossAxisXData.AttachCrossAnnotation(entity, role, axisNumber, parentRoadAxisName);
        return entity;
    }

    private static IEnumerable<(Entity Entity, string Text, Point3d Position)> EnumerateTexts(
        Transaction tr,
        Database db)
    {
        var modelSpace = (BlockTableRecord)tr.GetObject(
            SymbolUtilityServices.GetBlockModelSpaceId(db),
            OpenMode.ForRead);

        foreach (ObjectId id in modelSpace)
        {
            if (id.IsErased)
            {
                continue;
            }

            if (tr.GetObject(id, OpenMode.ForRead) is not Entity entity)
            {
                continue;
            }

            if (!TryGetTextContent(tr, entity, out var text, out var position))
            {
                continue;
            }

            yield return (entity, text, position);
        }
    }
}
