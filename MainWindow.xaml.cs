using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitAddin
{
    public partial class MainWindow : Window
    {
        private Document _doc;
        public ObservableCollection<SimulationElement> Elements { get; set; }

        private RevitEventHandler _handler;
        private ExternalEvent _exEvent;

        public MainWindow(Document doc, List<SimulationElement> uiElements, RevitEventHandler handler, ExternalEvent exEvent)
        {
            InitializeComponent();
            ThemeManager.ApplyTheme(this);
            _doc = doc;
            _handler = handler;
            _exEvent = exEvent;
            
            Elements = new ObservableCollection<SimulationElement>(uiElements);
            tvVariables.ItemsSource = Elements;
            
            // Default to Downloads folder for export
            string downloadsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            txtExportPath.Text = downloadsPath;

            // Pre-fill bundled default EPW so users can run immediately without browsing
            string bundledEpw = Path.Combine(
                Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location),
                "default.epw");
            if (File.Exists(bundledEpw))
                txtWeatherPath.Text = bundledEpw;

            BtnRunSensitivity.Visibility = HomeWindow.IsDebugMode ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        }

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            using (var fbd = new System.Windows.Forms.FolderBrowserDialog())
            {
                fbd.SelectedPath = txtExportPath.Text;
                if (fbd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    txtExportPath.Text = fbd.SelectedPath;
                }
            }
        }

        private void BtnBrowseWeather_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new Microsoft.Win32.OpenFileDialog();
            ofd.Filter = "EnergyPlus Weather (*.epw)|*.epw|All Files (*.*)|*.*";
            ofd.Title = "Select Weather File";
            if (ofd.ShowDialog() == true)
                txtWeatherPath.Text = ofd.FileName;
        }

        private void BtnUseDefaultWeather_Click(object sender, RoutedEventArgs e)
        {
            string bundledEpw = Path.Combine(
                Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location),
                "default.epw");
            if (File.Exists(bundledEpw))
            {
                txtWeatherPath.Text = bundledEpw;
            }
            else
            {
                MessageBox.Show(
                    "Bundled EPW not found. Make sure default.epw is in the add-in directory.",
                    "File Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnDownloadWeather_Click(object sender, RoutedEventArgs e)
        {
            // Opens the EnergyPlus weather data library in the user's default browser.
            // The user can search by location, download the .epw file, then Browse to it.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "https://energyplus.net/weather") { UseShellExecute = true });
        }

        private void BtnAddProperty_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            if (btn?.Tag is SimulationElement parentElement)
            {
                bool isWeather = parentElement.Category == "Environment";
                VariableCategory category = isWeather ? VariableCategory.Weather : 
                                         (parentElement.Category == "Building Variables" ? VariableCategory.Building :
                                          (parentElement.Category == "Spaces" || parentElement.Category == "Room") ? VariableCategory.Space : 
                                          (parentElement.Category == "Windows" || parentElement.Category == "Doors") ? VariableCategory.Opening : VariableCategory.Envelope);
                
                string baseName = $"{parentElement.Category}: {parentElement.ElementName} (New Property)";
                string newName = baseName;
                int counter = 1;
                while (parentElement.Properties.Any(p => p.Name == newName))
                {
                    counter++;
                    newName = $"{parentElement.Category}: {parentElement.ElementName} (New Property {counter})";
                }

                var newVar = new SimulationVariable(category)
                {
                    Name = newName
                };

                if (!isWeather && parentElement.ElementId != null && parentElement.ElementId != ElementId.InvalidElementId)
                {
                    var sourceElem = _doc.GetElement(parentElement.ElementId);
                    if (sourceElem is ElementType et)
                    {
                        var collector = new FilteredElementCollector(_doc).OfClass(et.GetType());
                        foreach (ElementType type in collector)
                        {
                            if (type.Category != null && type.Category.Id == et.Category.Id && type.Id != et.Id)
                            {
                                newVar.AvailableRevitTypes.Add(type.Name);
                            }
                        }
                    }
                }

                parentElement.Properties.Add(newVar);
            }
        }

        private string GetNextIndependentVariableName()
        {
            int maxId = 0;
            foreach (var elem in Elements)
            {
                foreach (var prop in elem.Properties)
                {
                    foreach (var iv in prop.IndependentVariables)
                    {
                        if (iv.Name != null && iv.Name.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                        {
                            if (int.TryParse(iv.Name.Substring(1), out int id))
                            {
                                if (id > maxId)
                                {
                                    maxId = id;
                                }
                            }
                        }
                    }
                }
            }
            return $"v{maxId + 1}";
        }

        private void BtnAddIndepVar_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            if (btn?.Tag is SimulationVariable parentVar)
            {
                var newVar = new SimulationVariable(parentVar.Category)
                {
                    Name = GetNextIndependentVariableName(),
                    Property = TargetProperty.Unitless,
                    IsIndependentVariable = true
                };
                
                parentVar.IndependentVariables.Add(newVar);
                
                tvVariables.ItemsSource = null;
                tvVariables.ItemsSource = Elements;
            }
        }

        private void BtnBack_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void BtnRun_Click(object sender, RoutedEventArgs e)
        {
            string exportPath = txtExportPath.Text;
            if (string.IsNullOrWhiteSpace(exportPath) || !Directory.Exists(exportPath))
            {
                MessageBox.Show("Please select a valid output directory.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            IEnumerable<SimulationElement> FlattenElements(IEnumerable<SimulationElement> elements)
            {
                foreach (var e in elements)
                {
                    yield return e;
                    foreach (var child in FlattenElements(e.SubElements))
                        yield return child;
                }
            }

            // Filter to only included elements/properties that are visible
            var activeElements = FlattenElements(Elements)
                .Where(e => e.IsVisible && e.Properties.Any(p => p.State != VariableState.NotIncluded))
                .ToList();
            
            if (activeElements.Count == 0)
            {
                MessageBox.Show("No variables are included for the simulation.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                var activeVariables = new List<SimulationVariable>();
                foreach (var elem in activeElements)
                {
                    foreach (var p in elem.Properties.Where(p => p.State != VariableState.NotIncluded))
                    {
                        activeVariables.Add(p);
                        foreach (var iv in p.IndependentVariables.Where(iv => iv.State != VariableState.NotIncluded))
                        {
                            activeVariables.Add(iv);
                        }
                    }
                }
                var units = activeVariables.ToDictionary(v => v.Name, v => v.SelectedUnit ?? "");
                var variableProperties = activeVariables.ToDictionary(v => v.Name, v => v.Property);
                
                string weatherPath = txtWeatherPath.Text;
                
                var variableElements = new Dictionary<string, ElementId>();
                foreach (var element in activeElements)
                {
                    foreach (var prop in element.Properties)
                    {
                        if (prop.State != VariableState.NotIncluded)
                        {
                            variableElements[prop.Name] = element.ElementId;
                        }
                    }
                }

                // Normal simulation uses the PermutationEngine to generate all combinations
                List<Dictionary<string, object>> explicitScenarios = null;


                bool flattenWeather = false;

                var activeVarList = activeVariables.ToList();

                ProgressWindow progressWindow = new ProgressWindow(_doc, exportPath, activeElements, units, variableProperties, variableElements, weatherPath, flattenWeather, _handler, _exEvent, activeVarList, explicitScenarios);
                progressWindow.Show();
                this.Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error generating scenarios: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnRunSensitivity_Click(object sender, RoutedEventArgs e)
        {
            string exportPath = txtExportPath.Text;
            if (string.IsNullOrWhiteSpace(exportPath) || !Directory.Exists(exportPath))
            {
                MessageBox.Show("Please select a valid output directory.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            IEnumerable<SimulationElement> FlattenElements(IEnumerable<SimulationElement> elements)
            {
                foreach (var el in elements)
                {
                    yield return el;
                    foreach (var child in FlattenElements(el.SubElements))
                        yield return child;
                }
            }

            var activeElements = FlattenElements(Elements)
                .Where(el => el.IsVisible && el.Properties.Any(p => p.State != VariableState.NotIncluded))
                .ToList();
            
            if (activeElements.Count == 0)
            {
                MessageBox.Show("No variables are included for the sensitivity test.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                var activeVariables = new List<SimulationVariable>();
                foreach (var elem in activeElements)
                {
                    foreach (var p in elem.Properties.Where(p => p.State != VariableState.NotIncluded))
                    {
                        activeVariables.Add(p);
                        foreach (var iv in p.IndependentVariables.Where(iv => iv.State != VariableState.NotIncluded))
                        {
                            activeVariables.Add(iv);
                        }
                    }
                }
                var units = activeVariables.ToDictionary(v => v.Name, v => v.SelectedUnit ?? "");
                var variableProperties = activeVariables.ToDictionary(v => v.Name, v => v.Property);
                
                string weatherPath = txtWeatherPath.Text;
                
                var variableElements = new Dictionary<string, ElementId>();
                foreach (var element in activeElements)
                {
                    foreach (var prop in element.Properties)
                    {
                        if (prop.State != VariableState.NotIncluded)
                        {
                            variableElements[prop.Name] = element.ElementId;
                        }
                    }
                }

                // --- Generate Sensitivity Scenarios ---
                var explicitScenarios = new List<Dictionary<string, object>>();
                
                // Scenario 0: Baseline (Empty override dictionary = all native defaults)
                var baselineScenario = new Dictionary<string, object>();
                baselineScenario["__SensitivityTestTarget"] = "Baseline";
                explicitScenarios.Add(baselineScenario);
                
                // For each active variable, we create a perturbed scenario
                // The perturbed scenario ONLY contains that single variable's generated value
                foreach (var v in activeVariables)
                {
                    var vals = v.GenerateValues();
                    if (vals.Count > 0)
                    {
                        var scenario = new Dictionary<string, object>();
                        scenario[v.Name] = vals.LastOrDefault(); // Apply only this one parameter
                        scenario["__SensitivityTestTarget"] = v.Name; // To identify it in the report
                        explicitScenarios.Add(scenario);
                    }
                }

                bool flattenWeather = false;
                var activeVarList = activeVariables.ToList();

                ProgressWindow progressWindow = new ProgressWindow(_doc, exportPath, activeElements, units, variableProperties, variableElements, weatherPath, flattenWeather, _handler, _exEvent, activeVarList, explicitScenarios);
                progressWindow.Show();
                this.Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error generating sensitivity scenarios: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnCopyCipher_Click(object sender, RoutedEventArgs e)
        {
            IEnumerable<SimulationElement> FlattenElements(IEnumerable<SimulationElement> elements)
            {
                foreach (var el in elements)
                {
                    yield return el;
                    foreach (var child in FlattenElements(el.SubElements))
                        yield return child;
                }
            }
            var activeElements = FlattenElements(Elements)
                .Where(el => el.IsVisible && el.Properties.Any(p => p.State != VariableState.NotIncluded))
                .ToList();

            var activeVariables = new List<SimulationVariable>();
            foreach (var elem in activeElements)
            {
                foreach (var p in elem.Properties.Where(p => p.State != VariableState.NotIncluded))
                {
                    activeVariables.Add(p);
                    foreach (var iv in p.IndependentVariables.Where(iv => iv.State != VariableState.NotIncluded))
                        activeVariables.Add(iv);
                }
            }
            
            if (activeVariables.Count == 0)
            {
                MessageBox.Show("No active variables to copy.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            
            string cipher = ExcelExporter.BuildCipher(activeVariables);
            Clipboard.SetText(cipher);
            MessageBox.Show("Ciphered text copied to clipboard!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.OriginalSource is DependencyObject depObj)
            {
                var parent = System.Windows.Media.VisualTreeHelper.GetParent(depObj);
                while (parent != null)
                {
                    // If we are over a popup or over another ScrollViewer (like the one inside a ComboBox dropdown), let it handle its own scroll!
                    if (parent is System.Windows.Controls.Primitives.Popup || (parent is ScrollViewer && parent != sender))
                        return;
                    parent = System.Windows.Media.VisualTreeHelper.GetParent(parent);
                }
            }

            var scrollViewer = sender as ScrollViewer;
            if (scrollViewer != null)
            {
                scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - (e.Delta / 3.0));
                e.Handled = true;
            }
        }

        private void ListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var listBox = sender as ListBox;
            if (listBox?.Tag is SimulationVariable simVar)
            {
                simVar.SelectedRevitTypes.Clear();
                foreach (var item in listBox.SelectedItems)
                {
                    simVar.SelectedRevitTypes.Add(item.ToString());
                }
            }
        }

        private void ListBox_Loaded(object sender, RoutedEventArgs e)
        {
            var lb = sender as ListBox;
            if (lb?.Tag is SimulationVariable simVar)
            {
                lb.SelectionChanged -= ListBox_SelectionChanged;
                lb.SelectedItems.Clear();
                foreach (var item in lb.Items)
                {
                    if (item != null && simVar.SelectedRevitTypes.Contains(item.ToString()))
                    {
                        lb.SelectedItems.Add(item);
                    }
                }
                lb.SelectionChanged += ListBox_SelectionChanged;
            }
        }

        private void LbStuds_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if ((sender as ListBox)?.Tag is SimulationVariable simVar)
            {
                simVar.SelectedStudTypes.Clear();
                foreach (var item in ((ListBox)sender).SelectedItems) simVar.SelectedStudTypes.Add(item.ToString());
            }
        }

        private void LbStuds_Loaded(object sender, RoutedEventArgs e)
        {
            var lb = sender as ListBox;
            if (lb?.Tag is SimulationVariable simVar)
            {
                lb.SelectionChanged -= LbStuds_SelectionChanged;
                lb.SelectedItems.Clear();
                foreach (var item in lb.Items)
                {
                    if (item != null && simVar.SelectedStudTypes.Contains(item.ToString()))
                    {
                        lb.SelectedItems.Add(item);
                    }
                }
                lb.SelectionChanged += LbStuds_SelectionChanged;
            }
        }

        private void LbInsulation_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if ((sender as ListBox)?.Tag is SimulationVariable simVar)
            {
                simVar.SelectedInsulationTypes.Clear();
                foreach (var item in ((ListBox)sender).SelectedItems) simVar.SelectedInsulationTypes.Add(item.ToString());
            }
        }

        private void LbInsulation_Loaded(object sender, RoutedEventArgs e)
        {
            var lb = sender as ListBox;
            if (lb?.Tag is SimulationVariable simVar)
            {
                lb.SelectionChanged -= LbInsulation_SelectionChanged;
                lb.SelectedItems.Clear();
                foreach (var item in lb.Items)
                {
                    if (item != null && simVar.SelectedInsulationTypes.Contains(item.ToString()))
                    {
                        lb.SelectedItems.Add(item);
                    }
                }
                lb.SelectionChanged += LbInsulation_SelectionChanged;
            }
        }

        /// <summary>
        /// Applies settings from a cipher string to the current variable tree.
        /// Only variables whose names match existing entries are updated.
        /// </summary>
        public void LoadFromCipher(string cipher)
        {
            var parsed = ExcelExporter.ParseCipher(cipher);
            if (parsed.Count == 0) return;

            IEnumerable<SimulationElement> FlattenElements(IEnumerable<SimulationElement> elements)
            {
                foreach (var e in elements)
                {
                    yield return e;
                    foreach (var child in FlattenElements(e.SubElements))
                        yield return child;
                }
            }
            var allElements = FlattenElements(Elements).ToList();

            foreach (var p in parsed)
            {
                // Find matching element. The property name starts with DisplayName.
                var element = allElements.FirstOrDefault(e => p.name == e.DisplayName || p.name.StartsWith(e.DisplayName + " ("));
                if (element == null) continue;

                // Find or create the property
                var prop = element.Properties.FirstOrDefault(x => x.Name == p.name);
                if (prop == null)
                {
                    VariableCategory cat = element.Category == "Environment" ? VariableCategory.Weather :
                                           (element.Category == "Building Variables" ? VariableCategory.Building :
                                            element.Category == "Spaces" ? VariableCategory.Space : 
                                            (element.Category == "Windows" || element.Category == "Doors") ? VariableCategory.Opening : VariableCategory.Envelope);
                                            
                    prop = new SimulationVariable(cat) { Name = p.name };
                    if (element.Category != "Environment" && element.ElementId != null && element.ElementId != ElementId.InvalidElementId)
                    {
                        var sourceElem = _doc.GetElement(element.ElementId);
                        if (sourceElem is ElementType et)
                        {
                            var collector = new FilteredElementCollector(_doc).OfClass(et.GetType());
                            foreach (ElementType type in collector)
                                if (type.Category != null && type.Category.Id == et.Category.Id && type.Id != et.Id)
                                    prop.AvailableRevitTypes.Add(type.Name);
                        }
                    }
                    element.Properties.Add(prop);
                }

                var src = p.variable;
                prop.Property   = src.Property;
                prop.State      = src.State;
                prop.Method     = src.Method;
                prop.SelectedUnit = src.SelectedUnit;
                prop.Min        = src.Min;
                prop.Max        = src.Max;
                prop.Interval   = src.Interval;
                prop.IsIntervalCount = src.IsIntervalCount;
                prop.ArrayValuesString = src.ArrayValuesString;
                prop.EquationString = src.EquationString;
                prop.IncludeOriginalType = src.IncludeOriginalType;
                
                prop.SelectedRevitTypes.Clear();
                foreach (var t in src.SelectedRevitTypes) prop.SelectedRevitTypes.Add(t);

                prop.SelectedStudTypes.Clear();
                foreach (var t in src.SelectedStudTypes) prop.SelectedStudTypes.Add(t);

                prop.SelectedInsulationTypes.Clear();
                foreach (var t in src.SelectedInsulationTypes) prop.SelectedInsulationTypes.Add(t);

                prop.EffStudRValue = src.EffStudRValue;
                prop.EffInsulationRValue = src.EffInsulationRValue;
                prop.EffWindowRValue = src.EffWindowRValue;
                prop.EffDoorRValue = src.EffDoorRValue;
                prop.EffFramingFactor = src.EffFramingFactor;
                prop.EffDensity = src.EffDensity;
                prop.EffSpecificHeat = src.EffSpecificHeat;
            }

            // Refresh the tree view
            tvVariables.ItemsSource = null;
            tvVariables.ItemsSource = Elements;
        }

        private void ComboBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            var comboBox = sender as System.Windows.Controls.ComboBox;
            if (comboBox != null && comboBox.IsDropDownOpen)
            {
                e.Handled = true;
            }
        }
    }
}
