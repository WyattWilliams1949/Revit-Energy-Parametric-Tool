% testComplexScript.m
% A more complicated MATLAB script testing multiple parametric scenarios in Revit.

function testComplexScript()
    fprintf('=== Starting Complex Revit API Test ===\n');
    
    revit = RevitAPI('http://localhost:8080');
    
    try
        % 1. Fetch elements to find exact property names
        fprintf('\n[1/3] Fetching active elements and variables from Revit...\n');
        elements = revit.GetElements();
        
        wallPropName = '';
        weatherPropName = '';
        
        for i = 1:length(elements)
            props = elements(i).Properties;
            if ~iscell(props)
                props = num2cell(props);
            end
            for p = 1:length(props)
                propName = props{p}.Name;
                if contains(propName, 'Walls') && isempty(wallPropName)
                    wallPropName = propName;
                elseif contains(propName, 'Weather Data') && isempty(weatherPropName)
                    weatherPropName = propName;
                end
            end
        end
        
        if isempty(wallPropName) || isempty(weatherPropName)
            error('Could not find required Wall and Weather variables. Please ensure they are active in Revit.');
        end
        
        fprintf('Found Wall Property: "%s"\n', wallPropName);
        fprintf('Found Weather Property: "%s"\n', weatherPropName);
        
        % 2. Define test variations
        wallRValues = [10, 20];      % 2 Variations
        weatherTemps = [65, 75, 85]; % 3 Variations
        
        totalSims = length(wallRValues) * length(weatherTemps);
        fprintf('\n[2/3] Running 2x3 Simulation Batch (%d scenarios)...\n', totalSims);
        
        simCount = 1;
        for w = 1:length(wallRValues)
            for t = 1:length(weatherTemps)
                rValue = wallRValues(w);
                temp = weatherTemps(t);
                
                fprintf('  -> Scenario %d/%d: Wall R-Value = %d, Weather Temp = %d\n', simCount, totalSims, rValue, temp);
                
                simFolder = fullfile(pwd, sprintf('test_complex_sim_%d', simCount));
                if ~exist(simFolder, 'dir')
                    mkdir(simFolder);
                end
                
                % Use containers.Map to allow special characters in property names
                % MATLAB struct field names cannot contain spaces, so Map is safer.
                scenario = containers.Map();
                scenario(wallPropName) = rValue;
                scenario(weatherPropName) = temp;
                
                % Send to Revit to mutate and export gbXML
                success = revit.SimulateScenario(simFolder, scenario);
                
                if success
                    fprintf('     Success! Exported gbXML to: %s\n', simFolder);
                else
                    warning('     Revit reported failure for scenario %d.', simCount);
                end
                
                simCount = simCount + 1;
            end
        end
        
        % 3. Revert changes
        fprintf('\n[3/3] Reverting Revit changes to original state...\n');
        revertSuccess = revit.RevertChanges();
        if revertSuccess
            fprintf('Changes successfully reverted.\n');
        end
        
        fprintf('\n=== Complex Revit API Test Completed Successfully ===\n');
        
    catch ME
        fprintf('\n!!! Error during test execution !!!\n');
        fprintf('Message: %s\n', ME.message);
        fprintf('Please ensure the Revit Add-in is running and "MATLAB Mode" is listening on port 8080.\n');
    end
end
