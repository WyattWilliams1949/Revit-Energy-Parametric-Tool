using System.Windows;
using System.Windows.Media;

namespace RevitAddin
{
    public partial class MatlabModeWindow : Window
    {
        private MatlabServer _server;

        public MatlabModeWindow(MatlabServer server)
        {
            InitializeComponent();
            _server = server;
            ThemeManager.ApplyTheme(this);
        }

        private void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            _server.Start(8080);
            txtStatus.Text = "Server Running...";
            txtStatus.Foreground = new SolidColorBrush(Colors.Green);
            btnStart.IsEnabled = false;
            btnStop.IsEnabled = true;
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            _server.Stop();
            txtStatus.Text = "Server Stopped.";
            txtStatus.Foreground = new SolidColorBrush(Colors.Red);
            btnStart.IsEnabled = true;
            btnStop.IsEnabled = false;
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            _server.Stop();
            this.Close();
        }

        protected override void OnClosed(System.EventArgs e)
        {
            _server.Stop();
            base.OnClosed(e);
        }
    }
}
