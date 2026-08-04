using System;
using System.Windows;
using System.IO;
using System.Reflection;

namespace RevitAddin
{
    public static class ThemeManager
    {
        public static bool IsDarkMode { get; private set; } = false;
        
        public static void ToggleTheme()
        {
            // Disabled per user request
        }

        public static void ApplyTheme(Window window)
        {
            try
            {
                var dict = new ResourceDictionary();
                string assemblyName = Assembly.GetExecutingAssembly().GetName().Name;
                string themeFile = "LightTheme.xaml";
                
                // Pack URI to load from the compiled assembly
                dict.Source = new Uri($"pack://application:,,,/{assemblyName};component/Themes/{themeFile}", UriKind.Absolute);
                
                // Remove existing theme dictionaries
                for (int i = window.Resources.MergedDictionaries.Count - 1; i >= 0; i--)
                {
                    var md = window.Resources.MergedDictionaries[i];
                    if (md.Source != null && md.Source.ToString().Contains("Theme.xaml"))
                    {
                        window.Resources.MergedDictionaries.RemoveAt(i);
                    }
                }
                
                window.Resources.MergedDictionaries.Add(dict);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to apply theme: {ex.Message}");
            }
        }
    }
}
