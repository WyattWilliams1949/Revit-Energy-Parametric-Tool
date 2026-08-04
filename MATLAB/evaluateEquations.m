function result = evaluateEquations(equationStr, scenarioDict)
    % EVALUATEEQUATIONS Safely evaluates an equation string substituting variables from scenarioDict.
    % Uses MATLAB's eval.
    % Note: equationStr must be valid MATLAB syntax, e.g. "2 * Wall_RValue + 5"
    
    if startsWith(equationStr, '=')
        equationStr = extractAfter(equationStr, 1);
    end
    
    % Get field names from struct
    fields = fieldnames(scenarioDict);
    for i = 1:length(fields)
        val = scenarioDict.(fields{i});
        if isnumeric(val)
            % Assign variable in local workspace so eval can see it
            eval(sprintf('%s = %f;', fields{i}, val));
        end
    end
    
    try
        result = eval(equationStr);
    catch ME
        warning('Failed to evaluate equation: %s', ME.message);
        result = 0;
    end
end
