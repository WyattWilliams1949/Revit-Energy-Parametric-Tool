using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using OfficeOpenXml;
using OfficeOpenXml.Drawing.Chart;
using OfficeOpenXml.ConditionalFormatting;
using RevitAddin;

public static class ExcelExporter
{
    // ── Cipher format ─────────────────────────────────────────────────────────

    public static string BuildCipher(
        IEnumerable<SimulationVariable> variables)
    {
        var parts = new List<string>();
        foreach (var v in variables)
        {
            if (v.State == VariableState.NotIncluded) continue;
            string data = v.Method switch
            {
                VariableMethod.MinMaxInterval =>
                    $"{v.Min},{v.Max},{v.Interval},{(v.IsIntervalCount ? 1 : 0)}",
                VariableMethod.Array =>
                    string.Join(",", v.ArrayValues),
                VariableMethod.Equation =>
                    (v.EquationString ?? "").Replace("|", "\u2016"),
                VariableMethod.TypeSelection =>
                    (v.IncludeOriginalType ? "incl:" : "") +
                    string.Join("\u2016", v.SelectedRevitTypes),
                VariableMethod.ReplaceElements =>
                    string.Join("\u2016", v.SelectedStudTypes) + "|||" + 
                    string.Join("\u2016", v.SelectedInsulationTypes) + $"|||{v.EffFramingFactor}",
                VariableMethod.Monolithic =>
                    string.Join("\u2016", v.SelectedStudTypes) + "|||" + 
                    string.Join("\u2016", v.SelectedInsulationTypes) + $"|||{v.EffFramingFactor}",
                VariableMethod.EffectiveRValue =>
                    $"{v.EffStudRValue},{v.EffInsulationRValue},{v.EffWindowRValue},{v.EffDoorRValue},{v.EffFramingFactor},{v.EffDensity},{v.EffSpecificHeat}",
                _ => ""
            };
            // Escape the variable name (no pipes)
            string safeName = v.Name.Replace("|", "\u2016");
            
            var extras = new Dictionary<string, object>
            {
                { "ConstantValue", v.ConstantValue },
                { "UseDefaultMetabolicHeat", v.UseDefaultMetabolicHeat },
                { "CustomMetabolicHeat", v.CustomMetabolicHeat },
                { "IsCustomSchedule", v.IsCustomSchedule },
                { "SelectedScheduleDefault", v.SelectedScheduleDefault },
                { "WeekdayOccupancy", v.WeekdayOccupancy.Select(h => h.Value).ToList() },
                { "WeekendOccupancy", v.WeekendOccupancy.Select(h => h.Value).ToList() },
                { "WeekdayLighting", v.WeekdayLighting.Select(h => h.Value).ToList() },
                { "WeekendLighting", v.WeekendLighting.Select(h => h.Value).ToList() },
                { "WeekdayHeating", v.WeekdayHeating.Select(h => h.Value).ToList() },
                { "WeekendHeating", v.WeekendHeating.Select(h => h.Value).ToList() },
                { "WeekdayCooling", v.WeekdayCooling.Select(h => h.Value).ToList() },
                { "WeekendCooling", v.WeekendCooling.Select(h => h.Value).ToList() },
                { "VaryRValueWithTemp", v.VaryRValueWithTemp },
                { "RValueTempEquation", v.RValueTempEquation },
                { "RValueTempEquationUnit", v.RValueTempEquationUnit },
                { "WinterMinTemp", v.WinterMinTemp },
                { "WinterMaxTemp", v.WinterMaxTemp },
                { "SummerMinTemp", v.SummerMinTemp },
                { "SummerMaxTemp", v.SummerMaxTemp }
            };
            string extrasJson = System.Text.Json.JsonSerializer.Serialize(extras);
            
            parts.Add($"EPTV1|{safeName}|{v.Property}|{v.State}|{v.Method}|{v.SelectedUnit ?? ""}|{data}|{extrasJson}");
        }
        return string.Join(";", parts);
    }

    public static List<(string name, SimulationVariable variable)> ParseCipher(string cipher)
    {
        var result = new List<(string, SimulationVariable)>();
        if (string.IsNullOrWhiteSpace(cipher)) return result;

        foreach (var record in System.Text.RegularExpressions.Regex.Split(cipher, @";(?=EPTV1\|)"))
        {
            if (string.IsNullOrWhiteSpace(record)) continue;
            var f = record.Split(new char[] { '|' }, 8);
            if (f.Length < 7 || f[0] != "EPTV1") continue;

            string name = f[1].Replace("\u2016", "|");
            if (!Enum.TryParse(f[2], out TargetProperty prop)) continue;
            if (!Enum.TryParse(f[3], out VariableState state)) continue;
            if (!Enum.TryParse(f[4], out VariableMethod method)) continue;
            string unit = f[5];
            string data = f[6];

            VariableCategory category = VariableCategory.Envelope;
            if (prop.ToString().StartsWith("Weather")) category = VariableCategory.Weather;
            else if (prop == TargetProperty.HeatingSetpoint || prop == TargetProperty.CoolingSetpoint || prop == TargetProperty.Infiltration || prop == TargetProperty.PeopleCount) category = VariableCategory.Space;
            
            var v = new SimulationVariable(category)
            {
                Name = name,
                Property = prop,
                State = state,
                Method = method
            };
            if (!string.IsNullOrEmpty(unit) && v.AvailableUnits.Contains(unit))
                v.SelectedUnit = unit;

            switch (method)
            {
                case VariableMethod.MinMaxInterval:
                    var mmi = data.Split(',');
                    if (mmi.Length >= 4)
                    {
                        double.TryParse(mmi[0], out double mn); v.Min = mn;
                        double.TryParse(mmi[1], out double mx); v.Max = mx;
                        double.TryParse(mmi[2], out double itv); v.Interval = itv;
                        v.IsIntervalCount = mmi[3] == "1";
                    }
                    break;
                case VariableMethod.Array:
                    v.ArrayValuesString = data;
                    break;
                case VariableMethod.Equation:
                    v.EquationString = data.Replace("\u2016", "|");
                    break;
                case VariableMethod.TypeSelection:
                    if (data.StartsWith("incl:")) { v.IncludeOriginalType = true; data = data.Substring(5); }
                    foreach (var t in data.Split('\u2016')) if (!string.IsNullOrEmpty(t)) v.SelectedRevitTypes.Add(t);
                    break;
                case VariableMethod.ReplaceElements:
                case VariableMethod.Monolithic:
                    var rParts = data.Split(new string[] { "|||" }, StringSplitOptions.None);
                    if (rParts.Length >= 2)
                    {
                        foreach (var t in rParts[0].Split('\u2016')) if (!string.IsNullOrEmpty(t)) v.SelectedStudTypes.Add(t);
                        foreach (var t in rParts[1].Split('\u2016')) if (!string.IsNullOrEmpty(t)) v.SelectedInsulationTypes.Add(t);
                        if (rParts.Length >= 3 && double.TryParse(rParts[2], out double ff)) v.EffFramingFactor = ff;
                    }
                    break;
                case VariableMethod.EffectiveRValue:
                    var eParts = data.Split(',');
                    if (eParts.Length >= 7)
                    {
                        if (double.TryParse(eParts[0], out double e0)) v.EffStudRValue = e0;
                        if (double.TryParse(eParts[1], out double e1)) v.EffInsulationRValue = e1;
                        if (double.TryParse(eParts[2], out double e2)) v.EffWindowRValue = e2;
                        if (double.TryParse(eParts[3], out double e3)) v.EffDoorRValue = e3;
                        if (double.TryParse(eParts[4], out double e4)) v.EffFramingFactor = e4;
                        if (double.TryParse(eParts[5], out double e5)) v.EffDensity = e5;
                        if (double.TryParse(eParts[6], out double e6)) v.EffSpecificHeat = e6;
                    }
                    break;
            }
            
            if (f.Length >= 8)
            {
                try
                {
                    string extrasJson = f[7];
                    var extras = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(extrasJson);
                    if (extras != null)
                    {
                        if (extras.TryGetValue("ConstantValue", out var cv)) { try { v.ConstantValue = cv.GetDouble(); } catch { if(double.TryParse(cv.GetString(), out double d)) v.ConstantValue = d; } }
                        if (extras.TryGetValue("UseDefaultMetabolicHeat", out var umh)) v.UseDefaultMetabolicHeat = umh.GetBoolean();
                        if (extras.TryGetValue("CustomMetabolicHeat", out var cmh)) v.CustomMetabolicHeat = cmh.GetDouble();
                        if (extras.TryGetValue("IsCustomSchedule", out var ics)) v.IsCustomSchedule = ics.GetBoolean();
                        if (extras.TryGetValue("SelectedScheduleDefault", out var ssd)) v.SelectedScheduleDefault = ssd.GetString();
                        
                        Action<System.Text.Json.JsonElement, System.Collections.ObjectModel.ObservableCollection<ProfileHour>> loadArray = (el, coll) => {
                            if (el.ValueKind == System.Text.Json.JsonValueKind.Array)
                            {
                                int idx = 0;
                                foreach (var item in el.EnumerateArray())
                                {
                                    if (idx < coll.Count) coll[idx].Value = item.GetDouble();
                                    idx++;
                                }
                            }
                        };
                        
                        if (extras.TryGetValue("WeekdayOccupancy", out var el1)) loadArray(el1, v.WeekdayOccupancy);
                        if (extras.TryGetValue("WeekendOccupancy", out var el2)) loadArray(el2, v.WeekendOccupancy);
                        if (extras.TryGetValue("WeekdayLighting", out var el3)) loadArray(el3, v.WeekdayLighting);
                        if (extras.TryGetValue("WeekendLighting", out var el4)) loadArray(el4, v.WeekendLighting);
                        if (extras.TryGetValue("WeekdayHeating", out var el5)) loadArray(el5, v.WeekdayHeating);
                        if (extras.TryGetValue("WeekendHeating", out var el6)) loadArray(el6, v.WeekendHeating);
                        if (extras.TryGetValue("WeekdayCooling", out var el7)) loadArray(el7, v.WeekdayCooling);
                        if (extras.TryGetValue("WeekendCooling", out var el8)) loadArray(el8, v.WeekendCooling);
                        
                        if (extras.TryGetValue("VaryRValueWithTemp", out var vr)) v.VaryRValueWithTemp = vr.GetBoolean();
                        if (extras.TryGetValue("RValueTempEquation", out var re)) v.RValueTempEquation = re.GetString();
                        if (extras.TryGetValue("RValueTempEquationUnit", out var ru)) v.RValueTempEquationUnit = ru.GetString();
                    }
                }
                catch { }
            }

            result.Add((name, v));
        }
        return result;
    }

    // ── Excel export ──────────────────────────────────────────────────────────

    public static string ExportData(
        string docName,
        string downloadPath,
        List<string> warnings,
        List<Dictionary<string, object>> scenarios,
        List<SimulationResult> results,
        Dictionary<string, string> units,
        Dictionary<string, TargetProperty> variableProperties = null,
        IEnumerable<SimulationVariable> activeVariables = null,
        string weatherFileName = null,
        List<MaterialReferenceData> materialReferences = null)
    {
        ExcelPackage.License.SetNonCommercialPersonal("Revit User");

        string dateStr = DateTime.Now.ToString("yyyy-MM-dd");
        string timeStr = DateTime.Now.ToString("HH-mm-ss");
        string fileName = $"{docName}_variableAnalisis_{dateStr}_{timeStr}.xlsx";
        string fullPath = System.IO.Path.Combine(downloadPath, fileName);

        if (scenarios.Count == 0) return null;
        HashSet<string> allKeys = new HashSet<string>();
        foreach (var dict in scenarios)
        {
            foreach (var key in dict.Keys)
            {
                allKeys.Add(key);
            }
        }
        List<string> variableNames = allKeys.ToList();

        try
        {
            using (var package = new ExcelPackage(new System.IO.FileInfo(fullPath)))
            {
                // ── Tab 1: Summary Data ───────────────────────────────────────────────────
                var wsProtected = package.Workbook.Worksheets.Add("Summary");

                wsProtected.Cells["A1"].Value = $"{docName}_variableAnalisis";
                wsProtected.Cells["A2"].Value = dateStr;
                wsProtected.Cells["B2"].Value = timeStr;
                wsProtected.Cells["A5"].Value = warnings.Count > 0
                    ? string.Join(" | ", warnings)
                    : "No Warnings";

                int headerRow   = 7;
                int unitsRow    = 8;
                int dataStartRow = 9;

                wsProtected.Cells[headerRow, 1].Value = "Index";
                for (int i = 0; i < variableNames.Count; i++)
                    wsProtected.Cells[headerRow, i + 2].Value = CleanHeader(variableNames[i], variableProperties);
                wsProtected.Cells[headerRow, variableNames.Count + 2].Value = "Average BTU/hour";
                wsProtected.Cells[headerRow, variableNames.Count + 3].Value = "Peak BTU/hour";

                var headerRange = wsProtected.Cells[headerRow, 1, headerRow, variableNames.Count + 3];
                headerRange.Style.Font.Bold = true;
                headerRange.Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
                headerRange.Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.FromArgb(43, 87, 154)); // #2B579A
                headerRange.Style.Font.Color.SetColor(System.Drawing.Color.White);

                wsProtected.Cells[unitsRow, 1].Value = "Units";
                for (int i = 0; i < variableNames.Count; i++)
                    wsProtected.Cells[unitsRow, i + 2].Value = units != null && units.ContainsKey(variableNames[i]) ? units[variableNames[i]] : "";
                wsProtected.Cells[unitsRow, variableNames.Count + 2].Value = "BTU/h";
                wsProtected.Cells[unitsRow, variableNames.Count + 3].Value = "BTU/h";

                for (int i = 0; i < scenarios.Count; i++)
                {
                    int row = dataStartRow + i;
                    wsProtected.Cells[row, 1].Value = i + 1;
                    for (int j = 0; j < variableNames.Count; j++)
                    {
                        if (scenarios[i].TryGetValue(variableNames[j], out var val))
                        {
                            if (val is double dVal)
                                wsProtected.Cells[row, j + 2].Value = double.IsInfinity(dVal) || double.IsNaN(dVal) ? "ERROR" : dVal;
                            else
                                wsProtected.Cells[row, j + 2].Value = val?.ToString() ?? "";
                        }
                        else
                        {
                            wsProtected.Cells[row, j + 2].Value = "N/A";
                        }
                    }
                    wsProtected.Cells[row, variableNames.Count + 2].Value = results[i].AverageBtu;
                    wsProtected.Cells[row, variableNames.Count + 3].Value = results[i].PeakBtu;

                    if (row % 2 == 0)
                    {
                        var rowRange = wsProtected.Cells[row, 1, row, variableNames.Count + 3];
                        rowRange.Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
                        rowRange.Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.FromArgb(238, 242, 250)); // #EEF2FA
                    }
                }
                wsProtected.Cells[wsProtected.Dimension.Address].AutoFitColumns();
                wsProtected.Protection.IsProtected = true;

                // ── Tab 2: Summary (Editable) ─────────────────────────────────────────────
                var wsEditable = package.Workbook.Worksheets.Add("Summary (Editable)", wsProtected);
                wsEditable.Protection.IsProtected = false;

                // Gather unique rooms for next tabs
                var allRooms = new HashSet<string>();
                foreach(var r in results) {
                    if (r.RoomData != null) {
                        foreach(var kvp in r.RoomData) {
                            if (kvp.Key.IndexOf("UNCONDITIONED", StringComparison.OrdinalIgnoreCase) < 0)
                                allRooms.Add(kvp.Key);
                        }
                    }
                }
                var roomList = allRooms.OrderBy(r => r).ToList();

                // ── Tab 3: Room By room brakedown ─────────────────────────────────────────
                var wsRoom = package.Workbook.Worksheets.Add("Room By room brakedown");
                wsRoom.Cells[1, 1].Value = "Simulation Index";
                wsRoom.Cells[1, 2].Value = "Room Name";
                wsRoom.Cells[1, 3].Value = "Total Heat (BTU/h)";
                wsRoom.Cells[1, 4].Value = "People Heat";
                wsRoom.Cells[1, 5].Value = "Lights Heat";
                wsRoom.Cells[1, 6].Value = "Sun Transmitted";
                wsRoom.Cells[1, 7].Value = "Windows Conduction";
                wsRoom.Cells[1, 8].Value = "Doors Conduction";
                wsRoom.Cells[1, 9].Value = "Walls Conduction";
                wsRoom.Cells[1, 10].Value = "Ceilings Conduction";
                wsRoom.Cells[1, 11].Value = "Floors Conduction";

                int rr = 2;
                for (int i = 0; i < scenarios.Count; i++) {
                    if (results[i].RoomData != null) {
                        foreach (var kvp in results[i].RoomData) {
                            if (kvp.Key.IndexOf("UNCONDITIONED", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                            var rd = kvp.Value;
                            wsRoom.Cells[rr, 1].Value = i + 1;
                            wsRoom.Cells[rr, 2].Value = kvp.Key;
                            wsRoom.Cells[rr, 3].Value = rd.PeopleHeat + rd.LightsHeat + rd.SunTransmitted + rd.WindowsConduction + rd.DoorsConduction + rd.WallsConduction + rd.CeilingsConduction + rd.FloorsConduction;
                            wsRoom.Cells[rr, 4].Value = rd.PeopleHeat;
                            wsRoom.Cells[rr, 5].Value = rd.LightsHeat;
                            wsRoom.Cells[rr, 6].Value = rd.SunTransmitted;
                            wsRoom.Cells[rr, 7].Value = rd.WindowsConduction;
                            wsRoom.Cells[rr, 8].Value = rd.DoorsConduction;
                            wsRoom.Cells[rr, 9].Value = rd.WallsConduction;
                            wsRoom.Cells[rr, 10].Value = rd.CeilingsConduction;
                            wsRoom.Cells[rr, 11].Value = rd.FloorsConduction;
                            rr++;
                        }
                    }
                }
                wsRoom.Cells[wsRoom.Dimension.Address].AutoFitColumns();

                // ── Tab 4: Input Variables sheet ─────────────────────────────────────────
                if (activeVariables != null)
                    AddInputVariablesSheet(package, docName, dateStr, timeStr, activeVariables, weatherFileName);

                // ── Tab 5: Building Materials ───────────────────────────────────────────────────
                if (materialReferences != null && materialReferences.Count > 0)
                {
                    var wsMat = package.Workbook.Worksheets.Add("Building Materials");
                    wsMat.Cells[1, 1].Value = "Category";
                    wsMat.Cells[1, 2].Value = "Type Name";
                    wsMat.Cells[1, 3].Value = "Thickness (in)";
                    wsMat.Cells[1, 4].Value = "R-Value (h·ft²·°F/Btu)";
                    wsMat.Cells[1, 5].Value = "U-Value (Btu/h·ft²·°F)";

                    var headRange = wsMat.Cells[1, 1, 1, 5];
                    headRange.Style.Font.Bold = true;
                    headRange.Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
                    headRange.Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.FromArgb(43, 87, 154));
                    headRange.Style.Font.Color.SetColor(System.Drawing.Color.White);

                    int rMat = 2;
                    foreach (var mat in materialReferences.OrderBy(m => m.Category).ThenBy(m => m.Name))
                    {
                        wsMat.Cells[rMat, 1].Value = mat.Category;
                        wsMat.Cells[rMat, 2].Value = mat.Name;
                        wsMat.Cells[rMat, 3].Value = mat.ThicknessFt > 0 ? Math.Round(mat.ThicknessFt * 12.0, 2) : 0;
                        wsMat.Cells[rMat, 4].Value = mat.RValueSI > 0 ? Math.Round(mat.RValueSI * 5.678263337, 2) : 0;
                        wsMat.Cells[rMat, 5].Value = mat.UValueSI > 0 ? Math.Round(mat.UValueSI / 5.678263337, 3) : 0;

                        if (rMat % 2 == 0)
                        {
                            var rowRange = wsMat.Cells[rMat, 1, rMat, 5];
                            rowRange.Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
                            rowRange.Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.FromArgb(238, 242, 250));
                        }
                        rMat++;
                    }

                    wsMat.Cells[wsMat.Dimension.Address].AutoFitColumns();
                }

                // ── Tab 2: Energy loss per simulation (Bar Chart) ───────────────────────────────────────────────────
                if (scenarios.Count > 0)
                {
                    var wsSimHeat = package.Workbook.Worksheets.Add("1. Energy Loss per Sim");
                    var simHeatChart = wsSimHeat.Drawings.AddChart("SimHeatChart", eChartType.ColumnClustered);
                    simHeatChart.Title.Text = "Total Energy Loss per Simulation (Average BTU/h)";
                    simHeatChart.SetPosition(1, 0, 1, 0);
                    simHeatChart.SetSize(1200, 600);
                    
                    var xAddressSim = wsProtected.Cells[dataStartRow, 1, dataStartRow + scenarios.Count - 1, 1];
                    var yAddressSim = wsProtected.Cells[dataStartRow, variableNames.Count + 2, dataStartRow + scenarios.Count - 1, variableNames.Count + 2];
                    var seriesSim = simHeatChart.Series.Add(yAddressSim.FullAddress, xAddressSim.FullAddress);
                    seriesSim.Header = "Average BTU/hour";

                    simHeatChart.XAxis.Title.Text = "Simulation Index";
                    simHeatChart.YAxis.Title.Text = "Average Heat Loss (BTU/h)";
                }

                // ── Tab 3: Energy lost per room per simulation (Heatmap) ───────────────────────────────────────────────────
                if (scenarios.Count > 0 && roomList.Count > 0)
                {
                    var wsHeatmap = package.Workbook.Worksheets.Add("2. Room Heatmap");
                    wsHeatmap.Cells[1, 1].Value = "Room \\ Simulation";

                    for(int i = 0; i < scenarios.Count; i++) {
                        wsHeatmap.Cells[1, i + 2].Value = $"Sim {i + 1}";
                    }
                    
                    double maxAbs = 0.001; // Avoid 0
                    for(int r = 0; r < roomList.Count; r++) {
                        wsHeatmap.Cells[r + 2, 1].Value = roomList[r];
                        for(int i = 0; i < scenarios.Count; i++) {
                            var roomData = results[i].RoomData;
                            if(roomData != null && roomData.TryGetValue(roomList[r], out var rd)) {
                                double totalHeat = rd.PeopleHeat + rd.LightsHeat + rd.SunTransmitted + rd.WindowsConduction + rd.DoorsConduction + rd.WallsConduction + rd.CeilingsConduction + rd.FloorsConduction;
                                wsHeatmap.Cells[r + 2, i + 2].Value = totalHeat;
                                maxAbs = Math.Max(maxAbs, Math.Abs(totalHeat));
                            } else {
                                wsHeatmap.Cells[r + 2, i + 2].Value = 0;
                            }
                        }
                    }
                    
                    var heatmapRange = wsHeatmap.Cells[2, 2, roomList.Count + 1, scenarios.Count + 1];
                    var cf = wsHeatmap.ConditionalFormatting.AddThreeColorScale(heatmapRange);
                    
                    cf.LowValue.Type = eExcelConditionalFormattingValueObjectType.Num;
                    cf.LowValue.Value = -maxAbs;
                    cf.LowValue.Color = System.Drawing.Color.FromArgb(248, 105, 107); // Red
                    
                    cf.MiddleValue.Type = eExcelConditionalFormattingValueObjectType.Num;
                    cf.MiddleValue.Value = 0;
                    cf.MiddleValue.Color = System.Drawing.Color.FromArgb(99, 190, 123); // Green
                    
                    cf.HighValue.Type = eExcelConditionalFormattingValueObjectType.Num;
                    cf.HighValue.Value = maxAbs;
                    cf.HighValue.Color = System.Drawing.Color.FromArgb(248, 105, 107); // Red
                    
                    wsHeatmap.Cells[wsHeatmap.Dimension.Address].AutoFitColumns();
                }

                // ── Tab 4: Energy lost per component per room (Stacked Bar Chart) ───────────────────────────────────────────────────
                if (scenarios.Count > 0 && roomList.Count > 0)
                {
                    var wsComp = package.Workbook.Worksheets.Add("3. Component Breakdown");
                    
                    wsComp.Cells["D2"].Value = "Select Simulation:";
                    wsComp.Cells["D2"].Style.Font.Bold = true;
                    wsComp.Cells["D2"].Style.HorizontalAlignment = OfficeOpenXml.Style.ExcelHorizontalAlignment.Right;
                    
                    var dropdownCell = wsComp.Cells["E2"];
                    dropdownCell.Value = "Sim 1";
                    dropdownCell.Style.Font.Bold = true;
                    dropdownCell.Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
                    dropdownCell.Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.FromArgb(255, 255, 204));
                    dropdownCell.Style.Border.BorderAround(OfficeOpenXml.Style.ExcelBorderStyle.Medium);
                    
                    wsComp.Cells["F2"].Value = "<- Select Here";
                    wsComp.Cells["F2"].Style.Font.Italic = true;
                    wsComp.Cells["F2"].Style.Font.Color.SetColor(System.Drawing.Color.Gray);
                    
                    string startCol = GetExcelColumnName(10);
                    string endCol = GetExcelColumnName(9 + (scenarios.Count * 8));
                    string absHeaderRange = $"${startCol}$1:${endCol}$1";
                    
                    for (int i = 0; i < scenarios.Count; i++) {
                        wsComp.Cells[2, 10 + i].Value = $"Sim {i + 1}";
                    }
                    string valListRange = $"${startCol}$2:${GetExcelColumnName(9 + scenarios.Count)}$2";
                    
                    var val = wsComp.DataValidations.AddListValidation("E2");
                    val.Formula.ExcelFormula = valListRange;
                    
                    wsComp.Cells[1, 1].Value = "Room";
                    wsComp.Cells[1, 2].Value = "People";
                    wsComp.Cells[1, 3].Value = "Lights";
                    wsComp.Cells[1, 4].Value = "Sun/Windows Trans.";
                    wsComp.Cells[1, 5].Value = "Windows Cond.";
                    wsComp.Cells[1, 6].Value = "Doors Cond.";
                    wsComp.Cells[1, 7].Value = "Walls Cond.";
                    wsComp.Cells[1, 8].Value = "Ceilings Cond.";
                    wsComp.Cells[1, 9].Value = "Floors Cond.";
                    
                    for (int i = 0; i < scenarios.Count; i++) {
                        for (int c = 0; c < 8; c++) {
                            wsComp.Cells[1, 10 + (i * 8) + c].Value = $"Sim {i + 1}";
                        }
                    }
                    
                    int row = 4;
                    foreach(var room in roomList) {
                        wsComp.Cells[row, 1].Value = room;
                        
                        for (int i = 0; i < scenarios.Count; i++) {
                            if(results[i].RoomData != null && results[i].RoomData.TryGetValue(room, out var rd)) {
                                wsComp.Cells[row, 10 + (i * 8) + 0].Value = Math.Abs(rd.PeopleHeat);
                                wsComp.Cells[row, 10 + (i * 8) + 1].Value = Math.Abs(rd.LightsHeat);
                                wsComp.Cells[row, 10 + (i * 8) + 2].Value = Math.Abs(rd.SunTransmitted);
                                wsComp.Cells[row, 10 + (i * 8) + 3].Value = Math.Abs(rd.WindowsConduction);
                                wsComp.Cells[row, 10 + (i * 8) + 4].Value = Math.Abs(rd.DoorsConduction);
                                wsComp.Cells[row, 10 + (i * 8) + 5].Value = Math.Abs(rd.WallsConduction);
                                wsComp.Cells[row, 10 + (i * 8) + 6].Value = Math.Abs(rd.CeilingsConduction);
                                wsComp.Cells[row, 10 + (i * 8) + 7].Value = Math.Abs(rd.FloorsConduction);
                            } else {
                                for(int c=0; c<8; c++) wsComp.Cells[row, 10 + (i * 8) + c].Value = 0;
                            }
                        }
                        
                        string dataRowRange = $"${startCol}${row}:${endCol}${row}";
                        for (int c = 0; c < 8; c++) {
                            wsComp.Cells[row, 2 + c].Formula = $"INDEX({dataRowRange}, 1, MATCH($E$2, {absHeaderRange}, 0) + {c})";
                        }
                        row++;
                    }
                    
                    var compChart = wsComp.Drawings.AddChart("CompChart", eChartType.ColumnStacked100);
                    compChart.Title.Text = $"Component Breakdown per Room";
                    compChart.SetPosition(4, 0, 3, 0); 
                    compChart.SetSize(1200, 600);
                    
                    if (compChart is OfficeOpenXml.Drawing.Chart.ExcelBarChart barChart) {
                        barChart.GapWidth = 50;
                    }
                    
                    var xAddress = wsComp.Cells[4, 1, row - 1, 1];
                    for (int col = 2; col <= 9; col++) {
                        var yAddress = wsComp.Cells[4, col, row - 1, col];
                        var series = compChart.Series.Add(yAddress.FullAddress, xAddress.FullAddress);
                        series.Header = wsComp.Cells[1, col].Text;
                    }

                    compChart.XAxis.Title.Text = "Rooms";
                    compChart.YAxis.Title.Text = "Percentage Contribution";
                    
                    for (int c = 10; c <= 9 + (scenarios.Count * 8); c++) {
                        wsComp.Column(c).Hidden = true;
                    }
                    
                    wsComp.Cells[wsComp.Dimension.Address].AutoFitColumns();
                }

                // ── Tab 8: Energy lost per building hierarchy (Sunburst Chart) ───────────────────────────────────────────────────
                if (scenarios.Count > 0 && roomList.Count > 0)
                {
                    var wsTree = package.Workbook.Worksheets.Add("4. Building Hierarchy");
                    wsTree.Cells[3, 1].Value = "Floor";
                    wsTree.Cells[3, 2].Value = "Room";
                    wsTree.Cells[3, 3].Value = "Total Heat";
                    
                    wsTree.Cells["D2"].Value = "Select Simulation:";
                    wsTree.Cells["D2"].Style.Font.Bold = true;
                    wsTree.Cells["D2"].Style.HorizontalAlignment = OfficeOpenXml.Style.ExcelHorizontalAlignment.Right;
                    
                    var dropdownCell = wsTree.Cells["E2"];
                    dropdownCell.Value = "Sim 1";
                    dropdownCell.Style.Font.Bold = true;
                    dropdownCell.Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
                    dropdownCell.Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.FromArgb(255, 255, 204));
                    dropdownCell.Style.Border.BorderAround(OfficeOpenXml.Style.ExcelBorderStyle.Medium);
                    
                    wsTree.Cells["F2"].Value = "<- Select Here";
                    wsTree.Cells["F2"].Style.Font.Italic = true;
                    wsTree.Cells["F2"].Style.Font.Color.SetColor(System.Drawing.Color.Gray);
                    
                    string startCol = GetExcelColumnName(10);
                    string endCol = GetExcelColumnName(9 + scenarios.Count);
                    string absHeaderRange = $"${startCol}$3:${endCol}$3";
                    
                    var val = wsTree.DataValidations.AddListValidation("E2");
                    val.Formula.ExcelFormula = absHeaderRange;
                    
                    for (int i = 0; i < scenarios.Count; i++) {
                        wsTree.Cells[3, 10 + i].Value = $"Sim {i + 1}";
                    }

                    int rTree = 4;
                    var roomsByFloor = new Dictionary<string, List<string>>();
                    foreach(var room in roomList) {
                        string roomNum = "";
                        int firstSpace = room.IndexOf(' ');
                        if (firstSpace > 0) roomNum = room.Substring(0, firstSpace);
                        else roomNum = room;
                        string floor = GetFloorName(roomNum);
                        
                        if(!roomsByFloor.ContainsKey(floor)) roomsByFloor[floor] = new List<string>();
                        roomsByFloor[floor].Add(room);
                    }

                    foreach(var floorKvp in roomsByFloor.OrderBy(f => f.Key)) {
                        foreach(var room in floorKvp.Value) {
                            wsTree.Cells[rTree, 1].Value = floorKvp.Key;
                            wsTree.Cells[rTree, 2].Value = room;
                            
                            for (int i = 0; i < scenarios.Count; i++) {
                                double energy = 0;
                                if(results[i].RoomData != null && results[i].RoomData.TryGetValue(room, out var rd)) {
                                    energy = rd.PeopleHeat + rd.LightsHeat + rd.SunTransmitted + rd.WindowsConduction + rd.DoorsConduction + rd.WallsConduction + rd.CeilingsConduction + rd.FloorsConduction;
                                }
                                wsTree.Cells[rTree, 10 + i].Value = Math.Max(0.01, Math.Abs(energy));
                            }
                            
                            string dataRowRange = $"${startCol}${rTree}:${endCol}${rTree}";
                            wsTree.Cells[rTree, 3].Formula = $"INDEX({dataRowRange}, 1, MATCH($E$2, {absHeaderRange}, 0))";
                            
                            rTree++;
                        }
                    }

                    var treeChart = wsTree.Drawings.AddChart("TreeChart", eChartType.Sunburst);
                    treeChart.Title.Text = "Building Hierarchy Energy Loss - Shows breakdown by Floor and Room";
                    treeChart.SetPosition(4, 0, 3, 0); 
                    treeChart.SetSize(1200, 800);
                    
                    var treeY = wsTree.Cells[4, 3, rTree - 1, 3];
                    var treeX = wsTree.Cells[4, 1, rTree - 1, 2];
                    treeChart.Series.Add(treeY.FullAddress, treeX.FullAddress);

                    for (int c = 10; c <= 9 + scenarios.Count; c++) {
                        wsTree.Column(c).Hidden = true;
                    }

                    wsTree.Cells[wsTree.Dimension.Address].AutoFitColumns();
                }

                package.Save();
                return fullPath;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ExcelExporter Error: {ex.Message}\n{ex.StackTrace}");
            try { System.IO.File.WriteAllText(System.IO.Path.Combine(downloadPath, "excel_error.txt"), ex.ToString()); } catch { }
            return null;
        }
    }

    private static string GetFloorName(string roomNumber)
    {
        if (string.IsNullOrEmpty(roomNumber)) return "Unknown Floor";
        if (roomNumber.StartsWith("0", StringComparison.Ordinal)) return "Basement";
        
        char first = roomNumber[0];
        if (char.IsDigit(first)) return "Floor " + first;
        
        return "Unknown Floor";
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string CleanHeader(string variableName, Dictionary<string, TargetProperty> variableProperties)
    {
        string cleaned = Regex.Replace(variableName, @"\s*\(New Property\s*\d*\)\s*$", "").TrimEnd();

        if (variableProperties != null && variableProperties.TryGetValue(variableName, out var prop))
        {
            if (prop == TargetProperty.RevitType) return cleaned;
            
            if (cleaned.Length < variableName.Length)
            {
                return $"{cleaned} - {prop}";
            }
            
            if (Regex.IsMatch(cleaned, @"\s*\(([^)]+)\)$"))
            {
                return Regex.Replace(cleaned, @"\s*\(([^)]+)\)$", " - $1").TrimEnd();
            }
            
            return $"{cleaned} - {prop}";
        }
        
        return Regex.Replace(cleaned, @"\s*\(([^)]+)\)$", " - $1").TrimEnd();
    }

    private static void AddInputVariablesSheet(
        ExcelPackage package,
        string docName,
        string dateStr,
        string timeStr,
        IEnumerable<SimulationVariable> activeVariables,
        string weatherFileName = null)
    {
        var ws = package.Workbook.Worksheets.Add("Input Variables");

        ws.Cells["A1"].Value = $"{docName} — Input Variable Configuration";
        ws.Cells["A1"].Style.Font.Bold = true;
        ws.Cells["A1"].Style.Font.Size = 14;
        ws.Cells["A2"].Value = $"Generated: {dateStr}  {timeStr.Replace("-", ":")}";
        ws.Cells["A2"].Style.Font.Italic = true;
        
        if (!string.IsNullOrEmpty(weatherFileName))
        {
            ws.Cells["A3"].Value = $"Weather File: {weatherFileName}";
            ws.Cells["A3"].Style.Font.Italic = true;
        }

        int hr = 4;
        string[] headers = { "Variable Name", "Property", "State", "Method", "Unit", "Min", "Max", "Step / Count", "Array Values / Types", "Equation" };
        for (int c = 0; c < headers.Length; c++)
        {
            var cell = ws.Cells[hr, c + 1];
            cell.Value = headers[c];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
            cell.Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.FromArgb(43, 87, 154));
            cell.Style.Font.Color.SetColor(System.Drawing.Color.White);
        }

        int row = hr + 1;
        var varList = activeVariables.Where(v => v.State != VariableState.NotIncluded).ToList();
        foreach (var v in varList)
        {
            ws.Cells[row, 1].Value = Regex.Replace(v.Name, @"\s*\(New Property\s*\d*\)\s*$", "").TrimEnd();
            ws.Cells[row, 2].Value = v.Property.ToString();
            ws.Cells[row, 3].Value = v.State.ToString();
            ws.Cells[row, 4].Value = v.Method.ToString();
            ws.Cells[row, 5].Value = v.SelectedUnit ?? "";

            switch (v.Method)
            {
                case VariableMethod.MinMaxInterval:
                    ws.Cells[row, 6].Value = v.Min;
                    ws.Cells[row, 7].Value = v.Max;
                    ws.Cells[row, 8].Value = v.IsIntervalCount
                        ? $"{v.Interval} points"
                        : $"step {v.Interval}";
                    break;
                case VariableMethod.Array:
                    ws.Cells[row, 9].Value = string.Join(", ", v.ArrayValues);
                    break;
                case VariableMethod.TypeSelection:
                    ws.Cells[row, 9].Value =
                        (v.IncludeOriginalType ? "Original + " : "") +
                        string.Join(", ", v.SelectedRevitTypes);
                    break;
                case VariableMethod.ReplaceElements:
                case VariableMethod.Monolithic:
                    ws.Cells[row, 9].Value = $"Studs: {string.Join(", ", v.SelectedStudTypes)} | Ins: {string.Join(", ", v.SelectedInsulationTypes)}";
                    break;
                case VariableMethod.EffectiveRValue:
                    ws.Cells[row, 9].Value = $"Stud R:{v.EffStudRValue}, Ins R:{v.EffInsulationRValue}";
                    break;
                case VariableMethod.Equation:
                    ws.Cells[row, 10].Value = v.EquationString ?? "";
                    break;
            }

            if (row % 2 == 0)
            {
                var rowRange = ws.Cells[row, 1, row, 10];
                rowRange.Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
                rowRange.Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.FromArgb(238, 242, 250));
            }
            row++;
        }

        row += 2;
        var labelCell = ws.Cells[row, 1];
        labelCell.Value = "Copy for Input:";
        labelCell.Style.Font.Bold = true;
        labelCell.Style.Font.Size = 12;
        ws.Cells[row, 1, row, 10].Merge = true;

        row++;
        string cipher = BuildCipher(varList);
        var cipherCell = ws.Cells[row, 1];
        cipherCell.Value = cipher;
        cipherCell.Style.Font.Name = "Consolas";
        cipherCell.Style.Font.Size = 9;
        cipherCell.Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
        cipherCell.Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.FromArgb(245, 245, 245));
        cipherCell.Style.Border.BorderAround(OfficeOpenXml.Style.ExcelBorderStyle.Thin);
        cipherCell.Style.Locked = false;
        ws.Cells[row, 1, row, 10].Merge = true;
        ws.Row(row).Height = 30;

        ws.Cells[ws.Dimension.Address].AutoFitColumns();
        ws.Protection.IsProtected = true;
    }

    private static string GetExcelColumnName(int columnNumber)
    {
        string columnName = "";
        while (columnNumber > 0)
        {
            int modulo = (columnNumber - 1) % 26;
            columnName = Convert.ToChar('A' + modulo) + columnName;
            columnNumber = (columnNumber - modulo) / 26;
        } 
        return columnName;
    }
}
