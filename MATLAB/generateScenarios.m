function scenarios = generateScenarios(variables)
    % GENERATESCENARIOS Generates a permutation grid of scenarios.
    %   variables: a cell array of structs. Each struct defines a variable:
    %     - Name: string
    %     - Method: 'Constant', 'Array', 'MinMaxInterval', 'Equation'
    %     - Value(s) depending on method.
    %
    % Returns a cell array of scenario structs (each mapping variable name to value).
    
    % Initialize with one empty scenario
    scenarios = {struct()};
    
    for i = 1:length(variables)
        varDef = variables{i};
        varName = varDef.Name;
        method = varDef.Method;
        
        currentValues = {};
        
        switch method
            case 'Constant'
                currentValues = { varDef.Value };
                
            case 'Array'
                % varDef.ArrayValues should be a cell array or numeric array
                if iscell(varDef.ArrayValues)
                    currentValues = varDef.ArrayValues;
                else
                    currentValues = num2cell(varDef.ArrayValues);
                end
                
            case 'MinMaxInterval'
                % varDef.Min, varDef.Max, varDef.Interval, varDef.IsIntervalCount
                if isfield(varDef, 'IsIntervalCount') && varDef.IsIntervalCount
                    steps = linspace(varDef.Min, varDef.Max, varDef.Interval);
                else
                    steps = varDef.Min : varDef.Interval : varDef.Max;
                end
                currentValues = num2cell(steps);
                
            case 'Equation'
                % We store the equation string. It gets evaluated later.
                currentValues = { varDef.EquationString };
                
            case 'TypeSelection'
                % Revit types string array or cell array
                currentValues = varDef.SelectedTypes;
                
            case {'ReplaceElements', 'Monolithic'}
                % Produces WallModConfig structs
                for sIdx = 1:length(varDef.SelectedStudTypes)
                    for iIdx = 1:length(varDef.SelectedInsulationTypes)
                        cfg = struct();
                        if strcmp(method, 'ReplaceElements')
                            cfg.Method = 4; % 4 = ReplaceElements
                        else
                            cfg.Method = 6; % 6 = Monolithic
                        end
                        cfg.StudType = varDef.SelectedStudTypes{sIdx};
                        cfg.InsulationType = varDef.SelectedInsulationTypes{iIdx};
                        cfg.FramingFactor = varDef.FramingFactor;
                        
                        if isfield(varDef, 'VaryRValueWithTemp')
                            cfg.VaryRValueWithTemp = varDef.VaryRValueWithTemp;
                        else
                            cfg.VaryRValueWithTemp = false;
                        end
                        
                        if isfield(varDef, 'RValueTempEquation')
                            cfg.RValueTempEquation = varDef.RValueTempEquation;
                        end
                        if isfield(varDef, 'RValueTempEquationUnit')
                            cfg.RValueTempEquationUnit = varDef.RValueTempEquationUnit;
                        end
                        
                        currentValues{end+1} = cfg;
                    end
                end
                
            case 'EffectiveRValue'
                cfg = struct();
                cfg.Method = 5; % 5 = EffectiveRValue in enum VariableMethod
                cfg.StudRValue = varDef.StudRValue;
                cfg.InsulationRValue = varDef.InsulationRValue;
                cfg.WindowRValue = varDef.WindowRValue;
                cfg.DoorRValue = varDef.DoorRValue;
                cfg.FramingFactor = varDef.FramingFactor;
                cfg.Density = varDef.Density;
                cfg.SpecificHeat = varDef.SpecificHeat;
                currentValues = {cfg};
                
            otherwise
                currentValues = {0};
        end
        
        % Multiply scenarios by currentValues
        newScenarios = {};
        for sIdx = 1:length(scenarios)
            baseScenario = scenarios{sIdx};
            for vIdx = 1:length(currentValues)
                newScenario = baseScenario;
                newScenario.(varName) = currentValues{vIdx};
                newScenarios{end+1} = newScenario;
            end
        end
        scenarios = newScenarios;
    end
end
