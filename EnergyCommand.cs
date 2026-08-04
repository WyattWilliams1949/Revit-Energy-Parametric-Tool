using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitAddin;

[Transaction(TransactionMode.Manual)]
public class EnergyParametricCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        Document doc = commandData.Application.ActiveUIDocument.Document;
        UIDocument uidoc = commandData.Application.ActiveUIDocument;

        List<SimulationElement> activeEnvelopeTypes = HarvestActiveEnvelope(doc);
        
        RevitEventHandler handler = new RevitEventHandler();
        ExternalEvent exEvent = ExternalEvent.Create(handler);
        
        HomeWindow homeWindow = new HomeWindow(doc, uidoc, activeEnvelopeTypes, handler, exEvent);
        homeWindow.ShowDialog();

        return Result.Succeeded;
    }

    private List<SimulationElement> HarvestActiveEnvelope(Document doc)
    {
        var elements = new List<SimulationElement>();

        var walls = new FilteredElementCollector(doc).OfClass(typeof(Wall)).WhereElementIsNotElementType()
            .Cast<Wall>().Select(w => doc.GetElement(w.GetTypeId()) as ElementType);
            
        var floors = new FilteredElementCollector(doc).OfClass(typeof(Floor)).WhereElementIsNotElementType()
            .Cast<Floor>().Select(f => doc.GetElement(f.GetTypeId()) as ElementType);
            
        var roofs = new FilteredElementCollector(doc).OfClass(typeof(RoofBase)).WhereElementIsNotElementType()
            .Cast<RoofBase>().Select(r => doc.GetElement(r.GetTypeId()) as ElementType);

        var allTypes = new List<ElementType>();
        allTypes.AddRange(walls);
        allTypes.AddRange(floors);
        allTypes.AddRange(roofs);

        var uniqueTypes = allTypes.GroupBy(t => t.Id).Select(g => g.First()).ToList();

        var weatherElement = new SimulationElement 
        { 
            Category = "Environment", 
            ElementName = "Weather Data",
            ElementId = ElementId.InvalidElementId
        };
        weatherElement.Properties.Add(new SimulationVariable(VariableCategory.Weather) 
        { 
            Name = "Environment: Weather Data (Dry Bulb Temp)"
        });
        elements.Add(weatherElement);

        var globalSpace = new SimulationElement
        {
            ElementName = "", // Name blank so it displays as "Building Variables"
            Category = "Building Variables",
            ElementId = ElementId.InvalidElementId,
            ShowEntireBuildingToggle = true
        };
        globalSpace.Properties.Add(new SimulationVariable(VariableCategory.Building) { Name = "Spaces: Entire Building (Heating Setpoint)", Property = TargetProperty.HeatingSetpoint });
        elements.Add(globalSpace);

        var wallsCategory = new SimulationElement { ElementName = "Walls", Category = "Envelope", ElementId = ElementId.InvalidElementId, CanHaveProperties = false };
        var floorsCategory = new SimulationElement { ElementName = "Floors", Category = "Envelope", ElementId = ElementId.InvalidElementId, CanHaveProperties = false };
        var roofsCategory = new SimulationElement { ElementName = "Roofs", Category = "Envelope", ElementId = ElementId.InvalidElementId, CanHaveProperties = false };

        foreach (var t in uniqueTypes)
        {
            var simElement = new SimulationElement
            {
                ElementName = t.Name,
                Category = t.FamilyName,
                ElementId = t.Id
            };
            
            var defaultVar = new SimulationVariable(VariableCategory.Envelope) 
            { 
                Name = $"{t.FamilyName}: {t.Name} (R-Value)",
                Property = TargetProperty.RValue
            };
            
            var collector = new FilteredElementCollector(doc).OfClass(t.GetType());
            foreach (ElementType type in collector)
            {
                if (type.Category != null && type.Category.Id == t.Category.Id && type.Id != t.Id)
                {
                    defaultVar.AvailableRevitTypes.Add(type.Name);
                }
            }
            
            simElement.Properties.Add(defaultVar);
            
            if (t is WallType) wallsCategory.SubElements.Add(simElement);
            else if (t is FloorType) floorsCategory.SubElements.Add(simElement);
            else roofsCategory.SubElements.Add(simElement);
        }
        
        if (wallsCategory.SubElements.Count > 0) elements.Add(wallsCategory);
        if (floorsCategory.SubElements.Count > 0) elements.Add(floorsCategory);
        if (roofsCategory.SubElements.Count > 0) elements.Add(roofsCategory);
        
        var mepSpaces = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_MEPSpaces)
            .OfClass(typeof(SpatialElement))
            .Cast<SpatialElement>()
            .Where(s => s.Area > 0).ToList();

        var archRooms = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms)
            .OfClass(typeof(SpatialElement))
            .Cast<SpatialElement>()
            .Where(s => s.Area > 0).ToList();

        var spatialElements = mepSpaces.Count > 0 ? mepSpaces : archRooms;

        var allWindows = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Windows).WhereElementIsNotElementType().Cast<FamilyInstance>().ToList();
        var allDoors = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Doors).WhereElementIsNotElementType().Cast<FamilyInstance>().ToList();

        int assignedWindows = 0;
        int assignedDoors = 0;

        if (spatialElements.Count > 0)
        {
            foreach (var space in spatialElements)
            {
                var spaceSim = new SimulationElement
                {
                    ElementName = space.Name + " (" + space.Number + ")",
                    Category = "Room",
                    ElementId = space.Id
                };
                spaceSim.Properties.Add(new SimulationVariable(VariableCategory.Space) { Name = $"Spaces: {spaceSim.ElementName} (Heating Setpoint)", Property = TargetProperty.HeatingSetpoint });
                
                bool WindowBelongsToSpace(FamilyInstance w)
                {
                    if (w.Space != null && w.Space.Id == space.Id) return true;
                    if (w.Room != null && w.Room.Id == space.Id) return true;
                    if (w.FromRoom != null && w.FromRoom.Id == space.Id) return true;
                    if (w.ToRoom != null && w.ToRoom.Id == space.Id) return true;
                    if (space is Autodesk.Revit.DB.Mechanical.Space mepSpace && mepSpace.Room != null)
                    {
                        if (w.FromRoom != null && w.FromRoom.Id == mepSpace.Room.Id) return true;
                        if (w.ToRoom != null && w.ToRoom.Id == mepSpace.Room.Id) return true;
                    }
                    return false;
                }

                var spaceWindowsList = allWindows.Where(WindowBelongsToSpace).ToList();
                if (spaceWindowsList.Count > 0)
                {
                    assignedWindows += spaceWindowsList.Count;
                    var spaceWindows = new SimulationElement
                    {
                        ElementName = space.Name + " Windows",
                        Category = "Windows",
                        ElementId = space.Id
                    };
                    spaceWindows.Properties.Add(new SimulationVariable(VariableCategory.Opening) { Name = $"Windows: {spaceWindows.ElementName} (Infiltration)", Property = TargetProperty.Infiltration });
                    spaceSim.SubElements.Add(spaceWindows);
                }

                var spaceDoorsList = allDoors.Where(WindowBelongsToSpace).ToList();
                if (spaceDoorsList.Count > 0)
                {
                    assignedDoors += spaceDoorsList.Count;
                    var spaceDoors = new SimulationElement
                    {
                        ElementName = space.Name + " Doors",
                        Category = "Doors",
                        ElementId = space.Id
                    };
                    spaceDoors.Properties.Add(new SimulationVariable(VariableCategory.Opening) { Name = $"Doors: {spaceDoors.ElementName} (Infiltration)", Property = TargetProperty.Infiltration });
                    spaceSim.SubElements.Add(spaceDoors);
                }

                globalSpace.SubElements.Add(spaceSim);
            }
        }

        if (assignedWindows < allWindows.Count || assignedDoors < allDoors.Count)
        {
            globalSpace.MissingRoomsWarning = "Not all windows and/or doors have been assigned a room. Check if the space the window/door is in has been marked as a room";
        }

        return elements;
    }
}
