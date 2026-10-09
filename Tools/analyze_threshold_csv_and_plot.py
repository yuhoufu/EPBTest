import csv
import math
import os
from collections import Counter, defaultdict

CSV_PATH = r"d:\EPB_Data\实测数据\log\12.31\Threshold_Analysis_1231.csv"
OUT_DIR = r"d:\Github\wanxiang\EPBTest\image\analysis_20251231"
DOC_PATH = r"d:\Github\wanxiang\EPBTest\开发日志\EPB峰值控制精度分析与优化_20251231.md"

# ----------------- helpers -----------------

def safe_float(s, default=None):
    try:
        if s is None:
            return default
        s = str(s).strip()
        if s == "":
            return default
        return float(s)
    except Exception:
        return default


def mean(xs):
    return sum(xs) / len(xs) if xs else 0.0


def corr(xs, ys):
    n = len(xs)
    if n == 0 or n != len(ys):
        return 0.0
    mx = mean(xs)
    my = mean(ys)
    num = sum((x - mx) * (y - my) for x, y in zip(xs, ys))
    denx = sum((x - mx) ** 2 for x in xs)
    deny = sum((y - my) ** 2 for y in ys)
    den = math.sqrt(denx * deny)
    return num / den if den else 0.0


def stddev(xs):
    if not xs:
        return 0.0
    m = mean(xs)
    return math.sqrt(sum((x - m) ** 2 for x in xs) / len(xs))


def percentile(xs, p):
    if not xs:
        return 0.0
    xs = sorted(xs)
    if p <= 0:
        return xs[0]
    if p >= 100:
        return xs[-1]
    k = (len(xs) - 1) * (p / 100.0)
    f = math.floor(k)
    c = math.ceil(k)
    if f == c:
        return xs[int(k)]
    return xs[f] * (c - k) + xs[c] * (k - f)


def histogram(values, bin_width=0.1):
    # bins aligned to 0.1A resolution
    if not values:
        return {}, 0.0, 0.0
    vmin = math.floor(min(values) / bin_width) * bin_width
    vmax = math.ceil(max(values) / bin_width) * bin_width
    bins = Counter()
    for v in values:
        b = math.floor((v - vmin) / bin_width) * bin_width + vmin
        # guard for rounding drift
        b = round(b, 10)
        bins[b] += 1
    return bins, vmin, vmax


def write_svg_histogram(path, bins, title, x_label="Imax(A)", y_label="Count", bin_width=0.1):
    # Simple standalone SVG bar chart, no external deps.
    # Layout
    width = 1200
    height = 500
    margin_left = 70
    margin_right = 20
    margin_top = 60
    margin_bottom = 80

    plot_w = width - margin_left - margin_right
    plot_h = height - margin_top - margin_bottom

    if not bins:
        svg = f"""<svg xmlns='http://www.w3.org/2000/svg' width='{width}' height='{height}'>
  <rect x='0' y='0' width='{width}' height='{height}' fill='white'/>
  <text x='{width/2}' y='{margin_top}' text-anchor='middle' font-family='Segoe UI' font-size='18'>{title}</text>
  <text x='{width/2}' y='{height/2}' text-anchor='middle' font-family='Segoe UI' font-size='14'>No data</text>
</svg>"""
        with open(path, "w", encoding="utf-8") as f:
            f.write(svg)
        return

    keys = sorted(bins.keys())
    max_count = max(bins.values()) if bins else 1

    # Bars
    n = len(keys)
    bar_w = max(1.0, plot_w / max(n, 1))

    def x_of(i):
        return margin_left + i * bar_w

    def y_of(count):
        return margin_top + plot_h * (1 - (count / max_count))

    parts = []
    parts.append(f"<svg xmlns='http://www.w3.org/2000/svg' width='{width}' height='{height}'>")
    parts.append(f"<rect x='0' y='0' width='{width}' height='{height}' fill='white' />")

    # Title
    parts.append(f"<text x='{width/2}' y='30' text-anchor='middle' font-family='Segoe UI' font-size='18'>{title}</text>")

    # Axes
    x0 = margin_left
    y0 = margin_top + plot_h
    parts.append(f"<line x1='{x0}' y1='{y0}' x2='{x0 + plot_w}' y2='{y0}' stroke='#222' stroke-width='1' />")
    parts.append(f"<line x1='{x0}' y1='{margin_top}' x2='{x0}' y2='{y0}' stroke='#222' stroke-width='1' />")

    # Y ticks (5)
    for t in range(6):
        frac = t / 5.0
        y = margin_top + plot_h * (1 - frac)
        val = int(round(max_count * frac))
        parts.append(f"<line x1='{x0-5}' y1='{y}' x2='{x0}' y2='{y}' stroke='#222' stroke-width='1' />")
        parts.append(f"<text x='{x0-10}' y='{y+4}' text-anchor='end' font-family='Segoe UI' font-size='12'>{val}</text>")
        parts.append(f"<line x1='{x0}' y1='{y}' x2='{x0 + plot_w}' y2='{y}' stroke='#eee' stroke-width='1' />")

    # Bars
    for i, k in enumerate(keys):
        c = bins[k]
        x = x_of(i)
        y = y_of(c)
        h = y0 - y
        parts.append(f"<rect x='{x}' y='{y}' width='{bar_w*0.95}' height='{h}' fill='#4C78A8' />")

    # X labels: show at most ~20 ticks
    step = max(1, int(math.ceil(n / 20)))
    for i in range(0, n, step):
        k = keys[i]
        x = x_of(i) + bar_w * 0.5
        parts.append(f"<line x1='{x}' y1='{y0}' x2='{x}' y2='{y0+5}' stroke='#222' stroke-width='1' />")
        parts.append(
            f"<text x='{x}' y='{y0+22}' text-anchor='middle' font-family='Segoe UI' font-size='11'>{k:.1f}</text>")

    # Axis labels
    parts.append(
        f"<text x='{margin_left + plot_w/2}' y='{height-25}' text-anchor='middle' font-family='Segoe UI' font-size='14'>{x_label} (bin={bin_width:.1f}A)</text>")
    parts.append(
        f"<text x='18' y='{margin_top + plot_h/2}' text-anchor='middle' font-family='Segoe UI' font-size='14' transform='rotate(-90 18 {margin_top + plot_h/2})'>{y_label}</text>")

    parts.append("</svg>")

    with open(path, "w", encoding="utf-8") as f:
        f.write("\n".join(parts))


# ----------------- main -----------------

os.makedirs(OUT_DIR, exist_ok=True)

with open(CSV_PATH, "r", encoding="utf-8-sig") as f:
    reader = csv.DictReader(f)
    rows = list(reader)

# Count by channel
channel_counts = Counter()
for r in rows:
    ch = r.get("EPB_Channel")
    if ch is not None and str(ch).strip() != "":
        channel_counts[str(ch).strip()] += 1

# Pick the top-2 channels by record count (if user didn't specify)
top2 = [ch for ch, _ in channel_counts.most_common(2)]

# Extract numeric columns
features = [
    ("Cutoff_Val", "Cutoff_Val"),
    ("SafetyMargin", "SafetyMargin"),
    ("CallbackInterval_ms", "CallbackInterval_ms"),
    ("ArrivalDelay_ms", "ArrivalDelay_ms"),
    ("Samples", "Samples"),
]


def collect_for_channel(ch=None):
    # returns dict with lists
    xs = {k: [] for k, _ in features}
    imax = []
    cutoff = []
    for r in rows:
        if ch is not None and str(r.get("EPB_Channel", "")).strip() != str(ch):
            continue
        v_imax = safe_float(r.get("Imax_A"), None)
        v_cutoff = safe_float(r.get("Cutoff_Val"), None)
        if v_imax is None or v_cutoff is None:
            continue
        imax.append(v_imax)
        cutoff.append(v_cutoff)
        for key, col in features:
            xs[key].append(safe_float(r.get(col), 0.0))
    overshoot = [a - b for a, b in zip(imax, cutoff)]
    return {
        "Imax": imax,
        "Cutoff": cutoff,
        "Overshoot": overshoot,
        **xs,
    }


# Overall correlations
all_data = collect_for_channel(None)

corr_overall = {
    key: corr(all_data["Imax"], all_data[key]) for key, _ in features
}

corr_overshoot = {
    key: corr(all_data["Overshoot"], all_data[key]) for key, _ in features
}

# Per-channel correlations for the chosen 2
per_ch = {}
for ch in top2:
    d = collect_for_channel(ch)
    per_ch[ch] = {
        "n": len(d["Imax"]),
        "corr_imax": {key: corr(d["Imax"], d[key]) for key, _ in features},
        "corr_overshoot": {key: corr(d["Overshoot"], d[key]) for key, _ in features},
        "overshoot_mean": mean(d["Overshoot"]),
        "overshoot_std": stddev(d["Overshoot"]),
        "overshoot_p10": percentile(d["Overshoot"], 10),
        "overshoot_p50": percentile(d["Overshoot"], 50),
        "overshoot_p90": percentile(d["Overshoot"], 90),
        "imax_mean": mean(d["Imax"]),
        "imax_std": stddev(d["Imax"]),
    }

# Plot histograms for top2
plot_paths = {}
for ch in top2:
    d = collect_for_channel(ch)
    bins, _, _ = histogram(d["Imax"], bin_width=0.1)
    out = os.path.join(OUT_DIR, f"Imax_hist_EPB{ch}_bin0.1A.svg")
    write_svg_histogram(out, bins, title=f"EPB[{ch}] Imax 分布 (0.1A 分辨率, N={len(d['Imax'])})")
    plot_paths[ch] = out

# Prepare markdown snippet

def fmt_r(v):
    return f"{v:.4f}"

md = []
md.append("\n## 5. Imax_A 相关性分析（基于 12.31 阈值记录 CSV）\n")
md.append(f"数据源：`{CSV_PATH}`（共 {len(all_data['Imax'])} 条有效记录；按通道自动选取出现最多的两个 EPB：{', '.join('EPB['+c+']' for c in top2)}）。\n")

md.append("### 5.1 整体相关性（Pearson r）\n")
md.append("Imax_A 与各因素相关系数（r）：\n")
md.append("\n| 因素 | r(Imax_A, 因素) | 备注 |\n|---|---:|---|")
for key, _ in features:
    note = ""
    if key == "Samples":
        note = "运行耗时/有效段长度的代理指标"
    if key == "Cutoff_Val":
        note = "断电触发点电流"
    if key == "CallbackInterval_ms":
        note = "DAQ 回调节拍（越大越抖）"
    if key == "ArrivalDelay_ms":
        note = "数据时间到回调到达的延迟"
    if key == "SafetyMargin":
        note = "提前断电裕量（当日志中存在时）"
    md.append(f"| {key} | {fmt_r(corr_overall[key])} | {note} |")

md.append("\n同时，过冲量 Overshoot = Imax_A - Cutoff_Val 与各因素的相关系数：\n")
md.append("\n| 因素 | r(Overshoot, 因素) |\n|---|---:|")
for key, _ in features:
    md.append(f"| {key} | {fmt_r(corr_overshoot[key])} |")

md.append("\n### 5.2 两个 EPB 的 Imax 分布（0.1A 分箱）\n")
for ch in top2:
    rel = os.path.relpath(plot_paths[ch], os.path.dirname(DOC_PATH))
    rel = rel.replace("\\", "/")
    md.append(f"#### EPB[{ch}]\n")
    md.append(f"- 样本数 N={per_ch[ch]['n']}\n")
    md.append(f"- Overshoot: mean={per_ch[ch]['overshoot_mean']:.3f}A, std={per_ch[ch]['overshoot_std']:.3f}A, P10={per_ch[ch]['overshoot_p10']:.3f}A, P50={per_ch[ch]['overshoot_p50']:.3f}A, P90={per_ch[ch]['overshoot_p90']:.3f}A\n")
    md.append(f"- Imax: mean={per_ch[ch]['imax_mean']:.3f}A, std={per_ch[ch]['imax_std']:.3f}A\n")
    md.append(f"\n![]({rel})\n")

md.append("\n> 说明：若你希望固定分析特定两个通道（例如 EPB[1] 与 EPB[3]），可以告诉我通道号，我会把“自动选取 top2”改为“指定通道”。\n")

# Append to doc
with open(DOC_PATH, "r", encoding="utf-8") as f:
    doc = f.read()

if "## 5. Imax_A 相关性分析" in doc:
    # Replace section 5 if already exists
    import re
    doc2 = re.sub(r"\n## 5\. Imax_A 相关性分析[\s\S]*$", "\n" + "\n".join(md).strip() + "\n", doc)
    doc = doc2
else:
    doc = doc.rstrip() + "\n" + "\n".join(md) + "\n"

with open(DOC_PATH, "w", encoding="utf-8") as f:
    f.write(doc)

print("Top2 channels:", top2)
print("Plots:")
for ch, p in plot_paths.items():
    print(f"  EPB[{ch}]: {p}")
print("Document updated:", DOC_PATH)
