#!/usr/bin/env python3
"""Project Plan schedule model: checks the plan and draws the PERT and Gantt charts.

Run from the repository root:  python3 docs/diagrams/project-plan/charts.py
Times are in weeks from Monday 19 Oct 2026, so W1 is the range 0 to 1.
"""
import os
import subprocess
import sys
from datetime import date, timedelta

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt

NAMES = {"ME": "Mehmet Elhan", "ÖFY": "Ömer Faruk Yılmaz", "MG": "Mücahit Günay",
         "İY": "İsmail Yeşilyurt", "MTY": "Mustafa Talha Yıldırım", "EBT": "Enes Birol Tatlı"}
PROJECT_END = 10

# id: (name, group, [(member, fte, start, duration), ...], predecessors)
A = {
 "15.1": ("Repository, solution skeleton & CI", "Foundation", [("ME", 1, 0, .5)], []),
 "2.1": ("Domain model & module contracts", "Foundation",
         [("ÖFY", .5, .5, .5), ("MG", .5, .5, .5), ("İY", .5, .5, .5), ("MTY", .5, .5, .5)], ["15.1"]),
 "2.2": ("Core catalog queries", "Extraction", [("ÖFY", .5, 1, 2)], ["2.1", "1.1"]),
 "2.3": ("Parallel extraction runner", "Extraction", [("ME", .5, 1, 1)], ["2.1", "1.1"]),
 "2.4": ("Permission checks & coverage probe", "Extraction", [("ÖFY", .5, 3, 2)], ["2.2"]),
 "4.3": ("Broad object coverage", "Extraction",
         [("ÖFY", .5, 5, 2), ("MG", .25, 5, 2), ("ME", .25, 5, 2)], ["2.4", "4.1"]),
 "11.1": ("Snapshot export/import & offline compare", "Extraction", [("ÖFY", .5, 7, 2)], ["10.1", "4.3"]),
 "3.1": ("ScriptDom normalizer", "Comparison", [("MG", .5, 1, 2)], ["2.1"]),
 "3.2": ("XxHash128 object & sub-part hashing", "Comparison", [("MG", .5, 3, 1)], ["3.1"]),
 "4.1": ("Hash-based diff engine", "Comparison", [("MG", .5, 4, 1), ("ME", .5, 4, 1)], ["3.2", "2.2"]),
 "4.2": ("Change tree & sub-changes", "Comparison", [("MG", .5, 5, 1)], ["4.1"]),
 "5.1": ("Rename detection", "Comparison", [("MG", .5, 6, 1)], ["4.2"]),
 "6.1": ("Script framework & dependency ordering", "Deployment", [("İY", .5, 1, 2)], ["2.1"]),
 "6.2": ("Table & type script generators", "Deployment", [("İY", .5, 3, 2), ("MTY", .5, 3, 2)], ["6.1"]),
 "6.3": ("Module, security & settings generators", "Deployment", [("İY", .5, 5, 2)], ["6.2"]),
 "6.4": ("Transaction wrapper, safe mode & selection", "Deployment", [("İY", .5, 7, 1)], ["6.3", "7.1"]),
 "6.5": ("Rollback & bidirectional scripts", "Deployment", [("İY", .75, 8, 1), ("MTY", .25, 8, 1)], ["6.4"]),
 "9.1": ("Script execution engine", "Risk & Migration", [("MTY", .5, 2, 1)], ["1.1"]),
 "7.1": ("Deployment risk analyzer", "Risk & Migration", [("MTY", .5, 5, 2)], ["4.1"]),
 "7.2": ("Trigger status report", "Risk & Migration", [("MTY", .25, 9, 1)], ["2.2"]),
 "8.1": ("Versioned migration file generation", "Risk & Migration", [("MTY", .25, 5, 2)], ["6.1"]),
 "8.2": ("Migration history, apply & drift detection", "Risk & Migration", [("MTY", .5, 7, 2)], ["8.1", "9.1"]),
 "10.1": ("Job configuration & CLI commands", "CLI & Reports", [("ME", .5, 2, 2)], ["2.1"]),
 "10.2": ("Parallel streaming runner & exit codes", "CLI & Reports", [("ME", .25, 5, 2)], ["10.1", "4.1"]),
 "12.1": ("HTML & JSON reports", "CLI & Reports", [("ME", .5, 8, 1)], ["10.2", "7.1", "4.2"]),
 "1.1": ("Connection factory & connection test", "Web UI & AI", [("EBT", 1, .5, .5)], ["15.1"]),
 "1.2": ("Credential store & recent connections", "Web UI & AI", [("EBT", .5, 1, 1)], ["1.1"]),
 "13.1": ("Minimal API & SSE backend", "Web UI & AI", [("EBT", .5, 2, 2)], ["1.2"]),
 "1.3": ("Connect dialog (UI)", "Web UI & AI", [("EBT", .5, 4, 1)], ["1.2", "13.1"]),
 "13.2": ("Compare view (SPA)", "Web UI & AI", [("EBT", .5, 5, 2)], ["13.1", "4.1"]),
 "13.3": ("Risk view, sub-change drill-down, settings & EN/TR localization", "Web UI & AI",
          [("EBT", .5, 7, 1), ("ME", .5, 7, 1)], ["13.2", "7.1", "4.2"]),
 "14.1": ("AI provider abstraction & Ollama", "Web UI & AI", [("EBT", .25, 5, 1)], ["13.1"]),
 "14.2": ("AI release notes, risk explanations & review", "Web UI & AI",
          [("EBT", .25, 7, 1), ("EBT", .75, 8, 1)], ["14.1", "7.1"]),
 "9.2": ("Execution UI with live messages", "Web UI & AI", [("EBT", .25, 9, 1)], ["13.2", "9.1"]),
 "15.2": ("Test fixture databases", "Quality & Docs", [("MTY", .5, 1, 1)], ["2.1"]),
 "15.6": ("10,000-object test database & early timing", "Quality & Docs", [("ÖFY", .25, 5, 1)], ["4.1", "2.3"]),
 "15.3": ("Coverage-matrix & cross-module tests", "Quality & Docs", [("MG", .5, 7, 2)], ["4.3", "5.1"]),
 "15.7": ("Fixture update for new object kinds", "Quality & Docs", [("İY", .25, 7, 1)], ["4.3", "15.2"]),
 "15.5": ("Performance benchmark vs DacFx", "Quality & Docs", [("ÖFY", .25, 8, 1)], ["4.3", "15.6"]),
 "15.8": ("Compatibility, permission & usability assessment", "Quality & Docs",
          [("MG", .25, 8, 1)], ["6.4", "13.2", "15.2"]),
 "15.9": ("First end-to-end pass", "Quality & Docs", [("ME", .25, 8, 1)], ["6.4", "10.2", "15.2", "9.1"]),
 "15.4": ("End-to-end tests & stabilization", "Quality & Docs",
         [("ÖFY", .25, 9, 1), ("MG", .25, 9, 1), ("İY", .25, 9, 1), ("MTY", .25, 9, 1)],
         ["6.5", "12.1", "13.3", "15.7", "15.8", "15.9"]),
 "16.1": ("User & developer documentation", "Quality & Docs",
          [("ME", .25, 9, 1), ("MG", .25, 9, 1)], ["12.1", "13.3"]),
 "16.2": ("Demo scenario & presentation material", "Quality & Docs", [("ME", .25, 9, 1)], ["12.1", "15.2"]),
}


def span(a):
    rows = A[a][2]
    start = min(r[2] for r in rows)
    return start, max(r[2] + r[3] for r in rows) - start


def effort(a):
    return sum(r[1] * r[3] for r in A[a][2])


def cpm():
    es, ef = {}, {}
    def forward(a):
        if a not in es:
            es[a] = max((forward(p) for p in A[a][3]), default=0)
            ef[a] = es[a] + span(a)[1]
        return ef[a]
    for a in A:
        forward(a)
    succ = {a: [b for b in A if a in A[b][3]] for a in A}
    ls, lf = {}, {}
    def backward(a):
        if a not in ls:
            lf[a] = min((backward(s) for s in succ[a]), default=PROJECT_END)
            ls[a] = lf[a] - span(a)[1]
        return ls[a]
    for a in A:
        backward(a)
    return es, ef, ls, lf


def allocation():
    table = {m: [0.0] * 10 for m in NAMES}
    for a in A:
        for m, fte, s, d in A[a][2]:
            for w in range(10):
                table[m][w] += fte * max(0, min(s + d, w + 1) - max(s, w))
    return table


def check():
    es, ef, ls, lf = cpm()
    errors = []
    if max(ef.values()) != PROJECT_END:
        errors.append(f"project length is {max(ef.values())}")
    for a in A:
        start, dur = span(a)
        if not es[a] <= start <= ls[a]:
            errors.append(f"{a}: start {start} outside ES {es[a]} to LS {ls[a]}")
        for p in A[a][3]:
            if sum(span(p)) > start:
                errors.append(f"{a}: starts at {start} before {p} finishes at {sum(span(p))}")
    table = allocation()
    for m, weeks in table.items():
        run = 0
        for w, v in enumerate(weeks, 1):
            if v > .75:
                errors.append(f"{m} W{w} is at {v}")
            if v > .5 and w < 6:
                errors.append(f"{m} W{w} has a peak before the midterm")
            run = run + 1 if v > .5 else 0
            if run > 2 and w < 10:
                errors.append(f"{m} has more than two peak weeks up to W{w}")
    return errors


def fmt(x):
    return f"{x:g}"


def monday(week):
    return date(2026, 10, 19) + timedelta(days=7 * week)


def weeks_label(rowstart, dur):
    if dur == .5:
        return "W1 (first half)" if rowstart == 0 else "W1 (second half)"
    first, last = int(rowstart) + 1, int(rowstart + dur)
    return f"W{first}" if first == last else f"W{first}–W{last}"


def pert(path_dot, path_png):
    es, ef, ls, lf = cpm()
    crit = {a for a in A if ls[a] == es[a]}
    out = ['digraph pert {', ' rankdir=LR nodesep=0.12 ranksep=0.45 bgcolor=white',
           ' node [shape=plain fontname="DejaVu Sans"] edge [color="#858585" arrowsize=0.7]',
           ' Start [shape=circle label="Start" width=0.8 fixedsize=true fontsize=12]',
           ' Finish [shape=doublecircle label="Finish" width=0.8 fixedsize=true fontsize=12]']
    for a in A:
        red = a in crit
        fill, line, title, w = ("#FDECEA", "#C0392B", "#C0392B", 3) if red else ("#F4F6F8", "#2C3E50", "#2C3E50", 1)
        name = A[a][0].replace("&", "&amp;")
        out.append(
            f' "{a}" [label=<<TABLE BORDER="{w}" COLOR="{line}" BGCOLOR="{fill}" CELLBORDER="0" CELLSPACING="0" CELLPADDING="2">'
            f'<TR><TD COLSPAN="3"><FONT POINT-SIZE="12" COLOR="{title}"><B>WBS-{a}</B></FONT></TD></TR>'
            f'<TR><TD COLSPAN="3"><FONT POINT-SIZE="10">{name}</FONT></TD></TR>'
            f'<TR><TD><FONT POINT-SIZE="8">ES {fmt(es[a])}</FONT></TD><TD><FONT POINT-SIZE="8">D {fmt(span(a)[1])}w</FONT></TD><TD><FONT POINT-SIZE="8">EF {fmt(ef[a])}</FONT></TD></TR>'
            f'<TR><TD><FONT POINT-SIZE="8">LS {fmt(ls[a])}</FONT></TD><TD><FONT POINT-SIZE="8">TF {fmt(ls[a]-es[a])}</FONT></TD><TD><FONT POINT-SIZE="8">LF {fmt(lf[a])}</FONT></TD></TR>'
            '</TABLE>>]')
    hot = ' [color="#C0392B" penwidth=2.6]'
    has_succ = set()
    for b in A:
        for p in A[b][3]:
            has_succ.add(p)
            critical = p in crit and b in crit and ef[p] == es[b]
            out.append(f' "{p}" -> "{b}"{hot if critical else ""}')
        if not A[b][3]:
            out.append(f' Start -> "{b}"{hot if b in crit else ""}')
    for a in A:
        if a not in has_succ:
            out.append(f' "{a}" -> Finish{hot if a in crit else ""}')
    out.append('}')
    with open(path_dot, "w", encoding="utf-8") as f:
        f.write("\n".join(out) + "\n")
    subprocess.run(["dot", "-Tpng", "-Gdpi=170", path_dot, "-o", path_png], check=True)


def gantt(path):
    es, ef, ls, lf = cpm()
    plt.rcParams["font.family"] = "DejaVu Sans"
    groups = []
    for a in A:
        if A[a][1] not in groups:
            groups.append(A[a][1])
    order = [a for g in groups for a in A if A[a][1] == g]
    fig, ax = plt.subplots(figsize=(11.5, 0.245 * len(order) + 1.9), dpi=200)
    def x(week):  # bars run Monday to Friday, weekends stay empty
        whole = int(week)
        return whole * 7 + (week - whole) * 5
    for i, a in enumerate(order):
        start, dur = span(a)
        critical = ls[a] == es[a]
        left, right = x(start), x(start + dur) if (start + dur) % 1 else x(start + dur) - 2
        ax.barh(i, right - left, left=left, height=.62, color="#C0392B" if critical else "#4A6F97", alpha=.95)
        members = list(dict.fromkeys(r[0] for r in A[a][2]))
        ax.text(right + .8, i, ", ".join(members), va="center", fontsize=7.5, color="#333333")
    ticks = list(range(0, 71, 7))
    ax.set_xticks(ticks)
    ax.set_xticklabels([monday(t // 7).strftime("%d %b") for t in ticks], fontsize=8)
    ax.set_yticks(range(len(order)))
    ax.set_yticklabels([f"WBS-{a}  {A[a][0]}" for a in order], fontsize=7.8)
    ax.set_ylim(len(order) - .3, -1.6)
    ax.set_xlim(0, 88)
    for t in ticks:
        ax.axvline(t, color="#DDDDDD", lw=.8, zorder=0)
    for w in range(10):
        ax.text(w * 7 + 3.5, -1.1, f"W{w+1}", ha="center", fontsize=8.5, color="#7F8C8D")
    ax.axvline(68, color="#27AE60", ls="--", lw=1.4)
    ax.text(68.6, -1.1, "Codebase deadline\n25 Dec 23:59", fontsize=7, color="#27AE60", va="center")
    pos = 0
    for g in groups:
        n = sum(1 for a in order if A[a][1] == g)
        ax.text(87.5, pos + (n - 1) / 2, g, ha="right", va="center", style="italic", fontsize=8, color="#7F8C8D")
        pos += n
        if pos < len(order):
            ax.axhline(pos - .5, color="#BBBBBB", lw=.8)
    for s in ("top", "right"):
        ax.spines[s].set_visible(False)
    ax.set_title("SchemaDiff, project timeline (19 Oct to 25 Dec 2026)", loc="left", fontsize=11)
    handles = [plt.Rectangle((0, 0), 1, 1, color="#C0392B"), plt.Rectangle((0, 0), 1, 1, color="#4A6F97")]
    ax.legend(handles, ["Critical path (zero float)", "Non-critical activity"], loc="upper center",
              bbox_to_anchor=(.3, -.035), ncol=2, frameon=False, fontsize=8)
    fig.tight_layout()
    fig.savefig(path)


def report():
    es, ef, ls, lf = cpm()
    print("total effort", sum(effort(a) for a in A), "activities", len(A))
    print("critical", [a for a in A if ls[a] == es[a]])
    print("float", {a: fmt(ls[a] - es[a]) for a in A if ls[a] != es[a]})
    table = allocation()
    for m, weeks in table.items():
        print(m, [fmt(v) for v in weeks], fmt(sum(weeks)))
    print("week totals", [fmt(sum(table[m][w] for m in NAMES)) for w in range(10)])


if __name__ == "__main__":
    problems = check()
    report()
    if problems:
        print("\n".join(problems))
        sys.exit(1)
    here = os.path.dirname(os.path.abspath(__file__))
    images = os.path.join(here, "..", "..", "images")
    pert(os.path.join(here, "pert.dot"), os.path.join(images, "pert-chart.png"))
    gantt(os.path.join(images, "gantt-chart.png"))
