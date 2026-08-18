with open(r"E:\Documents\AntigravityIDE\Revit Add-in\ProgressWindow.xaml.cs", "r", encoding="utf-8") as f:
    text = f.read()

text = text.replace('if (Path.GetFileName(f).ToLower() != "eplusout.sql")', 'if (Path.GetFileName(f).ToLower() != "eplusout.sql" && Path.GetFileName(f).ToLower() != "run.log" && Path.GetFileName(f).ToLower() != "in.osw")')

with open(r"E:\Documents\AntigravityIDE\Revit Add-in\ProgressWindow.xaml.cs", "w", encoding="utf-8") as f:
    f.write(text)
