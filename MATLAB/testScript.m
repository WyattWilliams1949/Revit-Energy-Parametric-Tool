% testScript.m
% A simple MATLAB script to test the RevitAPI wrapper connection to the Revit Add-in.

function testScript()
    fprintf('=== Starting Revit API Test ===\n');
    
    % Initialize the Revit API wrapper
    % Assumes the Revit Add-in is running and "MATLAB Mode" is active on port 8080.
    revit = RevitAPI('http://localhost:8080');
    
    try
        % 1. Test fetching elements
        fprintf('\n[1/3] Fetching active elements and variables from Revit...\n');
        elements = revit.GetElements();
        
        fprintf('Successfully retrieved elements:\n');
        for i = 1:length(elements)
            fprintf('  - Element: %s\n', elements(i).ElementName);
            props = elements(i).Properties;
            if iscell(props)
                for p = 1:length(props)
                    fprintf('      Property: %s = %s\n', props{p}.Name, props{p}.Property);
                end
            else
                for p = 1:length(props)
                    fprintf('      Property: %s = %s\n', props(p).Name, props(p).Property);
                end
            end
        end
        
        % 2. Test simulating a simple scenario
        fprintf('\n[2/3] Testing simulation scenario (gbXML export)...\n');
        simFolder = fullfile(pwd, 'test_simulation_output');
        if ~exist(simFolder, 'dir')
            mkdir(simFolder);
        end
        
        % Create an empty or dummy scenario
        % We send an empty struct, Revit will use existing values
        dummyScenario = struct();
        
        success = revit.SimulateScenario(simFolder, dummyScenario);
        if success
            fprintf('Simulation scenario accepted by Revit. gbXML should be exported to: %s\n', simFolder);
        else
            warning('Revit reported failure for the simulation scenario.');
        end
        
        % 3. Revert any changes
        fprintf('\n[3/3] Reverting Revit changes...\n');
        revertSuccess = revit.RevertChanges();
        if revertSuccess
            fprintf('Changes successfully reverted.\n');
        else
            warning('Failed to revert changes in Revit.');
        end
        
        fprintf('\n=== Revit API Test Completed Successfully ===\n');
        
    catch ME
        fprintf('\n!!! Error during test execution !!!\n');
        fprintf('Message: %s\n', ME.message);
        fprintf('Please ensure the Revit Add-in is running and "MATLAB Mode" is listening on port 8080.\n');
    end
end
