using Autodesk.AutoCAD.DatabaseServices;

namespace TcmInzenjering.Plugin.Roads;

internal static class StationTextStyleHelper
{
    public const string StyleName = "TCM_STACIONAZA";

    public static ObjectId Ensure(Transaction tr, Database db, string fontFileName)
    {
        StationFontPreferences.Load();
        var resolvedFont = string.IsNullOrWhiteSpace(fontFileName)
            ? StationFontPreferences.FontFileName
            : fontFileName.Trim();

        var textStyleTable = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
        if (textStyleTable.Has(StyleName))
        {
            var existing = (TextStyleTableRecord)tr.GetObject(textStyleTable[StyleName], OpenMode.ForWrite);
            if (!string.Equals(existing.FileName, resolvedFont, StringComparison.OrdinalIgnoreCase))
            {
                existing.FileName = resolvedFont;
            }

            return existing.ObjectId;
        }

        textStyleTable.UpgradeOpen();
        var style = new TextStyleTableRecord
        {
            Name = StyleName,
            FileName = resolvedFont
        };
        textStyleTable.Add(style);
        tr.AddNewlyCreatedDBObject(style, true);
        return style.ObjectId;
    }
}
