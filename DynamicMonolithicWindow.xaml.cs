using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitAddin
{
    public class MonolithicRow : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        void Notify([CallerMemberName] string p = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));

        public string Name { get; set; }
        public ElementId TypeId { get; set; }
        public string Category { get; set; }
        public bool IsWall => Category == "Walls";

        public ObservableCollection<string> StudTypes { get; set; } = new ObservableCollection<string>();
        public ObservableCollection<string> InsTypes  { get; set; } = new ObservableCollection<string>();

        private bool _isSelected;
        public bool IsSelected { get => _isSelected; set { _isSelected = value; Notify(); } }

        private string _studType;
        public string StudType { get => _studType; set { _studType = value; Notify(); } }

        private string _insulationType;
        public string InsulationType { get => _insulationType; set { _insulationType = value; Notify(); } }

        private string _framingFactor = "25";
        public string FramingFactor { get => _framingFactor; set { _framingFactor = value; Notify(); } }
    }

    public partial class DynamicMonolithicWindow : Window
    {
        private readonly Document _doc;
        private readonly UIDocument _uidoc;
        private readonly RevitEventHandler _handler;
        private readonly ExternalEvent _exEvent;
        private readonly ObservableCollection<MonolithicRow> _rows = new ObservableCollection<MonolithicRow>();

        public DynamicMonolithicWindow(Document doc, UIDocument uidoc, RevitEventHandler handler, ExternalEvent exEvent)
        {
            InitializeComponent(); ThemeManager.ApplyTheme(this);
            _doc = doc; _uidoc = uidoc; _handler = handler; _exEvent = exEvent;
            LoadElements();
            icRows.ItemsSource = _rows;
        }

        private void LoadElements()
        {
            var wallNames  = new FilteredElementCollector(_doc).OfClass(typeof(WallType)).Cast<WallType>().OrderBy(t => t.Name).Select(t => t.Name).ToList();
            var floorNames = new FilteredElementCollector(_doc).OfClass(typeof(FloorType)).Cast<FloorType>().OrderBy(t => t.Name).Select(t => t.Name).ToList();
            var roofNames  = new FilteredElementCollector(_doc).OfClass(typeof(RoofType)).Cast<RoofType>().OrderBy(t => t.Name).Select(t => t.Name).ToList();

            AddRows<Wall>   ("Walls",  wallNames,  wallNames);
            AddRows<Floor>  ("Floors", floorNames, floorNames);
            AddRows<RoofBase>("Roofs", roofNames,  roofNames);
        }

        private void AddRows<TEl>(string cat, List<string> studNames, List<string> insNames) where TEl : Element
        {
            var typesInView = new FilteredElementCollector(_doc, _doc.ActiveView.Id)
                .OfClass(typeof(TEl)).Cast<TEl>()
                .Select(e => _doc.GetElement(e.GetTypeId()) as ElementType)
                .Where(t => t != null)
                .GroupBy(t => t.Id).Select(g => g.First())
                .OrderBy(t => t.Name).ToList();

            foreach (var et in typesInView)
            {
                var row = new MonolithicRow
                {
                    Name = $"{et.Name}  [{cat}]",
                    TypeId = et.Id,
                    Category = cat,
                    StudType = studNames.FirstOrDefault(),
                    InsulationType = insNames.FirstOrDefault()
                };
                foreach (var n in studNames) row.StudTypes.Add(n);
                foreach (var n in insNames)  row.InsTypes.Add(n);
                _rows.Add(row);
            }
        }

        private void BtnBack_Click(object sender, RoutedEventArgs e) => Close();

        private void BtnGenerate_Click(object sender, RoutedEventArgs e)
        {
            var selected = _rows.Where(r => r.IsSelected).ToList();
            if (selected.Count == 0) { ShowStatus("Select at least one element type."); return; }

            lblStatus.Visibility = System.Windows.Visibility.Collapsed;

            _handler.CurrentAction = uiApp =>
            {
                var doc = uiApp.ActiveUIDocument.Document;

                using (var t = new Transaction(doc, "Generate Monolithic Thermal Elements"))
                {
                    t.Start();
                    var fo = t.GetFailureHandlingOptions();
                    fo.SetFailuresPreprocessor(new WarningSwallower());
                    t.SetFailureHandlingOptions(fo);

                    foreach (var row in selected)
                    {
                        double.TryParse(row.FramingFactor, out double ff);
                        ff = Math.Max(0, Math.Min(100, ff));

                        if (row.Category == "Walls")
                            ProcessWall(doc, row, ff);
                        else if (row.Category == "Floors")
                            ProcessFloor(doc, row);
                        else
                            ProcessRoof(doc, row);
                    }
                    t.Commit();
                }
                Dispatcher.Invoke(() =>
                    Autodesk.Revit.UI.TaskDialog.Show("Done", "Monolithic thermal elements applied."));
            };
            _exEvent.Raise();
            Close();
        }

        private void ProcessWall(Document doc, MonolithicRow row, double ff)
        {
            var studType = new FilteredElementCollector(doc).OfClass(typeof(WallType))
                .Cast<WallType>().FirstOrDefault(t => t.Name == row.StudType);
            var insType  = new FilteredElementCollector(doc).OfClass(typeof(WallType))
                .Cast<WallType>().FirstOrDefault(t => t.Name == row.InsulationType);
            var targetType = doc.GetElement(row.TypeId) as WallType;
            if (studType == null || insType == null || targetType == null) return;

            var studData = ExtractThermalData(doc, studType);
            var insData  = ExtractThermalData(doc, insType);
            var outData  = Blend(studData, insData, ff / 100.0);

            double thickness = insType.Width > 0.01 ? insType.Width : targetType.Width;
            CreateAndApplyWallType(doc, targetType, outData, thickness, ff, row.StudType, row.InsulationType);
        }

        private void ProcessFloor(Document doc, MonolithicRow row)
        {
            // For floors, apply the insulation type's thermal data directly as a monolithic type
            var insType    = new FilteredElementCollector(doc).OfClass(typeof(FloorType))
                .Cast<FloorType>().FirstOrDefault(t => t.Name == row.InsulationType);
            var targetType = new FilteredElementCollector(doc).OfClass(typeof(FloorType))
                .Cast<FloorType>().FirstOrDefault(t => t.Id == row.TypeId);
            if (insType == null || targetType == null) return;

            var floors = new FilteredElementCollector(doc, doc.ActiveView.Id).OfClass(typeof(Floor))
                .Cast<Floor>().Where(f => f.GetTypeId() == row.TypeId).ToList();
            foreach (var f in floors) f.FloorType = insType;
        }

        private void ProcessRoof(Document doc, MonolithicRow row)
        {
            var insType = new FilteredElementCollector(doc).OfClass(typeof(RoofType))
                .Cast<RoofType>().FirstOrDefault(t => t.Name == row.InsulationType);
            if (insType == null) return;

            var roofs = new FilteredElementCollector(doc, doc.ActiveView.Id).OfClass(typeof(RoofBase))
                .Cast<RoofBase>().Where(r => r.GetTypeId() == row.TypeId).ToList();
            foreach (var r in roofs) r.ChangeTypeId(insType.Id);
        }

        private void CreateAndApplyWallType(Document doc, WallType target, ThermalData outData,
            double thickness, double ff, string studName, string insName)
        {
            string kStr   = Math.Round(outData.Conductivity, 4).ToString();
            string name   = $"{target.Name} - Mono (k={kStr})";

            var newType = new FilteredElementCollector(doc).OfClass(typeof(WallType))
                .Cast<WallType>().FirstOrDefault(t => t.Name == name)
                ?? (WallType)target.Duplicate(name);

            string uid   = Guid.NewGuid().ToString();
            var matId    = Material.Create(doc, $"Mono-k{kStr} ({uid[..5]})");
            var mat      = doc.GetElement(matId) as Material;
            if (mat != null)
            {
                var ta = new ThermalAsset($"TA_{uid}", ThermalMaterialType.Solid)
                {
                    ThermalConductivity = outData.Conductivity / 0.1761101838,
                    Density             = outData.Density * 16.01846,
                    SpecificHeat        = outData.SpecificHeat * 4186.8,
                    Emissivity          = outData.Emissivity,
                    Permeability        = outData.Permeability,
                    Porosity            = outData.Porosity,
                    Reflectivity        = outData.Reflectivity,
                    ElectricalResistivity = outData.ElectricalResistivity
                };
                var pse = PropertySetElement.Create(doc, ta);
                mat.SetMaterialAspectByPropertySet(MaterialAspect.Thermal, pse.Id);
            }
            var cs = CompoundStructure.CreateSingleLayerCompoundStructure(
                MaterialFunctionAssignment.Structure, thickness, matId);
            newType.SetCompoundStructure(cs);

            var walls = new FilteredElementCollector(doc, doc.ActiveView.Id).OfClass(typeof(Wall))
                .Cast<Wall>().Where(w => w.GetTypeId() == target.Id).ToList();
            foreach (var w in walls) w.WallType = newType;
        }

        // ── Thermal data helpers (ported from macro) ──────────────────────────
        private ThermalData ExtractThermalData(Document doc, WallType wt)
        {
            var d = new ThermalData { Conductivity = 0.05, SpecificHeat = 0.2, Density = 10.0,
                                      Emissivity = 0.9, Permeability = 0, Porosity = 0.1,
                                      Reflectivity = 0.1, ElectricalResistivity = 0 };
            var cs = wt.GetCompoundStructure();
            if (cs == null) return d;
            int idx = cs.StructuralMaterialIndex;
            if (idx < 0 && cs.LayerCount > 0) idx = 0;
            if (idx < 0) return d;
            var mat = doc.GetElement(cs.GetMaterialId(idx)) as Material;
            if (mat != null && mat.ThermalAssetId != ElementId.InvalidElementId)
            {
                var pse = doc.GetElement(mat.ThermalAssetId) as PropertySetElement;
                if (pse != null)
                {
                    var ta = pse.GetThermalAsset();
                    d.Conductivity  = ta.ThermalConductivity * 0.1761101838;
                    d.Density       = ta.Density / 16.01846;
                    d.SpecificHeat  = ta.SpecificHeat / 4186.8;
                    d.Emissivity    = ta.Emissivity;
                    d.Permeability  = ta.Permeability;
                    d.Porosity      = ta.Porosity;
                    d.Reflectivity  = ta.Reflectivity;
                    d.ElectricalResistivity = ta.ElectricalResistivity;
                }
            }
            return d;
        }

        private static ThermalData Blend(ThermalData s, ThermalData ins, double ffDec)
        {
            double m1 = ffDec * s.Density, m2 = (1 - ffDec) * ins.Density;
            return new ThermalData
            {
                Conductivity  = ffDec * s.Conductivity  + (1 - ffDec) * ins.Conductivity,
                Density       = ffDec * s.Density       + (1 - ffDec) * ins.Density,
                SpecificHeat  = (m1 + m2) > 0
                    ? (m1 * s.SpecificHeat + m2 * ins.SpecificHeat) / (m1 + m2)
                    : ins.SpecificHeat,
                Emissivity    = ffDec * s.Emissivity    + (1 - ffDec) * ins.Emissivity,
                Permeability  = ffDec * s.Permeability  + (1 - ffDec) * ins.Permeability,
                Porosity      = ffDec * s.Porosity      + (1 - ffDec) * ins.Porosity,
                Reflectivity  = ffDec * s.Reflectivity  + (1 - ffDec) * ins.Reflectivity,
                ElectricalResistivity = ffDec * s.ElectricalResistivity + (1 - ffDec) * ins.ElectricalResistivity
            };
        }

        private void ShowStatus(string msg) { lblStatus.Text = msg; lblStatus.Visibility = System.Windows.Visibility.Visible; }
    }

    // Shared thermal data carrier (used by both window code-behinds)
    public class ThermalData
    {
        public double Conductivity { get; set; }
        public double SpecificHeat { get; set; }
        public double Density { get; set; }
        public double Emissivity { get; set; }
        public double Permeability { get; set; }
        public double Porosity { get; set; }
        public double Reflectivity { get; set; }
        public double ElectricalResistivity { get; set; }
    }
}
