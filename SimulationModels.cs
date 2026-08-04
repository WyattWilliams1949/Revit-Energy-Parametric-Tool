using System;
using System.Collections.Generic;
using System.Linq;
using NCalc;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
namespace RevitAddin
{
    public enum VariableState { NotIncluded, Constant, Variable }
    public enum VariableMethod { MinMaxInterval, Array, Equation, TypeSelection, ReplaceElements, EffectiveRValue, Monolithic, TemperatureDependent }
    public enum VariableCategory { Weather, Building, Envelope, Space, Opening }
    public class WallModConfig
    {
        public VariableMethod Method { get; set; }
        public string StudType { get; set; }
        public string InsulationType { get; set; }
        public double StudRValue { get; set; }
        public double InsulationRValue { get; set; }
        public double WindowRValue { get; set; }
        public double DoorRValue { get; set; }
        public double FramingFactor { get; set; }
        public double Density { get; set; }
        public double SpecificHeat { get; set; }
        
        public bool VaryRValueWithTemp { get; set; }
        public string RValueTempEquation { get; set; }
        public string RValueTempEquationUnit { get; set; }
        
        public override string ToString()
        {
            if (Method == VariableMethod.ReplaceElements) return $"Replace (Stud: {StudType}, Ins: {InsulationType}, TempDepend: {VaryRValueWithTemp})";
            if (Method == VariableMethod.Monolithic) return $"Monolithic (Stud: {StudType}, Ins: {InsulationType}, FF: {FramingFactor}%)";
            if (Method == VariableMethod.EffectiveRValue) return "Effective R-Value calculation";
            return "WallModConfig";
        }
    }

    public class PeopleConfig
    {
        public double Count { get; set; }
        public bool UseDefaultMetabolicHeat { get; set; }
        public double CustomMetabolicHeat { get; set; }

        public override string ToString()
        {
            return $"{Count} people (Metabolic: {(UseDefaultMetabolicHeat ? "Default" : CustomMetabolicHeat + "W")})";
        }
    }

    public class SettlingConfig
    {
        public string Method { get; set; } // "% (From Top)", "ft (From Top)", "ft (From Bottom)"
        public double Value { get; set; }
        public string SettledWallType { get; set; }
    }
    
    public class ProfileHour : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        public string HourLabel { get; set; }
        private double _value;
        public double Value 
        { 
            get => _value; 
            set { if (_value != value) { _value = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value))); } } 
        }
    }

    public class ScheduleConfig
    {
        public bool IsCustom { get; set; }
        public string DefaultType { get; set; }
        public List<double> WeekdayOccupancy { get; set; } = new List<double>();
        public List<double> WeekendOccupancy { get; set; } = new List<double>();
        public List<double> WeekdayLighting { get; set; } = new List<double>();
        public List<double> WeekendLighting { get; set; } = new List<double>();
        public List<double> WeekdayHeating { get; set; } = new List<double>();
        public List<double> WeekendHeating { get; set; } = new List<double>();
        public List<double> WeekdayCooling { get; set; } = new List<double>();
        public List<double> WeekendCooling { get; set; } = new List<double>();
    }
    
    public class SyntheticWeatherConfig
    {
        public double WinterMinTemp { get; set; }
        public double WinterMaxTemp { get; set; }
        public double SummerMinTemp { get; set; }
        public double SummerMaxTemp { get; set; }
        public double Offset { get; set; }
    }

    public class SpacePropertiesConfig
    {
        public bool UseDefaultMetabolicHeat { get; set; }
        public double CustomMetabolicHeat { get; set; }
    }
    public enum TargetProperty
    {
        RValue, UValue, Thickness, Conductivity, Density, SpecificHeat, Unitless,
        // Core weather fields
        [Description("Constant Temperature")]
        WeatherTemperature,
        [Description("Offset Temperatures from Existing")]
        WeatherTemperatureOffset, 
        [Description("Synthetic Weather Curve")]
        WeatherTemperatureSynthetic, 
        WeatherRelativeHumidity, WeatherSolarRadiation, WeatherWindSpeed,
        // Additional EPW fields
        WeatherDewPoint, WeatherAtmosphericPressure,
        WeatherDirectNormalRadiation, WeatherDiffuseHorizontalRadiation,
        WeatherWindDirection, WeatherTotalSkyCover,
        // Revit type swap
        RevitType,
        // Space parameters
        HeatingSetpoint, CoolingSetpoint, Infiltration, PeopleCount, Schedule, IsUnheated,
        // Wall properties
        InsulationSettling
    }

    public class SimulationElement : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public string ElementName { get; set; }
        public string Category { get; set; }
        public Autodesk.Revit.DB.ElementId ElementId { get; set; }
        public ObservableCollection<SimulationVariable> Properties { get; set; } = new ObservableCollection<SimulationVariable>();
        
        public ObservableCollection<SimulationElement> SubElements { get; set; } = new ObservableCollection<SimulationElement>();

        public string DisplayName => string.IsNullOrEmpty(ElementName) ? Category : $"{Category}: {ElementName}";

        private bool _isVisible = true;
        public bool IsVisible
        {
            get => _isVisible;
            set { if (_isVisible != value) { _isVisible = value; OnPropertyChanged(); } }
        }

        private bool _showEntireBuildingToggle = false;
        public bool ShowEntireBuildingToggle
        {
            get => _showEntireBuildingToggle;
            set { if (_showEntireBuildingToggle != value) { _showEntireBuildingToggle = value; OnPropertyChanged(); } }
        }

        private bool _isEntireBuilding = true;
        public bool IsEntireBuilding
        {
            get => _isEntireBuilding;
            set 
            { 
                if (_isEntireBuilding != value) 
                { 
                    _isEntireBuilding = value; 
                    OnPropertyChanged(); 
                    OnPropertyChanged(nameof(ShowSubElements));
                    OnPropertyChanged(nameof(ShowProperties));
                    OnPropertyChanged(nameof(ShowNoRoomsWarning));
                    OnPropertyChanged(nameof(HasMissingRoomsWarning));
                } 
            }
        }

        public bool ShowSubElements => (ShowEntireBuildingToggle ? !_isEntireBuilding : true) && SubElements.Count > 0;
        public bool CanHaveProperties { get; set; } = true;
        public bool ShowProperties => CanHaveProperties && (ShowEntireBuildingToggle ? _isEntireBuilding : true);
        public bool ShowNoRoomsWarning => ShowEntireBuildingToggle && !_isEntireBuilding && SubElements.Count == 0;

        private string _missingRoomsWarning = "";
        public string MissingRoomsWarning
        {
            get => _missingRoomsWarning;
            set { if (_missingRoomsWarning != value) { _missingRoomsWarning = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasMissingRoomsWarning)); } }
        }

        public bool HasMissingRoomsWarning => !string.IsNullOrEmpty(_missingRoomsWarning) && ShowEntireBuildingToggle && !_isEntireBuilding;
    }

    public class SimulationVariable : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public string Name { get; set; }
        public bool IsIndependentVariable { get; set; } = false;
        
        public double WinterMinTemp { get; set; } = 30.0;
        public double WinterMaxTemp { get; set; } = 50.0;
        public double SummerMinTemp { get; set; } = 70.0;
        public double SummerMaxTemp { get; set; } = 95.0;
        
        public VariableCategory Category { get; private set; }
        public bool IsWeatherVariable => Category == VariableCategory.Weather;
        
        public bool IsSyntheticWeather => Property == TargetProperty.WeatherTemperatureSynthetic;
        
        public bool ShowStateSelection => Property != TargetProperty.Schedule && Property != TargetProperty.WeatherTemperatureSynthetic;
        public bool ShowValueConfiguration => Property != TargetProperty.WeatherTemperatureSynthetic;

        public ObservableCollection<TargetProperty> AvailableProperties { get; } = new ObservableCollection<TargetProperty>();
        public ObservableCollection<VariableMethod> AvailableMethods { get; set; } = new ObservableCollection<VariableMethod>();
        
        private TargetProperty _property = TargetProperty.RValue;
        public TargetProperty Property 
        { 
            get => _property;
            set
            {
                if (_property != value)
                {
                    _property = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsRevitType));
                    OnPropertyChanged(nameof(IsUnheatedProperty));
                    OnPropertyChanged(nameof(IsNotUnheatedProperty));
                    OnPropertyChanged(nameof(IsNormalProperty));
                    OnPropertyChanged(nameof(IsSyntheticWeather));
                    OnPropertyChanged(nameof(ShowStateSelection));
                    OnPropertyChanged(nameof(ShowValueConfiguration));
                    UpdateAvailableUnits();
                    
                    if (_property == TargetProperty.RevitType)
                    {
                        Method = VariableMethod.TypeSelection;
                    }
                    else if (_property == TargetProperty.InsulationSettling)
                    {
                        // Default to MinMaxInterval for settling
                        Method = VariableMethod.MinMaxInterval;
                    }
                    else if (_property == TargetProperty.Schedule)
                    {
                        State = VariableState.Constant;
                    }
                    else if (Method == VariableMethod.TypeSelection)
                    {
                        Method = VariableMethod.MinMaxInterval;
                    }
                }
            }
        }

        private string _selectedUnit;
        public string SelectedUnit
        {
            get => _selectedUnit;
            set
            {
                if (_selectedUnit != value)
                {
                    _selectedUnit = value;
                    OnPropertyChanged();
                    
                    if (_property == TargetProperty.RevitType && _selectedUnit != null)
                    {
                        if (_selectedUnit == "Type Selection") Method = VariableMethod.TypeSelection;
                        else if (_selectedUnit == "Replace Elements") Method = VariableMethod.ReplaceElements;
                        else if (_selectedUnit == "Effective R-Value") Method = VariableMethod.EffectiveRValue;
                        else if (_selectedUnit == "Monolithic") Method = VariableMethod.Monolithic;
                    }
                }
            }
        }

        /// <summary>True when this variable needs to select Revit types.
        /// Used in the XAML to show the type selection ListBox.
        /// </summary>
        public bool IsRevitType => _property == TargetProperty.RevitType || _property == TargetProperty.InsulationSettling;
        public bool IsUnheatedProperty => _property == TargetProperty.IsUnheated;
        public bool IsNotUnheatedProperty => !IsUnheatedProperty;
        public bool IsNormalProperty => !IsUnheatedProperty && !IsRevitType;

        public bool IsUnheatedNotIncluded
        {
            get => State == VariableState.Constant && ConstantValue < -0.5;
            set
            {
                if (value)
                {
                    State = VariableState.Constant;
                    ConstantValue = -1;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsUnheatedHeated));
                    OnPropertyChanged(nameof(IsUnheatedNotHeated));
                    OnPropertyChanged(nameof(IsUnheatedVary));
                }
            }
        }
        
        public bool IsUnheatedHeated
        {
            get => State == VariableState.Constant && Math.Abs(ConstantValue) < 0.5;
            set
            {
                if (value)
                {
                    State = VariableState.Constant;
                    ConstantValue = 0;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsUnheatedNotIncluded));
                    OnPropertyChanged(nameof(IsUnheatedNotHeated));
                    OnPropertyChanged(nameof(IsUnheatedVary));
                }
            }
        }

        public bool IsUnheatedNotHeated
        {
            get => State == VariableState.Constant && Math.Abs(ConstantValue - 1.0) < 0.5;
            set
            {
                if (value)
                {
                    State = VariableState.Constant;
                    ConstantValue = 1;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsUnheatedNotIncluded));
                    OnPropertyChanged(nameof(IsUnheatedHeated));
                    OnPropertyChanged(nameof(IsUnheatedVary));
                }
            }
        }

        public bool IsUnheatedVary
        {
            get => State == VariableState.Variable && Method == VariableMethod.Array && ArrayValuesString == "0, 1";
            set
            {
                if (value)
                {
                    State = VariableState.Variable;
                    Method = VariableMethod.Array;
                    ArrayValuesString = "0, 1";
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsUnheatedNotIncluded));
                    OnPropertyChanged(nameof(IsUnheatedHeated));
                    OnPropertyChanged(nameof(IsUnheatedNotHeated));
                }
            }
        }


        public ObservableCollection<string> AvailableUnits { get; } = new ObservableCollection<string>();

        // Properties for Type Swapping
        public ObservableCollection<string> AvailableRevitTypes { get; } = new ObservableCollection<string>();
        public ObservableCollection<string> SelectedRevitTypes { get; } = new ObservableCollection<string>();

        private bool _includeOriginalType = false;
        public bool IncludeOriginalType
        {
            get => _includeOriginalType;
            set { if (_includeOriginalType != value) { _includeOriginalType = value; OnPropertyChanged(); } }
        }

        // Parametric Variations for Wall Mods
        public ObservableCollection<string> SelectedStudTypes { get; } = new ObservableCollection<string>();
        public ObservableCollection<string> SelectedInsulationTypes { get; } = new ObservableCollection<string>();

        // Effective R-Value and Monolithic parameters
        private double _effStudRValue = 4.38;
        public double EffStudRValue { get => _effStudRValue; set { _effStudRValue = value; OnPropertyChanged(); } }
        private double _effInsulationRValue = 13.0;
        public double EffInsulationRValue { get => _effInsulationRValue; set { _effInsulationRValue = value; OnPropertyChanged(); } }
        private double _effWindowRValue = 3.0;
        public double EffWindowRValue { get => _effWindowRValue; set { _effWindowRValue = value; OnPropertyChanged(); } }
        private double _effDoorRValue = 5.0;
        public double EffDoorRValue { get => _effDoorRValue; set { _effDoorRValue = value; OnPropertyChanged(); } }
        private double _effFramingFactor = 25.0;
        public double EffFramingFactor { get => _effFramingFactor; set { _effFramingFactor = value; OnPropertyChanged(); } }
        private double _effDensity = 25.0;
        public double EffDensity { get => _effDensity; set { _effDensity = value; OnPropertyChanged(); } }
        private double _effSpecificHeat = 0.2;
        public double EffSpecificHeat { get => _effSpecificHeat; set { _effSpecificHeat = value; OnPropertyChanged(); } }

        // Dynamic R-Value configuration
        private bool _varyRValueWithTemp = false;
        public bool VaryRValueWithTemp { get => _varyRValueWithTemp; set { _varyRValueWithTemp = value; OnPropertyChanged(); } }
        private string _rValueTempEquation = "0,4;5,6;10,8";
        public string RValueTempEquation { get => _rValueTempEquation; set { _rValueTempEquation = value; OnPropertyChanged(); } }
        
        public ObservableCollection<string> AvailableTempUnits { get; } = new ObservableCollection<string>() { "°F", "°C" };
        private string _rValueTempEquationUnit = "°F";
        public string RValueTempEquationUnit { get => _rValueTempEquationUnit; set { _rValueTempEquationUnit = value; OnPropertyChanged(); } }


        // Metabolic Heat
        private bool _useDefaultMetabolicHeat = true;
        public bool UseDefaultMetabolicHeat { get => _useDefaultMetabolicHeat; set { _useDefaultMetabolicHeat = value; OnPropertyChanged(); } }
        private double _customMetabolicHeat = 120.0; // Watts
        public double CustomMetabolicHeat { get => _customMetabolicHeat; set { _customMetabolicHeat = value; OnPropertyChanged(); } }

        // Schedule Configuration
        public ObservableCollection<string> AvailableScheduleDefaults { get; } = new ObservableCollection<string>() 
        {
            "Office: Medium", "Office: Large", "Office: Small", 
            "Retail: Stand-alone", "Retail: Strip Mall", 
            "Education: Primary School", "Education: Secondary School", 
            "Lodging: Large Hotel", "Lodging: Small Hotel", "Lodging: Midrise Apartment", 
            "Healthcare: Hospital", "Healthcare: Outpatient", 
            "Assembly: Warehouse", "Restaurant: Fast Food", "Restaurant: Sit-down",
            "Apartment: High Rise", "Apartment: Mid Rise", "Supermarket", 
            "Courthouse", "Data Center", "Laboratory", "Warehouse: Refrigerated",
            "Single Family Home"
        };
        private string _selectedScheduleDefault = "Office: Medium";
        public string SelectedScheduleDefault { get => _selectedScheduleDefault; set { _selectedScheduleDefault = value; OnPropertyChanged(); } }
        
        private bool _isCustomSchedule = false;
        public bool IsCustomSchedule { get => _isCustomSchedule; set { _isCustomSchedule = value; OnPropertyChanged(); } }

        public ObservableCollection<SimulationVariable> SyntheticWeatherParams { get; set; } = new ObservableCollection<SimulationVariable>();

        public SimulationVariable(VariableCategory category = VariableCategory.Envelope, bool isSub = false)
        {
            if (!isSub)
            {
                var wMin = new SimulationVariable(VariableCategory.Weather, true) { Name = "Winter Min", ConstantValue = 30 };
                var wMax = new SimulationVariable(VariableCategory.Weather, true) { Name = "Winter Max", ConstantValue = 50 };
                var sMin = new SimulationVariable(VariableCategory.Weather, true) { Name = "Summer Min", ConstantValue = 70 };
                var sMax = new SimulationVariable(VariableCategory.Weather, true) { Name = "Summer Max", ConstantValue = 95 };
                
                SyntheticWeatherParams.Add(wMin);
                SyntheticWeatherParams.Add(wMax);
                SyntheticWeatherParams.Add(sMin);
                SyntheticWeatherParams.Add(sMax);
            }
            Category = category;
            if (category == VariableCategory.Weather)
            {
                AvailableProperties.Add(TargetProperty.WeatherTemperature);
                AvailableProperties.Add(TargetProperty.WeatherTemperatureOffset);
                AvailableProperties.Add(TargetProperty.WeatherTemperatureSynthetic);
                AvailableProperties.Add(TargetProperty.WeatherDewPoint);
                AvailableProperties.Add(TargetProperty.WeatherRelativeHumidity);
                AvailableProperties.Add(TargetProperty.WeatherAtmosphericPressure);
                AvailableProperties.Add(TargetProperty.WeatherSolarRadiation);
                AvailableProperties.Add(TargetProperty.WeatherDirectNormalRadiation);
                AvailableProperties.Add(TargetProperty.WeatherDiffuseHorizontalRadiation);
                AvailableProperties.Add(TargetProperty.WeatherWindSpeed);
                AvailableProperties.Add(TargetProperty.WeatherWindDirection);
                AvailableProperties.Add(TargetProperty.WeatherTotalSkyCover);
                _property = TargetProperty.WeatherTemperatureOffset;
            }
            else if (category == VariableCategory.Building || category == VariableCategory.Space)
            {
                AvailableProperties.Add(TargetProperty.HeatingSetpoint);
                AvailableProperties.Add(TargetProperty.CoolingSetpoint);
                AvailableProperties.Add(TargetProperty.PeopleCount);
                AvailableProperties.Add(TargetProperty.Schedule);
                AvailableProperties.Add(TargetProperty.IsUnheated);
                _property = TargetProperty.HeatingSetpoint;
            }
            else if (category == VariableCategory.Opening)
            {
                AvailableProperties.Add(TargetProperty.Infiltration);
                _property = TargetProperty.Infiltration;
            }
            else
            {
                AvailableProperties.Add(TargetProperty.RValue);
                AvailableProperties.Add(TargetProperty.UValue);
                AvailableProperties.Add(TargetProperty.Thickness);
                AvailableProperties.Add(TargetProperty.Conductivity);
                AvailableProperties.Add(TargetProperty.Density);
                AvailableProperties.Add(TargetProperty.SpecificHeat);
                AvailableProperties.Add(TargetProperty.Unitless);
                AvailableProperties.Add(TargetProperty.InsulationSettling);
                AvailableProperties.Add(TargetProperty.RevitType);
                _property = TargetProperty.RValue;
            }
            
            UpdateAvailableMethods();
            UpdateAvailableUnits();
        }

        private void UpdateAvailableMethods()
        {
            AvailableMethods.Clear();
            if (Property == TargetProperty.RevitType)
            {
                AvailableMethods.Add(VariableMethod.TypeSelection);
                AvailableMethods.Add(VariableMethod.ReplaceElements);
                AvailableMethods.Add(VariableMethod.EffectiveRValue);
                AvailableMethods.Add(VariableMethod.Monolithic);
                Method = VariableMethod.TypeSelection;
            }
            else if (Category == VariableCategory.Envelope && (Property == TargetProperty.RValue || Property == TargetProperty.UValue))
            {
                AvailableMethods.Add(VariableMethod.MinMaxInterval);
                AvailableMethods.Add(VariableMethod.Array);
                AvailableMethods.Add(VariableMethod.Equation);
                AvailableMethods.Add(VariableMethod.TemperatureDependent);
                Method = VariableMethod.MinMaxInterval;
            }
            else
            {
                AvailableMethods.Add(VariableMethod.MinMaxInterval);
                AvailableMethods.Add(VariableMethod.Array);
                AvailableMethods.Add(VariableMethod.Equation);
                Method = VariableMethod.MinMaxInterval;
            }
        }

        private void UpdateAvailableUnits()
        {
            AvailableUnits.Clear();
            // Imperial units are listed first throughout as the default.
            switch (Property)
            {
                case TargetProperty.RValue:
                    AvailableUnits.Add("ft²·°F·h/BTU");   // Imperial first
                    AvailableUnits.Add("m²·K/W");
                    break;
                case TargetProperty.UValue:
                    AvailableUnits.Add("BTU/(h·ft²·°F)"); // Imperial first
                    AvailableUnits.Add("W/(m²·K)");
                    break;
                case TargetProperty.Thickness:
                    AvailableUnits.Add("in");   // Imperial first
                    AvailableUnits.Add("ft");
                    AvailableUnits.Add("mm");
                    AvailableUnits.Add("cm");
                    AvailableUnits.Add("m");
                    break;
                case TargetProperty.Conductivity:
                    AvailableUnits.Add("BTU·in/(h·ft²·°F)"); // Imperial first
                    AvailableUnits.Add("W/(m·K)");
                    break;
                case TargetProperty.Density:
                    AvailableUnits.Add("lb/ft³");  // Imperial first
                    AvailableUnits.Add("kg/m³");
                    break;
                case TargetProperty.SpecificHeat:
                    AvailableUnits.Add("BTU/(lb·°F)"); // Imperial first
                    AvailableUnits.Add("J/(kg·K)");
                    break;
                case TargetProperty.Unitless:
                    AvailableUnits.Add("-");
                    break;
                case TargetProperty.WeatherTemperature:
                case TargetProperty.WeatherTemperatureOffset:
                    AvailableUnits.Add("°F");  // Imperial first
                    AvailableUnits.Add("°C");
                    AvailableUnits.Add("K");
                    break;
                case TargetProperty.WeatherDewPoint:
                    AvailableUnits.Add("°F");  // Imperial first
                    AvailableUnits.Add("°C");
                    AvailableUnits.Add("K");
                    break;
                case TargetProperty.WeatherRelativeHumidity:
                    AvailableUnits.Add("%");
                    break;
                case TargetProperty.WeatherAtmosphericPressure:
                    AvailableUnits.Add("inHg"); // Imperial first
                    AvailableUnits.Add("Pa");
                    break;
                case TargetProperty.WeatherSolarRadiation:
                    AvailableUnits.Add("BTU/ft²"); // Imperial first
                    AvailableUnits.Add("Wh/m²");
                    break;
                case TargetProperty.WeatherDirectNormalRadiation:
                    AvailableUnits.Add("BTU/ft²"); // Imperial first
                    AvailableUnits.Add("Wh/m²");
                    break;
                case TargetProperty.WeatherDiffuseHorizontalRadiation:
                    AvailableUnits.Add("BTU/ft²"); // Imperial first
                    AvailableUnits.Add("Wh/m²");
                    break;
                case TargetProperty.WeatherWindSpeed:
                    AvailableUnits.Add("mph"); // Imperial first
                    AvailableUnits.Add("m/s");
                    break;
                case TargetProperty.WeatherWindDirection:
                    AvailableUnits.Add("° (0-360 from N)");
                    break;
                case TargetProperty.WeatherTotalSkyCover:
                    AvailableUnits.Add("tenths (0-10)");
                    break;
                case TargetProperty.RevitType:
                    AvailableUnits.Add("Type Selection");
                    AvailableUnits.Add("Replace Elements");
                    AvailableUnits.Add("Effective R-Value");
                    AvailableUnits.Add("Monolithic");
                    break;
                case TargetProperty.HeatingSetpoint:
                case TargetProperty.CoolingSetpoint:
                    AvailableUnits.Add("°F"); // Imperial first
                    AvailableUnits.Add("°C");
                    AvailableUnits.Add("K");
                    break;
                case TargetProperty.Infiltration:
                    AvailableUnits.Add("ACH"); // Air Changes per Hour
                    AvailableUnits.Add("minutes/day door or window open");
                    break;
                case TargetProperty.PeopleCount:
                    AvailableUnits.Add("count");
                    break;
                case TargetProperty.Schedule:
                    AvailableUnits.Add("String (Name)");
                    break;
                case TargetProperty.IsUnheated:
                    AvailableUnits.Add("Boolean (1=True, 0=False)");
                    break;
                case TargetProperty.InsulationSettling:
                    AvailableUnits.Add("% (From Top)");
                    AvailableUnits.Add("ft (From Top)");
                    AvailableUnits.Add("ft (From Bottom)");
                    break;
            }
            SelectedUnit = AvailableUnits.FirstOrDefault();
        }
        private VariableState _state = VariableState.NotIncluded;
        public VariableState State 
        { 
            get => _state; 
            set 
            { 
                // RevitType only makes sense as Variable (TypeSelection). Disallow Constant.
                var effectiveValue = (value == VariableState.Constant && _property == TargetProperty.RevitType)
                    ? VariableState.Variable
                    : value;
                if (_state != effectiveValue) { _state = effectiveValue; OnPropertyChanged(); } 
            } 
        }

        private VariableMethod _method;
        public VariableMethod Method 
        { 
            get => _method; 
            set { if (_method != value) { _method = value; OnPropertyChanged(); } } 
        }
        
        private double _constantValue;
        public double ConstantValue 
        { 
            get => _constantValue; 
            set { if (_constantValue != value) { _constantValue = value; OnPropertyChanged(); } } 
        }
        
        private double _min;
        public double Min 
        { 
            get => _min; 
            set { if (_min != value) { _min = value; OnPropertyChanged(); } } 
        }

        private double _max;
        public double Max 
        { 
            get => _max; 
            set { if (_max != value) { _max = value; OnPropertyChanged(); } } 
        }

        private double _interval;
        public double Interval 
        { 
            get => _interval; 
            set { if (_interval != value) { _interval = value; OnPropertyChanged(); } } 
        }

        private bool _isIntervalCount;
        public bool IsIntervalCount 
        { 
            get => _isIntervalCount; 
            set { if (_isIntervalCount != value) { _isIntervalCount = value; OnPropertyChanged(); } } 
        } 
        
        public List<double> ArrayValues { get; set; } = new List<double>();
        
        private string _equationString;
        public string EquationString 
        { 
            get => _equationString; 
            set { if (_equationString != value) { _equationString = value; OnPropertyChanged(); } } 
        }
        public ObservableCollection<SimulationVariable> IndependentVariables { get; set; } = new ObservableCollection<SimulationVariable>();


        public ObservableCollection<ProfileHour> WeekdayOccupancy { get; } = new ObservableCollection<ProfileHour>(Enumerable.Range(0, 24).Select(i => new ProfileHour { HourLabel = $"{i:D2}:00", Value = 1.0 }));
        public ObservableCollection<ProfileHour> WeekendOccupancy { get; } = new ObservableCollection<ProfileHour>(Enumerable.Range(0, 24).Select(i => new ProfileHour { HourLabel = $"{i:D2}:00", Value = 0.0 }));
        public ObservableCollection<ProfileHour> WeekdayLighting { get; } = new ObservableCollection<ProfileHour>(Enumerable.Range(0, 24).Select(i => new ProfileHour { HourLabel = $"{i:D2}:00", Value = 1.0 }));
        public ObservableCollection<ProfileHour> WeekendLighting { get; } = new ObservableCollection<ProfileHour>(Enumerable.Range(0, 24).Select(i => new ProfileHour { HourLabel = $"{i:D2}:00", Value = 0.0 }));
        public ObservableCollection<ProfileHour> WeekdayHeating { get; } = new ObservableCollection<ProfileHour>(Enumerable.Range(0, 24).Select(i => new ProfileHour { HourLabel = $"{i:D2}:00", Value = 70.0 }));
        public ObservableCollection<ProfileHour> WeekendHeating { get; } = new ObservableCollection<ProfileHour>(Enumerable.Range(0, 24).Select(i => new ProfileHour { HourLabel = $"{i:D2}:00", Value = 70.0 }));
        public ObservableCollection<ProfileHour> WeekdayCooling { get; } = new ObservableCollection<ProfileHour>(Enumerable.Range(0, 24).Select(i => new ProfileHour { HourLabel = $"{i:D2}:00", Value = 75.0 }));
        public ObservableCollection<ProfileHour> WeekendCooling { get; } = new ObservableCollection<ProfileHour>(Enumerable.Range(0, 24).Select(i => new ProfileHour { HourLabel = $"{i:D2}:00", Value = 75.0 }));



        // Helper for DataBinding (to hold comma separated string from UI)
        public string ArrayValuesString 
        { 
            get { return string.Join(", ", ArrayValues); }
            set 
            { 
                try { ArrayValues = value.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(double.Parse).ToList(); }
                catch { /* Handled via UI validation */ }
                OnPropertyChanged();
            }
        }

        public List<object> GenerateValues()
        {
            var values = GenerateRawValues();
            
            // Perform unit conversions so the PermutationEngine always outputs standard units (e.g. ACH for Infiltration)
            if (_property == TargetProperty.Infiltration && SelectedUnit == "minutes/day door/window is open")
            {
                for (int i = 0; i < values.Count; i++)
                {
                    if (values[i] is double minutes)
                    {
                        // Placeholder conversion: 1 minute door open = 0.005 ACH (needs tuning by user)
                        values[i] = minutes * 0.005;
                    }
                }
            }
            
            return values;
        }

        public List<object> GenerateRawValues()
        {
            if (State == VariableState.NotIncluded) return new List<object>();
            
            List<object> values = new List<object>();

            if (_property == TargetProperty.Schedule)
            {
                ScheduleConfig schedConfig;
                
                if (!IsCustomSchedule && SelectedScheduleDefault == "Single Family Home")
                {
                    // OpenStudio-standards doesn't support SingleFamily natively in 90.1, 
                    // so we intercept it and send a hardcoded typical residential profile.
                    schedConfig = new ScheduleConfig 
                    { 
                        IsCustom = true, // Force to custom so measure.rb uses these arrays
                        DefaultType = "Single Family Home",
                        // Weekday Occupied 5pm-8am, Weekend Occupied all day
                        WeekdayOccupancy = Enumerable.Range(0, 24).Select(h => (h >= 17 || h <= 8) ? 1.0 : 0.2).ToList(),
                        WeekendOccupancy = Enumerable.Range(0, 24).Select(h => 1.0).ToList(),
                        // Lighting peaks in evening
                        WeekdayLighting = Enumerable.Range(0, 24).Select(h => (h >= 17 && h <= 22) ? 0.8 : 0.1).ToList(),
                        WeekendLighting = Enumerable.Range(0, 24).Select(h => (h >= 17 && h <= 22) ? 0.8 : 0.1).ToList(),
                        // Heating: Setback to 62 during day, 68 at night
                        WeekdayHeating = Enumerable.Range(0, 24).Select(h => (h > 8 && h < 17) ? 62.0 : 68.0).ToList(),
                        WeekendHeating = Enumerable.Range(0, 24).Select(h => 68.0).ToList(),
                        // Cooling: Setup to 82 during day, 78 at night
                        WeekdayCooling = Enumerable.Range(0, 24).Select(h => (h > 8 && h < 17) ? 82.0 : 78.0).ToList(),
                        WeekendCooling = Enumerable.Range(0, 24).Select(h => 78.0).ToList()
                    };
                }
                else
                {
                    schedConfig = new ScheduleConfig 
                    { 
                        IsCustom = IsCustomSchedule, 
                        DefaultType = SelectedScheduleDefault,
                        WeekdayOccupancy = WeekdayOccupancy.Select(h => h.Value).ToList(),
                        WeekendOccupancy = WeekendOccupancy.Select(h => h.Value).ToList(),
                        WeekdayLighting = WeekdayLighting.Select(h => h.Value).ToList(),
                        WeekendLighting = WeekendLighting.Select(h => h.Value).ToList(),
                        WeekdayHeating = WeekdayHeating.Select(h => h.Value).ToList(),
                        WeekendHeating = WeekendHeating.Select(h => h.Value).ToList(),
                        WeekdayCooling = WeekdayCooling.Select(h => h.Value).ToList(),
                        WeekendCooling = WeekendCooling.Select(h => h.Value).ToList()
                    };
                }

                if (State == VariableState.Constant)
                {
                    values.Add(schedConfig);
                    return values;
                }
                // If variable schedule is needed later, we can add it here. For now just constant is typical.
                values.Add(schedConfig);
                return values;
            }

            if (_property == TargetProperty.WeatherTemperatureSynthetic)
            {
                var winterMinVals = SyntheticWeatherParams[0].GenerateRawValues();
                var winterMaxVals = SyntheticWeatherParams[1].GenerateRawValues();
                var summerMinVals = SyntheticWeatherParams[2].GenerateRawValues();
                var summerMaxVals = SyntheticWeatherParams[3].GenerateRawValues();
                
                foreach(var wMin in winterMinVals)
                foreach(var wMax in winterMaxVals)
                foreach(var sMin in summerMinVals)
                foreach(var sMax in summerMaxVals)
                {
                    values.Add(new SyntheticWeatherConfig {
                        WinterMinTemp = Convert.ToDouble(wMin),
                        WinterMaxTemp = Convert.ToDouble(wMax),
                        SummerMinTemp = Convert.ToDouble(sMin),
                        SummerMaxTemp = Convert.ToDouble(sMax),
                        Offset = 0
                    });
                }
                return values;
            }

            if (_property == TargetProperty.InsulationSettling)
            {
                if (State == VariableState.Constant) 
                {
                    values.Add(new SettlingConfig { Method = SelectedUnit, Value = ConstantValue, SettledWallType = SelectedRevitTypes.FirstOrDefault() });
                    return values;
                }

                if (Method == VariableMethod.MinMaxInterval)
                {
                    if (IsIntervalCount)
                    {
                        double stepSize = (Max - Min) / Math.Max(1, Interval - 1);
                        for (int i = 0; i < Interval; i++)
                        {
                            values.Add(new SettlingConfig { Method = SelectedUnit, Value = Min + (stepSize * i), SettledWallType = SelectedRevitTypes.FirstOrDefault() });
                        }
                    }
                    else
                    {
                        for (double v = Min; v <= Max; v += Interval)
                        {
                            values.Add(new SettlingConfig { Method = SelectedUnit, Value = v, SettledWallType = SelectedRevitTypes.FirstOrDefault() });
                        }
                    }
                }
                else if (Method == VariableMethod.Array)
                {
                    foreach(var val in ArrayValues)
                    {
                         values.Add(new SettlingConfig { Method = SelectedUnit, Value = val, SettledWallType = SelectedRevitTypes.FirstOrDefault() });
                    }
                }
                return values;
            }

            if (_property == TargetProperty.PeopleCount)
            {
                if (State == VariableState.Constant) 
                {
                    values.Add(new PeopleConfig { Count = ConstantValue, UseDefaultMetabolicHeat = this.UseDefaultMetabolicHeat, CustomMetabolicHeat = this.CustomMetabolicHeat });
                    return values;
                }

                if (Method == VariableMethod.MinMaxInterval)
                {
                    if (IsIntervalCount)
                    {
                        double stepSize = (Max - Min) / Math.Max(1, Interval - 1);
                        for (int i = 0; i < Interval; i++) values.Add(new PeopleConfig { Count = Min + (stepSize * i), UseDefaultMetabolicHeat = this.UseDefaultMetabolicHeat, CustomMetabolicHeat = this.CustomMetabolicHeat });
                    }
                    else
                    {
                        for (double v = Min; v <= Max; v += Interval) values.Add(new PeopleConfig { Count = v, UseDefaultMetabolicHeat = this.UseDefaultMetabolicHeat, CustomMetabolicHeat = this.CustomMetabolicHeat });
                    }
                }
                else if (Method == VariableMethod.Array)
                {
                    foreach(var val in ArrayValues) values.Add(new PeopleConfig { Count = val, UseDefaultMetabolicHeat = this.UseDefaultMetabolicHeat, CustomMetabolicHeat = this.CustomMetabolicHeat });
                }
                return values;
            }

            if (State == VariableState.Constant) return new List<object> { ConstantValue };

            switch (Method)
            {
                case VariableMethod.MinMaxInterval:
                    if (IsIntervalCount)
                    {
                        double stepSize = (Max - Min) / Math.Max(1, Interval - 1);
                        for (int i = 0; i < Interval; i++) values.Add(Min + (stepSize * i));
                    }
                    else
                    {
                        for (double v = Min; v <= Max; v += Interval) values.Add(v);
                    }
                    break;
                case VariableMethod.Array:
                    foreach(var val in ArrayValues) values.Add(val);
                    break;
                case VariableMethod.TypeSelection:
                    if (IncludeOriginalType) values.Add("Original");
                    foreach (var val in SelectedRevitTypes) values.Add(val);
                    break;
                case VariableMethod.Equation:
                    if (!string.IsNullOrWhiteSpace(EquationString))
                        values.Add(EquationString);
                    else
                        values.Add(0.0);
                    break;
                case VariableMethod.TemperatureDependent:
                    values.Add($"TempDependent:{RValueTempEquation}|{RValueTempEquationUnit}");
                    break;
                case VariableMethod.ReplaceElements:
                case VariableMethod.Monolithic:
                    if (SelectedStudTypes.Count == 0 || SelectedInsulationTypes.Count == 0)
                        values.Add(new WallModConfig { 
                            Method = this.Method, 
                            StudType = "None", 
                            InsulationType = "None", 
                            FramingFactor = EffFramingFactor,
                            VaryRValueWithTemp = this.VaryRValueWithTemp,
                            RValueTempEquation = this.RValueTempEquation,
                            RValueTempEquationUnit = this.RValueTempEquationUnit
                        }); 
                    foreach (var s in SelectedStudTypes)
                    {
                        foreach (var ins in SelectedInsulationTypes)
                        {
                            values.Add(new WallModConfig {
                                Method = this.Method,
                                StudType = s,
                                InsulationType = ins,
                                FramingFactor = EffFramingFactor,
                                VaryRValueWithTemp = this.VaryRValueWithTemp,
                                RValueTempEquation = this.RValueTempEquation,
                                RValueTempEquationUnit = this.RValueTempEquationUnit
                            });
                        }
                    }
                    break;
                case VariableMethod.EffectiveRValue:
                    values.Add(new WallModConfig {
                        Method = VariableMethod.EffectiveRValue,
                        StudRValue = EffStudRValue,
                        InsulationRValue = EffInsulationRValue,
                        WindowRValue = EffWindowRValue,
                        DoorRValue = EffDoorRValue,
                        FramingFactor = EffFramingFactor,
                        Density = EffDensity,
                        SpecificHeat = EffSpecificHeat
                    });
                    break;
            }
            return values;
        }
    }

    public static class PermutationEngine
    {
        // 1. Conflict Definition Structure
        private static readonly List<HashSet<TargetProperty>> _mutuallyExclusiveGroups = new List<HashSet<TargetProperty>>();

        static PermutationEngine()
        {
            // Weather temperature modes
            AddMutuallyExclusiveGroup(new[] { 
                TargetProperty.WeatherTemperature, 
                TargetProperty.WeatherTemperatureOffset, 
                TargetProperty.WeatherTemperatureSynthetic 
            });

            // Solar radiation modes
            AddMutuallyExclusiveGroup(new[] { 
                TargetProperty.WeatherSolarRadiation, 
                TargetProperty.WeatherDirectNormalRadiation, 
                TargetProperty.WeatherDiffuseHorizontalRadiation 
            });

            // Heating/Cooling state
            AddMutuallyExclusiveGroup(new[] { 
                TargetProperty.IsUnheated, 
                TargetProperty.HeatingSetpoint, 
                TargetProperty.CoolingSetpoint 
            });

            // Material Resistance
            AddMutuallyExclusiveGroup(new[] { 
                TargetProperty.RValue, 
                TargetProperty.UValue 
            });

            // RevitType conflicts with all other properties on the same element
            foreach (TargetProperty prop in Enum.GetValues(typeof(TargetProperty)))
            {
                if (prop != TargetProperty.RevitType)
                {
                    AddMutuallyExclusiveGroup(new[] { TargetProperty.RevitType, prop });
                }
            }
        }

        public static void AddMutuallyExclusiveGroup(IEnumerable<TargetProperty> group)
        {
            _mutuallyExclusiveGroups.Add(new HashSet<TargetProperty>(group));
        }

        private class VariationAxis
        {
            public TargetProperty Property { get; set; }
            public SimulationElement Element { get; set; }
            public List<KeyValuePair<string, object>> Values { get; set; }
        }

        public static IEnumerable<Dictionary<string, object>> GenerateAllScenarios(List<SimulationElement> activeElements)
        {
            var combinedValuesList = new List<VariationAxis>();

            foreach (var element in activeElements)
            {
                var activeProps = element.Properties.Where(p => p.State != VariableState.NotIncluded).ToList();
                var allActiveVarsForElement = new List<SimulationVariable>();
                foreach (var prop in activeProps)
                {
                    allActiveVarsForElement.Add(prop);
                    foreach (var indepVar in prop.IndependentVariables)
                    {
                        if (indepVar.State != VariableState.NotIncluded)
                        {
                            allActiveVarsForElement.Add(indepVar);
                        }
                    }
                }

                var elementGroups = allActiveVarsForElement.GroupBy(p => p.Property)
                    .OrderByDescending(g => g.Key == TargetProperty.RevitType || g.Key == TargetProperty.InsulationSettling ? 1 : 0)
                    .ToList();

                foreach (var group in elementGroups)
                {
                    var combinedValues = new List<KeyValuePair<string, object>>();
                    foreach (var variable in group)
                    {
                        var vals = variable.GenerateValues();
                        foreach (var val in vals)
                        {
                            combinedValues.Add(new KeyValuePair<string, object>(variable.Name, val));
                        }
                    }
                    if (combinedValues.Count > 0)
                    {
                        combinedValuesList.Add(new VariationAxis { Property = group.Key, Element = element, Values = combinedValues });
                    }
                }
            }

            // 2. Combinatorial Filtering (Branching Logic)
            var validAxisSets = ResolveConflicts(combinedValuesList);

            foreach (var axisSet in validAxisSets)
            {
                var lists = axisSet.Select(a => a.Values).ToList();
                foreach (var dict in GenerateCombinations(lists, 0, new Dictionary<string, object>()))
                {
                    yield return dict;
                }
            }
        }

        private static List<List<VariationAxis>> ResolveConflicts(List<VariationAxis> requestedAxes)
        {
            // Group axes by Element to isolate conflicts to their respective elements
            var axesByElement = requestedAxes.GroupBy(a => a.Element).ToList();
            var validBranchesPerElement = new List<List<List<VariationAxis>>>();

            foreach (var elementGroup in axesByElement)
            {
                var elementAxes = elementGroup.ToList();
                var requestedProperties = new HashSet<TargetProperty>(elementAxes.Select(a => a.Property));
                var conflictingGroups = new List<List<TargetProperty>>();
                var baseProperties = new HashSet<TargetProperty>(requestedProperties);

                foreach (var group in _mutuallyExclusiveGroups)
                {
                    var intersection = group.Intersect(requestedProperties).ToList();
                    if (intersection.Count > 1)
                    {
                        string elemName = elementGroup.Key != null ? elementGroup.Key.DisplayName : "Unknown Element";
                        System.Diagnostics.Debug.WriteLine($"Conflict detected on {elemName}: {string.Join(" and ", intersection)}. Splitting into separate simulation branches.");
                        
                        conflictingGroups.Add(intersection);
                        
                        foreach (var prop in intersection)
                        {
                            baseProperties.Remove(prop);
                        }
                    }
                }

                var validBranchesForThisElement = new List<List<VariationAxis>>();

                if (conflictingGroups.Count == 0)
                {
                    validBranchesForThisElement.Add(elementAxes);
                }
                else
                {
                    // Calculate combinations of conflicting properties for this element
                    var branchCombinations = GetCartesianProductOfKeys(conflictingGroups);

                    foreach (var combo in branchCombinations)
                    {
                        var validPropertiesForBranch = new HashSet<TargetProperty>(baseProperties);
                        foreach (var prop in combo)
                        {
                            validPropertiesForBranch.Add(prop);
                        }

                        var branchAxes = elementAxes.Where(a => validPropertiesForBranch.Contains(a.Property)).ToList();
                        validBranchesForThisElement.Add(branchAxes);
                    }
                }

                validBranchesPerElement.Add(validBranchesForThisElement);
            }

            // Now compute the global combinations of valid element branches
            return GetCartesianProductOfBranches(validBranchesPerElement);
        }

        private static List<List<TargetProperty>> GetCartesianProductOfKeys(List<List<TargetProperty>> sequences)
        {
            var result = new List<List<TargetProperty>> { new List<TargetProperty>() };

            foreach (var sequence in sequences)
            {
                var temp = new List<List<TargetProperty>>();
                foreach (var res in result)
                {
                    foreach (var item in sequence)
                    {
                        var newRes = new List<TargetProperty>(res) { item };
                        temp.Add(newRes);
                    }
                }
                result = temp;
            }

            return result;
        }

        private static List<List<VariationAxis>> GetCartesianProductOfBranches(List<List<List<VariationAxis>>> elementBranches)
        {
            var result = new List<List<VariationAxis>> { new List<VariationAxis>() };

            foreach (var branches in elementBranches)
            {
                var temp = new List<List<VariationAxis>>();
                foreach (var res in result)
                {
                    foreach (var branch in branches)
                    {
                        var newRes = new List<VariationAxis>(res);
                        newRes.AddRange(branch);
                        temp.Add(newRes);
                    }
                }
                result = temp;
            }

            return result;
        }

        private static IEnumerable<Dictionary<string, object>> GenerateCombinations(List<List<KeyValuePair<string, object>>> lists, int index, Dictionary<string, object> current)
        {
            if (index == lists.Count)
            {
                yield return new Dictionary<string, object>(current);
                yield break;
            }
            
            foreach (var kvp in lists[index])
            {
                current[kvp.Key] = kvp.Value;
                foreach (var dict in GenerateCombinations(lists, index + 1, current))
                {
                    yield return dict;
                }
            }
        }

        private class VariationAxisCount
        {
            public TargetProperty Property { get; set; }
            public SimulationElement Element { get; set; }
            public long Count { get; set; }
        }

        public static long GetTotalScenariosCount(List<SimulationElement> activeElements)
        {
            var axesCounts = new List<VariationAxisCount>();

            foreach (var element in activeElements)
            {
                var activeProps = element.Properties.Where(p => p.State != VariableState.NotIncluded).ToList();
                var allActiveVarsForElement = new List<SimulationVariable>();
                foreach (var prop in activeProps)
                {
                    allActiveVarsForElement.Add(prop);
                    foreach (var indepVar in prop.IndependentVariables)
                    {
                        if (indepVar.State != VariableState.NotIncluded)
                        {
                            allActiveVarsForElement.Add(indepVar);
                        }
                    }
                }

                var elementGroups = allActiveVarsForElement.GroupBy(p => p.Property);

                foreach (var group in elementGroups)
                {
                    long groupCount = 0;
                    foreach (var variable in group)
                    {
                        groupCount += variable.GenerateValues().Count;
                    }
                    if (groupCount > 0)
                    {
                        axesCounts.Add(new VariationAxisCount { Property = group.Key, Element = element, Count = groupCount });
                    }
                }
            }

            // Group axes by Element to isolate conflicts to their respective elements
            var axesByElement = axesCounts.GroupBy(a => a.Element).ToList();
            long totalScenarios = 1;

            foreach (var elementGroup in axesByElement)
            {
                var elementAxes = elementGroup.ToList();
                var requestedProperties = new HashSet<TargetProperty>(elementAxes.Select(a => a.Property));
                var conflictingGroups = new List<List<TargetProperty>>();
                var baseProperties = new HashSet<TargetProperty>(requestedProperties);

                foreach (var group in _mutuallyExclusiveGroups)
                {
                    var intersection = group.Intersect(requestedProperties).ToList();
                    if (intersection.Count > 1)
                    {
                        conflictingGroups.Add(intersection);
                        foreach (var prop in intersection)
                        {
                            baseProperties.Remove(prop);
                        }
                    }
                }

                if (conflictingGroups.Count == 0)
                {
                    long elementTotal = elementAxes.Aggregate(1L, (acc, a) => acc * a.Count);
                    totalScenarios *= elementTotal;
                }
                else
                {
                    var branchCombinations = GetCartesianProductOfKeys(conflictingGroups);
                    long elementBranchTotals = 0;

                    foreach (var combo in branchCombinations)
                    {
                        var validPropertiesForBranch = new HashSet<TargetProperty>(baseProperties);
                        foreach (var prop in combo)
                        {
                            validPropertiesForBranch.Add(prop);
                        }

                        long branchTotal = 1;
                        foreach (var axis in elementAxes.Where(a => validPropertiesForBranch.Contains(a.Property)))
                        {
                            branchTotal *= axis.Count;
                        }
                        elementBranchTotals += branchTotal;
                    }

                    totalScenarios *= elementBranchTotals;
                }
            }

            return totalScenarios;
        }

        public static double EvaluateEquation(string equation, Dictionary<string, object> currentScenario)
        {
            string cleanEq = equation.StartsWith("=") ? equation.Substring(1) : equation;
            Expression e = new Expression(cleanEq);
            
            foreach (var kvp in currentScenario)
            {
                // Only inject numeric parameters into NCalc equations
                if (kvp.Value is double || kvp.Value is int || kvp.Value is float)
                {
                    e.Parameters[kvp.Key] = kvp.Value;
                }
            }
            
            object result = e.Evaluate();
            return Convert.ToDouble(result);
        }
    }
}
