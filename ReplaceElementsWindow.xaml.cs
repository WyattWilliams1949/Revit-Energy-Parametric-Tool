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
    // ViewModel for a single mapping row
    public class ElementMappingRow : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        void Notify([CallerMemberName] string p = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));

        public string Name { get; set; }
        public ElementId TypeId { get; set; }
        public ElementType TypeRef { get; set; }     // wall, floor, or roof type
        public string Category { get; set; }         // "Walls", "Floors", "Roofs"
        public ObservableCollection<string> AllTypes { get; set; } = new ObservableCollection<string>();

        private bool _isSelected;
        public bool IsSelected { get => _isSelected; set { _isSelected = value; Notify(); } }

        private string _studType;
        public string StudType { get => _studType; set { _studType = value; Notify(); } }

        private string _insulationType;
        public string InsulationType { get => _insulationType; set { _insulationType = value; Notify(); } }
    }

    public partial class ReplaceElementsWindow : Window
    {
        private readonly Document _doc;
        private readonly UIDocument _uidoc;
        private readonly RevitEventHandler _handler;
        private readonly ExternalEvent _exEvent;
        private readonly ObservableCollection<ElementMappingRow> _rows = new ObservableCollection<ElementMappingRow>();

        // All available type names by category
        private Dictionary<string, List<string>> _typesByCategory = new Dictionary<string, List<string>>();

        public ReplaceElementsWindow(Document doc, UIDocument uidoc, RevitEventHandler handler, ExternalEvent exEvent)
        {
            InitializeComponent(); ThemeManager.ApplyTheme(this);
            _doc = doc;
            _uidoc = uidoc;
            _handler = handler;
            _exEvent = exEvent;
            LoadTypes();
            icMappings.ItemsSource = _rows;
        }

        private void LoadTypes()
        {
            // Collect all type names per category for dropdown population
            var allWallTypes = new FilteredElementCollector(_doc).OfClass(typeof(WallType))
                .Cast<WallType>().OrderBy(t => t.Name).ToList();
            var allFloorTypes = new FilteredElementCollector(_doc).OfClass(typeof(FloorType))
                .Cast<FloorType>().OrderBy(t => t.Name).ToList();
            var allRoofTypes = new FilteredElementCollector(_doc).OfClass(typeof(RoofType))
                .Cast<RoofType>().OrderBy(t => t.Name).ToList();

            var wallNames  = allWallTypes.Select(t => t.Name).ToList();
            var floorNames = allFloorTypes.Select(t => t.Name).ToList();
            var roofNames  = allRoofTypes.Select(t => t.Name).ToList();

            _typesByCategory["Walls"]  = wallNames;
            _typesByCategory["Floors"] = floorNames;
            _typesByCategory["Roofs"]  = roofNames;

            // Add rows: one per unique type in the active view
            AddRows<Wall>("Walls",  wallNames,  allWallTypes.Cast<ElementType>().ToList());
            AddRows<Floor>("Floors", floorNames, allFloorTypes.Cast<ElementType>().ToList());
            AddRows<RoofBase>("Roofs", roofNames, allRoofTypes.Cast<ElementType>().ToList());
        }

        private void AddRows<TElement>(string category, List<string> allNames, List<ElementType> allTypes)
            where TElement : Element
        {
            var typesInView = new FilteredElementCollector(_doc, _doc.ActiveView.Id)
                .OfClass(typeof(TElement)).Cast<TElement>()
                .Select(e => _doc.GetElement(e.GetTypeId()) as ElementType)
                .Where(t => t != null)
                .GroupBy(t => t.Id)
                .Select(g => g.First())
                .OrderBy(t => t.Name)
                .ToList();

            foreach (var et in typesInView)
            {
                var row = new ElementMappingRow
                {
                    Name = $"{et.Name}  [{category}]",
                    TypeId = et.Id,
                    TypeRef = et,
                    Category = category,
                    StudType = allNames.FirstOrDefault(),
                    InsulationType = allNames.FirstOrDefault()
                };
                foreach (var n in allNames) row.AllTypes.Add(n);
                _rows.Add(row);
            }
        }

        private void BtnBack_Click(object sender, RoutedEventArgs e) => Close();

        private void BtnReplace_Click(object sender, RoutedEventArgs e)
        {
            var selected = _rows.Where(r => r.IsSelected).ToList();
            if (selected.Count == 0)
            {
                ShowStatus("Select at least one element type to replace.");
                return;
            }

            // Validate all dropdowns
            foreach (var row in selected)
            {
                if (string.IsNullOrEmpty(row.StudType) || string.IsNullOrEmpty(row.InsulationType))
                {
                    ShowStatus($"Please select stud and insulation types for '{row.Name}'.");
                    return;
                }
            }

            lblStatus.Visibility = System.Windows.Visibility.Collapsed;

            _handler.CurrentAction = uiApp =>
            {
                var doc = uiApp.ActiveUIDocument.Document;
                int processed = 0;
                double studLenFt = 2.0 / 12.0;
                double insLenFt  = 14.0 / 12.0;
                double shortTol  = doc.Application.ShortCurveTolerance;

                using (var t = new Transaction(doc, "Replace Elements with Stud+Insulation"))
                {
                    t.Start();
                    var failOpt = t.GetFailureHandlingOptions();
                    failOpt.SetFailuresPreprocessor(new WarningSwallower());
                    t.SetFailureHandlingOptions(failOpt);

                    foreach (var row in selected)
                    {
                        string studName = row.StudType;
                        string insName  = row.InsulationType;

                        if (row.Category == "Walls")
                            processed += ElementReplacementUtils.ReplaceWalls(doc, row.TypeId, studName, insName, studLenFt, insLenFt, shortTol, doc.ActiveView.Id);
                        else if (row.Category == "Floors")
                            processed += ElementReplacementUtils.ReplaceFloors(doc, row.TypeId, studName, insName, doc.ActiveView.Id);
                        else if (row.Category == "Roofs")
                            processed += ElementReplacementUtils.ReplaceRoofs(doc, row.TypeId, studName, insName, doc.ActiveView.Id);
                    }
                    t.Commit();
                }
                Dispatcher.Invoke(() =>
                    Autodesk.Revit.UI.TaskDialog.Show("Done",
                        $"Processed {processed} element(s) across all mappings."));
            };
            _exEvent.Raise();
            Close();
        }



        private void ShowStatus(string msg)
        {
            lblStatus.Text = msg;
            lblStatus.Visibility = System.Windows.Visibility.Visible;
        }
    }

    // Shared WarningSwallower (reused from macro)
    public class WarningSwallower : Autodesk.Revit.DB.IFailuresPreprocessor
    {
        public bool HasError { get; private set; } = false;

        public Autodesk.Revit.DB.FailureProcessingResult PreprocessFailures(Autodesk.Revit.DB.FailuresAccessor fa)
        {
            foreach (var f in fa.GetFailureMessages())
            {
                if (f.GetSeverity() == Autodesk.Revit.DB.FailureSeverity.Warning) fa.DeleteWarning(f);
                else if (f.GetSeverity() == Autodesk.Revit.DB.FailureSeverity.Error)
                {
                    HasError = true;
                    return Autodesk.Revit.DB.FailureProcessingResult.ProceedWithRollBack;
                }
            }
            return Autodesk.Revit.DB.FailureProcessingResult.Continue;
        }
    }
}
