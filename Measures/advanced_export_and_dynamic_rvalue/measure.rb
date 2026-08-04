class AdvancedExportAndDynamicRvalue < OpenStudio::Measure::ModelMeasure
  def name
    return "Advanced Export and Dynamic RValue"
  end

  def description
    return "Adds detailed room breakdown variables."
  end

  def modeler_description
    return ""
  end

  def arguments(model)
    args = OpenStudio::Measure::OSArgumentVector.new
    
    arg = OpenStudio::Measure::OSArgument.makeStringArgument("json_overrides", true)
    arg.setDefaultValue("{}")
    args << arg

    return args
  end

  def run(model, runner, user_arguments)
    super(model, runner, user_arguments)

    if !runner.validateUserArguments(arguments(model), user_arguments)
      return false
    end

    # 1. Output variables for Room Breakdown
    vars = [
      "Zone People Sensible Heating Energy",
      "Zone Lights Total Heating Energy",
      "Surface Window Transmitted Solar Radiation Energy",
      "Surface Inside Face Conduction Heat Transfer Energy"
    ]
    vars.each do |v|
      out_var = OpenStudio::Model::OutputVariable.new(v, model)
      out_var.setReportingFrequency("Hourly")
    end

    return true
  end
end

AdvancedExportAndDynamicRvalue.new.registerWithApplication
