require 'json'

class DynamicRvalueWorkspace < OpenStudio::Measure::EnergyPlusMeasure
  def name
    return "Dynamic R-Value Workspace Measure"
  end

  def description
    return "Applies TemperatureDependentThermalConductivity to exterior envelope materials."
  end

  def arguments(workspace)
    args = OpenStudio::Measure::OSArgumentVector.new
    
    arg = OpenStudio::Measure::OSArgument.makeStringArgument("json_overrides", true)
    arg.setDefaultValue("{}")
    args << arg

    return args
  end

  def run(workspace, runner, user_arguments)
    super(workspace, runner, user_arguments)

    if !runner.validateUserArguments(arguments(workspace), user_arguments)
      return false
    end

    json_str = runner.getStringArgumentValue("json_overrides", user_arguments)
    
    overrides = {}
    begin
      overrides = JSON.parse(json_str)
    rescue => e
      runner.registerError("Failed to parse json_overrides: #{e.message}")
      return false
    end

    if overrides.empty?
      return true
    end

    # Parse thresholds for each config
    # Output hash: parsed_overrides[material_key] = [ {temp: x, r_value: y}, ... ]
    parsed_overrides = {}
    
    overrides.each do |mat_key, config|
      eq_str = config["equation"] || ""
      eq_unit = config["unit"] || "°F"
      
      thresholds = []
      eq_str.split(';').each do |pair|
        parts = pair.split(',')
        if parts.size == 2
          temp = parts[0].to_f
          r_val = parts[1].to_f
          if eq_unit.include?("F")
            temp = (temp - 32.0) * 5.0 / 9.0
          end
          thresholds << { temp: temp, r_value: r_val }
        end
      end
      thresholds.sort_by! { |t| t[:temp] }
      
      parsed_overrides[mat_key] = thresholds unless thresholds.empty?
    end

    if parsed_overrides.empty?
      return true
    end

    # Find exterior constructions
    ext_constructions = []
    workspace.getObjectsByType("BuildingSurface:Detailed".to_IddObjectType).each do |surf|
      bc = surf.getString(4).get.downcase # Outside Boundary Condition
      type = surf.getString(1).get.downcase # Surface Type
      if bc == "outdoors" && (type == "wall" || type == "roof")
        if surf.getString(2).is_initialized
          ext_constructions << surf.getString(2).get
        end
      end
    end
    ext_constructions.uniq!

    applied_count = 0

    ext_constructions.each do |const_name|
      consts = workspace.getObjectsByName(const_name, true)
      next if consts.empty?
      const = consts[0]

      # Iterate over all material layers in the construction
      # In EnergyPlus/OpenStudio, construction layers start at field index 1
      num_layers = const.numFields - 1
      
      (1..num_layers).each do |i|
        layer_name = const.getString(i).get
        mats = workspace.getObjectsByName(layer_name, true)
        next if mats.empty?
        mat = mats[0]
        mat_name = mat.getString(0).get
        
        # Determine if this material matches any of our overrides
        matched_key = nil
        if parsed_overrides.key?(mat_name)
          matched_key = mat_name
        else
          # Try case-insensitive or partial match, or GLOBAL if i==1
          parsed_overrides.keys.each do |k|
            if k == "GLOBAL" && i == 1
              matched_key = "GLOBAL"
            elsif mat_name.downcase.include?(k.downcase)
              matched_key = k
              break
            end
          end
        end
        
        next unless matched_key
        
        thresholds = parsed_overrides[matched_key]
        thickness = 0.1 # default 10cm if converting from NoMass

        if mat.iddObject.name == "Material:NoMass"
          # Convert Material:NoMass to Material
          r_value = mat.getDouble(2).get
          cond = thickness / r_value
          
          new_mat = OpenStudio::IdfObject.new("Material".to_IddObjectType)
          new_mat.setName("#{mat_name}_Standardized")
          new_mat.setString(1, "Smooth") # Roughness
          new_mat.setDouble(2, thickness) # Thickness
          new_mat.setDouble(3, cond) # Conductivity
          new_mat.setDouble(4, 100) # Density
          new_mat.setDouble(5, 1000) # Specific Heat
          
          workspace.addObject(new_mat)
          const.setString(i, new_mat.name.get)
          mat_name = new_mat.name.get
        else
          thickness = mat.getDouble(2).get
        end

        # Check if already applied to this material (multiple constructions can share materials)
        existing_props = workspace.getObjectsByType("MaterialProperty:TemperatureDependentThermalConductivity".to_IddObjectType)
        already_has_prop = existing_props.any? { |p| p.getString(0).get == mat_name }
        
        unless already_has_prop
          prop = OpenStudio::IdfObject.new("MaterialProperty:TemperatureDependentThermalConductivity".to_IddObjectType)
          prop.setString(0, mat_name)
          prop.setString(1, "LinearInterpolation")
          
          thresholds.each_with_index do |th, idx|
            r_value_si = th[:r_value] * 0.176110
            r_value_si = 0.1 if r_value_si <= 0
            cond = thickness / r_value_si
            
            prop.setDouble(2 + idx*2, th[:temp])
            prop.setDouble(3 + idx*2, cond)
          end
          workspace.addObject(prop)
          applied_count += 1
        end
      end
    end

    runner.registerInfo("Applied TemperatureDependentThermalConductivity to #{applied_count} materials in exterior constructions.")
    return true
  end
end

DynamicRvalueWorkspace.new.registerWithApplication
