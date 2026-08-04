using System;
using Autodesk.Revit.UI;

namespace RevitAddin
{
    public class RevitEventHandler : IExternalEventHandler
    {
        public Action<UIApplication> CurrentAction { get; set; }
        public event EventHandler ActionCompleted;

        public void Execute(UIApplication app)
        {
            try
            {
                CurrentAction?.Invoke(app);
            }
            catch (Exception ex)
            {
                // Just swallow and complete
                System.Diagnostics.Debug.WriteLine($"Error in RevitEventHandler: {ex.Message}");
            }
            finally
            {
                ActionCompleted?.Invoke(this, EventArgs.Empty);
            }
        }

        public string GetName()
        {
            return "Parametric Energy Background Event Handler";
        }
    }
}
