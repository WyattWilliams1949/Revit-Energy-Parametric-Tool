function test_jsonencode()
    map = containers.Map();
    map('Walls: Generic - 8" (R-Value)') = 15;
    
    payload = struct();
    payload.simFolder = 'C:\test';
    
    try
        payload.scenario = map;
        disp(jsonencode(payload));
    catch ME
        disp(ME.message);
    end
end
