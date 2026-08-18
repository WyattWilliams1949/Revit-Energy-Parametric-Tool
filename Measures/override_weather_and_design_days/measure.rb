require 'json'

class OverrideWeatherAndDesignDays < OpenStudio::Measure::ModelMeasure
  def name
    return "Override Weather and Design Days and Variables"
  end

  def description
    return "Overrides EPW data, and applies Space/Envelope parametric variables."
  end

  def modeler_description
    return "Uses json_overrides to modify weather, spaces, and constructions."
  end

  def arguments(model)
    args = OpenStudio::Measure::OSArgumentVector.new
    
    json_overrides = OpenStudio::Measure::OSArgument.makeStringArgument("json_overrides", true)
    json_overrides.setDisplayName("JSON Overrides")
    json_overrides.setDefaultValue("{}")
    args << json_overrides

    return args
  end

  def run(model, runner, user_arguments)
    super(model, runner, user_arguments)

    if !runner.validateUserArguments(arguments(model), user_arguments)
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

    # Space Variables
    if overrides.key?("SpaceVariables")
      overrides["SpaceVariables"].each do |sv|
        cad_id = sv["RevitElementId"]
        prop = sv["Property"]
        val = sv["Value"]

        model.getThermalZones.each do |zone|
          match = false
          if cad_id.to_s == "-1" || cad_id.to_s == ""
            match = true
          else
            if zone.additionalProperties.getFeatureAsString("CADObjectId").is_initialized
              feature_id = zone.additionalProperties.getFeatureAsString("CADObjectId").get
              match = true if feature_id == cad_id.to_s || feature_id.include?(cad_id.to_s)
            end
            match = true if zone.name.get.include?(cad_id.to_s) || (sv["RevitElementName"] && zone.name.get.include?(sv["RevitElementName"]))
          end
          
          if match
            
            if prop == "HeatingSetpoint" || prop == "CoolingSetpoint"
              temp_c = (val.to_f - 32.0) * 5.0 / 9.0
              thermostat = zone.thermostatSetpointDualSetpoint
              if thermostat.empty?
                thermostat_obj = OpenStudio::Model::ThermostatSetpointDualSetpoint.new(model)
                zone.setThermostatSetpointDualSetpoint(thermostat_obj)
                thermostat = thermostat_obj
              else
                thermostat = thermostat.get
              end
              
              schedule_ruleset = OpenStudio::Model::ScheduleRuleset.new(model)
              schedule_ruleset.defaultDaySchedule.addValue(OpenStudio::Time.new(0, 24, 0, 0), temp_c)
              
              if prop == "HeatingSetpoint"
                thermostat.setHeatingSetpointTemperatureSchedule(schedule_ruleset)
              else
                thermostat.setCoolingSetpointTemperatureSchedule(schedule_ruleset)
              end
              runner.registerInfo("Set #{prop} to #{temp_c}C for zone #{zone.name.get}")
              
            elsif prop == "IsUnheated" && val.to_s.downcase == "true"
              zone.resetThermostatSetpointDualSetpoint
              runner.registerInfo("Removed thermostat for zone #{zone.name.get}")
              
            elsif prop == "PeopleCount"
              zone.spaces.each do |space|
                people_def = OpenStudio::Model::PeopleDefinition.new(model)
                people_def.setNumberOfPeople(val.to_f)
                people = OpenStudio::Model::People.new(people_def)
                people.setSpace(space)
              end
              runner.registerInfo("Set PeopleCount to #{val} for zone #{zone.name.get}")
            end
          end
        end
      end
    end

    # Envelope Variables
    if overrides.key?("EnvelopeVariables")
      overrides["EnvelopeVariables"].each do |ev|
        revit_name = ev["RevitElementName"]
        cad_ids = ev["RevitElementIds"] || []
        cad_ids << ev["RevitElementId"].to_s if ev["RevitElementId"] && cad_ids.empty?
        
        prop = ev["Property"]
        val = ev["Value"].to_f

        revit_func = ev["RevitElementFunction"] || ""
        surfaces_to_modify = []
        model.getSurfaces.each do |surf|
          is_match = false
          if surf.additionalProperties.getFeatureAsString("CADObjectId").is_initialized
            surf_cad_id = surf.additionalProperties.getFeatureAsString("CADObjectId").get
            runner.registerInfo("measure.rb DEBUG: cad_ids=#{cad_ids.inspect}, surf_cad_id=#{surf_cad_id}")
            if cad_ids.any? { |id| surf_cad_id == id.to_s || surf_cad_id.include?(id.to_s) }
              is_match = true
            end
          end

          # Try matching by Name (Physical Name - works if Detailed Elements are exported)
          if !is_match && surf.construction.is_initialized && surf.construction.get.name.get.to_s.downcase.include?(revit_name.downcase)
            is_match = true
          end

          # Smart Fallback: If no ID or Name matched, assume Conceptual Types and match by Function
          if !is_match
            if revit_func == "ExteriorWall" && surf.surfaceType.downcase == "wall" && surf.outsideBoundaryCondition.downcase == "outdoors"
              is_match = true
            elsif revit_func == "InteriorWall" && surf.surfaceType.downcase == "wall" && surf.outsideBoundaryCondition.downcase != "outdoors"
              is_match = true
            elsif revit_func == "Roof" && surf.surfaceType.downcase == "roofceiling" && surf.outsideBoundaryCondition.downcase == "outdoors"
              is_match = true
            elsif revit_func == "Floor" && surf.surfaceType.downcase == "floor" && ["outdoors", "ground"].include?(surf.outsideBoundaryCondition.downcase)
              is_match = true
            end
          end

          if is_match
            surfaces_to_modify << surf
          end
        end

        model.getSubSurfaces.each do |surf|
          is_match = false
          if surf.additionalProperties.getFeatureAsString("CADObjectId").is_initialized
            surf_cad_id = surf.additionalProperties.getFeatureAsString("CADObjectId").get
            if cad_ids.include?("-1") || cad_ids.any? { |id| surf_cad_id == id.to_s || surf_cad_id.include?(id.to_s) }
              is_match = true
            end
          end
          if is_match
            surfaces_to_modify << surf
          end
        end

        surfaces_by_construction = surfaces_to_modify.group_by { |s| s.construction.is_initialized ? s.construction.get : nil }

        surfaces_by_construction.each do |const, surfaces|
          if prop == "Infiltration"
            surfaces.each do |surf|
              space = nil
              if surf.respond_to?(:space) && surf.space.is_initialized
                space = surf.space.get
              elsif surf.respond_to?(:surface) && surf.surface.is_initialized
                parent_surf = surf.surface.get
                if parent_surf.space.is_initialized
                  space = parent_surf.space.get
                end
              end
              if space
                flow_m3_s = val * 0.00508 * surf.netArea
                infiltration = OpenStudio::Model::SpaceInfiltrationDesignFlowRate.new(model)
                infiltration.setName("#{surf.name.get}_Infil_Override")
                infiltration.setSpace(space)
                infiltration.setDesignFlowRate(flow_m3_s)
                infiltration.setSchedule(model.alwaysOnDiscreteSchedule)
                runner.registerInfo("Added Infiltration #{flow_m3_s.round(4)} m3/s to space #{space.name.get} for surface #{surf.name.get}")
              end
            end
            next
          end

          next if const.nil?
          new_const = const.clone(model).to_Construction.get
          new_const.setName("#{const.name.get}_override_#{prop}_#{val}")
          
          if prop == "RValue" || prop == "EffectiveRValue"
            target_rsi = val / 5.678263337
            mat = OpenStudio::Model::MasslessOpaqueMaterial.new(model)
            mat.setName("#{new_const.name.get}_layer")
            mat.setThermalResistance(target_rsi)
            new_const.setLayers([mat])
            runner.registerInfo("Set #{prop} to #{val} for #{surfaces.size} surfaces.")
          elsif prop == "UValue"
            target_rsi = 1.0 / (val * 5.678263337)
            mat = OpenStudio::Model::MasslessOpaqueMaterial.new(model)
            mat.setName("#{new_const.name.get}_layer")
            mat.setThermalResistance(target_rsi)
            new_const.setLayers([mat])
            runner.registerInfo("Set U-Value to #{val} for #{surfaces.size} surfaces.")
          elsif prop == "Thickness" || prop == "Conductivity" || prop == "Density" || prop == "SpecificHeat"
            layers = new_const.layers
            if !layers.empty?
              first_layer = layers[0].to_StandardOpaqueMaterial
              if first_layer.is_initialized
                mat = first_layer.get.clone(model).to_StandardOpaqueMaterial.get
                mat.setName("#{first_layer.get.name.get}_override_#{prop}_#{val}")
                
                if prop == "Thickness"
                  mat.setThickness(val * 0.0254)
                  runner.registerInfo("Set Thickness to #{val} in for #{surfaces.size} surfaces.")
                elsif prop == "Conductivity"
                  mat.setConductivity(val)
                  runner.registerInfo("Set Conductivity to #{val} W/m-K for #{surfaces.size} surfaces.")
                elsif prop == "Density"
                  mat.setDensity(val)
                  runner.registerInfo("Set Density to #{val} kg/m3 for #{surfaces.size} surfaces.")
                elsif prop == "SpecificHeat"
                  mat.setSpecificHeat(val)
                  runner.registerInfo("Set SpecificHeat to #{val} J/kg-K for #{surfaces.size} surfaces.")
                end
                
                layers[0] = mat
                new_const.setLayers(layers)
              else
                mat = OpenStudio::Model::StandardOpaqueMaterial.new(model)
                mat.setName("#{new_const.name.get}_layer")
                mat.setThickness(prop == "Thickness" ? val * 0.0254 : 0.1)
                mat.setConductivity(prop == "Conductivity" ? val : 0.1)
                mat.setDensity(prop == "Density" ? val : 100)
                mat.setSpecificHeat(prop == "SpecificHeat" ? val : 1000)
                new_const.setLayers([mat])
                runner.registerInfo("Set #{prop} to #{val} for #{surfaces.size} surfaces (replaced).")
              end
            end
          end
          
          surfaces.each do |s|
            s.setConstruction(new_const)
            if s.adjacentSurface.is_initialized
              s.adjacentSurface.get.setConstruction(new_const)
            end
          end
        end
      end
    end

    # Process Design Days
    model.getDesignDays.each do |ddy|
      if overrides.key?("WeatherTemperature")
        temp = overrides["WeatherTemperature"].to_f
        if ddy.dayType == "SummerDesignDay" || ddy.dayType == "WinterDesignDay"
          ddy.setMaximumDryBulbTemperature(temp)
        end
      end
      if overrides.key?("WeatherDewPoint")
        ddy.setHumidityConditionType("Dewpoint")
        ddy.setHumidityConditionDaySchedule(nil) 
      end
    end

    # Process EPW
    weather_file = model.getWeatherFile
    if weather_file.path.is_initialized
      epw_path = weather_file.path.get.to_s
      actual_epw_path = runner.workflow.findFile(epw_path)
      
      if actual_epw_path.is_initialized
        actual_epw_path = actual_epw_path.get.to_s
        begin
          lines = File.readlines(actual_epw_path)

          # Find max values for offsets/scaling to preserve diurnal cycle
          max_temp = -999.0
          max_solar = 0.0
          lines.each_with_index do |line, idx|
            next if idx < 8
            parts = line.strip.split(",")
            next if parts.length < 22
            max_temp = [max_temp, parts[6].to_f].max
            max_solar = [max_solar, parts[13].to_f].max
          end

          temp_offset = overrides.key?("WeatherTemperatureOffset") ? overrides["WeatherTemperatureOffset"].to_f - max_temp : 0.0
          solar_multiplier = overrides.key?("WeatherSolarRadiation") && max_solar > 0 ? overrides["WeatherSolarRadiation"].to_f / max_solar : 1.0

          has_synthetic = overrides.key?("WeatherTemperatureSynthetic")
          if has_synthetic
            synth = overrides["WeatherTemperatureSynthetic"]
            win_min = synth["WinterMinTemp"].to_f
            win_max = synth["WinterMaxTemp"].to_f
            sum_min = synth["SummerMinTemp"].to_f
            sum_max = synth["SummerMaxTemp"].to_f
            synth_offset = synth["Offset"].to_f
            
            win_mean = (win_max + win_min) / 2.0
            sum_mean = (sum_max + sum_min) / 2.0
            win_amp = (win_max - win_min) / 2.0
            sum_amp = (sum_max - sum_min) / 2.0
          end

          File.open(actual_epw_path, "w") do |f|
            hour_of_year = 0
            lines.each_with_index do |line, idx|
              if idx < 8
                f.puts(line)
              else
                parts = line.strip.split(",")
                next if parts.length < 22

                if has_synthetic
                  day_of_year = (hour_of_year / 24).floor + 1
                  hour_of_day = hour_of_year % 24
                  
                  annual_phase = (day_of_year - 15) / 365.0 * 2.0 * Math::PI
                  annual_weight = Math.cos(annual_phase) 
                  
                  daily_mean = (win_mean + sum_mean) / 2.0 + (win_mean - sum_mean) / 2.0 * annual_weight
                  daily_amp = (win_amp + sum_amp) / 2.0 + (win_amp - sum_amp) / 2.0 * annual_weight
                  
                  diurnal_phase = (hour_of_day - 3) / 24.0 * 2.0 * Math::PI
                  diurnal_weight = Math.cos(diurnal_phase)
                  
                  temp = daily_mean - daily_amp * diurnal_weight + synth_offset
                  parts[6] = temp.round(1).to_s
                  hour_of_year += 1
                elsif overrides.key?("WeatherTemperature")
                  parts[6] = overrides["WeatherTemperature"].to_f.round(1).to_s
                elsif overrides.key?("WeatherTemperatureOffset")
                  parts[6] = (parts[6].to_f + temp_offset).round(1).to_s
                end
                
                parts[7] = overrides["WeatherDewPoint"].to_f.to_s if overrides.key?("WeatherDewPoint")
                parts[8] = overrides["WeatherRelativeHumidity"].to_f.to_s if overrides.key?("WeatherRelativeHumidity")
                parts[9] = overrides["WeatherAtmosphericPressure"].to_f.to_s if overrides.key?("WeatherAtmosphericPressure")
                
                if overrides.key?("WeatherSolarRadiation")
                  parts[13] = (parts[13].to_f * solar_multiplier).round(0).to_s
                  parts[14] = (parts[14].to_f * solar_multiplier).round(0).to_s
                  parts[15] = (parts[15].to_f * solar_multiplier).round(0).to_s
                end
                
                parts[14] = overrides["WeatherDirectNormalRadiation"].to_f.to_s if overrides.key?("WeatherDirectNormalRadiation")
                parts[15] = overrides["WeatherDiffuseHorizontalRadiation"].to_f.to_s if overrides.key?("WeatherDiffuseHorizontalRadiation")
                parts[20] = overrides["WeatherWindDirection"].to_f.to_s if overrides.key?("WeatherWindDirection")
                parts[21] = overrides["WeatherWindSpeed"].to_f.to_s if overrides.key?("WeatherWindSpeed")
                parts[22] = overrides["WeatherTotalSkyCover"].to_f.to_s if overrides.key?("WeatherTotalSkyCover")
                
                f.puts(parts.join(","))
              end
            end
          end
          
          runner.registerInfo("Successfully modified EPW file and saved to #{actual_epw_path}.")
        rescue => e
          runner.registerWarning("Failed to modify EPW: #{e.message}")
        end
      end
    end

    return true
  end
end

OverrideWeatherAndDesignDays.new.registerWithApplication
