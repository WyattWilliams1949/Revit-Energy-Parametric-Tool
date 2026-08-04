function printAvailableVariables(revitApi)
% printAvailableVariables Helper function to print all available simulation variables.
%   This function queries Revit for the currently active Envelope and Environment
%   elements, then pretty-prints the properties that can be varied in your
%   MATLAB scripts.
%
%   USAGE:
%       revitApi = MatlabRevitAPI('http://localhost:5000');
%       printAvailableVariables(revitApi);

    try
        elements = revitApi.GetElements();
    catch ME
        fprintf(2, 'Error: Could not connect to Revit MATLAB Mode Server.\n');
        fprintf(2, 'Make sure Revit is running and "MATLAB Mode Server" is active.\n');
        return;
    end
    
    if isempty(elements)
        fprintf('No elements returned from Revit.\n');
        return;
    end
    
    fprintf('\n======================================================\n');
    fprintf('           AVAILABLE SIMULATION VARIABLES\n');
    fprintf('======================================================\n');
    
    % Group by Category
    categories = unique({elements.Category});
    
    for i = 1:length(categories)
        cat = categories{i};
        fprintf('\n[%s]\n', upper(cat));
        
        catElements = elements(strcmp({elements.Category}, cat));
        
        for j = 1:length(catElements)
            elem = catElements(j);
            
            % Print element name
            fprintf('  - %s\n', elem.ElementName);
            
            % Check if it has properties
            if isfield(elem, 'Properties') && ~isempty(elem.Properties)
                props = elem.Properties;
                for k = 1:length(props)
                    prop = props(k);
                    
                    % Print property name and available properties
                    propNames = strjoin(prop.AvailableProperties, ', ');
                    
                    % Find default unit if possible
                    unitsStr = strjoin(prop.AvailableUnits, ', ');
                    
                    fprintf('      * Key Name: "%s"\n', prop.Name);
                    fprintf('        Properties: [%s]\n', propNames);
                    fprintf('        Units:      [%s]\n', unitsStr);
                end
            else
                fprintf('      * (No properties available)\n');
            end
        end
    end
    
    fprintf('\n======================================================\n');
    fprintf('USAGE EXAMPLE:\n');
    fprintf('  variables(1) = struct( ...\n');
    fprintf('      ''Name'', ''Basic Wall: Exterior - CMU Insulated'', ...\n');
    fprintf('      ''Property'', ''RevitType'', ...\n');
    fprintf('      ''Method'', ''ReplaceElements'', ...\n');
    fprintf('      ''SelectedStudTypes'', {{''Exterior - CMU on Mtl. Stud''}}, ...\n');
    fprintf('      ''SelectedInsulationTypes'', {{''Batt Insulation - 6"''}}, ...\n');
    fprintf('      ''FramingFactor'', 25, ...\n');
    fprintf('      ''VaryRValueWithTemp'', true, ...\n');
    fprintf('      ''RValueTempEquation'', ''0,4;5,6;10,8'', ...\n');
    fprintf('      ''RValueTempEquationUnit'', ''°F'' ...\n');
    fprintf('  );\n');
    fprintf('======================================================\n\n');
end
