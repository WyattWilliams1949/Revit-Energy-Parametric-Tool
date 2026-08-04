using System.Collections.Generic;

namespace RevitAddin
{
    public class RoomBreakdown
    {
        public double PeopleHeat { get; set; }
        public double LightsHeat { get; set; }
        public double SunTransmitted { get; set; }
        public double WindowsConduction { get; set; }
        public double DoorsConduction { get; set; }
        public double WallsConduction { get; set; }
        public double CeilingsConduction { get; set; }
        public double FloorsConduction { get; set; }
    }

    public class SimulationResult
    {
        public double AverageBtu { get; set; }
        public double PeakBtu { get; set; }
        public bool Success { get; set; }
        public Dictionary<string, RoomBreakdown> RoomData { get; set; } = new Dictionary<string, RoomBreakdown>();
    }
}
