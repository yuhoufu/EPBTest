import re
import csv
import os

# Input and output paths
log_file_path = r"d:\EPB_Data\实测数据\log\12.31\ErrorLog_1231_1.txt"
output_csv_path = r"d:\EPB_Data\实测数据\log\12.31\Threshold_Analysis_1231.csv"

# Regex pattern to match the log line
# Example: 
# 0009181	2025-12-31:17:59:04.892>  ,EPB  ,EPB[3]，阈值：15A,差值：0.437|0.837|1.050, 截断值：13.726|[1.487]A, 断电触发点：13.726A，触发时回调间隔≈0.04ms 到达延迟≈0.66ms (Dev1 N=20 Fs=2000Hz), 正向段峰值：Imax=14.563A @ 17:59:03.907，Samples=8060。
pattern = re.compile(
    r"(\d{4}-\d{2}-\d{2}:\d{2}:\d{2}:\d{2}\.\d{3}).*?"
    r"EPB\[(\d+)\].*?"
    r"阈值：([\d\.]+)A.*?"
    r"差值：([^,]+),.*?"
    r"截断值：([^,]+)A,.*?"
    r"断电触发点：([\d\.]+)A.*?"
    r"触发时回调间隔≈([\d\.]+)ms.*?"
    r"到达延迟≈([-\d\.]+)ms.*?"
    r"Imax=([\d\.]+)A.*?@\s*([\d:.]+).*?"
    r"Samples=(\d+)"
)

headers = [
    "LogTime", "EPB_Channel", "Threshold_A", 
    "Diff_Raw", "Diff_1", "Diff_2", "Diff_3",
    "Cutoff_Raw", "Cutoff_Val", "SafetyMargin",
    "TriggerPoint_A", "CallbackInterval_ms", "ArrivalDelay_ms",
    "Imax_A", "Imax_Time", "Samples"
]

rows = []

try:
    # Try reading with different encodings
    encodings = ['utf-8', 'gbk', 'utf-16']
    content = ""
    
    for enc in encodings:
        try:
            with open(log_file_path, 'r', encoding=enc) as f:
                # Read first line to check
                f.readline()
                # If successful, re-open and process
                print(f"Successfully opened with encoding: {enc}")
                with open(log_file_path, 'r', encoding=enc) as f2:
                    for line in f2:
                        if "EPB[" not in line or "阈值" not in line:
                            continue
                            
                        match = pattern.search(line)
                        if match:
                            log_time = match.group(1)
                            epb_ch = match.group(2)
                            threshold = match.group(3)
                            diff_raw = match.group(4)
                            cutoff_raw = match.group(5)
                            trigger_point = match.group(6)
                            cb_interval = match.group(7)
                            arrival_delay = match.group(8)
                            imax = match.group(9)
                            imax_time = match.group(10)
                            samples = match.group(11)

                            # Process Diff (e.g., 0.437|0.837|1.050)
                            diff_parts = diff_raw.split('|')
                            diff_1 = diff_parts[0] if len(diff_parts) > 0 else ""
                            diff_2 = diff_parts[1] if len(diff_parts) > 1 else ""
                            diff_3 = diff_parts[2] if len(diff_parts) > 2 else ""

                            # Process Cutoff (e.g., 13.726|[1.487])
                            # Sometimes it might be just numbers, sometimes with brackets
                            cutoff_parts = cutoff_raw.split('|')
                            cutoff_val = cutoff_parts[0] if len(cutoff_parts) > 0 else ""
                            safety_margin = cutoff_parts[1].replace('[', '').replace(']', '') if len(cutoff_parts) > 1 else ""

                            rows.append([
                                log_time, epb_ch, threshold,
                                diff_raw, diff_1, diff_2, diff_3,
                                cutoff_raw, cutoff_val, safety_margin,
                                trigger_point, cb_interval, arrival_delay,
                                imax, imax_time, samples
                            ])
                break # Stop if successful
        except UnicodeDecodeError:
            continue
        except Exception as e:
            print(f"Error with encoding {enc}: {e}")
            continue

    if not rows:
        print("No rows extracted. Check regex or file content.")
    else:
        # Write to CSV
        with open(output_csv_path, 'w', newline='', encoding='utf-8-sig') as f:
            writer = csv.writer(f)
            writer.writerow(headers)
            writer.writerows(rows)

        print(f"Successfully extracted {len(rows)} records to {output_csv_path}")

except Exception as e:
    print(f"Fatal Error: {e}")
