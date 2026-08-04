import sqlite3
conn = sqlite3.connect(r'E:\Documents\AntigravityIDE\Revit Add-in\errorLogs\old69\Sim_30f03ef7\run\eplusout.sql')
for row in conn.execute("SELECT name FROM sqlite_master WHERE type='table'"):
    print(row[0])

query = "SELECT * FROM Zones"
try:
    for row in conn.execute(query):
        print(row)
except Exception as e:
    print(e)
