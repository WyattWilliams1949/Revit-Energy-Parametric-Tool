require 'json'

class ApplyParametricVariations < OpenStudio::Measure::ModelMeasure
  def name
    return "Apply Parametric Variations"
  end

  def description
    return "Applies space-specific infiltration, occupancy, and setpoints from JSON overrides."
  end

  def modeler_description
    return "Parses json_overrides and applies them to spaces or the entire building."
  end

  def arguments(model)
    args = OpenStudio::Measure::OSArgumentVector.new
    
    arg = OpenStudio::Measure::OSArgument.makeStringArgument("json_overrides", true)
    arg.setDisplayName("JSON Overrides")
    arg.setDefaultValue("{}")
    args << arg

    return args
  end

  def run(model, runner, user_arguments)
    super(model, runner, user_arguments)
    
    if not runner.validateUserArguments(arguments(model), user_arguments)
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

    global_overrides = overrides["Entire Building"] || {}

    total_spaces = model.getSpaces.size

    model.getSpaces.each do |space|
      # Try to find space-specific overrides. If none, fall back to global.
      # Note: space.name.get might be something like "Kitchen (101)" or "Space 1"
      # But our regex parsed it so space_name matches exactly.
      space_name = space.name.is_initialized ? space.name.get : ""
      
      # Strip out the exact match we might have sent
      space_specific = overrides[space_name] || {}
      
      # Merge global overrides with space-specific ones (space-specific wins)
      space_ov = global_overrides.merge(space_specific)

      if space_ov.empty?
        next
      end

      # Schedule Helper
      create_profile_sch = lambda do |name, default_vals, wknd_vals, conversion_proc|
        sch = OpenStudio::Model::ScheduleRuleset.new(model)
        sch.setName(name)
        day_sch = sch.defaultDaySchedule
        day_sch.setName("#{name} Weekday")
        for i in 0..23
          val = default_vals[i] || 0.0
          val = conversion_proc.call(val) if conversion_proc
          day_sch.addValue(OpenStudio::Time.new(0, i+1, 0, 0), val)
        end
        
        wknd_rule = OpenStudio::Model::ScheduleRule.new(sch)
        wknd_rule.setName("#{name} Weekend Rule")
        wknd_rule.setApplySaturday(true)
        wknd_rule.setApplySunday(true)
        wknd_day = wknd_rule.daySchedule
        wknd_day.setName("#{name} Weekend")
        for i in 0..23
          val = wknd_vals[i] || 0.0
          val = conversion_proc.call(val) if conversion_proc
          wknd_day.addValue(OpenStudio::Time.new(0, i+1, 0, 0), val)
        end
        return sch
      end

      # Schedule Override
      if space_ov.key?("Schedule")
        sched = space_ov["Schedule"]
        if sched.is_a?(Hash) && sched["IsCustom"] == true
          # 1. Occupancy
          space.people.each { |p| p.remove }
          p_def = OpenStudio::Model::PeopleDefinition.new(model)
          p_def.setName("#{space_name} Custom People Def")
          p_def.setNumberofPeople(1.0) # Base 1.0, fraction is in schedule
          
          act_sch = OpenStudio::Model::ScheduleRuleset.new(model)
          act_sch.setName("#{space_name} Custom Activity Sch")
          act_sch.defaultDaySchedule.addValue(OpenStudio::Time.new(0,24,0,0), 120.0)
          
          num_sch = create_profile_sch.call("#{space_name} Custom Occupancy Sch", sched["WeekdayOccupancy"], sched["WeekendOccupancy"], nil)
          
          p = OpenStudio::Model::People.new(p_def)
          p.setName("#{space_name} Custom People")
          p.setSpace(space)
          p.setActivityLevelSchedule(act_sch)
          p.setNumberofPeopleSchedule(num_sch)

          # 2. Lighting
          space.lights.each { |l| l.remove }
          l_def = OpenStudio::Model::LightsDefinition.new(model)
          l_def.setName("#{space_name} Custom Lights Def")
          l_def.setLightingLevel(1.0) # Base 1.0, fraction is in schedule
          
          l_sch = create_profile_sch.call("#{space_name} Custom Lighting Sch", sched["WeekdayLighting"], sched["WeekendLighting"], nil)
          
          l = OpenStudio::Model::Lights.new(l_def)
          l.setName("#{space_name} Custom Lights")
          l.setSpace(space)
          l.setSchedule(l_sch)

          # 3. Thermostat
          zone = space.thermalZone
          if zone.is_initialized
            zone = zone.get
            tstat = zone.thermostatSetpointDualSetpoint
            if tstat.empty?
              tstat = OpenStudio::Model::ThermostatSetpointDualSetpoint.new(model)
              zone.setThermostatSetpointDualSetpoint(tstat)
            else
              tstat = tstat.get
            end

            f_to_c = lambda { |f| (f.to_f - 32.0) * 5.0 / 9.0 }
            
            h_sch = create_profile_sch.call("#{space_name} Custom Heating Sch", sched["WeekdayHeating"], sched["WeekendHeating"], f_to_c)
            tstat.setHeatingSetpointTemperatureSchedule(h_sch)

            c_sch = create_profile_sch.call("#{space_name} Custom Cooling Sch", sched["WeekdayCooling"], sched["WeekendCooling"], f_to_c)
            tstat.setCoolingSetpointTemperatureSchedule(c_sch)
          end
        elsif sched.is_a?(Hash) && sched["IsCustom"] == false
          begin
            require 'openstudio-standards'
            
            default_type = sched["DefaultType"] || "Office: Medium"
            bldg_type = "MediumOffice"
            case default_type
            when "Office: Medium" then bldg_type = "MediumOffice"
            when "Office: Large" then bldg_type = "LargeOffice"
            when "Office: Small" then bldg_type = "SmallOffice"
            when "Retail: Stand-alone" then bldg_type = "StandaloneRetail"
            when "Retail: Strip Mall" then bldg_type = "StripMall"
            when "Education: Primary School" then bldg_type = "PrimarySchool"
            when "Education: Secondary School" then bldg_type = "SecondarySchool"
            when "Lodging: Large Hotel" then bldg_type = "LargeHotel"
            when "Lodging: Small Hotel" then bldg_type = "SmallHotel"
            when "Lodging: Midrise Apartment", "Apartment: Mid Rise" then bldg_type = "MidriseApartment"
            when "Healthcare: Hospital" then bldg_type = "Hospital"
            when "Healthcare: Outpatient" then bldg_type = "Outpatient"
            when "Assembly: Warehouse", "Warehouse: Refrigerated" then bldg_type = "Warehouse"
            when "Restaurant: Fast Food" then bldg_type = "FastFoodRestaurant"
            when "Restaurant: Sit-down" then bldg_type = "SitDownRestaurant"
            when "Apartment: High Rise" then bldg_type = "HighriseApartment"
            when "Supermarket" then bldg_type = "SuperMarket"
            when "Single Family Home" then bldg_type = "SingleFamily"
            end
            
            std = Standard.build("90.1-2013")
            st_name = "#{bldg_type} WholeBuilding"
            st = model.getSpaceTypes.find { |s| s.name.is_initialized && s.name.get == st_name }
            
            if st.nil?
              st = std.model_add_space_type(model, bldg_type, "WholeBuilding")
            end
            
            if st
              # Remove hardcoded gbXML space loads so the new SpaceType's specific loads and schedules take precedence
              space.people.each { |p| p.remove }
              space.lights.each { |l| l.remove }
              space.electricEquipment.each { |e| e.remove }
              space.spaceInfiltrationDesignFlowRates.each { |inf| inf.remove }
              
              space.setSpaceType(st)
            end
            
          rescue LoadError
            runner.registerWarning("openstudio-standards gem not found. Could not apply default schedule for #{space_name}.")
          rescue => e
            runner.registerWarning("Error applying openstudio-standards: #{e.message}")
          end
        end
      end

      # Infiltration
      if space_ov.key?("Infiltration")
        inf_val = space_ov["Infiltration"].to_f
        inf_unit = space_ov["InfiltrationUnit"] || "ACH"

        space.spaceInfiltrationDesignFlowRates.each { |inf| inf.remove }
        inf = OpenStudio::Model::SpaceInfiltrationDesignFlowRate.new(model)
        inf.setName("#{space_name} Infiltration")
        inf.setSpace(space)

        if inf_unit == "minutes/day door or window open"
          # Assume 10 ACH base rate when open at 1 m/s wind.
          fraction_open = inf_val / 1440.0
          base_ach = 10.0 * fraction_open
          inf.setAirChangesperHour(base_ach)
          inf.setConstantTermCoefficient(0.0)
          inf.setTemperatureTermCoefficient(0.0)
          inf.setVelocityTermCoefficient(1.0)
          inf.setVelocitySquaredTermCoefficient(0.0)
        else
          # Standard ACH
          inf.setAirChangesperHour(inf_val)
          inf.setConstantTermCoefficient(1.0)
          inf.setTemperatureTermCoefficient(0.0)
          inf.setVelocityTermCoefficient(0.0)
          inf.setVelocitySquaredTermCoefficient(0.0)
        end
      end

      # People
      if space_ov.key?("PeopleCount")
        p_data = space_ov["PeopleCount"]
        
        if p_data.is_a?(Hash)
          p_val = p_data["Count"].to_f
          use_default_met = p_data["UseDefaultMetabolicHeat"]
          custom_met = p_data["CustomMetabolicHeat"].to_f
        else
          p_val = p_data.to_f
          use_default_met = true
          custom_met = 120.0
        end
        
        # If space_ov came from global, divide by total_spaces. If space-specific, use directly.
        if space_ov == global_overrides && total_spaces > 0
          p_val = p_val / total_spaces.to_f
        end

        space.people.each { |p| p.remove }
        p_def = OpenStudio::Model::PeopleDefinition.new(model)
        p_def.setName("#{space_name} People Def")
        p_def.setNumberofPeople(p_val)
        
        # EnergyPlus requires an activity level schedule
        act_sch = OpenStudio::Model::ScheduleRuleset.new(model)
        act_sch.setName("#{space_name} Activity Sch")
        act_sch.defaultDaySchedule.setName("#{space_name} Activity Default")
        act_val = use_default_met ? 120.0 : custom_met
        act_sch.defaultDaySchedule.addValue(OpenStudio::Time.new(0,24,0,0), act_val)
        
        # EnergyPlus also requires a number of people schedule
        num_sch = OpenStudio::Model::ScheduleRuleset.new(model)
        num_sch.setName("#{space_name} Number of People Sch")
        num_sch.defaultDaySchedule.setName("#{space_name} Number of People Default")
        num_sch.defaultDaySchedule.addValue(OpenStudio::Time.new(0,24,0,0), 1.0) # 100% occupancy
        
        p = OpenStudio::Model::People.new(p_def)
        p.setName("#{space_name} People")
        p.setSpace(space)
        p.setActivityLevelSchedule(act_sch)
        p.setNumberofPeopleSchedule(num_sch)
      end

      # Thermostat Setpoints
      if space_ov.key?("HeatingSetpoint") || space_ov.key?("CoolingSetpoint")
        zone = space.thermalZone
        if zone.is_initialized
          zone = zone.get
          tstat = zone.thermostatSetpointDualSetpoint
          if tstat.empty?
            tstat = OpenStudio::Model::ThermostatSetpointDualSetpoint.new(model)
            zone.setThermostatSetpointDualSetpoint(tstat)
          else
            tstat = tstat.get
          end

          h_f_user = space_ov.key?("HeatingSetpoint") ? space_ov["HeatingSetpoint"].to_f : nil
          c_f_user = space_ov.key?("CoolingSetpoint") ? space_ov["CoolingSetpoint"].to_f : nil

          h_f = h_f_user
          c_f = c_f_user

          # Helper to extract existing setpoint value from schedule
          get_existing_temp_f = lambda do |sch_opt|
            return nil unless sch_opt.is_initialized
            sch = sch_opt.get
            val_c = nil
            if sch.to_ScheduleRuleset.is_initialized
              day_sch = sch.to_ScheduleRuleset.get.defaultDaySchedule
              vals = day_sch.values
              val_c = vals[0] if vals.size > 0
            elsif sch.to_ScheduleConstant.is_initialized
              val_c = sch.to_ScheduleConstant.get.value
            end
            return val_c ? (val_c * 9.0 / 5.0 + 32.0) : nil
          end

          h_f_existing = get_existing_temp_f.call(tstat.heatingSetpointTemperatureSchedule)
          c_f_existing = get_existing_temp_f.call(tstat.coolingSetpointTemperatureSchedule)

          h_f ||= h_f_existing
          c_f ||= c_f_existing

          # Enforce a 2 F deadband to prevent EnergyPlus 'DualSetPointWithDeadBand' severe errors
          if h_f && c_f
            if c_f <= h_f + 2.0
              runner.registerWarning("Cooling setpoint (#{c_f} F) is too close to or below Heating setpoint (#{h_f} F) for #{space_name}. Enforcing 2 F deadband.")
              
              if h_f_user && c_f_user
                # Both provided by user: push from midpoint
                mid = (h_f + c_f) / 2.0
                h_f = mid - 1.0
                c_f = mid + 1.0
              elsif c_f_user
                # User only provided cooling; push heating down
                h_f = c_f - 2.0
              elsif h_f_user
                # User only provided heating; push cooling up
                c_f = h_f + 2.0
              end
            end
          end

          # Only overwrite schedules if the user provided the value, OR if we had to adjust it for deadband
          if h_f_user || (h_f && h_f != h_f_existing)
            h_c = (h_f - 32.0) * 5.0 / 9.0
            h_sch = OpenStudio::Model::ScheduleRuleset.new(model)
            h_sch.setName("#{space_name} Heating Sch")
            h_sch.defaultDaySchedule.setName("#{space_name} Heating Default")
            h_sch.defaultDaySchedule.addValue(OpenStudio::Time.new(0,24,0,0), h_c)
            tstat.setHeatingSetpointTemperatureSchedule(h_sch)
          end

          if c_f_user || (c_f && c_f != c_f_existing)
            c_c = (c_f - 32.0) * 5.0 / 9.0
            c_sch = OpenStudio::Model::ScheduleRuleset.new(model)
            c_sch.setName("#{space_name} Cooling Sch")
            c_sch.defaultDaySchedule.setName("#{space_name} Cooling Default")
            c_sch.defaultDaySchedule.addValue(OpenStudio::Time.new(0,24,0,0), c_c)
            tstat.setCoolingSetpointTemperatureSchedule(c_sch)
          end
        end
      end
    end

    # --- R-Value logic helper ---
    # 1 US R-Value = 0.176110 m^2*K/W
    apply_r_value = lambda do |surf, r_value_us|
      r_value_si = r_value_us * 0.176110
      if r_value_si <= 0
        r_value_si = 0.1 # Minimum fallback
      end

      if surf.construction.is_initialized
        orig_const = surf.construction.get
        
        # Don't clone if we already made one for this surface (safety check)
        return if orig_const.name.get.include?("Custom R-Value")

        const = orig_const.clone(model).to_Construction.get
        const.setName("#{surf.name.get} Custom R-Value #{r_value_us} Const")

        if surf.is_a?(OpenStudio::Model::SubSurface)
          mat = OpenStudio::Model::SimpleGlazing.new(model)
          mat.setName("#{surf.name.get} Custom U-Factor #{1.0/r_value_us} Mat")
          mat.setUFactor(1.0 / r_value_si)
          mat.setSolarHeatGainCoefficient(0.4) # Typical default
          const.eraseLayer(0)
          const.insertLayer(0, mat)
        else
          mat = OpenStudio::Model::MasslessOpaqueMaterial.new(model)
          mat.setName("#{surf.name.get} Custom R-Value #{r_value_us} Mat")
          mat.setThermalResistance(r_value_si)
          const.eraseLayer(0)
          const.insertLayer(0, mat)
        end

        surf.setConstruction(const)
      else
        # No construction assigned explicitly, create a new one
        const = OpenStudio::Model::Construction.new(model)
        const.setName("#{surf.name.get} Custom R-Value #{r_value_us} Const")
        if surf.is_a?(OpenStudio::Model::SubSurface)
          mat = OpenStudio::Model::SimpleGlazing.new(model)
          mat.setName("#{surf.name.get} Custom U-Factor #{1.0/r_value_us} Mat")
          mat.setUFactor(1.0 / r_value_si)
          mat.setSolarHeatGainCoefficient(0.4)
          const.insertLayer(0, mat)
        else
          mat = OpenStudio::Model::MasslessOpaqueMaterial.new(model)
          mat.setName("#{surf.name.get} Custom R-Value #{r_value_us} Mat")
          mat.setThermalResistance(r_value_si)
          const.insertLayer(0, mat)
        end
        surf.setConstruction(const)
      end
    end

    # --- Handle SubSurfaces (Windows and Doors) ---
    model.getSubSurfaces.each do |surf|
      if surf.space.is_initialized
        space_name = surf.space.get.name.get
        # Base space name without the "(ID)" suffix if gbXML put it there
        base_space_name = space_name.sub(/\s*\(\d+\)$/, "")
        
        target_key = nil
        if surf.subSurfaceType == "FixedWindow" || surf.subSurfaceType == "OperableWindow" || surf.subSurfaceType == "GlassDoor" || surf.subSurfaceType == "Skylight"
          target_key = "#{base_space_name} Windows"
        elsif surf.subSurfaceType == "Door"
          target_key = "#{base_space_name} Doors"
        end

        if target_key && overrides.key?(target_key) && overrides[target_key].key?("RValue")
          apply_r_value.call(surf, overrides[target_key]["RValue"].to_f)
        end
      end
    end

    # --- Handle Surfaces (Walls, Floors, Roofs) ---
    model.getSurfaces.each do |surf|
      if surf.construction.is_initialized
        const_name = surf.construction.get.name.get
        safe_const_name = const_name.delete('"')
        if overrides.key?(safe_const_name) && overrides[safe_const_name].key?("RValue")
          apply_r_value.call(surf, overrides[safe_const_name]["RValue"].to_f)
        elsif surf.surfaceType == "Wall" && overrides.key?("Walls") && overrides["Walls"].key?("RValue")
          apply_r_value.call(surf, overrides["Walls"]["RValue"].to_f)
        elsif surf.surfaceType == "Floor" && overrides.key?("Floors") && overrides["Floors"].key?("RValue")
          apply_r_value.call(surf, overrides["Floors"]["RValue"].to_f)
        elsif surf.surfaceType == "RoofCeiling" && overrides.key?("Roofs") && overrides["Roofs"].key?("RValue")
          apply_r_value.call(surf, overrides["Roofs"]["RValue"].to_f)
        end
      end
    end

    return true
  end
end

ApplyParametricVariations.new.registerWithApplication
