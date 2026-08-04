function rSI = getWallRValueByName(baseUrl, typeName)
% GETWALLRVALUEBYNAME  Fetches the SI thermal R-value of a Revit wall type by name.
%
%   rSI = getWallRValueByName(baseUrl, typeName)
%
%   Inputs:
%     baseUrl   - Base URL of the Revit HTTP server (e.g. 'http://localhost:8080')
%     typeName  - Display name of the wall type (e.g. 'Wall Insulation (2)')
%
%   Output:
%     rSI - R-value in SI units (m²·K/W). Returns 0 if the type is not found
%           or the server is unreachable.
%
%   Conversion: rIP (ft²·°F·h/BTU) = rSI / 0.17611

    rSI = 0;
    try
        encodedName = urlencode(typeName);
        url = [baseUrl '/wall-rvalue-by-name?typeName=' encodedName];
        opts = weboptions('Timeout', 10, 'ContentType', 'json');
        result = webread(url, opts);
        if isstruct(result) && isfield(result, 'rValueSI')
            rSI = result.rValueSI;
        end
    catch ex
        warning('getWallRValueByName: Could not fetch R-value for "%s": %s', typeName, ex.message);
    end
end
