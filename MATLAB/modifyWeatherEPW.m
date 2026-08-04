function outputPath = modifyWeatherEPW(baseEpwPath, outputPath, scenarioDict, variableProperties, unitsDict)
    % MODIFYWEATHEREPW Modifies a base EPW file with weather variables from a scenario.
    % Reads baseEpwPath, overrides meteorological data if present in scenarioDict,
    % and writes to outputPath.
    
    lines = readlines(baseEpwPath);
    
    % Find which properties are weather-related
    weatherFields = {};
    fields = fieldnames(scenarioDict);
    for i = 1:length(fields)
        propName = fields{i};
        if isKey(variableProperties, propName)
            propType = variableProperties(propName);
            if startsWith(propType, 'Weather')
                weatherFields{end+1} = propName;
            end
        elseif startsWith(propName, 'Weather')
            % If it's a Weather parameter not attached to a Revit element
            weatherFields{end+1} = propName;
        end
    end
    
    if isempty(weatherFields)
        copyfile(baseEpwPath, outputPath);
        return;
    end
    
    % EPW Data starts at line 9 (index 8 in zero-based, 9 in 1-based)
    for i = 9:length(lines)
        if strlength(lines(i)) == 0
            continue;
        end
        
        parts = split(lines(i), ',');
        if length(parts) < 7
            continue;
        end
        
        for w = 1:length(weatherFields)
            key = weatherFields{w};
            propType = '';
            if isKey(variableProperties, key)
                propType = variableProperties(key);
            else
                propType = key; % Fallback: assume the variable name IS the property type
            end
            
            val = scenarioDict.(key);
            
            % If it's an equation, evaluate it
            if ischar(val) || isstring(val)
                % Add Hour and Day to dict for evaluation
                hourFromMidnight = str2double(parts{4});
                dayFromStart = floor((i - 9) / 24);
                tempDict = scenarioDict;
                tempDict.Hour = hourFromMidnight;
                tempDict.Day = dayFromStart;
                val = evaluateEquations(val, tempDict);
            end
            
            unit = '';
            if isKey(unitsDict, key)
                unit = unitsDict(key);
            end
            
            switch propType
                case 'WeatherTemperature'
                    if strcmp(unit, '°F'), val = (val - 32) * 5/9;
                    elseif strcmp(unit, 'K'), val = val - 273.15; end
                    parts{7} = num2str(round(val, 2));
                case 'WeatherTemperatureOffset'
                    if strcmp(unit, '°F'), val = val * 5/9; end
                    orig = str2double(parts{7});
                    parts{7} = num2str(round(orig + val, 2));
                case 'WeatherDewPoint'
                    if strcmp(unit, '°F'), val = (val - 32) * 5/9;
                    elseif strcmp(unit, 'K'), val = val - 273.15; end
                    if length(parts) > 7, parts{8} = num2str(round(val, 2)); end
                case 'WeatherDewPointOffset'
                    if strcmp(unit, '°F'), val = val * 5/9; end
                    if length(parts) > 7
                        orig = str2double(parts{8});
                        parts{8} = num2str(round(orig + val, 2));
                    end
                case 'WeatherRelativeHumidity'
                    if length(parts) > 8, parts{9} = num2str(round(val, 0)); end
                case 'WeatherRelativeHumidityOffset'
                    if length(parts) > 8
                        orig = str2double(parts{9});
                        parts{9} = num2str(round(orig + val, 0));
                    end
                case 'WeatherAtmosphericPressure'
                    if strcmp(unit, 'inHg'), val = val * 3386.389; end
                    if length(parts) > 9, parts{10} = num2str(round(val, 0)); end
                case 'WeatherSolarRadiation'
                    if strcmp(unit, 'BTU/ft²'), val = val * 3.15459; end
                    if length(parts) > 13, parts{14} = num2str(round(val, 0)); end
                case 'WeatherDirectNormalRadiation'
                    if strcmp(unit, 'BTU/ft²'), val = val * 3.15459; end
                    if length(parts) > 14, parts{15} = num2str(round(val, 0)); end
                case 'WeatherDiffuseHorizontalRadiation'
                    if strcmp(unit, 'BTU/ft²'), val = val * 3.15459; end
                    if length(parts) > 15, parts{16} = num2str(round(val, 0)); end
                case 'WeatherWindDirection'
                    if length(parts) > 20, parts{21} = num2str(round(val, 0)); end
                case 'WeatherWindSpeed'
                    if strcmp(unit, 'mph'), val = val * 0.44704; end
                    if length(parts) > 21, parts{22} = num2str(round(val, 1)); end
                case 'WeatherWindSpeedOffset'
                    if strcmp(unit, 'mph'), val = val * 0.44704; end
                    if length(parts) > 21
                        orig = str2double(parts{22});
                        parts{22} = num2str(round(orig + val, 1));
                    end
                case 'WeatherTotalSkyCover'
                    if length(parts) > 22, parts{23} = num2str(round(val, 0)); end
            end
        end
        lines(i) = join(parts, ',');
    end
    
    writelines(lines, outputPath);
end
