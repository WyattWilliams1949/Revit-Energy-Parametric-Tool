function [avgBtu, peakBtu] = parseOpenStudioResults(simFolder)
    % PARSEOPENSTUDIORESULTS Parses data_point_out.json to calculate total site energy and peak demand
    
    avgBtu = 0;
    peakBtu = 0;
    
    jsonPath = fullfile(simFolder, 'run', 'data_point_out.json');
    
    if ~isfile(jsonPath)
        warning('Results JSON not found: %s', jsonPath);
        return;
    end
    
    % 1. Parse Total Site Energy from JSON
    try
        jsonText = fileread(jsonPath);
        data = jsondecode(jsonText);
        if isfield(data, 'OpenStudioResults')
            osRes = data.OpenStudioResults;
            if isfield(osRes, 'total_site_energy')
                avgBtu = osRes.total_site_energy * 1000; % Convert kBtu to Btu
            end
        end
    catch ME
        warning('Failed to parse JSON results: %s', ME.message);
    end
    
    % 2. Parse Peak Thermal Load from epluszsz.csv
    zszPath = fullfile(simFolder, 'run', 'epluszsz.csv');
    if isfile(zszPath)
        try
            lines = readlines(zszPath);
            if length(lines) > 1
                headers = split(lines(1), ',');
                
                % Find indices of Heat Load and Cool Load columns
                heatCols = find(contains(headers, 'Des Heat Load [W]'));
                coolCols = find(contains(headers, 'Des Sens Cool Load [W]'));
                
                % Read the numeric data
                dataMat = readmatrix(zszPath, 'NumHeaderLines', 1);
                
                % Sum the max heating and max cooling across all zones
                totalPeakW = 0;
                for i = 1:length(heatCols)
                    totalPeakW = totalPeakW + max(dataMat(:, heatCols(i)));
                end
                for i = 1:length(coolCols)
                    totalPeakW = totalPeakW + max(dataMat(:, coolCols(i)));
                end
                
                % Convert Watts to Btu/hr
                peakBtu = totalPeakW * 3.412142; 
            end
        catch ME
            warning('Failed to parse epluszsz.csv: %s', ME.message);
        end
    end
end
