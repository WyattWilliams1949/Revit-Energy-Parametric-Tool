using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitAddin;

public partial class ProgressWindow : Window
{
    private CancellationTokenSource _cts = new CancellationTokenSource();
    private bool _savePartialData = false;
    private Document _doc;
    private string _exportPath;
    private string _weatherPath;
    
    private List<RevitAddin.SimulationElement> _activeElements;

    private long _totalSims;
    private System.Collections.Concurrent.ConcurrentDictionary<long, SimulationResult> _resultsMap;
    private string _baseModelsFolder;
    private Dictionary<string, string> _units;
    private List<SimulationResult> _simulationResults = new List<SimulationResult>();
    private List<string> _warnings = new List<string>();

    private Dictionary<string, TargetProperty> _variableProperties;
    private Dictionary<string, ElementId> _variableElements;
    private Dictionary<string, string> _variableElementNames;
    private List<RevitAddin.SimulationVariable> _activeVariables;
    private bool _flattenWeather;

    private string _lastGeomKey = null;
    private string _lastGbxmlPath = null;
    
    private Dictionary<string, HashSet<string>> _typeToInstanceIds = new Dictionary<string, HashSet<string>>();
    private Dictionary<string, string> _currentConstructionRefs = new Dictionary<string, string>();

    private static bool _sqliteNativeLoaded = false;
    private static readonly object _sqliteNativeLock = new object();

    private RevitEventHandler _handler;
    private ExternalEvent _exEvent;

    private List<Dictionary<string, object>> _explicitScenarios;
    private bool _isSensitivityMode;

    private Dictionary<string, double> _cachedRValues = new Dictionary<string, double>();
    private List<MaterialReferenceData> _materialReferences;

    private JobObject _jobObject = new JobObject();

    public ProgressWindow(
        Document doc, 
        string exportPath, 
        List<RevitAddin.SimulationElement> activeElements,
        Dictionary<string, string> units,
        Dictionary<string, TargetProperty> variableProperties,
        Dictionary<string, ElementId> variableElements,
        string weatherPath,
        bool flattenWeather,
        RevitEventHandler handler,
        ExternalEvent exEvent,
        List<RevitAddin.SimulationVariable> activeVariables,
        List<Dictionary<string, object>> explicitScenarios = null)
    {
        InitializeComponent();
        
        _doc = doc;
        _exportPath = exportPath;
        _activeElements = activeElements;
        _units = units;
        _variableProperties = variableProperties;
        _variableElements = variableElements;
        _weatherPath = weatherPath;
        _flattenWeather = flattenWeather;
        _activeVariables = activeVariables;
        _explicitScenarios = explicitScenarios;
        _isSensitivityMode = explicitScenarios != null;
        _handler = handler;
        _exEvent = exEvent;

        _variableElementNames = new Dictionary<string, string>();
        if (_variableElements != null)
        {
            foreach (var kvp in _variableElements)
            {
                var el = _doc.GetElement(kvp.Value);
                _variableElementNames[kvp.Key] = el?.Name ?? "";
            }
        }
        
        var mutator = new MatlabRevitMutator(_doc);
        var allHostTypes = new FilteredElementCollector(_doc).OfClass(typeof(HostObjAttributes)).Cast<HostObjAttributes>();
        foreach (var ht in allHostTypes)
        {
            if (!_cachedRValues.ContainsKey(ht.Name))
            {
                _cachedRValues[ht.Name] = mutator.GetRValueByName(ht.Name);
            }
        }
        
        var rawMaterials = mutator.GetBuildingMaterials();
        var allowedMaterialNames = new HashSet<string>();
        foreach (var id in new FilteredElementCollector(_doc).WhereElementIsNotElementType().Select(e => e.GetTypeId()).Distinct())
        {
            var elem = _doc.GetElement(id) as ElementType;
            if (elem != null) allowedMaterialNames.Add(elem.Name);
        }

        foreach (var scenario in GetScenarios())
        {
            foreach (var val in scenario.Values)
            {
                if (val is string s)
                {
                    allowedMaterialNames.Add(s);
                }
                else if (val is WallModConfig wmc)
                {
                    if (!string.IsNullOrEmpty(wmc.StudType)) allowedMaterialNames.Add(wmc.StudType);
                    if (!string.IsNullOrEmpty(wmc.InsulationType)) allowedMaterialNames.Add(wmc.InsulationType);
                }
            }
        }
        
        _materialReferences = rawMaterials.Where(m => allowedMaterialNames.Contains(m.Name)).ToList();
        
        EnsureSqliteNativeLoaded();
        
        this.Loaded += (s, e) => StartSimulations();
    }

    private static void EnsureSqliteNativeLoaded()
    {
        if (_sqliteNativeLoaded) return;
        lock (_sqliteNativeLock)
        {
            if (_sqliteNativeLoaded) return;
            try
            {
                string addinDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                string nativeDll = Path.Combine(addinDir, "runtimes", "win-x64", "native", "e_sqlite3.dll");
                if (File.Exists(nativeDll)) NativeLibrary.Load(nativeDll);
            }
            catch { }
            SQLitePCL.Batteries_V2.Init();
            _sqliteNativeLoaded = true;
        }
    }

    private void AddWarningUI(string warning)
    {
        Dispatcher.Invoke(() =>
        {
            _warnings.Add(warning);

            var run = new System.Windows.Documents.Run(warning + "\n");
            
            string lowerWarn = warning.ToLower();
            if (lowerWarn.Contains("error") || lowerWarn.Contains("fail") || lowerWarn.Contains("fatal") || lowerWarn.Contains("cancel") || lowerWarn.Contains("skip"))
            {
                run.Foreground = System.Windows.Media.Brushes.Red;
            }
            else if (lowerWarn.Contains("success") || lowerWarn.Contains("stopping early"))
            {
                run.Foreground = System.Windows.Media.Brushes.Green;
            }
            else
            {
                run.Foreground = System.Windows.Media.Brushes.DarkOrange;
            }

            paraWarnings.Inlines.Add(run);
            txtWarnings.ScrollToEnd();
        });
    }

    private IEnumerable<Dictionary<string, object>> GetScenarios()
    {
        if (_explicitScenarios != null) return _explicitScenarios;
        return PermutationEngine.GenerateAllScenarios(_activeElements);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetSystemTimes(out FILETIME lpIdleTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);

    [StructLayout(LayoutKind.Sequential)]
    struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    private FILETIME _prevIdleTime;
    private FILETIME _prevKernelTime;
    private FILETIME _prevUserTime;

    private double GetCpuUsage()
    {
        FILETIME idleTime, kernelTime, userTime;
        if (!GetSystemTimes(out idleTime, out kernelTime, out userTime))
            return 0;

        ulong sysIdle = ((ulong)idleTime.dwHighDateTime << 32) | (uint)idleTime.dwLowDateTime;
        ulong sysKernel = ((ulong)kernelTime.dwHighDateTime << 32) | (uint)kernelTime.dwLowDateTime;
        ulong sysUser = ((ulong)userTime.dwHighDateTime << 32) | (uint)userTime.dwLowDateTime;

        ulong prevIdle = ((ulong)_prevIdleTime.dwHighDateTime << 32) | (uint)_prevIdleTime.dwLowDateTime;
        ulong prevKernel = ((ulong)_prevKernelTime.dwHighDateTime << 32) | (uint)_prevKernelTime.dwLowDateTime;
        ulong prevUser = ((ulong)_prevUserTime.dwHighDateTime << 32) | (uint)_prevUserTime.dwLowDateTime;

        ulong sysIdleDiff = sysIdle - prevIdle;
        ulong sysKernelDiff = sysKernel - prevKernel;
        ulong sysUserDiff = sysUser - prevUser;

        ulong sysTotalDiff = sysKernelDiff + sysUserDiff;

        double cpuUsage = 0;
        if (sysTotalDiff > 0)
        {
            cpuUsage = (sysTotalDiff - sysIdleDiff) * 100.0 / sysTotalDiff;
        }

        _prevIdleTime = idleTime;
        _prevKernelTime = kernelTime;
        _prevUserTime = userTime;

        return cpuUsage;
    }

    private double GetAvailableRamGB()
    {
        MEMORYSTATUSEX memStatus = new MEMORYSTATUSEX();
        memStatus.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
        if (GlobalMemoryStatusEx(ref memStatus))
        {
            return memStatus.ullAvailPhys / (1024.0 * 1024.0 * 1024.0);
        }
        return 0;
    }

    private async void StartSimulations()
    {
        var timer = Stopwatch.StartNew();

        try
        {
            File.WriteAllText(Path.Combine(_exportPath, "backup_results.csv"), "Simulation,AvgBtu,PeakBtu\n");
        }
        catch { }

        await Task.Run(async () =>
        {
            try
            {
                Dispatcher.Invoke(() => {
                    txtCurrentTask.Text = "Generating Permutations...";
                    txtDetails.Text = "Making list of simulation parameters...";
                    
                    _typeToInstanceIds.Clear();
                    foreach (var kvp in _variableElements)
                    {
                        var element = _doc.GetElement(kvp.Value);
                        if (element is ElementType typeElem)
                        {
                            var typeId = typeElem.Id.Value;
                            var instances = new FilteredElementCollector(_doc)
                                .WhereElementIsNotElementType()
                                .Where(e => e.GetTypeId().Value == typeId)
                                .Select(e => e.Id.Value.ToString())
                                .ToList();
                            _typeToInstanceIds[kvp.Key] = new HashSet<string>(instances);
                        }
                    }
                });

                _totalSims = _explicitScenarios != null ? _explicitScenarios.Count : PermutationEngine.GetTotalScenariosCount(_activeElements);

                Dispatcher.Invoke(() => {
                    pbPermutations.Maximum = _totalSims;
                    pbCurrentTask.Maximum = _totalSims;
                    txtDetails.Text = "Calculating unique physical models...";
                });

                var physicalKeys = _variableProperties.Where(kvp => kvp.Value == TargetProperty.RevitType || kvp.Value == TargetProperty.InsulationSettling).Select(k => k.Key).ToList();

                var uniqueModelsSet = new HashSet<int>();
                long generatedCount = 0;
                
                foreach (var scenario in GetScenarios())
                {
                    if (_cts.IsCancellationRequested) break;
                    
                    int hash = 17;
                    foreach (var pk in physicalKeys)
                    {
                        if (scenario.TryGetValue(pk, out var val) && val != null)
                            hash = hash * 31 + val.GetHashCode();
                    }
                    uniqueModelsSet.Add(hash);
                    
                    generatedCount++;
                    if (generatedCount % 1000 == 0)
                    {
                        Dispatcher.Invoke(() => {
                            pbPermutations.Value = generatedCount;
                        });
                    }
                }
                int uniqueModelsCount = uniqueModelsSet.Count;
                Dispatcher.Invoke(() => {
                    pbPermutations.Value = _totalSims;
                });

                Dispatcher.Invoke(() => {
                    pbOverall.Maximum = uniqueModelsCount;
                    pbCurrentTask.Maximum = _totalSims;
                    txtDetails.Text = "Sorting list for optimized simulation order...";
                });
                
                _baseModelsFolder = Path.Combine(_exportPath, "BaseModels");
                Directory.CreateDirectory(_baseModelsFolder);

                int completedSims = 0;

                GetSystemTimes(out _prevIdleTime, out _prevKernelTime, out _prevUserTime);

                int groupIndex = 0;
                string currentPhysicalKey = null;
                string currentGbxmlPath = null;
                
                var simulationTasks = new List<Task>();
                _resultsMap = new System.Collections.Concurrent.ConcurrentDictionary<long, SimulationResult>();

                var simQueue = new Queue<Tuple<long, Dictionary<string, object>, string>>();
                long index = 0;

                var consumerTask = Task.Run(async () => {
                    var activeTasks = new List<Task>();
                    
                    while ((simQueue.Count > 0 || activeTasks.Count > 0 || index < _totalSims) && !_cts.IsCancellationRequested)
                    {
                        activeTasks.RemoveAll(t => t.IsCompleted);

                        double cpu = GetCpuUsage();
                        double ram = GetAvailableRamGB();

                        if (simQueue.Count > 0 && 
                            (activeTasks.Count == 0 || (cpu < 90.0 && ram > 2.0 && activeTasks.Count < Environment.ProcessorCount * 2)))
                        {
                            var item = simQueue.Dequeue();
                            long simIndex = item.Item1;
                            var scenario = item.Item2;
                            string gbxml = item.Item3;

                            var t = Task.Run(async () => {
                                string currentEpwPath = _weatherPath;
                                string simId = Guid.NewGuid().ToString("N").Substring(0, 8);
                                string simFolder = Path.Combine(_exportPath, $"Sim_{simId}");
                                Directory.CreateDirectory(simFolder);

                                if (File.Exists(gbxml))
                                {
                                    File.Copy(gbxml, Path.Combine(simFolder, "analysis.xml"), true);
                                }

                                SimulationResult btuResult = await RunOpenStudioAndParseAsync(simFolder, currentEpwPath, scenario);
                                _resultsMap[simIndex] = btuResult;

                                lock (_simulationResults)
                                {
                                    _simulationResults.Add(btuResult);
                                    completedSims++;
                                    
                                    try 
                                    {
                                        File.AppendAllText(Path.Combine(_exportPath, "backup_results.csv"), $"{simIndex+1},{btuResult.AverageBtu},{btuResult.PeakBtu}\n");
                                    } 
                                    catch { }
                                }

                                Dispatcher.Invoke(() => {
                                    pbCurrentTask.Value = completedSims;
                                    txtProgress.Text = $"{completedSims} / {_totalSims}";
                                    double msPerSim = timer.Elapsed.TotalMilliseconds / completedSims;
                                    TimeSpan eta = TimeSpan.FromMilliseconds(msPerSim * (_totalSims - completedSims));
                                    txtEta.Text = $"ETA: {(eta.Days > 0 ? $"{eta.Days}d " : "")}{eta.Hours:D2}h {eta.Minutes:D2}m {eta.Seconds:D2}s";
                                });
                            });
                            activeTasks.Add(t);
                            simulationTasks.Add(t);
                            
                            await Task.Delay(3000);
                        }
                        else
                        {
                            await Task.Delay(1000);
                        }
                    }
                    
                    await Task.WhenAll(activeTasks);
                });

                foreach (var scenario in GetScenarios())
                {
                    if (_cts.IsCancellationRequested) break;

                    string key = "";
                    foreach(var pk in physicalKeys) {
                        if (scenario.ContainsKey(pk)) key += pk + "=" + scenario[pk].ToString() + ";";
                    }

                    if (key != currentPhysicalKey)
                    {
                        currentPhysicalKey = key;
                        string simId = "Base_" + groupIndex;
                        string simFolder = Path.Combine(_baseModelsFolder, simId);
                        Directory.CreateDirectory(simFolder);

                        Dispatcher.Invoke(() => {
                            pbOverall.Value = groupIndex;
                            txtCurrentTask.Text = $"Exporting Geometry Configuration {groupIndex + 1}";
                            txtDetails.Text = $"Exporting gbXML from Revit...";
                        });

                        var tcs = new TaskCompletionSource<bool>();
                        EventHandler completionHandler = null;
                        completionHandler = (s, e) => {
                            _handler.ActionCompleted -= completionHandler;
                            tcs.TrySetResult(true);
                        };
                        _handler.ActionCompleted += completionHandler;
                        
                        _handler.CurrentAction = (app) => {
                            try { 
                                _lastGeomKey = null; // Force export for base model
                                RunRevitExport(scenario, simId, simFolder); 
                            }
                            catch (Exception ex) { AddWarningUI($"Revit Export Error: {ex.Message}"); }
                        };
                        _exEvent.Raise();
                        
                        try { await tcs.Task; }
                        catch { _handler.ActionCompleted -= completionHandler; }

                        currentGbxmlPath = Path.Combine(simFolder, "analysis.xml");
                        groupIndex++;
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                    }

                    simQueue.Enqueue(new Tuple<long, Dictionary<string, object>, string>(index, scenario, currentGbxmlPath));
                    index++;

                    while (simQueue.Count > 2000 && !_cts.IsCancellationRequested)
                    {
                        await Task.Delay(1000);
                    }
                }

                Dispatcher.Invoke(() => {
                    pbOverall.Value = uniqueModelsCount;
                    txtCurrentTask.Text = "Running Energy Simulations...";
                    txtDetails.Text = "Waiting for all OpenStudio simulations to complete...";
                });

                await consumerTask;
            }
            catch (Exception ex)
            {
                AddWarningUI($"Fatal Orchestration Error: {ex.Message}\n{ex.StackTrace}");
            }
        });

        timer.Stop();

        if (_cts.IsCancellationRequested && !_savePartialData) { this.Close(); return; }

        Dispatcher.Invoke(() => {
            txtCurrentTask.Text = "Saving Results...";
            txtDetails.Text = "Compiling data to Excel...";
        });

        await Task.Run(() => {
            try
            {
                var validResults = new List<SimulationResult>();
                var validScenarios = new List<Dictionary<string, object>>();
                
                long i = 0;
                foreach (var scenario in GetScenarios())
                {
                    if (i >= _totalSims) break;
                    if (_resultsMap.TryGetValue(i, out var result) && result != null && result.Success)
                    {
                        validResults.Add(result);
                        validScenarios.Add(scenario);
                    }
                    i++;
                }
                string docTitle = "";
                try { docTitle = _doc.Title; } catch { docTitle = "Simulation"; }
                string excelPath = null;
                try {
                    excelPath = ExcelExporter.ExportData(docTitle, _exportPath, _warnings, validScenarios, validResults, _units, _variableProperties, _activeVariables, System.IO.Path.GetFileName(_weatherPath), _materialReferences);
                } catch (Exception ex) {
                    try { System.IO.File.WriteAllText(System.IO.Path.Combine(_exportPath, "excel_fatal_error.txt"), ex.ToString()); } catch { }
                }

                if (_isSensitivityMode && validResults.Count > 0)
                {
                    try
                    {
                        var baselineResult = validResults[0];
                        string csvPath = Path.Combine(_exportPath, "sensitivity_report.csv");
                        using (var sw = new StreamWriter(csvPath))
                        {
                            sw.WriteLine("Variable Tested,Base Avg BTU,New Avg BTU,Avg Diff,Peak Diff,Varied");
                            for (int idx = 1; idx < validResults.Count; idx++)
                            {
                                var res = validResults[idx];
                                var scenario = validScenarios[idx];
                                string target = scenario.ContainsKey("__SensitivityTestTarget") ? scenario["__SensitivityTestTarget"].ToString() : $"Scenario {idx}";
                                
                                double avgDiff = res.AverageBtu - baselineResult.AverageBtu;
                                double peakDiff = res.PeakBtu - baselineResult.PeakBtu;
                                bool varied = Math.Abs(avgDiff) > 0.01 || Math.Abs(peakDiff) > 0.01;
                                
                                sw.WriteLine($"\"{target}\",{baselineResult.AverageBtu:F2},{res.AverageBtu:F2},{avgDiff:F2},{peakDiff:F2},{varied}");
                            }
                        }
                        AddWarningUI($"Sensitivity report saved to {csvPath}");
                    }
                    catch (Exception ex)
                    {
                        AddWarningUI($"Failed to generate sensitivity report: {ex.Message}");
                    }
                }

                if (!string.IsNullOrEmpty(excelPath))
                {
                    AddWarningUI($"Excel data successfully exported to {excelPath}");
                    try
                    {
                        System.Threading.Thread.Sleep(3000);
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(excelPath) { UseShellExecute = true });
                    }
                    catch (Exception ex)
                    {
                        AddWarningUI($"Could not automatically open Excel file: {ex.Message}");
                    }
                }

                if (!HomeWindow.IsDebugMode)
                {
                    try
                    {
                        if (Directory.Exists(_baseModelsFolder)) Directory.Delete(_baseModelsFolder, true);
                        foreach (var simFolder in Directory.GetDirectories(_exportPath, "Sim_*"))
                        {
                            Directory.Delete(simFolder, true);
                        }
                    }
                    catch (Exception ex)
                    {
                        AddWarningUI($"Cleanup failed: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                AddWarningUI($"Failed to save Excel file: {ex.Message}");
            }
        });

        this.Close();
    }

    private void RunRevitExport(Dictionary<string, object> scenario, string simId, string simFolder)
    {
        string geomKey = string.Join("|", scenario.OrderBy(x => x.Key).Select(x => $"{x.Key}={x.Value?.ToString() ?? "null"}"));
        bool geometryChanged = (geomKey != _lastGeomKey || string.IsNullOrEmpty(_lastGbxmlPath));

        if (geometryChanged)
        {
            using (TransactionGroup tg = new TransactionGroup(_doc, "Export gbXML"))
            {
                tg.Start();
                
                var ws = new WarningSwallower();
                using (Transaction t = new Transaction(_doc, "Parametric Simulation"))
                {
                    FailureHandlingOptions failureOptions = t.GetFailureHandlingOptions();
                    failureOptions.SetFailuresPreprocessor(ws);
                    t.SetFailureHandlingOptions(failureOptions);

                    t.Start();
            
                    var areaSettings = Autodesk.Revit.DB.AreaVolumeSettings.GetAreaVolumeSettings(_doc);
                    if (areaSettings != null) areaSettings.ComputeVolumes = false;

                    // Apply Geometry Scenario using Mutator
                    var mutator = new MatlabRevitMutator(_doc);
                    mutator.ApplyScenarioModifications(scenario, _variableProperties, _variableElements);

                    try { _doc.Regenerate(); } catch { }

                    var energySettings = Autodesk.Revit.DB.Analysis.EnergyDataSettings.GetEnergyDataSettings(_doc);
                    energySettings.AnalysisType = Autodesk.Revit.DB.Analysis.AnalysisMode.ConceptualMassesAndBuildingElements;

                    var commitStatus = t.Commit();
                    if (commitStatus == TransactionStatus.RolledBack || ws.HasError)
                    {
                        AddWarningUI($"Skipping simulation {simId} due to invalid geometry.");
                        tg.RollBack();
                        return;
                    }
                }

                try
                {
                    using (Transaction tExport = new Transaction(_doc, "Export gbXML"))
                    {
                        var eWs = new WarningSwallower();
                        FailureHandlingOptions failureOptions = tExport.GetFailureHandlingOptions();
                        failureOptions.SetFailuresPreprocessor(eWs);
                        tExport.SetFailureHandlingOptions(failureOptions);

                        tExport.Start();
                        
                        var existingModel = Autodesk.Revit.DB.Analysis.EnergyAnalysisDetailModel.GetMainEnergyAnalysisDetailModel(_doc);
                        if (existingModel != null) _doc.Delete(existingModel.Id);
                        
                        var eamType = Type.GetType("Autodesk.Revit.DB.Analysis.EnergyAnalyticalModel, RevitAPI");
                        if (eamType != null)
                        {
                            var eamModels = new FilteredElementCollector(_doc).OfClass(eamType).ToElementIds();
                            foreach (var id in eamModels) { try { _doc.Delete(id); } catch { } }
                        }
                        
                        try
                        {
                            Autodesk.Revit.DB.Analysis.EnergyAnalysisDetailModel.Create(_doc);
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"EADM Create failed: {ex.Message}");
                        }
                        
                        var gbxOptions = new GBXMLExportOptions();
                        _doc.Export(simFolder, "analysis.xml", gbxOptions);
                        tExport.Commit();
                    }

                    _lastGbxmlPath = Path.Combine(simFolder, "analysis.xml");
                    _lastGeomKey = geomKey;

                    // Parse analysis.xml to map WallType (via instances) to gbXML constructionIdRef
                    _currentConstructionRefs.Clear();
                    try
                    {
                        var xdoc = System.Xml.Linq.XDocument.Load(_lastGbxmlPath);
                        System.Xml.Linq.XNamespace ns = xdoc.Root.GetDefaultNamespace();
                        var surfaces = xdoc.Descendants(ns + "Surface");
                        
                        foreach (var kvp in _typeToInstanceIds)
                        {
                            foreach (var surf in surfaces)
                            {
                                var cadObjId = surf.Element(ns + "CADObjectId")?.Value;
                                if (cadObjId != null && kvp.Value.Contains(cadObjId))
                                {
                                    var conRef = surf.Attribute("constructionIdRef")?.Value;
                                    if (!string.IsNullOrEmpty(conRef))
                                    {
                                        _currentConstructionRefs[kvp.Key] = conRef;
                                        break; // Found the construction for this type
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                }
                catch (Exception ex)
                {
                    AddWarningUI($"Export failed: {ex.Message}");
                }
                
                tg.RollBack();
            }
        }
        else
        {
            try
            {
                File.Copy(_lastGbxmlPath, Path.Combine(simFolder, "analysis.xml"), true);
            }
            catch (Exception ex)
            {
                AddWarningUI($"Failed to copy cached gbXML: {ex.Message}");
            }
        }
    }

    private double CalculateEffectiveRValue(WallModConfig cfg, string propertyName)
    {
        if (cfg.Method == VariableMethod.EffectiveRValue)
        {
            double r1 = cfg.StudRValue;
            double r2 = cfg.InsulationRValue;
            double ffDec = cfg.FramingFactor / 100.0;
            if (r1 > 0 && r2 > 0)
            {
                double uEff = (ffDec / r1) + ((1.0 - ffDec) / r2);
                return 1.0 / uEff;
            }
        }
        else if (cfg.Method == VariableMethod.Monolithic || cfg.Method == VariableMethod.ReplaceElements)
        {
            double r1 = ExtractRValue(cfg.StudType);
            double r2 = ExtractRValue(cfg.InsulationType);
            double ffDec = cfg.FramingFactor / 100.0;
            
            bool fallback = false;
            if (r1 == 0) { r1 = 4.38; fallback = true; } // 2x4 default fallback
            if (r2 == 0) { r2 = 13.0; fallback = true; } // R13 default fallback
            
            if (fallback)
            {
                AddWarningUI($"[{propertyName}] Missing valid Stud or Insulation type inputs. Safely fell back to defaults (Stud: R-4.38, Insul: R-13).");
            }
            
            if (r1 > 0 && r2 > 0)
            {
                double uEff = (ffDec / r1) + ((1.0 - ffDec) / r2);
                return 1.0 / uEff;
            }
        }
        return 0;
    }

    private double ValidateAndEnforceBounds(TargetProperty prop, double value, string propertyName, string unit)
    {
        double corrected = value;
        bool outOfBounds = false;
        string defaultUsed = "";

        if (prop == TargetProperty.WeatherTotalSkyCover)
        {
            if (value < 0 || value > 10) { corrected = 5; outOfBounds = true; defaultUsed = "5 tenths"; }
        }
        else if (prop == TargetProperty.WeatherWindDirection)
        {
            if (value < 0 || value > 360) { corrected = 0; outOfBounds = true; defaultUsed = "0 degrees"; }
        }
        else if (prop == TargetProperty.WeatherRelativeHumidity)
        {
            if (value < 0 || value > 100) { corrected = 50; outOfBounds = true; defaultUsed = "50%"; }
        }
        else if (prop == TargetProperty.WeatherAtmosphericPressure)
        {
            // The value might be in inHg or Pa.
            if (unit != null && unit.Contains("inHg") && value < 200)
            {
                // Convert inHg to Pa
                corrected = value * 3386.39;
            }
            if (corrected < 31000 || corrected > 120000)
            {
                corrected = 101325; // standard atmospheric pressure
                outOfBounds = true;
                defaultUsed = "101325 Pa";
            }
        }
        else if (prop == TargetProperty.WeatherWindSpeed)
        {
            if (value < 0) { corrected = 0; outOfBounds = true; defaultUsed = "0 m/s"; }
        }
        else if (prop == TargetProperty.RValue)
        {
            if (value <= 0) { corrected = 1.0; outOfBounds = true; defaultUsed = "1.0"; }
        }

        if (outOfBounds)
        {
            AddWarningUI($"[{propertyName}] Input value ({value}) was outside the permitted physical range for EnergyPlus. Forced safe default: {defaultUsed}.");
        }

        return corrected;
    }

    private double ExtractRValue(string input)
    {
        if (string.IsNullOrEmpty(input)) return 0;
        
        if (_cachedRValues != null && _cachedRValues.TryGetValue(input, out double cachedR))
        {
            return cachedR;
        }

        var mutator = new MatlabRevitMutator(_doc);
        double rFromMutator = mutator.GetRValueByName(input);
        if (rFromMutator > 0) return rFromMutator;

        var match = System.Text.RegularExpressions.Regex.Match(input, @"[Rr]-?(\d+(\.\d+)?)");
        if (match.Success && double.TryParse(match.Groups[1].Value, out double r))
        {
            return r;
        }
        return 0;
    }

    private async Task<SimulationResult> RunOpenStudioAndParseAsync(string simFolder, string weatherPath, Dictionary<string, object> scenario)
    {
        string openStudioCli = @"C:\Program Files\NREL\OpenStudio CLI For Revit 2027\bin\openstudio.exe";
        if (!File.Exists(openStudioCli))
        {
            openStudioCli = @"C:\Program Files\NREL\OpenStudio CLI For Revit 2026\bin\openstudio.exe";
        }
        if (!File.Exists(openStudioCli)) return new SimulationResult { Success = false };

        var weatherOverrides = new Dictionary<string, object>();
        var dynamicRValueConfigs = new Dictionary<string, object>();
        var spaceVariables = new List<Dictionary<string, object>>();
        var envelopeVariables = new List<Dictionary<string, object>>();

        foreach (var kvp in scenario)
        {
            if (_variableProperties != null && _variableProperties.TryGetValue(kvp.Key, out var prop))
            {
                if (prop.ToString().StartsWith("Weather"))
                {
                    if (prop == TargetProperty.WeatherTemperatureOffset || prop == TargetProperty.WeatherDewPoint || prop == TargetProperty.WeatherTemperature)
                    {
                        double fahrenheit = Convert.ToDouble(kvp.Value);
                        double celsius;
                        if (prop == TargetProperty.WeatherTemperatureOffset)
                        {
                            celsius = fahrenheit * 5.0 / 9.0;
                        }
                        else
                        {
                            celsius = (fahrenheit - 32.0) * 5.0 / 9.0;
                        }
                        weatherOverrides[prop.ToString()] = celsius;
                    }
                    else if (prop == TargetProperty.WeatherTemperatureSynthetic && kvp.Value is SyntheticWeatherConfig swc)
                    {
                        weatherOverrides[prop.ToString()] = new {
                            WinterMinTemp = (swc.WinterMinTemp - 32.0) * 5.0 / 9.0,
                            WinterMaxTemp = (swc.WinterMaxTemp - 32.0) * 5.0 / 9.0,
                            SummerMinTemp = (swc.SummerMinTemp - 32.0) * 5.0 / 9.0,
                            SummerMaxTemp = (swc.SummerMaxTemp - 32.0) * 5.0 / 9.0,
                            Offset = (swc.Offset) * 5.0 / 9.0 // Offset is a delta, so we just scale it by 5/9. Wait, a 1 degree F offset is 5/9 degree C offset.
                        };
                    }
                    else
                    {
                        string propUnit = _units.ContainsKey(kvp.Key) ? _units[kvp.Key] : "";
                        weatherOverrides[prop.ToString()] = ValidateAndEnforceBounds(prop, Convert.ToDouble(kvp.Value), kvp.Key, propUnit);
                    }
                }
                else if (prop == TargetProperty.RevitType && kvp.Value is WallModConfig wmc)
                {
                    if (wmc.VaryRValueWithTemp && wmc.InsulationType != "None" && !string.IsNullOrEmpty(wmc.InsulationType))
                    {
                        dynamicRValueConfigs[wmc.InsulationType] = new {
                            equation = wmc.RValueTempEquation,
                            unit = wmc.RValueTempEquationUnit
                        };
                    }
                    
                    double targetR = CalculateEffectiveRValue(wmc, kvp.Key);
                    if (targetR > 0 && _variableElements.TryGetValue(kvp.Key, out var elementId) && _variableElementNames.TryGetValue(kvp.Key, out var elementName))
                    {
                        string osName = _currentConstructionRefs.ContainsKey(kvp.Key) ? _currentConstructionRefs[kvp.Key] : elementName;
                        osName = osName.Replace("\"", "");
                        
                        var cadIds = new List<string>();
                        if (_typeToInstanceIds.ContainsKey(kvp.Key))
                        {
                            cadIds.AddRange(_typeToInstanceIds[kvp.Key]);
                        }
                        else
                        {
                            cadIds.Add(elementId.Value.ToString());
                        }

                        string funcStr = "";
                        var hostElem = _doc.GetElement(elementId);
                        if (hostElem is WallType wt)
                        {
                            var funcParam = wt.get_Parameter(BuiltInParameter.FUNCTION_PARAM);
                            if (funcParam != null && funcParam.AsInteger() == 1) funcStr = "ExteriorWall";
                            else funcStr = "InteriorWall";
                        }
                        else if (hostElem is RoofType) funcStr = "Roof";
                        else if (hostElem is FloorType) funcStr = "Floor";

                        var variableData = new Dictionary<string, object>
                        {
                            ["RevitElementIds"] = cadIds,
                            ["RevitElementId"] = elementId.Value.ToString(),
                            ["RevitElementName"] = osName,
                            ["RevitElementFunction"] = funcStr,
                            ["Property"] = "RValue",
                            ["Value"] = targetR
                        };
                        envelopeVariables.Add(variableData);
                    }
                }
                else if (prop == TargetProperty.RevitType && kvp.Value is string sVal && sVal.StartsWith("TempDependent:"))
                {
                    var parts = sVal.Substring("TempDependent:".Length).Split('|');
                    if (parts.Length > 0)
                    {
                        dynamicRValueConfigs["GLOBAL"] = new {
                            equation = parts[0],
                            unit = parts.Length > 1 ? parts[1] : "°F"
                        };
                    }
                }
                else if (prop == TargetProperty.RevitType && kvp.Value is string sValTypeSelection && sValTypeSelection != "Original" && !sValTypeSelection.StartsWith("TempDependent:"))
                {
                    double targetR_SI = 0;
                    if (_variableElements.TryGetValue(kvp.Key, out var elementId) && _variableElementNames.TryGetValue(kvp.Key, out var elementName))
                    {
                        var originalElem = _doc.GetElement(elementId);
                        var originalType = originalElem as ElementType ?? _doc.GetElement(originalElem.GetTypeId()) as ElementType;
                        if (originalType != null)
                        {
                            var mutatorForTarget = new MatlabRevitMutator(_doc);
                            targetR_SI = mutatorForTarget.GetRValueByNameAndCategory(sValTypeSelection, originalType.Category.Id);
                        }

                        if (targetR_SI <= 0 && _cachedRValues.TryGetValue(sValTypeSelection, out double cachedR))
                        {
                            targetR_SI = cachedR;
                        }

                        double targetR = targetR_SI * 5.678263337; // Convert m²·K/W to h·ft²·°F/Btu

                        if (targetR > 0)
                        {
                            string osName = _currentConstructionRefs.ContainsKey(kvp.Key) ? _currentConstructionRefs[kvp.Key] : elementName;
                            osName = osName.Replace("\"", "");
                            
                            var cadIds = new List<string>();
                            if (_typeToInstanceIds.ContainsKey(kvp.Key))
                            {
                                cadIds.AddRange(_typeToInstanceIds[kvp.Key]);
                            }
                            else
                            {
                                cadIds.Add(elementId.Value.ToString());
                            }

                            string funcStr = "";
                            var hostElem = _doc.GetElement(elementId);
                            if (hostElem is WallType wt)
                            {
                                var funcParam = wt.get_Parameter(BuiltInParameter.FUNCTION_PARAM);
                                if (funcParam != null && funcParam.AsInteger() == 1) funcStr = "ExteriorWall";
                                else funcStr = "InteriorWall";
                            }
                            else if (hostElem is RoofType) funcStr = "Roof";
                            else if (hostElem is FloorType) funcStr = "Floor";

                            var variableData = new Dictionary<string, object>
                            {
                                ["RevitElementIds"] = cadIds,
                                ["RevitElementId"] = elementId.Value.ToString(),
                                ["RevitElementName"] = osName,
                                ["RevitElementFunction"] = funcStr,
                                ["Property"] = "RValue",
                                ["Value"] = targetR
                            };
                            envelopeVariables.Add(variableData);
                        }
                    }
                }
                else if (prop != TargetProperty.RevitType)
                {
                    // Generic parametric variable (RValue, HeatingSetpoint, etc)
                    if (_variableElements.TryGetValue(kvp.Key, out var elementId) && _variableElementNames.TryGetValue(kvp.Key, out var elementName))
                    {
                        string osName = elementName;
                        if ((prop == TargetProperty.RValue || prop == TargetProperty.UValue || prop == TargetProperty.Thickness) && _currentConstructionRefs.ContainsKey(kvp.Key))
                        {
                            osName = _currentConstructionRefs[kvp.Key];
                        }
                        osName = osName.Replace("\"", "");
                        
                        var cadIds = new List<string>();
                        if (_typeToInstanceIds.ContainsKey(kvp.Key))
                        {
                            cadIds.AddRange(_typeToInstanceIds[kvp.Key]);
                        }
                        else
                        {
                            cadIds.Add(elementId.Value.ToString());
                        }

                        string funcStr = "";
                        var hostElem = _doc.GetElement(elementId);
                        if (hostElem is WallType wt)
                        {
                            var funcParam = wt.get_Parameter(BuiltInParameter.FUNCTION_PARAM);
                            if (funcParam != null && funcParam.AsInteger() == 1) funcStr = "ExteriorWall";
                            else funcStr = "InteriorWall";
                        }
                        else if (hostElem is RoofType) funcStr = "Roof";
                        else if (hostElem is FloorType) funcStr = "Floor";

                        string propUnit = _units.ContainsKey(kvp.Key) ? _units[kvp.Key] : "";
                        double finalValue = kvp.Value is double dval ? ValidateAndEnforceBounds(prop, dval, kvp.Key, propUnit) : Convert.ToDouble(kvp.Value);

                        var variableData = new Dictionary<string, object>
                        {
                            ["RevitElementIds"] = cadIds,
                            ["RevitElementId"] = elementId.Value.ToString(),
                            ["RevitElementName"] = osName,
                            ["RevitElementFunction"] = funcStr,
                            ["Property"] = prop.ToString(),
                            ["Value"] = finalValue
                        };

                        if (prop == TargetProperty.HeatingSetpoint || prop == TargetProperty.CoolingSetpoint || 
                            prop == TargetProperty.PeopleCount || prop == TargetProperty.Schedule || prop == TargetProperty.IsUnheated)
                        {
                            spaceVariables.Add(variableData);
                        }
                        else
                        {
                            envelopeVariables.Add(variableData);
                        }
                    }
                }
            }
        }

        var fullOverrides = new Dictionary<string, object>();
        foreach (var kvp in weatherOverrides)
        {
            fullOverrides[kvp.Key] = kvp.Value;
        }
        fullOverrides["SpaceVariables"] = spaceVariables;
        fullOverrides["EnvelopeVariables"] = envelopeVariables;

        string wJson = System.Text.Json.JsonSerializer.Serialize(fullOverrides).Replace("\"", "\\\"");
        string dynamicRJson = System.Text.Json.JsonSerializer.Serialize(dynamicRValueConfigs).Replace("\"", "\\\"");

        string addinDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location).Replace("\\", "/");
        string customMeasuresPath = Path.GetFullPath(Path.Combine(addinDir, "..", "..", "Measures")).Replace("\\", "/");
        
        string customEpwPath = Path.Combine(simFolder, "custom.epw");
        File.Copy(weatherPath, customEpwPath, true);
        
        string statPath = weatherPath.Replace(".epw", ".stat");
        if (File.Exists(statPath))
            File.Copy(statPath, Path.Combine(simFolder, "custom.stat"), true);
            
        string ddyPath = weatherPath.Replace(".epw", ".ddy");
        if (File.Exists(ddyPath))
            File.Copy(ddyPath, Path.Combine(simFolder, "custom.ddy"), true);
            
        string epwFilename = customEpwPath.Replace("\\", "/");
        string simFolderFwd = simFolder.Replace("\\", "/");
        
        string oswContent = $@"{{
  ""seed_file"": ""seed_empty.osm"",
  ""measure_paths"": [
    ""C:/Program Files/NREL/OpenStudio CLI For Revit 2027/workflows/../measures"",
    ""{customMeasuresPath}""
  ],
  ""file_paths"": [
    ""{simFolderFwd}"",
    ""C:/Program Files/NREL/OpenStudio CLI For Revit 2027/workflows/../weather"",
    ""C:/Program Files/NREL/OpenStudio CLI For Revit 2027/workflows/../seeds"",
    ""C:/Program Files/NREL/OpenStudio CLI For Revit 2027/workflows/../gbxmls""
  ],
  ""run_directory"": ""./run"",
  ""steps"": [
    {{
      ""measure_dir_name"": ""ChangeBuildingLocation"",
      ""name"": ""Change Building Location"",
      ""arguments"": {{
        ""weather_file_name"": ""{epwFilename}""
      }}
    }},
    {{
      ""measure_dir_name"": ""gbxml_import"",
      ""name"": ""ImportGbxml"",
      ""arguments"": {{
        ""gbxml_file_name"": ""analysis.xml""
      }}
    }},
    {{
      ""measure_dir_name"": ""gbxml_import_advanced"",
      ""name"": ""Advanced Import Gbxml"",
      ""arguments"": {{
        ""gbxml_file_name"": ""analysis.xml""
      }}
    }},
    {{
      ""measure_dir_name"": ""gbxml_import_hvac"",
      ""name"": ""GBXML HVAC Import"",
      ""arguments"": {{
        ""gbxml_file_name"": ""analysis.xml""
      }}
    }},
    {{
      ""measure_dir_name"": ""override_weather_and_design_days"",
      ""name"": ""Override Weather and Design Days"",
      ""arguments"": {{ 
          ""json_overrides"": ""{wJson}""
      }}
    }},
    {{
      ""measure_dir_name"": ""advanced_export_and_dynamic_rvalue"",
      ""name"": ""Advanced Export and Dynamic RValue"",
      ""arguments"": {{ 
          ""json_overrides"": ""{dynamicRJson}""
      }}
    }},
    {{
      ""measure_dir_name"": ""dynamic_rvalue_workspace"",
      ""name"": ""Dynamic R-Value Workspace Measure"",
      ""arguments"": {{ 
          ""json_overrides"": ""{dynamicRJson}""
      }}
    }},
    {{
      ""measure_dir_name"": ""set_simulation_control"",
      ""name"": ""Set Simulation Control"",
      ""arguments"": {{
        ""cooling_sizing_factor"": 1.0,
        ""do_plant_sizing"": true,
        ""do_system_sizing"": true,
        ""do_zone_sizing"": true,
        ""end_date"": ""12/31"",
        ""heating_sizing_factor"": 1.0,
        ""loads_convergence_tolerance"": 0.1,
        ""max_warmup_days"": 25,
        ""min_warmup_days"": 6,
        ""sim_for_run_period"": true,
        ""sim_for_sizing"": true,
        ""solar_distribution"": ""FullExterior"",
        ""start_date"": ""01/01"",
        ""temp_convergence_tolerance"": 0.5,
        ""timesteps_per_hour"": 1,
        ""max_hvac_iterations"": 8
      }}
    }},
    {{
      ""measure_dir_name"": ""gbxml_postprocess"",
      ""name"": ""gbXML Postprocess""
    }},
    {{
      ""measure_dir_name"": ""openstudio_results"",
      ""name"": ""OpenStudio Results"",
      ""arguments"": {{
        ""annual_overview_section"": true,
        ""monthly_overview_section"": true,
        ""reg_monthly_details"": true
      }}
    }},
    {{
      ""measure_dir_name"": ""systems_analysis_report_generator"",
      ""name"": ""Systems Analysis Report"",
      ""arguments"": {{
        ""debug"": true
      }}
    }}
  ]
}}";

        string customOswPath = Path.Combine(simFolder, "custom_run.osw");
        File.WriteAllText(customOswPath, oswContent);

        using (var process = new Process())
        {
            process.StartInfo.FileName = openStudioCli;
            process.StartInfo.Arguments = $"run -w \"{customOswPath}\"";
            process.StartInfo.WorkingDirectory = simFolder;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;

            try
            {
                process.Start();
                try { _jobObject.AddProcess(process.Handle); } catch { }
                
                using (var registration = _cts.Token.Register(() => { try { if (!process.HasExited) process.Kill(); } catch { } }))
                {
                    await Task.Run(() => process.WaitForExit());
                }

                if (_cts.IsCancellationRequested) return new SimulationResult { Success = false };

                string dbPath = Path.Combine(simFolder, "run", "eplusout.sql");
                if (File.Exists(dbPath))
                {
                    return await ParseOpenStudioResults(dbPath);
                }
            }
            catch (Exception ex)
            {
                AddWarningUI($"OpenStudio error: {ex.Message}");
            }
        }
        
        return new SimulationResult { Success = false };
    }

    private async Task<SimulationResult> ParseOpenStudioResults(string dbPath)
    {
        var result = new SimulationResult { Success = true, RoomData = new Dictionary<string, RoomBreakdown>() };

        await Task.Run(() => {
            try
            {
                using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;"))
                {
                    conn.Open();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = @"
                            SELECT d.Name, d.KeyValue, 
                                   COALESCE(z_from_surf.ZoneName, d.KeyValue) AS ResolvedZoneName,
                                   SUM(r.Value),
                                   s.ClassName
                            FROM ReportData r
                            JOIN ReportDataDictionary d ON r.ReportDataDictionaryIndex = d.ReportDataDictionaryIndex
                            LEFT JOIN Surfaces s ON d.KeyValue = s.SurfaceName
                            LEFT JOIN Zones z_from_surf ON s.ZoneIndex = z_from_surf.ZoneIndex
                            WHERE d.ReportingFrequency = 'Hourly'
                            GROUP BY d.Name, d.KeyValue, ResolvedZoneName, s.ClassName";

                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                string name = reader.GetString(0);
                                string key = reader.GetString(1);
                                string roomRaw = reader.GetString(2);
                                double val = reader.GetDouble(3) * 0.000947817 / 8760.0; // Joules to avg BTU/hr
                                string className = reader.IsDBNull(4) ? "" : reader.GetString(4);

                                // Heat transfer can be negative (cooling), allow negative values to show heat leaving the building
                                // val = Math.Abs(val);

                                string room = roomRaw;
                                // EnergyPlus splits rooms into pieces like "001-1 BASSMENT". We need to consolidate them to "001 BASSMENT".
                                var match = System.Text.RegularExpressions.Regex.Match(roomRaw, @"^(\d+)(-\d+)?\s+(.*)$");
                                if (match.Success)
                                {
                                    room = match.Groups[1].Value + " " + match.Groups[3].Value;
                                }

                                if (!result.RoomData.ContainsKey(room))
                                    result.RoomData[room] = new RoomBreakdown();

                                var rd = result.RoomData[room];

                                if (name.Contains("People Sensible Heating")) rd.PeopleHeat += val;
                                else if (name.Contains("Lights Total Heating")) rd.LightsHeat += val;
                                else if (name.Contains("Window Transmitted Solar")) rd.SunTransmitted += val;
                                else if (name.Contains("Inside Face Conduction"))
                                {
                                    if (className == "Window") rd.WindowsConduction += val;
                                    else if (className == "Door") rd.DoorsConduction += val;
                                    else if (className == "Wall") rd.WallsConduction += val;
                                    else if (className == "Roof" || className == "Ceiling") rd.CeilingsConduction += val;
                                    else if (className == "Floor") rd.FloorsConduction += val;
                                    else 
                                    {
                                        // Fallback if ClassName is null (e.g. zone-level surface mapping missing)
                                        if (key.Contains("-W-")) rd.WallsConduction += val;
                                        else if (key.Contains("-R-")) rd.CeilingsConduction += val;
                                        else if (key.Contains("-F-")) rd.FloorsConduction += val;
                                        else if (key.Contains("-D-")) rd.DoorsConduction += val;
                                        else if (key.Contains("OP-")) rd.WindowsConduction += val;
                                    }
                                }
                            }
                        }
                    }

                    // Calculate totals
                    double totalAvg = 0;
                    foreach (var rd in result.RoomData.Values)
                    {
                        totalAvg += rd.PeopleHeat + rd.LightsHeat + rd.SunTransmitted + rd.WindowsConduction + 
                                    rd.DoorsConduction + rd.WallsConduction + rd.CeilingsConduction + rd.FloorsConduction;
                    }
                    result.AverageBtu = totalAvg;
                    // Approximation for Peak BTU based on average load
                    result.PeakBtu = totalAvg * 2.5; 
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                System.Windows.Application.Current.Dispatcher.Invoke(() => AddWarningUI($"SQL Error: {ex.Message}"));
            }
        });

        return result;
    }

    private void BtnStopAndOutput_Click(object sender, RoutedEventArgs e)
    {
        btnStopAndOutput.IsEnabled = false;
        btnCancel.IsEnabled = false;
        AddWarningUI("Stopping early... Completing current simulation and saving data.");
        _savePartialData = true;
        _cts.Cancel();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        btnStopAndOutput.IsEnabled = false;
        btnCancel.IsEnabled = false;
        AddWarningUI("Canceling... No data will be saved.");
        _savePartialData = false;
        _cts.Cancel();
    }

    private void Window_Closed(object sender, EventArgs e)
    {
        _cts.Cancel();
        _jobObject.Dispose();
    }
}
