using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitAddin
{
    public partial class EffectiveRValueWindow : Window
    {
        private readonly Document _doc;
        private readonly UIDocument _uidoc;
        private readonly RevitEventHandler _handler;
        private readonly ExternalEvent _exEvent;

        // Map displayed name → WallType
        private readonly Dictionary<string, WallType> _wallTypeMap = new Dictionary<string, WallType>();

        public EffectiveRValueWindow(Document doc, UIDocument uidoc, RevitEventHandler handler, ExternalEvent exEvent)
        {
            InitializeComponent(); ThemeManager.ApplyTheme(this);
            _doc  = doc;
            _uidoc = uidoc;
            _handler = handler;
            _exEvent = exEvent;
            LoadWallTypes();
        }

        private void LoadWallTypes()
        {
            var types = new FilteredElementCollector(_doc).OfClass(typeof(WallType))
                .Cast<WallType>().OrderBy(t => t.Name).ToList();
            foreach (var wt in types)
            {
                _wallTypeMap[wt.Name] = wt;
                lbWallTypes.Items.Add(wt.Name);
            }
        }

        private void BtnBack_Click(object sender, RoutedEventArgs e) => Close();

        private void BtnCalculate_Click(object sender, RoutedEventArgs e)
        {
            var selectedNames = lbWallTypes.SelectedItems.Cast<string>().ToList();
            if (selectedNames.Count == 0)
            {
                ShowStatus("Select at least one wall type from the list.");
                return;
            }

            if (!TryParseDoubles(out double rStud, out double rIns, out double rWin,
                                  out double rDoor, out double ff, out double density,
                                  out double specificHeat))
            {
                ShowStatus("One or more numeric fields contain invalid values.");
                return;
            }

            lblStatus.Visibility = System.Windows.Visibility.Collapsed;

            _handler.CurrentAction = uiApp =>
            {
                var doc = uiApp.ActiveUIDocument.Document;
                int processed = 0;

                Func<FamilyInstance, double> getInsertArea = fi =>
                {
                    var wP = fi.Symbol.get_Parameter(BuiltInParameter.FAMILY_WIDTH_PARAM)
                              ?? fi.Symbol.LookupParameter("Width");
                    var hP = fi.Symbol.get_Parameter(BuiltInParameter.FAMILY_HEIGHT_PARAM)
                              ?? fi.Symbol.LookupParameter("Height");
                    return (wP != null && hP != null) ? wP.AsDouble() * hP.AsDouble() : 0.0;
                };

                using (var t = new Transaction(doc, "Calculate Effective R-Value"))
                {
                    t.Start();
                    var failOpt = t.GetFailureHandlingOptions();
                    failOpt.SetFailuresPreprocessor(new WarningSwallower());
                    t.SetFailureHandlingOptions(failOpt);

                    // Get all walls whose type matches selected
                    foreach (var typeName in selectedNames)
                    {
                        var wallType = new FilteredElementCollector(doc).OfClass(typeof(WallType))
                            .Cast<WallType>().FirstOrDefault(wt => wt.Name == typeName);
                        if (wallType == null) continue;

                        var walls = new FilteredElementCollector(doc).OfClass(typeof(Wall))
                            .Cast<Wall>().Where(w => w.GetTypeId() == wallType.Id).ToList();
                        if (walls.Count == 0) continue;

                        // For R-value calculation, analyse the first instance's area breakdown;
                        // then create a type and apply it to all instances.
                        double opaqueArea  = 0; double windowArea = 0; double doorArea = 0;
                        foreach (var w in walls)
                        {
                            var aParam = w.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED);
                            opaqueArea += aParam?.AsDouble() ?? 0;

                            var inserts = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance))
                                .Cast<FamilyInstance>().Where(f => f.Host?.Id == w.Id).ToList();
                            foreach (var fi in inserts)
                            {
                                if (fi.Category.Id.Value == (long)BuiltInCategory.OST_Windows)
                                    windowArea += getInsertArea(fi);
                                else if (fi.Category.Id.Value == (long)BuiltInCategory.OST_Doors)
                                    doorArea += getInsertArea(fi);
                            }
                        }

                        double grossArea = opaqueArea + windowArea + doorArea;
                        if (grossArea <= 0.001) continue;

                        double ffDec    = ff / 100.0;
                        double uOpaque  = (ffDec / rStud) + ((1.0 - ffDec) / rIns);
                        double uWindow  = rWin  > 0 ? 1.0 / rWin  : 0;
                        double uDoor    = rDoor > 0 ? 1.0 / rDoor : 0;
                        double uOverall = ((uOpaque * opaqueArea) + (uWindow * windowArea) + (uDoor * doorArea)) / grossArea;
                        double rEff     = 1.0 / uOverall;
                        string rStr     = Math.Round(rEff, 2).ToString();

                        double kRequired = (1.0 / rEff) / 0.1761101838;
                        double densitySI = density * 16.01846;
                        double shSI      = specificHeat * 4186.8;

                        string newName = $"{wallType.Name} - Eff R-{rStr}";
                        var newType = new FilteredElementCollector(doc).OfClass(typeof(WallType))
                            .Cast<WallType>().FirstOrDefault(wt => wt.Name == newName)
                            ?? (WallType)wallType.Duplicate(newName);

                        string uid    = Guid.NewGuid().ToString();
                        string matName = $"Thermal Mass - R{rStr} ({uid[..5]})";
                        var matId     = Material.Create(doc, matName);
                        var newMat    = doc.GetElement(matId) as Material;
                        if (newMat != null)
                        {
                            var ta = new ThermalAsset($"TA_{uid}", ThermalMaterialType.Solid)
                                { ThermalConductivity = kRequired, Density = densitySI, SpecificHeat = shSI };
                            var pse = PropertySetElement.Create(doc, ta);
                            newMat.SetMaterialAspectByPropertySet(MaterialAspect.Thermal, pse.Id);
                        }
                        var cs = CompoundStructure.CreateSingleLayerCompoundStructure(
                            MaterialFunctionAssignment.Structure, 1.0, matId);
                        newType.SetCompoundStructure(cs);

                        var comments = newType.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS);
                        if (comments != null && !comments.IsReadOnly)
                            comments.Set($"Effective R: {rStr} | Framing {ff}% @ R-{rStud}, Ins: R-{rIns}");

                        foreach (var w in walls) { w.WallType = newType; processed++; }
                    }
                    t.Commit();
                }
                Dispatcher.Invoke(() =>
                    Autodesk.Revit.UI.TaskDialog.Show("Done",
                        $"Updated {processed} wall(s) with effective R-value types."));
            };
            _exEvent.Raise();
            Close();
        }

        private bool TryParseDoubles(out double rStud, out double rIns, out double rWin,
            out double rDoor, out double ff, out double density, out double specificHeat)
        {
            rStud = rIns = rWin = rDoor = ff = density = specificHeat = 0;
            return double.TryParse(txtRStud.Text, out rStud)
                && double.TryParse(txtRIns.Text, out rIns)
                && double.TryParse(txtRWin.Text, out rWin)
                && double.TryParse(txtRDoor.Text, out rDoor)
                && double.TryParse(txtFF.Text, out ff)
                && double.TryParse(txtDensity.Text, out density)
                && double.TryParse(txtSpecificHeat.Text, out specificHeat);
        }

        private void ShowStatus(string msg)
        {
            lblStatus.Text = msg;
            lblStatus.Visibility = System.Windows.Visibility.Visible;
        }
    }
}
