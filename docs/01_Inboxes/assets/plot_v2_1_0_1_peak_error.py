from pathlib import Path

import matplotlib.pyplot as plt
import numpy as np


learning_epb8 = [0.101, 0.975, 0.390, 1.020, 0.920, -0.959, -0.634, -0.817, -0.387, -0.821]
learning_epb9 = [0.057, 0.658, -0.447, 0.186, 0.408, -0.139, -0.649, -0.476, -0.023, 0.048]
formal_epb8 = [
    0.040, -0.573, 0.201, -0.474, 0.133, 0.005, 0.182, -0.445, -0.117, 0.021,
    0.108, -0.339, 0.124, -0.143, 0.015, 0.050, 0.011, -0.130, 0.044, 0.278,
]
formal_epb9 = [
    -0.122, -0.029, 0.061, 0.012, -0.068, 0.221, -0.074, 0.176, 0.224, 0.497,
    -0.129, 0.501, 0.099, -0.235, 0.144, 0.025, 0.237, 0.138, 0.330, 0.131,
]

x = np.arange(1, 31)
epb8 = np.array(learning_epb8 + formal_epb8)
epb9 = np.array(learning_epb9 + formal_epb9)

plt.rcParams["font.sans-serif"] = ["Microsoft YaHei", "SimHei", "DejaVu Sans"]
plt.rcParams["axes.unicode_minus"] = False

fig, ax = plt.subplots(figsize=(12.5, 6.4), dpi=160)
fig.patch.set_facecolor("#FAFAFA")
ax.set_facecolor("#FFFFFF")

ax.axhspan(-0.8, 0.8, color="#DCE6F2", alpha=0.55, label="±0.8A 平衡带", zorder=0)
ax.axhline(0, color="#374151", linewidth=1.1, zorder=1)
ax.axhline(0.8, color="#6B7280", linewidth=1.0, linestyle=(0, (4, 4)), zorder=1)
ax.axhline(-0.8, color="#6B7280", linewidth=1.0, linestyle=(0, (4, 4)), zorder=1)
ax.axvline(10.5, color="#4B5563", linewidth=1.1, linestyle=(0, (2, 3)), zorder=1)

ax.plot(
    x, epb8, color="#2563EB", linewidth=2.0, marker="o", markersize=4.2,
    markerfacecolor="#FFFFFF", markeredgewidth=1.3, label="EPB8", zorder=3,
)
ax.plot(
    x, epb9, color="#D97706", linewidth=2.0, linestyle=(0, (5, 3)),
    marker="s", markersize=4.0, markerfacecolor="#D97706", markeredgewidth=0.8,
    label="EPB9", zorder=3,
)

ax.text(5.5, 1.13, "学习阶段（10圈）", ha="center", va="center", color="#374151", fontsize=10)
ax.text(20.5, 1.13, "正式阶段（20圈）", ha="center", va="center", color="#374151", fontsize=10)
ax.text(10.65, -1.13, "阶段边界", ha="left", va="center", color="#4B5563", fontsize=8.5)

ax.set_title(
    "EPB8 / EPB9 正向峰值误差（学习与正式阶段）\n"
    "目标电流 15A；运行批次 c8ad6874ce474cc1aadf6a4689ffc883",
    loc="left", fontsize=15, fontweight="semibold", color="#111827", pad=18,
)
ax.set_xlabel("圈序（1–10：学习；11–30：正式）", color="#374151", labelpad=10)
ax.set_ylabel("峰值误差（A）", color="#374151", labelpad=8)
ax.set_xlim(0.5, 30.5)
ax.set_ylim(-1.25, 1.25)
ax.set_xticks([1, 5, 10, 11, 15, 20, 25, 30])
ax.set_yticks(np.arange(-1.2, 1.21, 0.4))
ax.grid(axis="y", color="#E5E7EB", linewidth=0.8)
ax.grid(axis="x", visible=False)
ax.tick_params(colors="#4B5563")
for side in ("top", "right"):
    ax.spines[side].set_visible(False)
for side in ("left", "bottom"):
    ax.spines[side].set_color("#9CA3AF")

handles, labels = ax.get_legend_handles_labels()
order = [1, 2, 0]
ax.legend(
    [handles[i] for i in order],
    [labels[i] for i in order],
    loc="lower center",
    bbox_to_anchor=(0.5, 1.01),
    ncol=3,
    frameon=False,
    fontsize=9.5,
)

fig.text(
    0.01, 0.012,
    "误差 = 实际正向峰值 − 15A；正值为高于目标。现场日志与 epb_cycles 按运行批次和阶段对账。",
    color="#6B7280", fontsize=8.5,
)
fig.tight_layout(rect=[0, 0.045, 1, 0.98])

output = Path(__file__).with_name("2026-07-30_V2.1.0.1_peak_error.png")
fig.savefig(output, facecolor=fig.get_facecolor(), bbox_inches="tight")
print(output)
