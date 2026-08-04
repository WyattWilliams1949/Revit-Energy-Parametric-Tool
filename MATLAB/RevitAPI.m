classdef RevitAPI
    % REVITAPI A wrapper class to communicate with the Revit Add-in Local REST API
    %   Handles fetching elements and pushing simulation scenarios to Revit.
    
    properties
        BaseUrl = 'http://localhost:8080'
    end
    
    methods
        function obj = RevitAPI(baseUrl)
            if nargin > 0
                obj.BaseUrl = baseUrl;
            end
        end
        
        function elements = GetElements(obj)
            % GETELEMENTS Retrieves active envelope types and weather variables from Revit
            options = weboptions('Timeout', 10, 'MediaType', 'application/json');
            try
                elements = webread(sprintf('%s/elements', obj.BaseUrl), options);
            catch ME
                error('RevitAPI:ConnectionError', 'Failed to connect to Revit. Ensure MATLAB Mode is running in Revit. Error: %s', ME.message);
            end
        end
        
        function success = SimulateScenario(obj, simFolder, scenarioDict)
            % SIMULATESCENARIO Sends a scenario to Revit to mutate the model and export gbXML.
            %   simFolder: Path to the folder where analysis.xml will be exported
            %   scenarioDict: Structure or Map containing variable overrides
            
            payload = struct();
            payload.simFolder = simFolder;
            payload.scenario = scenarioDict;
            
            options = weboptions('MediaType', 'application/json', 'Timeout', 60);
            try
                response = webwrite(sprintf('%s/simulate', obj.BaseUrl), payload, options);
                success = response.success;
            catch ME
                error('RevitAPI:SimulationError', 'Failed to run simulation in Revit. Error: %s', ME.message);
            end
        end
        
        function success = RevertChanges(obj)
            % REVERTCHANGES Reverts any type overrides or material changes in Revit.
            options = weboptions('MediaType', 'application/json', 'Timeout', 10);
            try
                response = webwrite(sprintf('%s/revert', obj.BaseUrl), struct(), options);
                success = response.success;
            catch ME
                warning('RevitAPI:RevertError', 'Failed to revert Revit changes. Error: %s', ME.message);
                success = false;
            end
        end
    end
end
