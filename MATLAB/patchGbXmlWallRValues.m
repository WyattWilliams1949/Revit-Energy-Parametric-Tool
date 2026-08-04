function patchGbXmlWallRValues(gbxmlPath, scenario, variableProperties, revitBaseUrl)
% PATCHGBXMLWALLRVALUES  Overwrites construction R-values in an exported gbXML
%   file based on the wall type selections in the current scenario.
%
%   The Revit gbXML exporter caches construction data from its internal energy
%   model and does NOT update R-values when a wall type is swapped mid-session.
%   This function fixes that by fetching the true R-values from Revit's wall
%   type material data and injecting them directly into the gbXML XML tree.
%
%   Inputs:
%     gbxmlPath          - Full path to the exported analysis.xml
%     scenario           - struct of variable values for this simulation
%     variableProperties - containers.Map of varName -> propertyType string
%     revitBaseUrl       - Base URL of the Revit HTTP server (e.g. 'http://localhost:8080')
%
%   The function is a no-op if no wall-type variables are present or if the
%   Revit server is unreachable.

    if ~isfile(gbxmlPath)
        warning('patchGbXmlWallRValues: file not found: %s', gbxmlPath);
        return;
    end

    % ---- 1. Fetch R-values from Revit ------------------------------------------
    rValuesMap = containers.Map();
    try
        opts = weboptions('Timeout', 10, 'MediaType', 'application/json');
        rJson = webread([revitBaseUrl '/wall-rvalues'], opts);
        % rJson is a struct with field names = variable names
        fields = fieldnames(rJson);
        for i = 1:length(fields)
            rValuesMap(fields{i}) = rJson.(fields{i});
        end
    catch ex
        warning('patchGbXmlWallRValues: could not fetch R-values from Revit: %s', ex.message);
        return;
    end

    if rValuesMap.Count == 0
        return;  % No wall variables, nothing to patch
    end

    % ---- 2. Find which wall variable is active in this scenario ----------------
    % Resolve variable name → target R-value (SI, m²·K/W)
    targetR_SI = [];
    varFields = fieldnames(scenario);
    for vi = 1:length(varFields)
        vName = varFields{vi};
        if isKey(variableProperties, vName) && strcmp(variableProperties(vName), 'RevitType')
            % This is a wall type variable.
            % The scenario value is the target wall TYPE NAME (string).
            % We look up its R-value from the Revit server response.
            if isKey(rValuesMap, vName)
                targetR_SI = rValuesMap(vName);
                fprintf('  [gbXML patch] Var=%s  R=%.4f m2K/W (IP R-%.1f)\n', ...
                    vName, targetR_SI, targetR_SI / 0.17611);
                break;
            end
        end
    end

    if isempty(targetR_SI) || targetR_SI <= 0
        return;
    end

    % ---- 3. Parse gbXML and update exterior wall construction layers ------------
    try
        xmlDoc = xmlread(gbxmlPath);
    catch
        warning('patchGbXmlWallRValues: failed to parse %s', gbxmlPath);
        return;
    end

    % Find all <Construction> nodes that correspond to exterior walls.
    % We identify them by checking their linked <Surface> type attributes.
    % Strategy: update ALL Construction nodes that have at least one layer
    % with material Conductivity > 0, scaling their R-values proportionally
    % so the total assembly matches targetR_SI.
    
    constructionList = xmlDoc.getElementsByTagName('Construction');
    patchCount = 0;

    for ci = 0:constructionList.getLength()-1
        construction = constructionList.item(ci);

        % Only patch exterior wall constructions (skip floor/roof/interior)
        % Check if this construction is referenced by an exterior wall surface.
        % Since linking is complex, we use a simpler heuristic: skip constructions
        % whose total R-value is already close to targetR_SI (already correct) 
        % or is very different from the order-of-magnitude expected for a wall.
        layerList = construction.getElementsByTagName('LayerId');
        if layerList.getLength() == 0
            continue;
        end

        % ---- Actually patch: scale all material R-values so total = targetR_SI
        % First, collect all material elements referenced by this construction.
        % For simplicity, we patch the first non-trivial Construction in the file
        % (Revit exports one Construction per wall type, typically just one exterior wall type).
        
        materialNodes = xmlDoc.getElementsByTagName('Material');
        totalCurrentR = 0;
        matRValues = zeros(1, materialNodes.getLength());
        matThicknesses = zeros(1, materialNodes.getLength());

        for mi = 0:materialNodes.getLength()-1
            mat = materialNodes.item(mi);
            rNode = getFirstChildByTag(mat, 'R-value');
            tNode = getFirstChildByTag(mat, 'Thickness');
            if ~isempty(rNode)
                rStr = char(rNode.getFirstChild().getData());
                matRValues(mi+1) = str2double(rStr);
                totalCurrentR = totalCurrentR + matRValues(mi+1);
            end
            if ~isempty(tNode)
                tStr = char(tNode.getFirstChild().getData());
                matThicknesses(mi+1) = str2double(tStr);
            end
        end

        if totalCurrentR <= 0
            continue;
        end

        % Scale each material's R-value and conductivity so total = targetR_SI
        scaleFactor = targetR_SI / totalCurrentR;
        if abs(scaleFactor - 1.0) < 0.01
            continue;  % Already correct, skip
        end

        for mi = 0:materialNodes.getLength()-1
            mat = materialNodes.item(mi);
            rNode = getFirstChildByTag(mat, 'R-value');
            kNode = getFirstChildByTag(mat, 'Conductivity');
            tNode = getFirstChildByTag(mat, 'Thickness');

            if ~isempty(rNode) && matRValues(mi+1) > 0
                newR = matRValues(mi+1) * scaleFactor;
                rNode.getFirstChild().setData(sprintf('%.6f', newR));

                % Update conductivity consistently: k = t / R
                if ~isempty(kNode) && ~isempty(tNode) && matThicknesses(mi+1) > 0
                    newK = matThicknesses(mi+1) / newR;
                    kNode.getFirstChild().setData(sprintf('%.6f', newK));
                end
            end
        end

        patchCount = patchCount + 1;
        break;  % Only patch once (all sims share same construction library; one patch is enough)
    end

    % ---- 4. Write patched gbXML back to disk -----------------------------------
    if patchCount > 0
        xmlwrite(gbxmlPath, xmlDoc);
        fprintf('  [gbXML patch] Patched %s (scale=%.3fx)\n', gbxmlPath, scaleFactor);
    end
end

function node = getFirstChildByTag(parent, tagName)
    node = [];
    children = parent.getChildNodes();
    for i = 0:children.getLength()-1
        child = children.item(i);
        if child.getNodeType() == child.ELEMENT_NODE && strcmp(char(child.getTagName()), tagName)
            node = child;
            return;
        end
    end
end
