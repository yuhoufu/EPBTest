import csv
import math

csv_path = r'd:\EPB_Data\实测数据\log\12.31\Threshold_Analysis_1231.csv'

data = []
headers = []

try:
    with open(csv_path, 'r', encoding='utf-8-sig') as f:
        reader = csv.reader(f)
        headers = next(reader)
        for row in reader:
            data.append(row)
except Exception as e:
    print(f'Error reading file: {e}')
    exit()

def get_col_index(name):
    try:
        return headers.index(name)
    except:
        return -1

idx_imax = get_col_index('Imax_A')
idx_cutoff = get_col_index('Cutoff_Val')
idx_margin = get_col_index('SafetyMargin')
idx_interval = get_col_index('CallbackInterval_ms')
idx_delay = get_col_index('ArrivalDelay_ms')
idx_threshold = get_col_index('Threshold_A')
idx_samples = get_col_index('Samples')

parsed_data = []
for row in data:
    try:
        imax = float(row[idx_imax])
        cutoff = float(row[idx_cutoff]) if idx_cutoff != -1 and row[idx_cutoff] else 0
        margin = float(row[idx_margin]) if idx_margin != -1 and row[idx_margin] else 0
        interval = float(row[idx_interval]) if idx_interval != -1 and row[idx_interval] else 0
        delay = float(row[idx_delay]) if idx_delay != -1 and row[idx_delay] else 0
        threshold = float(row[idx_threshold]) if idx_threshold != -1 and row[idx_threshold] else 0
        samples = float(row[idx_samples]) if idx_samples != -1 and row[idx_samples] else 0
        
        overshoot = imax - cutoff
        
        parsed_data.append({
            'Imax': imax,
            'Cutoff': cutoff,
            'Margin': margin,
            'Interval': interval,
            'Delay': delay,
            'Threshold': threshold,
            'Samples': samples,
            'Overshoot': overshoot
        })
    except ValueError:
        continue

if not parsed_data:
    print('No valid data parsed')
    exit()

def mean(values):
    return sum(values) / len(values)

def correlation(x, y):
    n = len(x)
    if n != len(y) or n == 0: return 0
    mu_x = mean(x)
    mu_y = mean(y)
    numerator = sum((xi - mu_x) * (yi - mu_y) for xi, yi in zip(x, y))
    sum_sq_diff_x = sum((xi - mu_x) ** 2 for xi in x)
    sum_sq_diff_y = sum((yi - mu_y) ** 2 for yi in y)
    denominator = math.sqrt(sum_sq_diff_x * sum_sq_diff_y)
    if denominator == 0: return 0
    return numerator / denominator

imaxs = [d['Imax'] for d in parsed_data]
cutoffs = [d['Cutoff'] for d in parsed_data]
margins = [d['Margin'] for d in parsed_data]
intervals = [d['Interval'] for d in parsed_data]
delays = [d['Delay'] for d in parsed_data]
thresholds = [d['Threshold'] for d in parsed_data]
samples = [d['Samples'] for d in parsed_data]
overshoots = [d['Overshoot'] for d in parsed_data]

print(f'Total records analyzed: {len(parsed_data)}')
print(f'Correlation with Imax_A:')
print(f'  Cutoff_Val (Trigger Current): {correlation(imaxs, cutoffs):.4f}')
print(f'  SafetyMargin: {correlation(imaxs, margins):.4f}')
print(f'  CallbackInterval_ms (Jitter): {correlation(imaxs, intervals):.4f}')
print(f'  ArrivalDelay_ms: {correlation(imaxs, delays):.4f}')
print(f'  Samples (Duration): {correlation(imaxs, samples):.4f}')

print(f'\nCorrelation with Overshoot (Imax - Cutoff):')
print(f'  Cutoff_Val: {correlation(overshoots, cutoffs):.4f}')
print(f'  CallbackInterval_ms: {correlation(overshoots, intervals):.4f}')
print(f'  ArrivalDelay_ms: {correlation(overshoots, delays):.4f}')

avg_overshoot = mean(overshoots)
def std_dev(values):
    mu = mean(values)
    return math.sqrt(sum((x - mu) ** 2 for x in values) / len(values))

print(f'\nAverage Overshoot (Inertia): {avg_overshoot:.4f} A')
print(f'Std Dev of Overshoot: {std_dev(overshoots):.4f} A')
print(f'Min Overshoot: {min(overshoots):.4f} A')
print(f'Max Overshoot: {max(overshoots):.4f} A')

high_latency_overshoots = [d['Overshoot'] for d in parsed_data if d['Interval'] > 12]
low_latency_overshoots = [d['Overshoot'] for d in parsed_data if d['Interval'] <= 12]

if high_latency_overshoots:
    print(f'Avg Overshoot with High Latency (>12ms): {mean(high_latency_overshoots):.4f} A (Count: {len(high_latency_overshoots)})')
if low_latency_overshoots:
    print(f'Avg Overshoot with Low Latency (<=12ms): {mean(low_latency_overshoots):.4f} A (Count: {len(low_latency_overshoots)})')
