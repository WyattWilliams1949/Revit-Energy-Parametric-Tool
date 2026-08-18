using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitAddin
{
    public class MatlabRevitMutator
    {
        private Document _doc;
        private Dictionary<ElementId, ElementId> _originalTypesMap = new Dictionary<ElementId, ElementId>();
        private Dictionary<ElementId, (double? Heating, double? Cooling, double? Infiltration, double? People, int? CalcMode, int? ConditionType)> _originalSetpoints = new Dictionary<ElementId, (double? Heating, double? Cooling, double? Infiltration, double? People, int? CalcMode, int? ConditionType)>();
        private List<ElementId> _tempElementsToDelete = new List<ElementId>();
        private ElementId _currentReportViewId = null;

        public MatlabRevitMutator(Document doc)
        {
            _doc = doc;
        }

        /// <summary>
        /// Returns the effective thermal R-value (SI: m²·K/W) for each wall
        /// variable, keyed by variable name.  Used by MATLAB to patch the
        /// gbXML construction R-values after Revit exports.
        /// </summary>
        public Dictionary<string, double> GetEnvelopeRValues(
            Dictionary<string, TargetProperty> variableProperties,
            Dictionary<string, ElementId> variableElements)
        {
            var result = new Dictionary<string, double>();

            foreach (var kvp in variableProperties)
            {
                if (kvp.Value != TargetProperty.RevitType) continue;

                if (!variableElements.TryGetValue(kvp.Key, out var typeId)) continue;
                var wt = _doc.GetElement(typeId) as HostObjAttributes;
                if (wt == null) continue;

                // Compute total R by summing layer-by-layer contributions from ThermalAsset conductivities
                double computed = ComputeRValue_SI(wt);
                if (computed > 0)
                {
                    result[kvp.Key] = computed;
                }
            }

            return result;
        }

        /// <summary>
        /// Returns the effective thermal R-value (SI: m²·K/W) for a wall type
        /// looked up by its display name. Returns 0 if no matching type is found.
        /// </summary>
        public double GetRValueByName(string typeName)
        {
            var type = new FilteredElementCollector(_doc)
                .OfClass(typeof(HostObjAttributes))
                .Cast<HostObjAttributes>()
                .FirstOrDefault(t => t.Name == typeName);
            if (type == null) return 0;

            return ComputeRValue_SI(type);
        }

        /// <summary>
        /// Returns the effective thermal R-value (SI: m²·K/W) for an envelope type
        /// looked up by its display name and category. Returns 0 if no matching type is found.
        /// </summary>
        public double GetRValueByNameAndCategory(string typeName, ElementId categoryId)
        {
            var type = new FilteredElementCollector(_doc)
                .OfCategoryId(categoryId)
                .WhereElementIsElementType()
                .Cast<HostObjAttributes>()
                .FirstOrDefault(t => t.Name == typeName);
            if (type == null) return 0;

            return ComputeRValue_SI(type);
        }

        public void ApplyModifications(Dictionary<string, object> scenario, Dictionary<string, TargetProperty> variableProperties, Dictionary<string, ElementId> variableElements)
        {
            using (Transaction t = new Transaction(_doc, "Parametric Simulation - MATLAB"))
            {
                FailureHandlingOptions failureOptions = t.GetFailureHandlingOptions();
                failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                t.SetFailureHandlingOptions(failureOptions);

                t.Start();

                ApplyScenarioModifications(scenario, variableProperties, variableElements);

                t.Commit();
            }
        }

        public void ApplyScenarioModifications(Dictionary<string, object> scenario, Dictionary<string, TargetProperty> variableProperties, Dictionary<string, ElementId> variableElements)
        {
            foreach (var kvp in scenario)
            {
                if (variableProperties.TryGetValue(kvp.Key, out var prop) && variableElements.TryGetValue(kvp.Key, out var element))
                {
                    if (prop == TargetProperty.RevitType)
                    {
                        var val = kvp.Value;
                        if (val is System.Text.Json.JsonElement je)
                        {
                            if (je.ValueKind == System.Text.Json.JsonValueKind.String)
                            {
                                string targetTypeName = je.GetString();
                                if (targetTypeName == "Original") continue;
                                ApplyTypeChange(element, targetTypeName);
                            }
                            else if (je.ValueKind == System.Text.Json.JsonValueKind.Object)
                            {
                                WallModConfig cfg = System.Text.Json.JsonSerializer.Deserialize<WallModConfig>(je.GetRawText(), new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                                ApplyWallModConfig(element, cfg);
                            }
                        }
                        else if (val is string targetTypeName)
                        {
                            if (targetTypeName == "Original") continue;
                            ApplyTypeChange(element, targetTypeName);
                        }
                        else if (val is WallModConfig cfg)
                        {
                            ApplyWallModConfig(element, cfg);
                        }
                    }
                    else if (prop == TargetProperty.InsulationSettling)
                    {
                        var val = kvp.Value;
                        if (val is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.Object)
                        {
                            SettlingConfig cfg = System.Text.Json.JsonSerializer.Deserialize<SettlingConfig>(je.GetRawText(), new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            ApplyInsulationSettling(element, cfg);
                        }
                        else if (val is SettlingConfig cfg)
                        {
                            ApplyInsulationSettling(element, cfg);
                        }
                    }
                    else if (prop == TargetProperty.HeatingSetpoint || prop == TargetProperty.CoolingSetpoint || prop == TargetProperty.Infiltration || prop == TargetProperty.PeopleCount || prop == TargetProperty.IsUnheated)
                    {
                        double rawValue = Convert.ToDouble(kvp.Value.ToString());
                        double valueToSet = rawValue;
                        
                        if (prop == TargetProperty.HeatingSetpoint || prop == TargetProperty.CoolingSetpoint)
                        {
                            valueToSet = (rawValue - 32.0) * (5.0 / 9.0) + 273.15; // Convert F to K
                        }

                        if (element == ElementId.InvalidElementId) // Entire Building
                        {
                            var spaces = new FilteredElementCollector(_doc).OfCategory(BuiltInCategory.OST_MEPSpaces).OfClass(typeof(SpatialElement)).ToElements();
                            foreach (var space in spaces)
                            {
                                ApplySpaceSetpoint(space, prop, valueToSet);
                            }
                        }
                        else
                        {
                            var space = _doc.GetElement(element);
                            if (space != null)
                            {
                                ApplySpaceSetpoint(space, prop, valueToSet);
                            }
                        }
                    }
                }
            }
        }

        private void ApplySpaceSetpoint(Element space, TargetProperty prop, double val)
        {
            if (!_originalSetpoints.ContainsKey(space.Id))
            {
                var hParam = space.get_Parameter(BuiltInParameter.SPACE_HEATING_SET_POINT);
                var cParam = space.get_Parameter(BuiltInParameter.SPACE_COOLING_SET_POINT);
                var infParam = space.get_Parameter(BuiltInParameter.SPACE_INFILTRATION_PARAM);
                var pplParam = space.get_Parameter(BuiltInParameter.SPACE_NUMBER_OF_PEOPLE);
                var calcParam = space.get_Parameter(BuiltInParameter.SPACE_PEOPLE_LOAD_PARAM); // Or similar for method
                var condParam = space.get_Parameter(BuiltInParameter.ROOM_CONDITION_TYPE_PARAM);

                double? hVal = hParam != null && hParam.HasValue ? hParam.AsDouble() : (double?)null;
                double? cVal = cParam != null && cParam.HasValue ? cParam.AsDouble() : (double?)null;
                double? iVal = infParam != null && infParam.HasValue ? infParam.AsDouble() : (double?)null;
                double? pVal = pplParam != null && pplParam.HasValue ? pplParam.AsDouble() : (double?)null;
                int? calcVal = calcParam != null && calcParam.HasValue ? calcParam.AsInteger() : (int?)null;
                int? condVal = condParam != null && condParam.HasValue ? condParam.AsInteger() : (int?)null;
                
                _originalSetpoints[space.Id] = (hVal, cVal, iVal, pVal, calcVal, condVal);
            }

            // Force calculation method to "Specified" (usually integer value 1) for people and infiltration if needed
            if (prop == TargetProperty.PeopleCount || prop == TargetProperty.Infiltration)
            {
                // In Revit, Space occupancy and infiltration loads can be forced to "Specified".
                // 1 typically means "Specified", 0 means "Default" (by Space Type).
                var calcPplParam = space.get_Parameter(BuiltInParameter.SPACE_PEOPLE_LOAD_PARAM);
                if (calcPplParam != null && !calcPplParam.IsReadOnly) calcPplParam.Set(1);
            }

            Parameter targetParam = null;
            if (prop == TargetProperty.HeatingSetpoint) targetParam = space.get_Parameter(BuiltInParameter.SPACE_HEATING_SET_POINT);
            else if (prop == TargetProperty.CoolingSetpoint) targetParam = space.get_Parameter(BuiltInParameter.SPACE_COOLING_SET_POINT);
            else if (prop == TargetProperty.Infiltration) targetParam = space.get_Parameter(BuiltInParameter.SPACE_INFILTRATION_PARAM);
            else if (prop == TargetProperty.PeopleCount) targetParam = space.get_Parameter(BuiltInParameter.SPACE_NUMBER_OF_PEOPLE);
            else if (prop == TargetProperty.IsUnheated) targetParam = space.get_Parameter(BuiltInParameter.ROOM_CONDITION_TYPE_PARAM);


            if (targetParam != null && !targetParam.IsReadOnly)
            {
                // Convert ACH to internal units for Infiltration. Volume * ACH / 3600 = Airflow.
                if (prop == TargetProperty.Infiltration)
                {
                    // If targetParam is looking for Airflow (ft3/s)
                    var volParam = space.get_Parameter(BuiltInParameter.ROOM_VOLUME);
                    if (volParam != null && volParam.HasValue)
                    {
                        double volume = volParam.AsDouble(); // internal units (ft3)
                        double airflow = (volume * val) / 3600.0;
                        targetParam.Set(airflow);
                        return;
                    }
                }
                else if (prop == TargetProperty.IsUnheated)
                {
                    if (val < -0.5) return; // Not included
                    // ConditionType: 3 = Unconditioned, 2 = HeatedAndCooled
                    targetParam.Set(val > 0.5 ? 3 : 2);
                    return;
                }
                
                targetParam.Set(val);
            }
        }

        private void ApplyTypeChange(ElementId typeId, string targetTypeName)
        {
            var originalType = _doc.GetElement(typeId) as ElementType;
            if (originalType == null) return;

            var targetType = new FilteredElementCollector(_doc)
                .OfClass(originalType.GetType())
                .Cast<ElementType>()
                .FirstOrDefault(x => x.Name == targetTypeName && x.Category.Id == originalType.Category.Id);

            if (targetType == null) return;

            // For WallType: extract target's thermal properties and apply them at the
            // ORIGINAL wall's thickness. This prevents geometry conflicts (thickness change
            // breaks wall/floor joins) while correctly updating heat transfer properties.
            if (originalType is WallType originalWt && targetType is WallType targetWt)
            {
                var instances = new FilteredElementCollector(_doc)
                    .OfCategoryId(originalType.Category.Id)
                    .WhereElementIsNotElementType()
                    .Where(x => x.GetTypeId() == originalType.Id)
                    .Cast<Wall>()
                    .ToList();

                foreach (var inst in instances)
                {
                    if (!_originalTypesMap.ContainsKey(inst.Id))
                        _originalTypesMap[inst.Id] = inst.GetTypeId();
                }

                // Compute effective R (SI: m²·K/W) of the TARGET wall type
                // by reading its compound structure layers
                double targetR_SI = ComputeRValue_SI(targetWt);
                double targetR_SI_safe = targetR_SI > 0 ? targetR_SI : 0.18; // fallback ~R1 SI

                // Keep the ORIGINAL wall's thickness to avoid geometry conflicts
                double originalThickness_ft = originalWt.Width;
                double originalThickness_m  = originalThickness_ft * 0.3048;

                // k = thickness / R
                double k_SI = originalThickness_m / targetR_SI_safe;

                var thermalData = ExtractThermalData(targetWt);
                thermalData.Conductivity = k_SI;

                CreateAndApplyWallType(originalWt, thermalData, originalThickness_ft, instances);
            }
            else
            {
                // Non-wall types: direct type swap (no thickness/geometry concerns)
                var instances = new FilteredElementCollector(_doc)
                    .OfCategoryId(originalType.Category.Id)
                    .WhereElementIsNotElementType()
                    .Where(x => x.GetTypeId() == originalType.Id)
                    .ToList();

                foreach (var inst in instances)
                {
                    if (!_originalTypesMap.ContainsKey(inst.Id))
                        _originalTypesMap[inst.Id] = inst.GetTypeId();
                    inst.ChangeTypeId(targetType.Id);
                }
            }
        }

        /// <summary>
        /// Computes the total thermal resistance (m²·K/W) of a wall type
        /// by summing each layer's contribution: R_layer = thickness_m / k_SI.
        /// </summary>
        private double ComputeRValue_SI(HostObjAttributes wt)
        {
            double totalR = 0;
            var cs = wt.GetCompoundStructure();
            if (cs == null) return 0;

            for (int i = 0; i < cs.LayerCount; i++)
            {
                double layerThickness_ft = cs.GetLayerWidth(i);
                double layerThickness_m  = layerThickness_ft * 0.3048;
                var matId = cs.GetMaterialId(i);
                double k_SI = 0.1; // default fallback (W/m·K)

                if (matId != ElementId.InvalidElementId)
                {
                    var mat = _doc.GetElement(matId) as Material;
                    if (mat != null && mat.ThermalAssetId != ElementId.InvalidElementId)
                    {
                        var pse = _doc.GetElement(mat.ThermalAssetId) as PropertySetElement;
                        if (pse != null)
                        {
                            var ta = pse.GetThermalAsset();
                            // Revit stores ThermalConductivity in W/(m·K)
                            if (ta.ThermalConductivity > 0)
                                k_SI = ta.ThermalConductivity;
                        }
                    }
                }

                if (layerThickness_m > 0 && k_SI > 0)
                    totalR += layerThickness_m / k_SI;
            }

            return totalR;
        }


        private void ApplyWallModConfig(ElementId typeId, WallModConfig cfg)
        {
            var originalType = _doc.GetElement(typeId) as ElementType;
            if (originalType is WallType wt)
            {
                var instances = new FilteredElementCollector(_doc)
                    .OfCategoryId(originalType.Category.Id)
                    .WhereElementIsNotElementType()
                    .Where(x => x.GetTypeId() == originalType.Id)
                    .Cast<Wall>()
                    .ToList();

                if (instances.Count == 0) return;

                foreach (var inst in instances)
                {
                    if (!_originalTypesMap.ContainsKey(inst.Id))
                        _originalTypesMap[inst.Id] = inst.GetTypeId();
                }

                if (cfg.Method == VariableMethod.ReplaceElements)
                {
                    double shortTol = _doc.Application.ShortCurveTolerance;
                    ElementReplacementUtils.ReplaceWalls(_doc, wt.Id, cfg.StudType, cfg.InsulationType, 2.0 / 12.0, 14.0 / 12.0, shortTol);
                }
                else if (cfg.Method == VariableMethod.Monolithic)
                {
                    ApplyMonolithic(wt, cfg.StudType, cfg.InsulationType, cfg.FramingFactor, instances);
                }
                else if (cfg.Method == VariableMethod.EffectiveRValue)
                {
                    ApplyEffectiveRValue(wt, cfg, instances);
                }
            }
            else if (originalType is FloorType && cfg.Method == VariableMethod.ReplaceElements)
            {
                ElementReplacementUtils.ReplaceFloors(_doc, originalType.Id, cfg.StudType, cfg.InsulationType);
            }
            else if (originalType is RoofType && cfg.Method == VariableMethod.ReplaceElements)
            {
                ElementReplacementUtils.ReplaceRoofs(_doc, originalType.Id, cfg.StudType, cfg.InsulationType);
            }
        }

        private void ApplyInsulationSettling(ElementId typeId, SettlingConfig cfg)
        {
            var originalType = _doc.GetElement(typeId) as WallType;
            if (originalType == null) return;
            
            var settledType = new FilteredElementCollector(_doc).OfClass(typeof(WallType))
                .Cast<WallType>().FirstOrDefault(t => t.Name == cfg.SettledWallType);
            if (settledType == null) return;

            var instances = new FilteredElementCollector(_doc)
                .OfCategoryId(originalType.Category.Id)
                .WhereElementIsNotElementType()
                .Where(x => x.GetTypeId() == originalType.Id)
                .Cast<Wall>()
                .ToList();

            if (instances.Count == 0) return;

            foreach (var inst in instances)
            {
                if (!_originalTypesMap.ContainsKey(inst.Id))
                    _originalTypesMap[inst.Id] = inst.GetTypeId();
            }

            double percentSettled = 0;
            if (cfg.Method.Contains("%"))
            {
                percentSettled = cfg.Value / 100.0;
            }
            else
            {
                double avgHeight = 0;
                int count = 0;
                foreach (var w in instances)
                {
                    var param = w.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM);
                    if (param != null && param.HasValue) 
                    {
                        avgHeight += param.AsDouble();
                        count++;
                    }
                }
                if (count > 0) avgHeight /= count;
                
                if (avgHeight > 0)
                {
                    if (cfg.Method.Contains("From Top"))
                        percentSettled = cfg.Value / avgHeight;
                    else if (cfg.Method.Contains("From Bottom"))
                        percentSettled = (avgHeight - cfg.Value) / avgHeight;
                }
            }
            
            percentSettled = Math.Max(0, Math.Min(1, percentSettled));

            double thickness = originalType.Width;
            var sData = ExtractThermalData(settledType);
            var iData = ExtractThermalData(originalType);
            var outData = Blend(sData, iData, percentSettled);

            CreateAndApplyWallType(originalType, outData, thickness, instances);
        }

        public void ExportGbXml(string simFolder)
        {
            using (Transaction tDel = new Transaction(_doc, "Delete Energy Model"))
            {
                tDel.Start();
                var existingModel = Autodesk.Revit.DB.Analysis.EnergyAnalysisDetailModel.GetMainEnergyAnalysisDetailModel(_doc);
                if (existingModel != null) _doc.Delete(existingModel.Id);
                
                var eamType = Type.GetType("Autodesk.Revit.DB.Analysis.EnergyAnalyticalModel, RevitAPI");
                if (eamType != null)
                {
                    var eamModels = new FilteredElementCollector(_doc).OfClass(eamType).ToElementIds();
                    foreach (var id in eamModels) { try { _doc.Delete(id); } catch { } }
                }
                tDel.Commit();
            }

            bool energyModelCreated = false;
            var eWs = new WarningSwallower();
            using (Transaction t = new Transaction(_doc, "Export gbXML - Energy Model"))
            {
                FailureHandlingOptions failureOptions = t.GetFailureHandlingOptions();
                failureOptions.SetFailuresPreprocessor(eWs);
                t.SetFailureHandlingOptions(failureOptions);

                t.Start();
                
                _doc.Regenerate();
                try
                {
                    Autodesk.Revit.DB.Analysis.EnergyAnalysisDetailModel.Create(_doc);
                    energyModelCreated = true;
                }
                catch (Exception ex)
                {
                    // Log but don't rethrow; the commit check below will handle the failure state
                    System.Diagnostics.Debug.WriteLine($"EADM Create failed: {ex.Message}");
                }
                
                var status = t.Commit();
                if (status == TransactionStatus.RolledBack || eWs.HasError)
                    energyModelCreated = false;
            }

            // Degenerate geometry warnings are now handled by WarningSwallower during energy model creation.
            // If the energy model creation emitted a geometry warning, it automatically rolled back.

            if (!energyModelCreated)
                throw new InvalidOperationException("Energy model creation failed or was rolled back. gbXML export skipped.");

            var gbxOptions = new GBXMLExportOptions();
            _doc.Export(simFolder, "analysis.xml", gbxOptions);
        }

        public void RevertRevitChanges()
        {
            if (_originalTypesMap.Count == 0 && _tempElementsToDelete.Count == 0) return;

            using (Transaction t = new Transaction(_doc, "Revert Parametric Simulation - MATLAB"))
            {
                FailureHandlingOptions failureOptions = t.GetFailureHandlingOptions();
                failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                t.SetFailureHandlingOptions(failureOptions);

                t.Start();
                foreach (var kvp in _originalTypesMap)
                {
                    var inst = _doc.GetElement(kvp.Key);
                    if (inst != null && inst.IsValidObject)
                        inst.ChangeTypeId(kvp.Value);
                }
                foreach (var kvp in _originalSetpoints)
                {
                    var space = _doc.GetElement(kvp.Key);
                    if (space != null && space.IsValidObject)
                    {
                        var hParam = space.get_Parameter(BuiltInParameter.SPACE_HEATING_SET_POINT);
                        var cParam = space.get_Parameter(BuiltInParameter.SPACE_COOLING_SET_POINT);
                        var infParam = space.get_Parameter(BuiltInParameter.SPACE_INFILTRATION_PARAM);
                        var pplParam = space.get_Parameter(BuiltInParameter.SPACE_NUMBER_OF_PEOPLE);
                        var calcParam = space.get_Parameter(BuiltInParameter.SPACE_PEOPLE_LOAD_PARAM);
                        var condParam = space.get_Parameter(BuiltInParameter.ROOM_CONDITION_TYPE_PARAM);
                        if (calcParam != null && !calcParam.IsReadOnly && kvp.Value.CalcMode.HasValue) calcParam.Set(kvp.Value.CalcMode.Value);
                        if (condParam != null && !condParam.IsReadOnly && kvp.Value.ConditionType.HasValue) condParam.Set(kvp.Value.ConditionType.Value);
                        if (hParam != null && !hParam.IsReadOnly && kvp.Value.Heating.HasValue) hParam.Set(kvp.Value.Heating.Value);
                        if (cParam != null && !cParam.IsReadOnly && kvp.Value.Cooling.HasValue) cParam.Set(kvp.Value.Cooling.Value);
                        if (infParam != null && !infParam.IsReadOnly && kvp.Value.Infiltration.HasValue) infParam.Set(kvp.Value.Infiltration.Value);
                        if (pplParam != null && !pplParam.IsReadOnly && kvp.Value.People.HasValue) pplParam.Set(kvp.Value.People.Value);
                    }
                }
                foreach (var id in _tempElementsToDelete) { try { _doc.Delete(id); } catch { } }
                _tempElementsToDelete.Clear();
                _doc.Regenerate();
                t.Commit();
            }
            _originalTypesMap.Clear();
            _originalSetpoints.Clear();
        }

        struct ThermalData
        {
            public double Conductivity; public double SpecificHeat; public double Density;
            public double Emissivity; public double Permeability; public double Porosity;
            public double Reflectivity; public double ElectricalResistivity;
        }


        private void ApplyMonolithic(WallType target, string studName, string insName, double framingFactorPercent, List<Wall> instances)
        {
            var studType = new FilteredElementCollector(_doc).OfClass(typeof(WallType)).Cast<WallType>().FirstOrDefault(t => t.Name == studName);
            var insType = new FilteredElementCollector(_doc).OfClass(typeof(WallType)).Cast<WallType>().FirstOrDefault(t => t.Name == insName);
            if (studType == null || insType == null) return;
            double thickness = target.Width;
            var sData = ExtractThermalData(studType);
            var iData = ExtractThermalData(insType);
            var outData = Blend(sData, iData, framingFactorPercent / 100.0);
            CreateAndApplyWallType(target, outData, thickness, instances);
        }

        private void ApplyEffectiveRValue(WallType target, WallModConfig cfg, List<Wall> instances)
        {
            // Parallel-path framing factor method (IP units: R in ft²·°F·h/BTU)
            double uff = cfg.FramingFactor / 100.0;
            double uStud = 1.0 / cfg.StudRValue;
            double uIns = 1.0 / cfg.InsulationRValue;
            double uClear = (uff * uStud) + ((1 - uff) * uIns);
            double effectiveR_IP = 1.0 / uClear; // ft²·°F·h/BTU

            // Convert effective R from IP to SI: 1 ft²·°F·h/BTU = 0.17611 m²·K/W
            double effectiveR_SI = effectiveR_IP * 0.17611;

            // Wall thickness in Revit internal units (feet) → convert to metres
            double thickness_ft = target.Width;
            double thickness_m = thickness_ft * 0.3048;

            // k (W/m·K) = thickness_m / R_SI
            // NOTE: CreateAndApplyWallType stores Conductivity as W/m·K directly
            // (it converts to Revit-internal via /0.1761101838 in ThermalAsset)
            // So we must NOT pre-convert here; pass the SI value directly.
            double k_SI = thickness_m / effectiveR_SI;

            var outData = new ThermalData
            {
                Conductivity = k_SI,
                Density = cfg.Density,
                SpecificHeat = cfg.SpecificHeat,
                Emissivity = 0.9, Permeability = 0, Porosity = 0.1, Reflectivity = 0.1, ElectricalResistivity = 0
            };

            CreateAndApplyWallType(target, outData, thickness_ft, instances);
        }

        private void CreateAndApplyWallType(WallType target, ThermalData outData, double thickness, List<Wall> instances)
        {
            string kStr = Math.Round(outData.Conductivity, 4).ToString();
            string name = $"{target.Name} - Mono (k={kStr})";

            var newType = new FilteredElementCollector(_doc).OfClass(typeof(WallType))
                .Cast<WallType>().FirstOrDefault(t => t.Name == name)
                ?? (WallType)target.Duplicate(name);

            string uid = Guid.NewGuid().ToString();
            var matId = Material.Create(_doc, $"Mono-k{kStr} ({uid.Substring(0, 5)})");
            var mat = _doc.GetElement(matId) as Material;
            _tempElementsToDelete.Add(matId);
            if (mat != null)
            {
                var ta = new ThermalAsset($"TA_{uid}", ThermalMaterialType.Solid)
                {
                    // outData.Conductivity is in W/(m·K). Revit ThermalConductivity internal unit
                    // is W/(m·K) as well — no conversion needed. The 0.1761101838 factor was
                    // previously used to convert IP BTU·in/(hr·ft²·°F) → W/(m·K), which was wrong.
                    ThermalConductivity = outData.Conductivity,
                    Density = outData.Density * 16.01846,
                    SpecificHeat = outData.SpecificHeat * 4186.8,
                    Emissivity = outData.Emissivity,
                    Permeability = outData.Permeability,
                    Porosity = outData.Porosity,
                    Reflectivity = outData.Reflectivity,
                    ElectricalResistivity = outData.ElectricalResistivity
                };
                var pse = PropertySetElement.Create(_doc, ta);
                _tempElementsToDelete.Add(pse.Id);
                mat.SetMaterialAspectByPropertySet(MaterialAspect.Thermal, pse.Id);
            }
            var cs = CompoundStructure.CreateSingleLayerCompoundStructure(MaterialFunctionAssignment.Structure, thickness, matId);
            newType.SetCompoundStructure(cs);
            _tempElementsToDelete.Add(newType.Id);

            foreach (var w in instances) w.WallType = newType;
        }

        private ThermalData ExtractThermalData(WallType wt)
        {
            var d = new ThermalData { Conductivity = 0.05, SpecificHeat = 0.2, Density = 10.0, Emissivity = 0.9, Permeability = 0, Porosity = 0.1, Reflectivity = 0.1, ElectricalResistivity = 0 };
            var cs = wt.GetCompoundStructure();
            if (cs == null) return d;
            int idx = cs.StructuralMaterialIndex;
            if (idx < 0 && cs.LayerCount > 0) idx = 0;
            if (idx < 0) return d;
            var mat = _doc.GetElement(cs.GetMaterialId(idx)) as Material;
            if (mat != null && mat.ThermalAssetId != ElementId.InvalidElementId)
            {
                var pse = _doc.GetElement(mat.ThermalAssetId) as PropertySetElement;
                if (pse != null)
                {
                    var ta = pse.GetThermalAsset();
                    // Revit stores ThermalConductivity in W/(m·K) internally
                    d.Conductivity = ta.ThermalConductivity;
                    d.Density = ta.Density / 16.01846;
                    d.SpecificHeat = ta.SpecificHeat / 4186.8;
                    d.Emissivity = ta.Emissivity;
                    d.Permeability = ta.Permeability;
                    d.Porosity = ta.Porosity;
                    d.Reflectivity = ta.Reflectivity;
                    d.ElectricalResistivity = ta.ElectricalResistivity;
                }
            }
            return d;
        }

        private static ThermalData Blend(ThermalData s, ThermalData ins, double ffDec)
        {
            double m1 = ffDec * s.Density, m2 = (1 - ffDec) * ins.Density;
            return new ThermalData
            {
                Conductivity = ffDec * s.Conductivity + (1 - ffDec) * ins.Conductivity,
                Density = ffDec * s.Density + (1 - ffDec) * ins.Density,
                SpecificHeat = (m1 + m2) > 0 ? (m1 * s.SpecificHeat + m2 * ins.SpecificHeat) / (m1 + m2) : s.SpecificHeat,
                Emissivity = ffDec * s.Emissivity + (1 - ffDec) * ins.Emissivity,
                Permeability = ffDec * s.Permeability + (1 - ffDec) * ins.Permeability,
                Porosity = ffDec * s.Porosity + (1 - ffDec) * ins.Porosity,
                Reflectivity = ffDec * s.Reflectivity + (1 - ffDec) * ins.Reflectivity,
                ElectricalResistivity = ffDec * s.ElectricalResistivity + (1 - ffDec) * ins.ElectricalResistivity
            };
        }

        private class WarningSwallower : IFailuresPreprocessor
        {
            public bool HasError { get; private set; } = false;

            public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
            {
                IList<FailureMessageAccessor> failures = a.GetFailureMessages();
                if (failures.Count == 0) return FailureProcessingResult.Continue;

                foreach (FailureMessageAccessor f in failures)
                {
                    FailureSeverity severity = f.GetSeverity();
                    string desc = f.GetDescriptionText();

                    if (severity == FailureSeverity.Warning)
                    {
                        if (desc.IndexOf("trimmer loop", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            desc.IndexOf("perimeter zone", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            HasError = true;
                            return FailureProcessingResult.ProceedWithRollBack;
                        }
                        a.DeleteWarning(f);
                    }
                    else if (severity == FailureSeverity.Error || severity == FailureSeverity.DocumentCorruption)
                    {
                        HasError = true;
                        return FailureProcessingResult.ProceedWithRollBack;
                    }
                }

                return FailureProcessingResult.Continue;
            }
        }

        public List<MaterialReferenceData> GetBuildingMaterials()
        {
            var list = new List<MaterialReferenceData>();
            var allHostTypes = new FilteredElementCollector(_doc)
                .OfClass(typeof(HostObjAttributes))
                .Cast<HostObjAttributes>();

            foreach (var ht in allHostTypes)
            {
                string catName = ht.Category?.Name ?? "HostObj";
                double rValue = ComputeRValue_SI(ht);
                double thickness = 0;
                var cs = ht.GetCompoundStructure();
                if (cs != null)
                {
                    for (int i = 0; i < cs.LayerCount; i++) thickness += cs.GetLayerWidth(i);
                }
                
                list.Add(new MaterialReferenceData {
                    Category = catName,
                    Name = ht.Name,
                    ThicknessFt = thickness,
                    RValueSI = rValue,
                    UValueSI = rValue > 0 ? 1.0 / rValue : 0
                });
            }

            var allWindowTypes = new FilteredElementCollector(_doc)
                .OfCategory(BuiltInCategory.OST_Windows)
                .WhereElementIsElementType();
            foreach (var w in allWindowTypes)
            {
                list.Add(new MaterialReferenceData {
                    Category = "Windows",
                    Name = w.Name,
                    ThicknessFt = 0,
                    RValueSI = 0,
                    UValueSI = 0
                });
            }

            var allDoorTypes = new FilteredElementCollector(_doc)
                .OfCategory(BuiltInCategory.OST_Doors)
                .WhereElementIsElementType();
            foreach (var d in allDoorTypes)
            {
                list.Add(new MaterialReferenceData {
                    Category = "Doors",
                    Name = d.Name,
                    ThicknessFt = 0,
                    RValueSI = 0,
                    UValueSI = 0
                });
            }

            return list;
        }
    }

    public class MaterialReferenceData
    {
        public string Category { get; set; }
        public string Name { get; set; }
        public double ThicknessFt { get; set; }
        public double RValueSI { get; set; }
        public double UValueSI { get; set; }
    }
}
