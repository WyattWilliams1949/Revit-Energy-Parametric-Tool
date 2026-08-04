function test_variables_sensitivity(baseEpwPath, exportPath, openStudioCliPath)
    % TEST_VARIABLES_SENSITIVITY Performs a One-At-A-Time sensitivity test
    % on all available Revit active variables.
    
    if nargin < 3
        openStudioCliPath = 'C:\Program Files\NREL\OpenStudio CLI For Revit 2027\bin\openstudio.exe';
    end
    if nargin < 2
        exportPath = fullfile(pwd, 'sensitivity_runs');
    end
    if nargin < 1
        baseEpwPath = 'E:\Documents\AntigravityIDE\Revit Add-in\default.epw';
    end

    if ~exist(exportPath, 'dir')
        mkdir(exportPath);
    end

    revit = RevitAPI();
    
    fprintf('Connecting to Revit MATLAB Mode Server...\n');
    try
        activeElements = revit.GetElements();
    catch ME
        error('Cannot communicate with Revit. Is MATLAB Mode running? %s', ME.message);
    end
    
    fprintf('Connected! Found %d elements.\n', length(activeElements));
    
    % Build Property Map & Baseline Scenario
    variableProperties = containers.Map();
    baselineScenario = containers.Map();
    varList = {};
    
    for e = 1:length(activeElements)
        props = activeElements(e).Properties;
        if iscell(props)
            propsArr = [props{:}];
        else
            propsArr = props;
        end
        
        for p = 1:length(propsArr)
            propDef = propsArr(p);
            varName = propDef.Name;
            propType = propDef.Property;
            
            variableProperties(varName) = propType;
            varList{end+1} = varName;
            
            % Assign a baseline value based on type
            if strcmp(propType, 'RevitType')
                if isfield(propDef, 'AvailableRevitTypes') && ~isempty(propDef.AvailableRevitTypes)
                    baselineScenario(varName) = propDef.AvailableRevitTypes{1};
                else
                    baselineScenario(varName) = ''; % fallback
                end
            elseif strcmp(propType, 'HeatingSetpoint')
                baselineScenario(varName) = 70;
            elseif strcmp(propType, 'CoolingSetpoint')
                baselineScenario(varName) = 75;
            elseif strcmp(propType, 'Infiltration')
                baselineScenario(varName) = 0.5;
            elseif strcmp(propType, 'PeopleCount')
                baselineScenario(varName) = 5;
            elseif strcmp(propType, 'UValue') || strcmp(propType, 'RValueEff') || strcmp(propType, 'EffectiveRValue')
                baselineScenario(varName) = 5;
            elseif strcmp(propType, 'RValue')
                baselineScenario(varName) = 5;
            elseif startsWith(propType, 'Weather')
                if strcmp(propType, 'WeatherTemperature')
                    baselineScenario(varName) = 20;
                elseif strcmp(propType, 'WeatherRelativeHumidity')
                    baselineScenario(varName) = 50;
                elseif strcmp(propType, 'WeatherWindSpeed')
                    baselineScenario(varName) = 3;
                else
                    baselineScenario(varName) = 10;
                end
            else
                baselineScenario(varName) = 1; % generic numeric fallback
            end
        end
    end
    
    if isempty(varList)
        disp('No variables selected in Revit UI to test.');
        return;
    end
    
    % Store results
    results = {};
    
    % Function to run one simulation
    function [avgBtu, peakBtu, success] = RunOneScenario(simName, scenarioDict)
        avgBtu = NaN;
        peakBtu = NaN;
        success = false;
        
        simFolder = fullfile(exportPath, simName);
        if ~exist(simFolder, 'dir')
            mkdir(simFolder);
        end
        
        % 1. Send to Revit
        try
            revit.SimulateScenario(simFolder, scenarioDict);
        catch ME
            fprintf('  Failed Revit Export: %s\n', ME.message);
            return;
        end
        
        % 2. Setup Weather
        weatherDest = fullfile(simFolder, 'custom_weather.epw');
        unitsDict = containers.Map();
        modifyWeatherEPW(baseEpwPath, weatherDest, scenarioDict, variableProperties, unitsDict);
        
        statDest = fullfile(simFolder, 'custom_weather.stat');
        ddyDest = fullfile(simFolder, 'custom_weather.ddy');
        
        [epwDir, epwName, ~] = fileparts(baseEpwPath);
        baseStatPath = fullfile(epwDir, [epwName, '.stat']);
        baseDdyPath = fullfile(epwDir, [epwName, '.ddy']);
        
        if exist(baseStatPath, 'file')
            copyfile(baseStatPath, statDest);
        else
            copyfile('C:\Program Files\NREL\OpenStudio CLI For Revit 2027\weather\USA_CO_Denver.Intl.AP.725650_TMY3.stat', statDest);
        end
        
        if exist(baseDdyPath, 'file')
            copyfile(baseDdyPath, ddyDest);
        else
            copyfile('C:\Program Files\NREL\OpenStudio CLI For Revit 2027\weather\USA_CO_Denver.Intl.AP.725650_TMY3.ddy', ddyDest);
        end
        
        % 3. Prepare measure arguments using JSON overrides
        % 3. Prepare measure arguments using JSON overrides
        overridesMap = containers.Map();
        fnames = scenarioDict.keys;
        for f = 1:length(fnames)
            fName = fnames{f};
            if isKey(variableProperties, fName)
                pType = variableProperties(fName);
                
                % Parse Category and Target Name
                targetName = 'Entire Building';
                category = '';
                pat = '^(.*?):\s*(.*)\s*\(';
                tokens = regexp(fName, pat, 'tokens');
                if ~isempty(tokens)
                    category = strtrim(tokens{1}{1});
                    targetName = strtrim(tokens{1}{2});
                end
                
                % GbXML Translation
                if strcmp(category, 'Spaces')
                    patGbxml = '(.+?)\s+([a-zA-Z0-9]+)\s+\(\d+\)$';
                    gbxmlTokens = regexp(targetName, patGbxml, 'tokens');
                    if ~isempty(gbxmlTokens)
                        spNamePart = gbxmlTokens{1}{1};
                        spNumPart = gbxmlTokens{1}{2};
                        targetName = sprintf('sp-%s%s', spNumPart, strrep(spNamePart, ' ', ''));
                    end
                elseif strcmp(category, 'Windows') || strcmp(category, 'Doors')
                    patGbxmlWin = '(.+?)\s+([a-zA-Z0-9]+)\s+(Windows|Doors)$';
                    gbxmlTokensWin = regexp(targetName, patGbxmlWin, 'tokens');
                    if ~isempty(gbxmlTokensWin)
                        spNamePart = gbxmlTokensWin{1}{1};
                        spNumPart = gbxmlTokensWin{1}{2};
                        suffix = gbxmlTokensWin{1}{3};
                        targetName = sprintf('sp-%s%s %s', spNumPart, strrep(spNamePart, ' ', ''), suffix);
                    end
                end
                
                % --- Space-level properties (HeatingSetpoint, Infiltration, etc.) ---
                if strcmp(pType, 'Infiltration') || strcmp(pType, 'PeopleCount') || ...
                   strcmp(pType, 'HeatingSetpoint') || strcmp(pType, 'CoolingSetpoint')
                    
                    % Infiltration for subsurfaces applies to the parent space in OS
                    if strcmp(pType, 'Infiltration') && (strcmp(category, 'Windows') || strcmp(category, 'Doors'))
                        targetName = strrep(targetName, ' Windows', '');
                        targetName = strrep(targetName, ' Doors', '');
                    end
                    
                    if ~isKey(overridesMap, targetName)
                        overridesMap(targetName) = containers.Map();
                    end
                    spaceMap = overridesMap(targetName);
                    spaceMap(pType) = scenarioDict(fName);
                    
                    unitVarName = strrep(fName, pType, [pType 'Unit']);
                    if isKey(scenarioDict, unitVarName)
                        spaceMap([pType 'Unit']) = scenarioDict(unitVarName);
                    end
                    
                % --- Construction Properties (RValue, UValue, etc) ---
                elseif strcmp(pType, 'RValue') || strcmp(pType, 'UValue') || strcmp(pType, 'EffectiveRValue') || strcmp(pType, 'RValueEff')
                    rIP = scenarioDict(fName);
                    if strcmp(pType, 'UValue') && rIP > 0
                        rIP = 1.0 / rIP;
                    end
                    
                    if rIP > 0
                        keyName = targetName;
                        if strcmp(category, 'Walls') || strcmp(category, 'Floors') || strcmp(category, 'Roofs')
                            keyName = category;
                        end
                        if ~isKey(overridesMap, keyName)
                            overridesMap(keyName) = containers.Map();
                        end
                        wMap = overridesMap(keyName);
                        wMap('RValue') = rIP;
                        overridesMap(keyName) = wMap;
                    end
                    
                % --- Wall R-value: RevitType wall type swap ---
                elseif strcmp(pType, 'RevitType')
                    targetTypeName = scenarioDict(fName);
                    if ~strcmp(targetTypeName, 'Original') && ~isempty(targetTypeName)
                        rSI = getWallRValueByName(revit.BaseUrl, targetTypeName);
                        if rSI > 0
                            rIP = rSI / 0.17611;
                            if ~isKey(overridesMap, 'Walls')
                                overridesMap('Walls') = containers.Map();
                            end
                            wMap = overridesMap('Walls');
                            wMap('RValue') = rIP;
                            overridesMap('Walls') = wMap;
                        end
                    end
                end
            end
        end
        
        measureStep = '';
        if overridesMap.Count > 0
            % Manually build JSON to avoid makeValidName altering room names with spaces
            jsonParts = {};
            spKeys = overridesMap.keys;
            for k = 1:length(spKeys)
                spName = spKeys{k};
                % Strip double quotes from space names to prevent JSON parsing errors
                spNameSafe = strrep(spName, '"', '');
                
                pMap = overridesMap(spName);
                pKeys = pMap.keys;
                
                spParts = {};
                for p = 1:length(pKeys)
                    val = pMap(pKeys{p});
                    if ischar(val) || isstring(val)
                        valSafe = strrep(val, '"', '');
                        spParts{end+1} = sprintf('\\"%s\\": \\"%s\\"', pKeys{p}, valSafe);
                    else
                        spParts{end+1} = sprintf('\\"%s\\": %g', pKeys{p}, val);
                    end
                end
                
                jsonParts{end+1} = sprintf('\\"%s\\": { %s }', spNameSafe, strjoin(spParts, ', '));
            end
            
            jsonStrEscaped = sprintf('{ %s }', strjoin(jsonParts, ', '));
            measureStep = sprintf('{ "measure_dir_name": "ApplyParametricVariations", "name": "Apply Variations", "arguments": { "json_overrides": "%s" } },\n    ', jsonStrEscaped);
        end
        
        % 4. Write OSW
        oswPath = fullfile(simFolder, 'custom_run.osw');
        simFolderFwd = strrep(simFolder, '\', '/');
        measuresPath = 'C:/Program Files/NREL/OpenStudio CLI For Revit 2027/measures';
        customMeasurePath = 'E:/Documents/AntigravityIDE/Revit Add-in/Measures';
        
        oswJson = sprintf(['{\n' ...
          '  "seed_file": "seed_empty.osm",\n' ...
          '  "weather_file": "custom_weather.epw",\n' ...
          '  "measure_paths": [\n' ...
          '    "%s",\n' ...
          '    "%s"\n' ...
          '  ],\n' ...
          '  "file_paths": [\n' ...
          '    "%s",\n' ...
          '    "C:/Program Files/NREL/OpenStudio CLI For Revit 2027/seeds"\n' ...
          '  ],\n' ...
          '  "run_directory": "./run",\n' ...
          '  "steps": [\n' ...
          '    { "measure_dir_name": "ChangeBuildingLocation", "name": "Change Building Location", "arguments": { "weather_file_name": "custom_weather.epw", "climate_zone": "ASHRAE 169-2013-4B" } },\n' ...
          '    { "measure_dir_name": "gbxml_import", "name": "ImportGbxml", "arguments": { "gbxml_file_name": "analysis.xml" } },\n' ...
          '    { "measure_dir_name": "gbxml_import_advanced", "name": "Advanced Import Gbxml", "arguments": { "gbxml_file_name": "analysis.xml" } },\n' ...
          '    { "measure_dir_name": "gbxml_import_hvac", "name": "GBXML HVAC Import", "arguments": { "gbxml_file_name": "analysis.xml" } },\n' ...
          '    %s' ...
          '    { "measure_dir_name": "set_simulation_control", "name": "Set Simulation Control", "arguments": { "cooling_sizing_factor": 1.0, "do_plant_sizing": true, "do_system_sizing": true, "do_zone_sizing": true, "end_date": "12/31", "heating_sizing_factor": 1.0, "loads_convergence_tolerance": 0.1, "max_warmup_days": 25, "min_warmup_days": 6, "sim_for_run_period": true, "sim_for_sizing": true, "solar_distribution": "FullInteriorAndExterior", "start_date": "01/01", "temp_convergence_tolerance": 0.5, "timesteps_per_hour": 1, "max_hvac_iterations": 8 } },\n' ...
          '    { "measure_dir_name": "gbxml_postprocess", "name": "gbXML Postprocess" },\n' ...
          '    { "measure_dir_name": "openstudio_results", "name": "OpenStudio Results", "arguments": { "annual_overview_section": true, "monthly_overview_section": true, "reg_monthly_details": true } },\n' ...
          '    { "measure_dir_name": "systems_analysis_report_generator", "name": "Systems Analysis Report", "arguments": { "debug": false } }\n' ...
          '  ]\n' ...
          '}'], measuresPath, customMeasurePath, simFolderFwd, measureStep);
        writelines(oswJson, oswPath);
        
        % 5. Run OpenStudio with 1 retry for sporadic crashes
        cmd = sprintf('"%s" run -w "%s"', openStudioCliPath, oswPath);
        oldDir = pwd;
        
        maxRetries = 2;
        success = false;
        for attempt = 1:maxRetries
            cd(simFolder);
            [status, ~] = system(cmd);
            cd(oldDir);
            
            if status == 0
                [avgBtu, peakBtu] = parseOpenStudioResults(simFolder);
                if avgBtu > 0 || peakBtu > 0
                    success = true;
                    break;
                end
            end
            if attempt < maxRetries
                fprintf('  Attempt %d failed, retrying...\n', attempt);
                pause(2); % Wait before retry
            end
        end
    end

    % --- RUN BASELINE ---
    fprintf('\n--- Running Baseline Scenario ---\n');
    [baseAvg, basePeak, baseSuccess] = RunOneScenario('baseline', baselineScenario);
    
    if ~baseSuccess
        fprintf('Error: Baseline simulation failed. Cannot perform sensitivity analysis.\n');
        revit.RevertChanges();
        return;
    end
    fprintf('Baseline Success! Avg: %.2f, Peak: %.2f\n', baseAvg, basePeak);
    
    % --- RUN SENSITIVITY ---
    for i = 1:length(varList)
        testVarName = varList{i};
        testPropType = variableProperties(testVarName);
        
        fprintf('\nTesting Variable [%d/%d]: %s\n', i, length(varList), testVarName);
        
        % Clone baseline
        testScenario = containers.Map(baselineScenario.keys, baselineScenario.values);
        
        % Perturb the specific variable
        if strcmp(testPropType, 'RevitType')
            continue; % Skipping RevitType for now to avoid failing Revit Export if type missing
        elseif strcmp(testPropType, 'HeatingSetpoint')
            testScenario(testVarName) = testScenario(testVarName) + 5;
        elseif strcmp(testPropType, 'CoolingSetpoint')
            testScenario(testVarName) = testScenario(testVarName) - 5;
        elseif strcmp(testPropType, 'Infiltration')
            testScenario(testVarName) = testScenario(testVarName) * 2;
        elseif strcmp(testPropType, 'PeopleCount')
            testScenario(testVarName) = testScenario(testVarName) * 2 + 10;
        elseif strcmp(testPropType, 'EffectiveRValue') || strcmp(testPropType, 'RValueEff') || strcmp(testPropType, 'UValue') || strcmp(testPropType, 'RValue')
            testScenario(testVarName) = testScenario(testVarName) * 1.5;
        elseif startsWith(testPropType, 'Weather')
            if strcmp(testPropType, 'WeatherTemperature')
                testScenario(testVarName) = testScenario(testVarName) + 5; % Increase temp by 5C
            elseif strcmp(testPropType, 'WeatherRelativeHumidity')
                testScenario(testVarName) = testScenario(testVarName) + 20; % Increase RH by 20%
            else
                testScenario(testVarName) = testScenario(testVarName) * 1.5;
            end
        else
            testScenario(testVarName) = testScenario(testVarName) * 2;
        end
        
        folderName = sprintf('var_%d', i);
        [testAvg, testPeak, testSuccess] = RunOneScenario(folderName, testScenario);
        
        if testSuccess
            diffAvg = testAvg - baseAvg;
            diffPeak = testPeak - basePeak;
            fprintf('  Success! Avg Diff: %+.2f, Peak Diff: %+.2f\n', diffAvg, diffPeak);
        else
            fprintf('  FAILED!\n');
            diffAvg = NaN;
            diffPeak = NaN;
        end
        
        % Log
        res = struct();
        res.Variable = testVarName;
        res.PropertyType = testPropType;
        res.Success = testSuccess;
        res.BaseAvgBtu = baseAvg;
        res.TestAvgBtu = testAvg;
        res.DiffAvgBtu = diffAvg;
        res.BasePeakBtu = basePeak;
        res.TestPeakBtu = testPeak;
        res.DiffPeakBtu = diffPeak;
        
        results{end+1} = res;
    end
    
    % Cleanup
    revit.RevertChanges();
    
    % Report summary
    fprintf('\n======================================================\n');
    fprintf('               SENSITIVITY TEST SUMMARY               \n');
    fprintf('======================================================\n');
    for i = 1:length(results)
        r = results{i};
        statusStr = 'FAIL';
        if r.Success; statusStr = 'PASS'; end
        fprintf('[%s] %s (Type: %s) -> Avg Diff: %+.2f\n', statusStr, r.Variable, r.PropertyType, r.DiffAvgBtu);
    end
    
    % Optionally save to table
    if ~isempty(results)
        resultsTable = struct2table(cell2mat(results));
        disp(resultsTable);
        writetable(resultsTable, fullfile(exportPath, 'sensitivity_summary.csv'));
        fprintf('Detailed results saved to %s\n', fullfile(exportPath, 'sensitivity_summary.csv'));
    end
end
