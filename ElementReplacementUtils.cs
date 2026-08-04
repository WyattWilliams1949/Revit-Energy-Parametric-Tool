using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitAddin
{
    public static class ElementReplacementUtils
    {
        public static int ReplaceWalls(Document doc, ElementId targetTypeId,
            string studName, string insName,
            double studLenFt, double insLenFt, double shortTol, ElementId viewId = null)
        {
            var studType = GetWallType(doc, studName);
            var insType  = GetWallType(doc, insName);
            if (studType == null || insType == null) return 0;

            FilteredElementCollector collector;
            if (viewId != null && viewId != ElementId.InvalidElementId)
                collector = new FilteredElementCollector(doc, viewId);
            else
                collector = new FilteredElementCollector(doc);

            var walls = collector.OfClass(typeof(Wall)).Cast<Wall>()
                .Where(w => w.GetTypeId() == targetTypeId).ToList();

            int count = 0;
            Func<FamilySymbol, double> getWidth = sym =>
            {
                var p = sym.get_Parameter(BuiltInParameter.FAMILY_WIDTH_PARAM)
                     ?? sym.LookupParameter("Width") ?? sym.LookupParameter("Rough Width");
                return p?.StorageType == StorageType.Double ? p.AsDouble() : 4.0 / 12.0;
            };

            foreach (var wall in walls)
            {
                if (wall.Location is not LocationCurve lc) continue;
                var curve    = lc.Curve;
                var start    = curve.GetEndPoint(0);
                var end      = curve.GetEndPoint(1);
                double len   = curve.Length;
                var dir      = (end - start).Normalize();
                var levelId  = wall.LevelId;
                double baseOff = wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET).AsDouble();
                double height  = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble() ?? 10.0;
                bool flipped   = wall.Flipped;

                // Save hosted elements
                var inserts = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance))
                    .Cast<FamilyInstance>().Where(f => f.Host?.Id == wall.Id).ToList();
                var savedInserts = inserts.Where(f => f.Location is LocationPoint)
                    .Select(f =>
                    {
                        var lp    = (LocationPoint)f.Location;
                        var sill  = f.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM);
                        return new { f.Symbol, lp.Point, f.LevelId, f.HandFlipped, f.FacingFlipped,
                                     SillHeight = sill?.AsDouble() ?? 0.0 };
                    }).ToList();

                // Build insert exclusion zones
                var zones = savedInserts.Select(ins =>
                {
                    double w = getWidth(ins.Symbol);
                    double half = w / 2.0 + 2.0 / 12.0;
                    double d = (ins.Point - start).DotProduct(dir);
                    return (Math.Max(0, d - half), Math.Min(len, d + half));
                }).OrderBy(z => z.Item1).Aggregate(new List<(double, double)>(), (acc, z) =>
                {
                    if (acc.Count == 0) { acc.Add(z); return acc; }
                    var last = acc[^1];
                    if (z.Item1 <= last.Item2 + 0.01) acc[^1] = (last.Item1, Math.Max(last.Item2, z.Item2));
                    else acc.Add(z);
                    return acc;
                });

                double pos = 0; bool isStud = true;
                var segments = new List<Wall>();
                while (pos < len - shortTol)
                {
                    var zone = zones.FirstOrDefault(z => pos >= z.Item1 - 0.001 && pos < z.Item2 - shortTol);
                    if (zone != default)
                    {
                        double segLen = Math.Min(zone.Item2 - pos, len - pos);
                        if (segLen > shortTol) segments.Add(CreateWallSeg(doc, start, dir, pos, segLen, insType.Id, levelId, height, baseOff, flipped));
                        pos += segLen; continue;
                    }
                    double std = isStud ? studLenFt : insLenFt;
                    var nz = zones.FirstOrDefault(z => z.Item1 > pos + shortTol);
                    if (nz != default && pos + std > nz.Item1) std = nz.Item1 - pos;
                    if (pos + std > len) std = len - pos;
                    if (std > shortTol) segments.Add(CreateWallSeg(doc, start, dir, pos, std,
                        (isStud ? studType : insType).Id, levelId, height, baseOff, flipped));
                    pos += std; isStud = !isStud;
                }

                // Re-host inserts
                foreach (var ins in savedInserts)
                {
                    if (!ins.Symbol.IsActive) ins.Symbol.Activate();
                    var closest = segments.OrderBy(s =>
                        (s.Location as LocationCurve)?.Curve.Distance(ins.Point) ?? double.MaxValue).First();
                    var lvl = doc.GetElement(ins.LevelId) as Level;
                    if (lvl == null) continue;
                    var fi = doc.Create.NewFamilyInstance(ins.Point, ins.Symbol, closest, lvl,
                        Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                    if (fi.CanFlipFacing && fi.FacingFlipped != ins.FacingFlipped) fi.flipFacing();
                    if (fi.CanFlipHand && fi.HandFlipped != ins.HandFlipped) fi.flipHand();
                    var sill = fi.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM);
                    if (sill != null && !sill.IsReadOnly) sill.Set(ins.SillHeight);
                }
                doc.Delete(wall.Id);
                count++;
            }
            return count;
        }

        private static Wall CreateWallSeg(Document doc, XYZ start, XYZ dir, double pos, double len,
            ElementId typeId, ElementId levelId, double height, double baseOff, bool targetFlipped)
        {
            var line = Line.CreateBound(start + dir.Multiply(pos), start + dir.Multiply(pos + len));
            var w = Wall.Create(doc, line, typeId, levelId, height, baseOff, false, false);
            if (targetFlipped != w.Flipped) w.Flip();
            WallUtils.DisallowWallJoinAtEnd(w, 0);
            WallUtils.DisallowWallJoinAtEnd(w, 1);
            return w;
        }

        public static int ReplaceFloors(Document doc, ElementId targetTypeId, string studName, string insName, ElementId viewId = null)
        {
            var insType = new FilteredElementCollector(doc).OfClass(typeof(FloorType))
                .Cast<FloorType>().FirstOrDefault(ft => ft.Name == insName);
            if (insType == null)
            {
                System.Diagnostics.Debug.WriteLine($"[ElementReplacementUtils] WARNING: FloorType '{insName}' not found. Structural replacement will skip.");
                return 0;
            }

            FilteredElementCollector collector;
            if (viewId != null && viewId != ElementId.InvalidElementId)
                collector = new FilteredElementCollector(doc, viewId);
            else
                collector = new FilteredElementCollector(doc);

            var floors = collector.OfClass(typeof(Floor)).Cast<Floor>()
                .Where(f => f.GetTypeId() == targetTypeId).ToList();

            foreach (var fl in floors)
                fl.FloorType = insType;
            return floors.Count;
        }

        public static int ReplaceRoofs(Document doc, ElementId targetTypeId, string studName, string insName, ElementId viewId = null)
        {
            var insType = new FilteredElementCollector(doc).OfClass(typeof(RoofType))
                .Cast<RoofType>().FirstOrDefault(rt => rt.Name == insName);
            if (insType == null)
            {
                System.Diagnostics.Debug.WriteLine($"[ElementReplacementUtils] WARNING: RoofType '{insName}' not found. Structural replacement will skip.");
                return 0;
            }

            FilteredElementCollector collector;
            if (viewId != null && viewId != ElementId.InvalidElementId)
                collector = new FilteredElementCollector(doc, viewId);
            else
                collector = new FilteredElementCollector(doc);

            var roofs = collector.OfClass(typeof(RoofBase)).Cast<RoofBase>()
                .Where(r => r.GetTypeId() == targetTypeId).ToList();

            foreach (var r in roofs)
                r.ChangeTypeId(insType.Id);
            return roofs.Count;
        }

        public static WallType GetWallType(Document doc, string name)
        {
            var type = new FilteredElementCollector(doc).OfClass(typeof(WallType))
                .Cast<WallType>().FirstOrDefault(t => t.Name == name);
            if (type == null)
                System.Diagnostics.Debug.WriteLine($"[ElementReplacementUtils] WARNING: WallType '{name}' not found. Structural replacement will skip.");
            return type;
        }
    }
}
