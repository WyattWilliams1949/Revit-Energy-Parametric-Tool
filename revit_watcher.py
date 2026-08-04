import os
import sys
import time
import glob
import subprocess
import datetime
import threading
import queue

OUTPUT_FILE = "revit_diagnostic_log.txt"
POLL_INTERVAL = 0.1  # Very fast polling for journal

def get_latest_journal():
    appdata = os.environ.get('LOCALAPPDATA', '')
    if not appdata:
        return None
        
    # Search for latest Revit 2027 journal
    journal_path = os.path.join(appdata, 'Autodesk', 'Revit', 'Autodesk Revit 2027', 'Journals', 'journal.*.txt')
    files = glob.glob(journal_path)
    if not files:
        # Fallback to 2026/2025/2024
        for year in ['2026', '2025', '2024']:
            journal_path = os.path.join(appdata, 'Autodesk', 'Revit', f'Autodesk Revit {year}', 'Journals', 'journal.*.txt')
            files = glob.glob(journal_path)
            if files:
                break
                
    if not files:
        return None
        
    latest_file = max(files, key=os.path.getmtime)
    return latest_file

def log_output(msg, file_handle):
    ts = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S.%f")[:-3]
    line = f"[{ts}] {msg}"
    print(line)
    file_handle.write(line + "\n")
    file_handle.flush()

def check_event_viewer(last_seen_record):
    new_records = []
    highest_record = last_seen_record
    try:
        query = '*[System[(Level=1 or Level=2) and TimeCreated[timediff(@SystemTime) <= 120000]]]'
        cmd = f'wevtutil qe Application /q:"{query}" /f:text /c:10 /rd:false'
        
        result = subprocess.check_output(cmd, shell=True, stderr=subprocess.STDOUT, text=True)
        if not result.strip():
            return [], highest_record
            
        events = result.split('Event[')
        for evt in events:
            if not evt.strip():
                continue
                
            record_id = -1
            for line in evt.splitlines():
                if "Event Record ID:" in line or "Record ID:" in line:
                    try:
                        record_id = int(line.split(":")[1].strip())
                    except ValueError:
                        pass
                    
            if record_id > last_seen_record:
                source = ""
                desc = ""
                for line in evt.splitlines():
                    if "Source:" in line:
                        source = line.replace("Source:", "").strip()
                    elif "Description:" in line:
                        desc = evt[evt.find("Description:"):]
                        desc = desc.replace("\n", " ").replace("\r", " ")
                        if len(desc) > 300:
                            desc = desc[:300] + "..."
                        break
                
                new_records.append(f"EVENT VIEWER ERROR [Source: {source}]: {desc}")
                if record_id > highest_record:
                    highest_record = record_id
                    
    except Exception as e:
        pass
        
    return new_records, highest_record

def powershell_monitor(q):
    # Runs a continuous powershell loop yielding metrics every 250ms
    ps_code = """
$ErrorActionPreference = 'SilentlyContinue'
while ($true) {
  $procs = Get-Process Revit,RevitWorker,openstudio,energyplus,ruby,cmd
  if ($procs) {
    foreach ($p in $procs) {
      $ws = [math]::Round($p.WorkingSet64 / 1MB, 2)
      $pm = [math]::Round($p.PrivateMemorySize64 / 1MB, 2)
      Write-Host "$($p.Name) (PID $($p.Id)) | RAM: ${ws}MB | Private: ${pm}MB | Handles: $($p.HandleCount) | Threads: $($p.Threads.Count)"
    }
  }
  Write-Host "---"
  Start-Sleep -Milliseconds 250
}
    """
    
    try:
        p = subprocess.Popen(['powershell', '-NoProfile', '-Command', ps_code], stdout=subprocess.PIPE, text=True, bufsize=1)
        for line in iter(p.stdout.readline, ''):
            clean_line = line.strip()
            if clean_line:
                q.put(clean_line)
    except Exception as e:
        q.put(f"Powershell Monitor Error: {e}")

def main():
    print("=" * 60)
    print("  Revit Watcher - High Frequency Diagnostic Tool")
    print("=" * 60)
    print(f"Logging to: {os.path.abspath(OUTPUT_FILE)}")
    
    with open(OUTPUT_FILE, 'a', encoding='utf-8') as f:
        log_output("--- Started High-Frequency Revit Watcher ---", f)
        
        journal_file = get_latest_journal()
        if not journal_file:
            log_output("WARNING: Could not find any Revit Journal file.", f)
            journal_f = None
        else:
            log_output(f"Watching Journal: {journal_file}", f)
            try:
                journal_f = open(journal_file, 'r', encoding='utf-8', errors='replace')
                journal_f.seek(0, os.SEEK_END)
            except Exception as e:
                log_output(f"Failed to open journal: {e}", f)
                journal_f = None

        highest_event_record = -1
        _, highest_event_record = check_event_viewer(-1)
        
        # Start powershell background process to stream process metrics
        ps_queue = queue.Queue()
        ps_thread = threading.Thread(target=powershell_monitor, args=(ps_queue,), daemon=True)
        ps_thread.start()
        
        poll_count = 0
        last_event_check = time.time()
        
        try:
            while True:
                # 1. Drain powershell process metrics queue
                metrics_block = []
                while not ps_queue.empty():
                    msg = ps_queue.get_nowait()
                    if msg == "---":
                        if metrics_block:
                            log_output(f"[PROCESSES] " + " || ".join(metrics_block), f)
                            metrics_block = []
                    else:
                        metrics_block.append(msg)

                # 2. Read new journal lines rapidly
                if journal_f:
                    try:
                        line = journal_f.readline()
                        while line:
                            clean_line = line.strip()
                            if clean_line:
                                log_output(f"[JOURNAL] {clean_line}", f)
                            line = journal_f.readline()
                    except Exception as e:
                        log_output(f"[JOURNAL ERROR] {e}", f)

                # 3. Check Event Viewer (every 5 seconds based on time)
                if time.time() - last_event_check > 5.0:
                    new_events, highest_event_record = check_event_viewer(highest_event_record)
                    for evt in new_events:
                        log_output(f"[FAULT] {evt}", f)
                    last_event_check = time.time()

                time.sleep(POLL_INTERVAL)
                poll_count += 1
                
        except KeyboardInterrupt:
            log_output("--- Stopped by User ---", f)
            print("\nExiting watcher.")

if __name__ == "__main__":
    main()
