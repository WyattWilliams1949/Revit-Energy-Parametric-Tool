using System.Collections.Generic;
using System.Windows;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitAddin
{
    public partial class HomeWindow : Window
    {
        public static bool IsDebugMode { get; set; } = false;

        private readonly Document _doc;
        private readonly UIDocument _uidoc;
        private readonly List<SimulationElement> _envelopeElements;
        private readonly RevitEventHandler _handler;
        private readonly ExternalEvent _exEvent;

        public HomeWindow(Document doc, UIDocument uidoc, List<SimulationElement> envelopeElements,
            RevitEventHandler handler, ExternalEvent exEvent)
        {
            InitializeComponent();
            ThemeManager.ApplyTheme(this);
            _doc = doc;
            _uidoc = uidoc;
            _envelopeElements = envelopeElements;
            _handler = handler;
            _exEvent = exEvent;
        }



        private void BtnAutoAnalysis_Click(object sender, RoutedEventArgs e)
        {
            this.Hide();
            var mainWin = new MainWindow(_doc, _envelopeElements, _handler, _exEvent);
            mainWin.ShowDialog();
            this.Show();
        }

        private void BtnPasteSettings_Click(object sender, RoutedEventArgs e)
        {
            this.Hide();
            var inputWin = new InputSettingsWindow(_doc, _envelopeElements, _handler, _exEvent);
            inputWin.ShowDialog();
            this.Show();
        }

        private void BtnReplaceWalls_Click(object sender, RoutedEventArgs e)
        {
            this.Hide();
            var win = new ReplaceElementsWindow(_doc, _uidoc, _handler, _exEvent);
            win.ShowDialog();
            this.Show();
        }

        private void BtnRValue_Click(object sender, RoutedEventArgs e)
        {
            this.Hide();
            var win = new EffectiveRValueWindow(_doc, _uidoc, _handler, _exEvent);
            win.ShowDialog();
            this.Show();
        }

        private void BtnMonolithic_Click(object sender, RoutedEventArgs e)
        {
            this.Hide();
            var win = new DynamicMonolithicWindow(_doc, _uidoc, _handler, _exEvent);
            win.ShowDialog();
            this.Show();
        }

        private void ChkDebugMode_Checked(object sender, RoutedEventArgs e)
        {
            IsDebugMode = true;
        }

        private void ChkDebugMode_Unchecked(object sender, RoutedEventArgs e)
        {
            IsDebugMode = false;
        }
    }
}
