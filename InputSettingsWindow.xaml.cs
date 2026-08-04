using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitAddin
{
    public partial class InputSettingsWindow : Window
    {
        private readonly Document _doc;
        private readonly List<SimulationElement> _envelopeElements;
        private readonly RevitEventHandler _handler;
        private readonly ExternalEvent _exEvent;

        public InputSettingsWindow(Document doc, List<SimulationElement> envelopeElements,
            RevitEventHandler handler, ExternalEvent exEvent)
        {
            InitializeComponent(); ThemeManager.ApplyTheme(this);
            _doc = doc;
            _envelopeElements = envelopeElements;
            _handler = handler;
            _exEvent = exEvent;
        }

        private void BtnBack_Click(object sender, RoutedEventArgs e) => Close();

        private void BtnReview_Click(object sender, RoutedEventArgs e)
        {
            var cipher = txtCipher.Text.Trim();
            if (!ValidateCipher(cipher)) return;

            var mainWin = new MainWindow(_doc, _envelopeElements, _handler, _exEvent);
            mainWin.LoadFromCipher(cipher);
            mainWin.ShowDialog();
            Close();
        }

        private void BtnRunDirect_Click(object sender, RoutedEventArgs e)
        {
            var cipher = txtCipher.Text.Trim();
            if (!ValidateCipher(cipher)) return;

            // Apply cipher to a fresh element tree, then launch directly
            var tempMain = new MainWindow(_doc, _envelopeElements, _handler, _exEvent);
            tempMain.LoadFromCipher(cipher);

            // Build scenario list the same way MainWindow.BtnRun_Click does
            var activeElements = tempMain.Elements
                .Where(el => el.Properties.Any(p => p.State != VariableState.NotIncluded))
                .ToList();

            if (activeElements.Count == 0)
            {
                ShowError("No active variables found in the cipher. Please review your settings.");
                return;
            }

            var activeVariables = activeElements
                .SelectMany(el => el.Properties)
                .Where(p => p.State != VariableState.NotIncluded)
                .ToList();

            var units = activeVariables.ToDictionary(v => v.Name, v => v.SelectedUnit ?? "");
            var variableProperties = activeVariables.ToDictionary(v => v.Name, v => v.Property);
            var variableElements = new Dictionary<string, ElementId>();
            foreach (var elem in activeElements)
                foreach (var prop in elem.Properties)
                    if (prop.State != VariableState.NotIncluded)
                        variableElements[prop.Name] = elem.ElementId;

            // Use default weather path
            string weatherPath = Path.Combine(
                Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location),
                "default.epw");

            string exportPath = Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), "Downloads");

            var progressWindow = new ProgressWindow(
                _doc, exportPath, activeElements, units, variableProperties,
                variableElements, weatherPath, false, _handler, _exEvent, activeVariables);

            progressWindow.Show();
            Close();
        }

        private bool ValidateCipher(string cipher)
        {
            if (string.IsNullOrWhiteSpace(cipher))
            {
                ShowError("Please paste a cipher string first.");
                return false;
            }
            var parsed = ExcelExporter.ParseCipher(cipher);
            if (parsed.Count == 0)
            {
                ShowError("Could not parse the cipher string. Make sure you copied the complete 'Copy for Input' cell contents.");
                return false;
            }
            lblStatus.Visibility = System.Windows.Visibility.Collapsed;
            return true;
        }

        private void ShowError(string msg)
        {
            lblStatus.Text = msg;
            lblStatus.Visibility = System.Windows.Visibility.Visible;
        }
    }
}
