function resultsTable = runSimulationBatch(variables, baseEpwPath, exportPath, openStudioCliPath, debugMode)
    % RUNSIMULATIONBATCH Orchestrates the full parametric simulation loop via MATLAB.
    %   variables:          Cell array of variable definitions
    %   baseEpwPath:        Path to default or user-provided .epw file
    %   exportPath:         Base directory for storing temporary simulation folders
    %   openStudioCliPath:  Path to openstudio.exe
    %   debugMode:          (optional) true = keep all sim files; false (default) = delete after each run
    
    if nargin < 5
        debugMode = false;
    end
    
    revit = RevitAPI();
    
    % Ensure Revit is responding
    try
        activeElements = revit.GetElements();
    catch ME
        error('Cannot communicate with Revit. Is MATLAB Mode running? %s', ME.message);
    end
    
    % Flatten elements into property map
    variableProperties = containers.Map();
    for e = 1:length(activeElements)
        props = activeElements(e).Properties;
        if iscell(props)
            for p = 1:length(props)
                variableProperties(props{p}.Name) = props{p}.Property;
            end
        else
            for p = 1:length(props)
                variableProperties(props(p).Name) = props(p).Property;
            end
        end
    end
    
    % Sort variables: Geometric first (need Revit export), Non-Geometric last.
    geometricVars = {};
    nonGeometricVars = {};
    
    for i = 1:length(variables)
        varDef = variables{i};
        varName = varDef.Name;
        
        if isKey(variableProperties, varName)
            propType = variableProperties(varName);
            if strcmp(propType, 'HeatingSetpoint') || ...
               strcmp(propType, 'CoolingSetpoint') || ...
               strcmp(propType, 'Infiltration') || ...
               strcmp(propType, 'PeopleCount')
                nonGeometricVars{end+1} = varDef;
            else
                geometricVars{end+1} = varDef;
            end
        else
            if startsWith(varName, 'Weather')
                nonGeometricVars{end+1} = varDef;
            else
                geometricVars{end+1} = varDef;
            end
        end
    end
    
    variables = [geometricVars, nonGeometricVars];
    
    scenarios = generateScenarios(variables);
    totalSims = length(scenarios);
    
    fprintf('Generated %d scenarios to simulate. Debug mode: %s\n', totalSims, mat2str(debugMode));
    
    results = cell(totalSims, 1);
    
    lastGeomKey = '';
    lastBaseGbxmlPath = '';
    customMeasurePath = 'e:/Documents/AntigravityIDE/Revit Add-in/Measures';
    
    for i = 1:totalSims
        fprintf('Running Scenario %d of %d...\n', i, totalSims);
        
        scenario = scenarios{i};
        simId = sprintf('Sim_%s', num2hex(rand()));
        simFolder = fullfile(exportPath, simId);
        if ~exist(simFolder, 'dir')
            mkdir(simFolder);
        end
        
        % 1. EPW Weather Gen
        epwDest = fullfile(simFolder, 'custom_weather.epw');
        unitsDict = containers.Map(); 
        modifyWeatherEPW(baseEpwPath, epwDest, scenario, variableProperties, unitsDict);
        
        % 1.5 Copy matching .stat and .ddy files
        [epwDir, epwName, ~] = fileparts(baseEpwPath);
        baseStatPath = fullfile(epwDir, [epwName, '.stat']);
        baseDdyPath = fullfile(epwDir, [epwName, '.ddy']);
        
        statDest = fullfile(simFolder, 'custom_weather.stat');
        ddyDest = fullfile(simFolder, 'custom_weather.ddy');
        
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
        
        % Separate Geometric and Non-Geometric Scenarios
        geomScenario = struct();
        nonGeomScenario = struct();
        
        fields = fieldnames(scenario);
        for f = 1:length(fields)
            fName = fields{f};
            if isKey(variableProperties, fName)
                propType = variableProperties(fName);
                if strcmp(propType, 'HeatingSetpoint') || ...
                   strcmp(propType, 'CoolingSetpoint') || ...
                   strcmp(propType, 'Infiltration') || ...
                   strcmp(propType, 'PeopleCount')
                    nonGeomScenario.(fName) = scenario.(fName);
                else
                    geomScenario.(fName) = scenario.(fName);
                end
            else
                if startsWith(fName, 'Weather')
                    nonGeomScenario.(fName) = scenario.(fName);
                else
                    geomScenario.(fName) = scenario.(fName);
                end
            end
        end
        
        % Check if geometry changed (drives whether we need a new Revit export)
        geomKey = jsonencode(geomScenario);
        
        if ~strcmp(geomKey, lastGeomKey)
            fprintf('  Geometry changed. Calling Revit to export Base gbXML...\n');
            try
                success = revit.SimulateScenario(simFolder, geomScenario);
                % Revert changes so Revit model is clean for the next scenario
                revit.RevertChanges();
            catch ex
                revit.RevertChanges();
                warning('Error during Revit SimulateScenario: %s', ex.message);
                success = false;
            end
            
            if ~success
                warning('Revit failed to export scenario %d.', i);
                results{i} = struct('Scenario', i, 'AvgBtu', NaN, 'PeakBtu', NaN);
                continue;
            end
            
            lastGeomKey = geomKey;
            lastBaseGbxmlPath = fullfile(simFolder, 'analysis.xml');
            % NOTE: We do NOT patch the gbXML here anymore. R-value changes are applied
            % correctly by the ApplyParametricVariations OpenStudio measure after gbXML import.
        else
            fprintf('  Geometry unchanged. Reusing cached gbXML...\n');
            copyfile(lastBaseGbxmlPath, fullfile(simFolder, 'analysis.xml'));
        end
        
        % Build measure arguments using JSON overrides
        % -------------------------------------------------------------------
        % This map controls what the ApplyParametricVariations Ruby measure applies
        % inside OpenStudio AFTER gbXML import. It handles:
        %   - Space setpoints (HeatingSetpoint, CoolingSetpoint)
        %   - Infiltration and occupancy
        %   - Wall/Floor/Roof R-values (EffectiveRValue, RevitType wall swaps)
        % -------------------------------------------------------------------
        overridesMap = containers.Map();
        for f = 1:length(fields)
            fName = fields{f};
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
                    spaceMap(pType) = scenario.(fName);
                    
                    unitVarName = strrep(fName, pType, [pType 'Unit']);
                    if isKey(scenario, unitVarName)
                        spaceMap([pType 'Unit']) = scenario.(unitVarName);
                    end
                    
                % --- Construction Properties (RValue, UValue, etc) ---
                elseif strcmp(pType, 'RValue') || strcmp(pType, 'UValue') || strcmp(pType, 'EffectiveRValue') || strcmp(pType, 'RValueEff')
                    rIP = scenario.(fName);
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
                    targetTypeName = scenario.(fName);
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
        
        % Build the JSON measure step string
        measureStep = '';
        if overridesMap.Count > 0
            jsonParts = {};
            spKeys = overridesMap.keys;
            for k = 1:length(spKeys)
                spName = spKeys{k};
                spNameSafe = strrep(spName, '"', '');
                
                pMap = overridesMap(spName);
                pKeys = pMap.keys;
                
                spParts = {};
                for p = 1:length(pKeys)
                    val = pMap(pKeys{p});
                    if ischar(val) || isstring(val)
                        valSafe = strrep(val, '"', '');
                        spParts{end+1} = sprintf('\\\"%s\\\": \\\"%s\\\"', pKeys{p}, valSafe);
                    elseif isstruct(val)
                        jsonStr = jsonencode(val);
                        jsonStrSafe = strrep(jsonStr, '"', '\\"');
                        spParts{end+1} = sprintf('\\\"%s\\\": %s', pKeys{p}, jsonStrSafe);
                    else
                        spParts{end+1} = sprintf('\\\"%s\\\": %g', pKeys{p}, val);
                    end
                end
                
                jsonParts{end+1} = sprintf('\\\"%s\\\": { %s }', spNameSafe, strjoin(spParts, ', '));
            end
            
            jsonStrEscaped = sprintf('{ %s }', strjoin(jsonParts, ', '));
            measureStep = sprintf('{ "measure_dir_name": "ApplyParametricVariations", "name": "Apply Variations", "arguments": { "json_overrides": "%s" } },\n    ', jsonStrEscaped);
        end
        
        % 3. Generate OSW
        oswPath = fullfile(simFolder, 'custom_run.osw');
        simFolderFwd = strrep(simFolder, '\', '/');
        measuresPath = 'C:/Program Files/NREL/OpenStudio CLI For Revit 2027/measures';
        
        % FIX: Use FullInteriorAndExterior instead of FullExterior.
        % FullExterior with non-convex Revit surfaces causes EnergyPlus to fall back
        % to MinimalShadowing (zone floor solar gains error). FullInteriorAndExterior
        % handles non-convex geometry correctly.
        oswJson = sprintf(['{\n' ...
          '  "seed_file": "seed_empty.osm",\n' ...
          '  "weather_file": "custom_weather.epw",\n' ...
          '  "measure_paths": [\n' ...
          '    "%s",\n' ...
          '    "%s"\n' ...
          '  ],\n' ...
          '  "file_paths": [\n' ...
          '    "%s"\n' ...
          '  ],\n' ...
          '  "run_directory": "./run",\n' ...
          '  "steps": [\n' ...
          '    { "measure_dir_name": "ChangeBuildingLocation", "name": "Change Building Location", "arguments": { "weather_file_name": "custom_weather.epw" } },\n' ...
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
        
        % 4. Run OpenStudio
        cmd = sprintf('"%s" run -w "%s"', openStudioCliPath, oswPath);
        oldDir = pwd;
        cd(simFolder);
        [status, cmdout] = system(cmd);
        cd(oldDir);
        
        if status ~= 0
            warning('OpenStudio execution failed for Scenario %d. Log: %s', i, cmdout);
        end
        
        % 5. Parse Results
        sqlPath = fullfile(simFolder, 'run', 'eplusout.sql');
        [avgBtu, peakBtu] = parseOpenStudioResults(sqlPath);
        
        res = struct();
        res.Scenario = i;
        res.AvgBtu = avgBtu;
        res.PeakBtu = peakBtu;
        
        % Flatten scenario vars into result
        fields = fieldnames(scenario);
        for f = 1:length(fields)
            val = scenario.(fields{f});
            if isstruct(val)
                res.(fields{f}) = 'StructData';
            else
                res.(fields{f}) = val;
            end
        end
        results{i} = res;
        
        % 6. Cleanup: In non-debug mode, delete the simulation folder to save disk space.
        %    In debug mode, keep everything so you can inspect the OSM, IDF, SQL, reports, etc.
        if ~debugMode
            try
                rmdir(simFolder, 's');
                fprintf('  [Cleanup] Deleted sim folder: %s\n', simFolder);
            catch cleanEx
                warning('Could not delete sim folder %s: %s', simFolder, cleanEx.message);
            end
        else
            fprintf('  [Debug] Keeping sim folder: %s\n', simFolder);
        end
    end
    
    % Convert cell array of structs to table
    resultsTable = struct2table(cell2mat(results));
end
