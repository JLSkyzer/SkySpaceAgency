# Builds the SkySpaceAgency UI concept screens that share the app window (header, tab bar,
# three columns, phase timeline, action bar) plus the video overlay. lift/transfer/land.svg were
# drawn by hand first; this script draws the other screens with the same parts and palette.
# Usage: python make_screens.py  (writes the .svg files next to it)
import os

os.chdir(os.path.dirname(os.path.abspath(__file__)))

NIGHT, CARD, LINE, CHIP = "#0a1230", "#0e1840", "#1f2f66", "#17245a"
TXT, MUTE, DIM = "#f4f7ff", "#8ea3d6", "#5c6ea6"
GOLD, GOLDL, CYAN, OK, CORAL, NODE = "#e8a93a", "#ffe29a", "#7fe7ff", "#d6f53c", "#ff7a5a", "#4fa8ff"
TABS = [("LIFT", 123), ("ORBIT", 283), ("TRANSFER", 443), ("RENDEZVOUS", 613), ("LAND", 773), ("RETURN", 913), ("NODES", 1053)]

DEFS = """  <defs>
    <linearGradient id="win" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#111c44"/><stop offset="1" stop-color="#080e26"/></linearGradient>
    <linearGradient id="gold" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#ffe29a"/><stop offset="1" stop-color="#d08a20"/></linearGradient>
    <linearGradient id="sky" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#0a1230"/><stop offset="1" stop-color="#13306a"/></linearGradient>
    <radialGradient id="planet" cx="40%" cy="35%" r="70%"><stop offset="0" stop-color="#5fd0e6"/><stop offset="0.5" stop-color="#2486b8"/><stop offset="1" stop-color="#0c2f5c"/></radialGradient>
  </defs>
"""


def t(x, y, s, size=14, fill=TXT, anchor="start", weight="400", extra=""):
    return f'<text x="{x}" y="{y}" font-size="{size}" fill="{fill}" text-anchor="{anchor}" font-weight="{weight}" {extra}>{s}</text>'


def card(x, y, w, h, title=None, fill=CARD):
    s = f'<rect x="{x}" y="{y}" width="{w}" height="{h}" rx="14" fill="{fill}" stroke="{LINE}"/>'
    if title:
        s += t(x + 24, y + 36, title, 14, MUTE, extra='letter-spacing="2"')
    return s


def field(x, y, w, label, value, unit=""):
    return (t(x, y, label, 14, MUTE) + f'<rect x="{x}" y="{y+10}" width="{w}" height="44" rx="10" fill="{NIGHT}" stroke="#24366e"/>'
            + t(x + 16, y + 39, value, 20) + (t(x + w - 18, y + 39, unit, 14, DIM, "end") if unit else ""))


def toggle(x, y, label, on, w=352):
    knob = f'<rect x="{x+w-60}" y="{y-16}" width="60" height="28" rx="14" fill="{OK if on else "#1d2c66"}"/>'
    knob += f'<circle cx="{x+w-14 if on else x+w-46}" cy="{y-2}" r="11" fill="{NIGHT if on else DIM}"/>'
    return t(x, y + 4, label, 15) + knob


def segmented(x, y, w, options, active):
    n = len(options)
    seg = w / n
    s = f'<rect x="{x}" y="{y}" width="{w}" height="40" rx="10" fill="{NIGHT}"/>'
    s += f'<rect x="{x+4+seg*active}" y="{y+4}" width="{seg-8}" height="32" rx="8" fill="#1d2c66"/>'
    for i, o in enumerate(options):
        s += t(x + seg * i + seg / 2, y + 25, o, 15, GOLDL if i == active else MUTE, "middle")
    return s


def tile(x, y, label, value, unit="", color=TXT, w=160, h=92):
    return (f'<rect x="{x}" y="{y}" width="{w}" height="{h}" rx="12" fill="{NIGHT}"/>' + t(x + 16, y + 26, label, 13, MUTE)
            + t(x + 16, y + 68, value, 30, color, weight="600") + (t(x + w - 16, y + 68, unit, 13, DIM, "end") if unit else ""))


def status(x, y, text, kind="ok", w=336):
    fill, stroke, col = ("#132a1e", "#3d6b2a", OK) if kind == "ok" else ("#2a1216", "#7a3030", CORAL) if kind == "alert" else ("#1a2350", "#2c4a9a", NODE)
    return (f'<rect x="{x}" y="{y}" width="{w}" height="44" rx="10" fill="{fill}" stroke="{stroke}"/>'
            f'<circle cx="{x+22}" cy="{y+22}" r="6" fill="{col}"/>' + t(x + 38, y + 27, text, 14, col))


def timeline(steps, current):
    s = card(40, 696, 1520, 80)
    xs = [120 + i * (1360 / (len(steps) - 1)) for i in range(len(steps))]
    s += f'<line x1="120" y1="726" x2="1480" y2="726" stroke="#1d2c66" stroke-width="4"/>'
    if current > 0:
        s += f'<line x1="120" y1="726" x2="{xs[current]}" y2="726" stroke="url(#gold)" stroke-width="4"/>'
    for i, (x, label) in enumerate(zip(xs, steps)):
        if i < current:
            s += f'<circle cx="{x}" cy="726" r="12" fill="{GOLD}"/>' + t(x, 760, label, 14, GOLDL, "middle")
        elif i == current:
            s += f'<circle cx="{x}" cy="726" r="15" fill="{NIGHT}" stroke="{GOLDL}" stroke-width="4"/><circle cx="{x}" cy="726" r="6" fill="{GOLDL}"/>'
            s += t(x, 760, label, 14, TXT, "middle", "700")
        else:
            s += f'<circle cx="{x}" cy="726" r="12" fill="#1d2c66"/>' + t(x, 760, label, 14, MUTE, "middle")
    return s


def actions(note, buttons):
    s = t(48, 832, note, 14, MUTE)
    x = 1560
    for label, kind in reversed(buttons):
        w = 150
        x -= w
        fill, stroke, col = {"main": ("url(#gold)", "none", "#1a1205"), "abort": ("#3a1a1a", "#7a3030", "#ff9a7a"),
                             "plain": (CHIP, "#24366e", TXT)}[kind]
        s += f'<rect x="{x}" y="800" width="{w}" height="52" rx="12" fill="{fill}" stroke="{stroke}"/>' + t(x + w / 2, 832, label, 17, col, "middle", "600")
        x -= 16
    return s


def window(active, chips, body, badge=None):
    s = f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1600 900" width="1600" height="900" font-family="Bahnschrift, Segoe UI, Arial, sans-serif">\n'
    s += f'  <!-- SkySpaceAgency UI concept, {active.title()} tab (generated by make_screens.py). -->\n' + DEFS
    s += '<rect width="1600" height="900" fill="#050816"/><rect x="20" y="20" width="1560" height="860" rx="18" fill="url(#win)" stroke="#24366e" stroke-width="2"/>'
    s += f'<circle cx="62" cy="62" r="24" fill="{NIGHT}" stroke="url(#gold)" stroke-width="4"/><path d="M 58 76 L 58 54 Q 62 42 66 54 L 66 76 Z" fill="#fff"/>'
    s += f'<path d="M 46 70 A 22 8 -15 0 0 80 60" fill="none" stroke="{GOLD}" stroke-width="3"/>'
    s += t(100, 58, "SKYSPACEAGENCY", 24, TXT, weight="700", extra='letter-spacing="2"') + t(100, 80, "FLY FURTHER", 14, MUTE, extra='letter-spacing="1"')
    x = 520
    for i, c in enumerate(chips):
        w = 230 if i == 0 else 12 + 9 * len(c[0] + c[1]) + 20
        s += f'<rect x="{x}" y="44" width="{w}" height="36" rx="18" fill="{CHIP}"/>'
        if i == 0:
            s += f'<circle cx="{x+22}" cy="62" r="6" fill="{OK}"/>' + t(x + 36, 67, c[1], 15)
        else:
            s += f'<text x="{x+20}" y="67" font-size="15" fill="{MUTE}">{c[0]} <tspan fill="{TXT}">{c[1]}</tspan></text>'
        x += w + 12
    s += f'<rect x="1470" y="48" width="28" height="28" rx="6" fill="{CHIP}"/><path d="M 1477 62 h 14" stroke="{MUTE}" stroke-width="2.5"/>'
    s += f'<rect x="1508" y="48" width="28" height="28" rx="6" fill="{CHIP}"/><path d="M 1515 55 l 14 14 M 1529 55 l -14 14" stroke="{MUTE}" stroke-width="2.5"/>'
    s += '<rect x="40" y="104" width="1520" height="52" rx="12" fill="#0c1538"/>'
    for name, cx in TABS:
        if name == active:
            s += f'<rect x="{cx-75}" y="110" width="150" height="40" rx="9" fill="#1d2c66"/><rect x="{cx-75}" y="146" width="150" height="4" rx="2" fill="url(#gold)"/>'
        s += t(cx, 137, name, 17, GOLDL if name == active else MUTE, "middle", "600", 'letter-spacing="1"')
    if active == "SETTINGS":
        s += '<rect x="1400" y="110" width="150" height="40" rx="9" fill="#1d2c66"/><rect x="1400" y="146" width="150" height="4" rx="2" fill="url(#gold)"/>'
    s += t(1500, 137, "SETTINGS", 17, GOLDL if active == "SETTINGS" else DIM, "end", "600" if active == "SETTINGS" else "400", 'letter-spacing="1"')
    if badge:
        s += f'<rect x="1180" y="116" width="150" height="28" rx="14" fill="#2a2140" stroke="#7a5ab0"/>' + t(1255, 135, badge, 13, "#c9b2ff", "middle", "600", 'letter-spacing="2"')
    return s + body + "\n</svg>\n"


# ---------------------------------------------------------------- ORBIT
def orbit():
    b = card(40, 172, 400, 508, "TARGET ORBIT")
    b += segmented(64, 224, 352, ["From orbit", "From flyby"], 0)
    b += field(64, 300, 168, "Apoapsis", "250", "km") + field(248, 300, 168, "Periapsis", "250", "km")
    b += field(64, 388, 168, "Inclination", "45.0", "°") + field(248, 388, 168, "Ascending node", "free", "")
    b += toggle(64, 500, "Keep Ap", True) + toggle(64, 544, "Keep Pe", True) + toggle(64, 588, "Change inclination", True)
    b += toggle(64, 632, "One node at a time", True)
    b += card(456, 172, 704, 508, "ORBIT VIEW", "url(#sky)") + t(1136, 208, "top view, Kerbin", 13, DIM, "end")
    b += '<circle cx="808" cy="430" r="70" fill="url(#planet)"/>'
    b += f'<ellipse cx="808" cy="430" rx="120" ry="112" fill="none" stroke="{CYAN}" stroke-width="3" stroke-dasharray="10 8"/>'
    b += f'<ellipse cx="808" cy="430" rx="210" ry="150" fill="none" stroke="url(#gold)" stroke-width="5" transform="rotate(-25 808 430)"/>'
    b += f'<circle cx="928" cy="430" r="11" fill="{NIGHT}" stroke="{NODE}" stroke-width="4"/><circle cx="928" cy="430" r="4" fill="{NODE}"/>' + t(946, 424, "1 · raise Ap · 412 m/s", 13, "#8fc4ff")
    b += f'<circle cx="618" cy="338" r="11" fill="{NIGHT}" stroke="{NODE}" stroke-width="4"/><circle cx="618" cy="338" r="4" fill="{NODE}"/>' + t(600, 318, "2 · circularize + plane · 690 m/s", 13, "#8fc4ff", "middle")
    b += '<g transform="translate(808 318)"><circle r="12" fill="#0a1230" stroke="#ffe29a" stroke-width="3"/><path d="M -3 5 L -3 -5 Q 0 -10 3 -5 L 3 5 Z" fill="#fff"/></g>'
    b += t(480, 650, "current 100 x 100 km, 0°", 13, CYAN) + t(1136, 650, "target 250 x 250 km, 45°", 13, GOLDL, "end")
    b += card(1176, 172, 384, 508, "CURRENT ORBIT")
    b += tile(1200, 224, "APOAPSIS", "100.2", "km") + tile(1376, 224, "PERIAPSIS", "99.8", "km")
    b += tile(1200, 328, "INCLINATION", "0.1", "°") + tile(1376, 328, "PERIOD", "32:10")
    b += t(1200, 456, "PLAN", 13, MUTE, extra='letter-spacing="2"')
    b += t(1200, 486, "1  Raise Ap to 250 km", 14) + t(1536, 486, "412 m/s", 14, GOLDL, "end")
    b += t(1200, 516, "2  Circularize, tilt to 45°", 14) + t(1536, 516, "690 m/s", 14, GOLDL, "end")
    b += f'<line x1="1200" y1="534" x2="1536" y2="534" stroke="#24366e"/>' + t(1200, 562, "Total, combined burn", 14, MUTE) + t(1536, 562, "1 102 m/s", 14, OK, "end")
    b += status(1200, 614, "Plane change merged: 380 m/s saved")
    b += timeline(["Plan", "Raise Ap", "Coast to Ap", "Circularize + plane", "Check orbit", "Done"], 0)
    b += actions("Each step is planned again from the orbit actually reached", [("Plan", "plain"), ("Start", "main")])
    return window("ORBIT", [("", "Kerbal Explorer II"), ("Kerbin ·", "Orbit"), ("Δv", "2 070 m/s"), ("Inc", "0.1°")], b)


# ---------------------------------------------------------------- RENDEZVOUS
def rendezvous():
    b = card(40, 172, 400, 508, "TARGET")
    for i, (name, dist, sel) in enumerate([("Kerbin Station Alpha", "182 km", True), ("Fuel Depot", "2 410 km", False), ("Comms Relay 3", "9 800 km", False)]):
        y = 224 + i * 64
        b += f'<rect x="64" y="{y}" width="352" height="54" rx="12" fill="{"#1a2350" if sel else NIGHT}" stroke="{GOLD if sel else "#24366e"}"/>'
        b += t(84, y + 33, name, 16, TXT if sel else MUTE, weight="600" if sel else "400") + t(400, y + 33, dist, 14, MUTE, "end")
    b += segmented(64, 424, 352, ["Rendezvous", "+ Dock", "Dock only"], 1)
    b += t(64, 500, "Docking ports", 14, MUTE)
    b += f'<rect x="64" y="510" width="352" height="60" rx="12" fill="{NIGHT}" stroke="#24366e"/>'
    b += t(84, 536, "Ours: Clamp-O-Tron (nose)", 14) + t(84, 558, "Theirs: Clamp-O-Tron (port 2)", 14) + t(400, 548, "Change", 14, GOLDL, "end", "600")
    b += toggle(64, 612, "Use RCS for the approach", True) + toggle(64, 656, "Main engine if no RCS", True)
    b += card(456, 172, 704, 508, "APPROACH", "url(#sky)") + t(1136, 208, "target frame, port axis", 13, DIM, "end")
    for r, lab in [(70, "10 m"), (150, "50 m"), (230, "200 m")]:
        b += f'<circle cx="808" cy="440" r="{r}" fill="none" stroke="{CYAN}" stroke-opacity="0.18" stroke-dasharray="4 6"/>' + t(808 + r * 0.7 + 6, 440 - r * 0.7, lab, 12, DIM)
    b += f'<path d="M 808 440 L 1100 360 L 1100 520 Z" fill="{OK}" fill-opacity="0.06" stroke="{OK}" stroke-opacity="0.35"/>' + t(1090, 350, "approach corridor", 12, OK, "end")
    b += '<g transform="translate(808 440)"><rect x="-40" y="-12" width="80" height="24" rx="4" fill="#dfe5f0"/><g fill="#2a5fb0" stroke="#9ec8ff"><rect x="-34" y="-58" width="26" height="44"/><rect x="-34" y="14" width="26" height="44"/><rect x="8" y="-58" width="26" height="44"/><rect x="8" y="14" width="26" height="44"/></g><rect x="40" y="-6" width="10" height="12" fill="#ffe29a"/></g>'
    b += f'<line x1="858" y1="440" x2="1010" y2="420" stroke="{GOLDL}" stroke-width="3" stroke-dasharray="8 6"/>'
    b += '<g transform="translate(1030 417) rotate(-98)"><path d="M -10 -12 L 10 -12 L 7 10 L -7 10 Z" fill="#dfe5f0"/><rect x="-4" y="-17" width="8" height="5" fill="#ffe29a"/></g>'
    b += t(1030, 460, "hold at 50 m", 13, GOLDL, "middle")
    b += card(1176, 172, 384, 508, "RELATIVE")
    b += tile(1200, 224, "DISTANCE", "48.6", "m") + tile(1376, 224, "CLOSING", "0.42", "m/s")
    b += tile(1200, 328, "LATERAL ERR", "0.18", "m") + tile(1376, 328, "RCS LEFT", "86", "%", OK)
    b += t(1200, 456, "CHECKS", 13, MUTE, extra='letter-spacing="2"')
    for i, (c, ok) in enumerate([("Target never commanded", True), ("Port aligned, 0.6°", True), ("Speed under limit", True)]):
        b += f'<circle cx="1208" cy="{482+i*30}" r="6" fill="{OK if ok else CORAL}"/>' + t(1224, 487 + i * 30, c, 14)
    b += status(1200, 614, "Holding at 50 m, then final approach")
    b += timeline(["Plane match", "Phasing", "Intercept", "Brake 200 m", "Hold 50 m", "Hold 10 m", "Docked"], 4)
    b += actions("A vessel switch stops the pilot; our throttle and RCS are cut", [("Back off", "plain"), ("Abort", "abort"), ("Docking…", "main")])
    return window("RENDEZVOUS", [("", "Ferry K-7"), ("Kerbin ·", "Orbit 120 km"), ("Δv", "640 m/s"), ("RCS", "86 %")], b)


# ---------------------------------------------------------------- RETURN (planned)
def ret():
    b = card(40, 172, 400, 508, "BOOSTER")
    b += f'<rect x="64" y="224" width="352" height="64" rx="12" fill="{NIGHT}" stroke="#24366e"/>' + t(84, 251, "Stage 1 · booster", 16, weight="600") + t(84, 273, "3 engines · grid fins · legs", 13, MUTE)
    b += f'<rect x="64" y="300" width="352" height="54" rx="12" fill="#132a1e" stroke="#3d6b2a"/>' + f'<circle cx="86" cy="327" r="6" fill="{OK}"/>' + t(102, 333, "Recovery Computer found (SM)", 14, OK)
    b += t(64, 386, "Return point", 14, MUTE)
    b += f'<rect x="64" y="396" width="352" height="64" rx="12" fill="{NIGHT}" stroke="{GOLD}"/>' + t(84, 423, "KSC · Landing pad 1", 16) + t(84, 445, "0.10° S · 74.56° W · 2.3 km from the pad", 13, MUTE)
    b += f'<rect x="64" y="470" width="170" height="36" rx="9" fill="{CHIP}"/>' + t(149, 493, "Pick on map", 14, anchor="middle")
    b += f'<rect x="246" y="470" width="170" height="36" rx="9" fill="{CHIP}"/>' + t(331, 493, "Coordinates", 14, anchor="middle")
    b += toggle(64, 560, "Start Return on taking control", True) + toggle(64, 604, "Record the booster", True) + toggle(64, 648, "Ditch at sea if short", False)
    b += card(456, 172, 704, 508, "RETURN PROFILE", "url(#sky)") + t(1136, 208, "side view, downrange", 13, DIM, "end")
    b += f'<rect x="480" y="612" width="656" height="4" fill="#3f9b52"/>'
    b += f'<path d="M 520 612 C 560 480, 640 330, 820 260 S 1060 230, 1130 228" fill="none" stroke="{CYAN}" stroke-opacity="0.5" stroke-width="3"/>' + t(1126, 220, "upper stage", 12, CYAN, "end")
    b += f'<circle cx="760" cy="282" r="9" fill="{GOLD}"/>' + t(760, 262, "separation", 12, GOLDL, "middle")
    b += f'<path d="M 760 282 C 820 250, 860 270, 820 330 C 760 410, 640 470, 560 560 L 540 600" fill="none" stroke="{GOLDL}" stroke-width="4" stroke-dasharray="12 8"/>'
    b += f'<path d="M 760 282 L 840 230 L 850 300 Z" fill="{CORAL}" fill-opacity="0.12" stroke="{CORAL}" stroke-opacity="0.4"/>' + t(856, 244, "keep-out cone", 12, "#ff9a7a")
    b += f'<circle cx="842" cy="290" r="7" fill="{NODE}"/>' + t(856, 296, "flip + boostback", 12, "#8fc4ff")
    b += f'<circle cx="700" cy="430" r="7" fill="{GOLD}"/>' + t(714, 434, "entry burn", 12, GOLDL)
    b += f'<circle cx="560" cy="560" r="7" fill="{GOLD}"/>' + t(574, 562, "landing burn", 12, GOLDL)
    b += f'<g transform="translate(538 612)"><ellipse rx="34" ry="8" fill="#13325a" stroke="{CYAN}" stroke-width="3"/></g>' + t(538, 646, "pad", 12, CYAN, "middle")
    b += f'<g transform="translate(520 612)"><path d="M -6 0 L -6 -26 Q 0 -36 6 -26 L 6 0 Z" fill="#fff"/></g>'
    b += card(1176, 172, 384, 508, "RECOVERY BUDGET")
    b += tile(1200, 224, "Δv TO KEEP", "640", "m/s", GOLDL) + tile(1376, 224, "SEPARATE AT", "2:24", "T+")
    b += tile(1200, 328, "BOOSTBACK", "310", "m/s") + tile(1376, 328, "LANDING", "260", "m/s")
    b += t(1200, 456, "CLEARANCE AT SEPARATION", 13, MUTE, extra='letter-spacing="2"')
    for i, c in enumerate(["Distance over 60 m", "Upper stage out of the plume", "Paths never closer than 40 m"]):
        b += f'<circle cx="1208" cy="{482+i*30}" r="6" fill="{OK}"/>' + t(1224, 487 + i * 30, c, 14)
    b += status(1200, 614, "Engines light only when all checks pass", "info")
    b += timeline(["Separation", "Clearance", "Flip", "Boostback", "Entry burn", "Landing burn", "Landed"], 1)
    b += actions("Planned: booster recovery, after the transfers and rendezvous work", [("Abort", "abort"), ("Return", "main")])
    return window("RETURN", [("", "Booster · Explorer II"), ("Kerbin ·", "Sub-orbital"), ("Δv", "702 m/s"), ("Fuel", "18 %")], b, "PLANNED")


# ---------------------------------------------------------------- NODES
def nodes():
    b = card(40, 172, 400, 508, "MANEUVER NODES")
    for i, (name, dv, tt, sel) in enumerate([("Node 1 · prograde", "412 m/s", "in 4:12", True), ("Node 2 · normal", "38 m/s", "in 21:40", False)]):
        y = 224 + i * 74
        b += f'<rect x="64" y="{y}" width="352" height="64" rx="12" fill="{"#1a2350" if sel else NIGHT}" stroke="{NODE if sel else "#24366e"}"/>'
        b += f'<circle cx="92" cy="{y+32}" r="10" fill="{NIGHT}" stroke="{NODE}" stroke-width="4"/>'
        b += t(114, y + 28, name, 15, TXT if sel else MUTE) + t(114, y + 50, dv, 13, GOLDL) + t(400, y + 40, tt, 14, MUTE, "end")
    b += t(64, 404, "Warp during burns", 14, MUTE) + segmented(64, 414, 352, ["Off", "Physics x4", "Rails, long"], 1)
    b += field(64, 488, 168, "Lead time", "60", "s") + field(248, 488, 168, "Max angle", "2.0", "°")
    b += toggle(64, 612, "Drop empty stages", True) + toggle(64, 656, "Closed-loop trim at the end", True)
    b += card(456, 172, 704, 508, "BURN", "url(#sky)") + t(1136, 208, "attitude and remaining Δv", 13, DIM, "end")
    b += f'<circle cx="808" cy="420" r="170" fill="#0a1230" stroke="#24366e" stroke-width="2"/>'
    b += f'<path d="M 638 420 A 170 170 0 0 0 978 420 Z" fill="#3a2a1a" opacity="0.6"/><line x1="638" y1="420" x2="978" y2="420" stroke="#ffffff" stroke-opacity="0.3"/>'
    b += f'<g stroke="{CYAN}" stroke-opacity="0.12" fill="none"><ellipse cx="808" cy="420" rx="85" ry="170"/><ellipse cx="808" cy="420" rx="170" ry="85"/></g>'
    b += f'<g transform="translate(830 404)" fill="none" stroke="{NODE}" stroke-width="5"><circle r="22"/><circle r="5" fill="{NODE}"/><line y1="-22" y2="-36"/><line x1="19" y1="11" x2="31" y2="18"/><line x1="-19" y1="11" x2="-31" y2="18"/></g>'
    b += f'<g stroke="{GOLDL}" stroke-width="4" fill="none"><line x1="788" y1="420" x2="800" y2="420"/><line x1="816" y1="420" x2="828" y2="420"/><path d="M 800 420 l 8 8 l 8 -8"/></g>'
    b += t(808, 630, "aligned to 0.8°", 14, OK, "middle")
    b += card(1176, 172, 384, 508, "BURN DATA")
    b += tile(1200, 224, "Δv LEFT", "286", "m/s", GOLDL) + tile(1376, 224, "BURN TIME", "0:41")
    b += tile(1200, 328, "THROTTLE", "100", "%") + tile(1376, 328, "WARP", "x4", "phys", CYAN)
    b += t(1200, 456, "Δv APPLIED", 13, MUTE, extra='letter-spacing="2"')
    b += f'<rect x="1200" y="470" width="336" height="14" rx="7" fill="#1d2c66"/><rect x="1200" y="470" width="103" height="14" rx="7" fill="url(#gold)"/>'
    b += t(1200, 510, "126 of 412 m/s · gravity not counted as missing", 13, MUTE)
    b += status(1200, 614, "Burning, target orbit converging")
    b += timeline(["Align", "Warp to node", "Ignite", "Burn", "Trim", "Done"], 3)
    b += actions("The engine is cut before any rails warp, never during a burn", [("Skip node", "plain"), ("Abort", "abort"), ("Executing…", "main")])
    return window("NODES", [("", "Kerbal Explorer II"), ("Kerbin ·", "Orbit"), ("Δv", "2 070 m/s"), ("Nodes", "2")], b)


# ---------------------------------------------------------------- SETTINGS
def settings():
    b = card(40, 172, 300, 508, "CATEGORIES")
    for i, (c, sel) in enumerate([("General", True), ("Pilots", False), ("Warp", False), ("Interface", False), ("Recording", False), ("Logs", False), ("About", False)]):
        y = 220 + i * 60
        if sel:
            b += f'<rect x="56" y="{y}" width="268" height="46" rx="10" fill="#1d2c66"/><rect x="56" y="{y}" width="4" height="46" rx="2" fill="url(#gold)"/>'
        b += t(80, y + 29, c, 16, GOLDL if sel else MUTE, weight="600" if sel else "400")
    b += card(356, 172, 804, 508, "GENERAL")
    rows = [("Language", "English"), ("Units", "Metric (km, m/s)"), ("Open the window", "Alt + S")]
    for i, (k, v) in enumerate(rows):
        y = 230 + i * 70
        b += t(380, y + 30, k, 16) + f'<rect x="760" y="{y}" width="376" height="48" rx="10" fill="{NIGHT}" stroke="#24366e"/>' + t(780, y + 31, v, 16) + f'<path d="M 1110 {y+20} l 8 8 l 8 -8" fill="none" stroke="{MUTE}" stroke-width="2.5"/>'
    b += t(380, 470, "Interface scale", 16) + t(1136, 470, "110 %", 16, anchor="end")
    b += f'<rect x="760" y="484" width="376" height="6" rx="3" fill="#1d2c66"/><rect x="760" y="484" width="220" height="6" rx="3" fill="{CYAN}"/><circle cx="980" cy="487" r="10" fill="{TXT}" stroke="{CYAN}" stroke-width="3"/>'
    b += t(380, 540, "Accent colour", 16)
    for i, c in enumerate([GOLD, CYAN, OK, "#c9b2ff", CORAL]):
        b += f'<circle cx="{780+i*52}" cy="534" r="16" fill="{c}" stroke="{"#ffffff" if i == 0 else "none"}" stroke-width="3"/>'
    b += toggle(380, 610, "Compact flight bar while a pilot runs", True, 756) + toggle(380, 654, "Explain each step before it flies", True, 756)
    b += card(1176, 172, 384, 508, "ABOUT")
    b += f'<circle cx="1368" cy="290" r="64" fill="{NIGHT}" stroke="url(#gold)" stroke-width="6"/><path d="M 1360 322 L 1360 270 Q 1368 244 1376 270 L 1376 322 Z" fill="#fff"/>'
    b += f'<path d="M 1316 314 A 60 20 -15 0 0 1420 290" fill="none" stroke="{GOLD}" stroke-width="5"/>'
    b += t(1368, 390, "SkySpaceAgency", 22, TXT, "middle", "700") + t(1368, 416, "development build · not released", 14, MUTE, "middle")
    b += t(1368, 470, "CC BY-SA 4.0", 14, GOLDL, "middle") + t(1368, 496, "Built on K2-D2 by Christophe Floutier", 13, MUTE, "middle") + t(1368, 516, "and its Redux port by IanMealworm", 13, MUTE, "middle")
    b += status(1200, 614, "Settings saved per profile (vacuum / atmosphere)", "info")
    b += actions("Changes apply at once; pilots read them at their next phase", [("Reset", "plain"), ("Done", "main")])
    return window("SETTINGS", [("", "Kerbal Explorer II"), ("Kerbin ·", "Pad"), ("Version", "dev"), ("Mods", "1")], b)


# ---------------------------------------------------------------- VIDEO OVERLAY
def overlay():
    W, H = 1920, 1080
    s = f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {W} {H}" width="{W}" height="{H}" font-family="Bahnschrift, Segoe UI, Arial, sans-serif">\n'
    s += '  <!-- SkySpaceAgency recording overlay concept (webcast style): both vehicles, mission clock, event strip. -->\n'
    s += """  <defs>
    <linearGradient id="air" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#02040c"/><stop offset="0.55" stop-color="#0b1d44"/><stop offset="0.8" stop-color="#3a7ab8"/><stop offset="1" stop-color="#9fd6f0"/></linearGradient>
    <linearGradient id="plume" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#fffbe8"/><stop offset="0.3" stop-color="#ffb02e"/><stop offset="1" stop-color="#ff4d1a" stop-opacity="0"/></linearGradient>
    <linearGradient id="band" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#000000" stop-opacity="0"/><stop offset="1" stop-color="#000000" stop-opacity="0.75"/></linearGradient>
    <linearGradient id="gold" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#ffe29a"/><stop offset="1" stop-color="#d08a20"/></linearGradient>
  </defs>
"""
    s += f'<rect width="{W}" height="{H}" fill="url(#air)"/>'
    s += '<ellipse cx="960" cy="2300" rx="2600" ry="1400" fill="#2a6aa0"/><ellipse cx="960" cy="2310" rx="2600" ry="1400" fill="#3f9b52" opacity="0.35"/>'
    s += '<g transform="translate(980 470) rotate(-12)"><path d="M -40 170 Q 0 420 40 170 Z" fill="url(#plume)"/>'
    s += '<rect x="-40" y="-200" width="80" height="370" rx="6" fill="#e9edf5"/><rect x="-40" y="40" width="80" height="22" fill="#1b2a52"/>'
    s += '<g fill="#2b3449"><rect x="-70" y="-190" width="30" height="26"/><rect x="40" y="-190" width="30" height="26"/><path d="M -40 120 L -84 186 L -78 190 L -36 134 Z"/><path d="M 40 120 L 84 186 L 78 190 L 36 134 Z"/></g></g>'
    s += f'<rect y="{H-300}" width="{W}" height="300" fill="url(#band)"/>'
    s += '<circle cx="78" cy="78" r="30" fill="#0a1230" stroke="url(#gold)" stroke-width="4"/><path d="M 73 95 L 73 67 Q 78 54 83 67 L 83 95 Z" fill="#fff"/>'
    s += t(124, 72, "SKYSPACEAGENCY", 24, TXT, weight="700", extra='letter-spacing="3"') + t(124, 98, "DUNA LANDER I · BOOSTER CAM 2", 15, "#c4d0ef", extra='letter-spacing="2"')
    s += f'<rect x="1730" y="52" width="130" height="36" rx="18" fill="#2a1216" stroke="{CORAL}"/><circle cx="1754" cy="70" r="7" fill="{CORAL}"/>' + t(1772, 76, "REC", 16, "#ffb09a", weight="700", extra='letter-spacing="2"')

    def vehicle(x, title, speed, alt, fuel, engines_on, n_eng, state=None):
        v = t(x, 840, title, 18, "#ffffff", weight="700", extra='letter-spacing="4"')
        if state:
            v += f'<rect x="{x}" y="862" width="420" height="120" rx="12" fill="#000000" fill-opacity="0.35" stroke="#ffffff" stroke-opacity="0.25"/>'
            v += t(x + 210, 934, state, 30, "#ffffff", "middle", "700", 'letter-spacing="6" opacity="0.75"')
            return v
        v += t(x, 880, "SPEED", 13, "#c4d0ef", extra='letter-spacing="2"') + t(x, 924, speed, 40, "#ffffff", weight="600") + t(x + 150, 924, "KM/H", 14, "#c4d0ef")
        v += t(x + 230, 880, "ALTITUDE", 13, "#c4d0ef", extra='letter-spacing="2"') + t(x + 230, 924, alt, 40, "#ffffff", weight="600") + t(x + 360, 924, "KM", 14, "#c4d0ef")
        for i, (name, f) in enumerate(fuel):
            yy = 950 + i * 26
            v += t(x, yy + 10, name, 12, "#c4d0ef", extra='letter-spacing="2"')
            v += f'<rect x="{x+56}" y="{yy}" width="200" height="10" rx="5" fill="#ffffff" fill-opacity="0.2"/><rect x="{x+56}" y="{yy}" width="{200*f}" height="10" rx="5" fill="#ffffff"/>'
        cx, cy = x + 420, 1012
        v += f'<circle cx="{cx}" cy="{cy}" r="44" fill="none" stroke="#ffffff" stroke-opacity="0.5" stroke-width="2"/>'
        pts = [(0, 0)] if n_eng == 1 else [(0, -18), (-16, 10), (16, 10)]
        for j, (dx, dy) in enumerate(pts):
            on = j < engines_on
            v += f'<circle cx="{cx+dx}" cy="{cy+dy}" r="11" fill="{"#ffffff" if on else "none"}" stroke="#ffffff" stroke-width="2"/>'
        return v

    s += vehicle(80, "BOOSTER", "1 248", "38.6", [("CH4", 0.22), ("LOX", 0.25)], 1, 3)
    s += vehicle(1420, "UPPER STAGE", "", "", [], 0, 1, "SIGNAL LOST")
    s += t(960, 880, "T+ 00:03:12", 46, "#ffffff", "middle", "700", 'letter-spacing="3"')
    s += t(960, 910, "INCLINATION 0.1° · BOOSTBACK BURN", 15, "#c4d0ef", "middle", extra='letter-spacing="3"')
    x0, x1, y = 640, 1280, 960
    s += f'<line x1="{x0}" y1="{y}" x2="{x1}" y2="{y}" stroke="#ffffff" stroke-opacity="0.35" stroke-width="3"/>'
    s += f'<line x1="{x0}" y1="{y}" x2="{x0+(x1-x0)*0.42}" y2="{y}" stroke="#ffffff" stroke-width="3"/>'
    events = [("LIFTOFF", 0.0), ("MAX-Q", 0.14), ("SEP", 0.3), ("BOOSTBACK", 0.42), ("ENTRY", 0.62), ("LANDING", 0.85), ("TOUCHDOWN", 1.0)]
    for i, (e, f) in enumerate(events):
        xx = x0 + (x1 - x0) * f
        done = f <= 0.42
        s += f'<circle cx="{xx}" cy="{y}" r="7" fill="{"#ffffff" if done else "#000000"}" stroke="#ffffff" stroke-width="2"/>'
        s += t(xx, y + (30 if i % 2 == 0 else -18), e, 11, "#ffffff" if done else "#c4d0ef", "middle", extra='letter-spacing="1"')
    s += t(W - 40, H - 30, "development build · overlay concept", 12, "#c4d0ef", "end")
    return s + "\n</svg>\n"


# ---------------------------------------------------------------- VIDEO OVERLAY, webcast style (v2)
import math


def arc(cx, cy, r, a0, a1):
    # Arc path from angle a0 to a1 (degrees, 0 = right, clockwise on screen).
    x0, y0 = cx + r * math.cos(math.radians(a0)), cy + r * math.sin(math.radians(a0))
    x1, y1 = cx + r * math.cos(math.radians(a1)), cy + r * math.sin(math.radians(a1))
    large = 1 if (a1 - a0) % 360 > 180 else 0
    return f"M {x0:.1f} {y0:.1f} A {r} {r} 0 {large} 1 {x1:.1f} {y1:.1f}"


def gauge(cx, cy, label, value, unit, frac, dim=False):
    col = "#ffffff" if not dim else "#8a93a8"
    s = f'<path d="{arc(cx, cy, 64, 135, 405)}" fill="none" stroke="#ffffff" stroke-opacity="0.16" stroke-width="7" stroke-linecap="round"/>'
    if frac > 0:
        a1 = 135 + 270 * frac
        if not dim:
            s += f'<path d="{arc(cx, cy, 64, 135, a1)}" fill="none" stroke="#7fe7ff" stroke-width="12" stroke-opacity="0.45" stroke-linecap="round" filter="url(#glow)"/>'
        s += f'<path d="{arc(cx, cy, 64, 135, a1)}" fill="none" stroke="{col}" stroke-width="7" stroke-linecap="round"/>'
    s += t(cx, cy + 4, value, 28, col, "middle", "700")
    s += t(cx, cy + 26, unit, 11, "#c4d0ef", "middle", extra='letter-spacing="2"')
    s += t(cx, cy + 84, label, 12, "#c4d0ef", "middle", extra='letter-spacing="3"')
    return s


def engines(cx, cy, lit, dim=False):
    s = f'<circle cx="{cx}" cy="{cy}" r="46" fill="#000000" fill-opacity="0.35" stroke="#ffffff" stroke-opacity="{0.25 if dim else 0.55}" stroke-width="2"/>'
    for j, (dx, dy) in enumerate([(0, -17), (-15, 9), (15, 9)]):
        on = j < lit and not dim
        if on:
            s += f'<circle cx="{cx+dx}" cy="{cy+dy}" r="14" fill="#ffd27a" opacity="0.55" filter="url(#glow)"/>'
        s += f'<circle cx="{cx+dx}" cy="{cy+dy}" r="11" fill="{"#ffffff" if on else "none"}" stroke="#ffffff" stroke-opacity="{0.3 if dim else 1}" stroke-width="2"/>'
    return s


def props(x, y, fills, dim=False):
    s = ""
    for i, (name, f) in enumerate(fills):
        xx = x + i * 30
        s += f'<rect x="{xx}" y="{y}" width="10" height="104" rx="5" fill="#ffffff" fill-opacity="0.15"/>'
        h = 104 * f
        s += f'<rect x="{xx}" y="{y+104-h:.0f}" width="10" height="{h:.0f}" rx="5" fill="{"#8a93a8" if dim else "#ffffff"}"/>'
        s += t(xx + 5, y + 124, name, 10, "#c4d0ef", "middle", extra='letter-spacing="1"')
    return s


def overlay_v2():
    W, H = 1920, 1080
    s = f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {W} {H}" width="{W}" height="{H}" font-family="Bahnschrift, Segoe UI, Arial, sans-serif">\n'
    s += '  <!-- SkySpaceAgency recording overlay concept, webcast style: the booster seen by its chase camera during the boostback, both vehicles, mission clock on an arc, event banner. -->\n'
    s += """  <defs>
    <linearGradient id="air" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#010208"/><stop offset="0.45" stop-color="#071431"/><stop offset="0.72" stop-color="#1d4d8c"/><stop offset="0.86" stop-color="#6fb3e0"/><stop offset="1" stop-color="#cfeaf7"/></linearGradient>
    <radialGradient id="sunflare" cx="0.5" cy="0.5" r="0.5"><stop offset="0" stop-color="#fffbe8" stop-opacity="0.95"/><stop offset="0.15" stop-color="#ffe9b0" stop-opacity="0.5"/><stop offset="1" stop-color="#ffd27a" stop-opacity="0"/></radialGradient>
    <radialGradient id="limb" cx="0.5" cy="0.5" r="0.5"><stop offset="0.9" stop-color="#9fdcff" stop-opacity="0"/><stop offset="0.965" stop-color="#bfeaff" stop-opacity="0.85"/><stop offset="1" stop-color="#bfeaff" stop-opacity="0"/></radialGradient>
    <radialGradient id="ground" cx="0.45" cy="0.02" r="0.7"><stop offset="0" stop-color="#3a8ec0"/><stop offset="0.5" stop-color="#1f5f8f"/><stop offset="1" stop-color="#0b2a4a"/></radialGradient>
    <linearGradient id="hull" x1="0" y1="0" x2="1" y2="0"><stop offset="0" stop-color="#6e7890"/><stop offset="0.35" stop-color="#f2f5fa"/><stop offset="0.6" stop-color="#ffffff"/><stop offset="1" stop-color="#7d889f"/></linearGradient>
    <linearGradient id="soot" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#1a1d26" stop-opacity="0"/><stop offset="1" stop-color="#1a1d26" stop-opacity="0.75"/></linearGradient>
    <linearGradient id="plume" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#ffffff"/><stop offset="0.12" stop-color="#fff2c2"/><stop offset="0.35" stop-color="#ffb02e" stop-opacity="0.9"/><stop offset="1" stop-color="#ff4d1a" stop-opacity="0"/></linearGradient>
    <radialGradient id="plumeglow" cx="0.5" cy="0.3" r="0.6"><stop offset="0" stop-color="#ffd27a" stop-opacity="0.75"/><stop offset="1" stop-color="#ff7a1a" stop-opacity="0"/></radialGradient>
    <radialGradient id="vignette" cx="0.5" cy="0.45" r="0.75"><stop offset="0.6" stop-color="#000000" stop-opacity="0"/><stop offset="1" stop-color="#000000" stop-opacity="0.7"/></radialGradient>
    <linearGradient id="band" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#000000" stop-opacity="0"/><stop offset="0.45" stop-color="#000000" stop-opacity="0.55"/><stop offset="1" stop-color="#000000" stop-opacity="0.85"/></linearGradient>
    <linearGradient id="gold" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#ffe29a"/><stop offset="1" stop-color="#d08a20"/></linearGradient>
    <linearGradient id="bannerline" x1="0" y1="0" x2="1" y2="0"><stop offset="0" stop-color="#ffe29a" stop-opacity="0"/><stop offset="0.5" stop-color="#ffe29a"/><stop offset="1" stop-color="#ffe29a" stop-opacity="0"/></linearGradient>
    <filter id="glow" x="-50%" y="-50%" width="200%" height="200%"><feGaussianBlur stdDeviation="6"/></filter>
    <filter id="soft" x="-50%" y="-50%" width="200%" height="200%"><feGaussianBlur stdDeviation="18"/></filter>
    <filter id="haze" x="-20%" y="-20%" width="140%" height="140%"><feGaussianBlur stdDeviation="3"/></filter>
  </defs>
"""
    # ---- stand-in for the game camera feed (in game: the real booster, seen by the chosen camera)
    s += f'<rect width="{W}" height="{H}" fill="url(#air)"/>'
    s += f'<rect width="{W}" height="{H}" fill="#000000" fill-opacity="0.25"/>'
    s += t(960, 470, "GAME CAMERA FEED", 40, "#ffffff", "middle", "700", 'letter-spacing="14" opacity="0.18"')
    s += t(960, 510, "the real vessel, seen by the chosen camera", 18, "#ffffff", "middle", extra='letter-spacing="3" opacity="0.18"')
    # ---- top: brand, LIVE, camera, event banner, small telemetry
    s += '<circle cx="78" cy="76" r="30" fill="#0a1230" stroke="url(#gold)" stroke-width="4"/><path d="M 73 93 L 73 65 Q 78 52 83 65 L 83 93 Z" fill="#fff"/><path d="M 56 88 A 28 10 -15 0 0 102 74" fill="none" stroke="#e8a93a" stroke-width="3"/>'
    s += t(124, 70, "SKYSPACEAGENCY", 22, "#ffffff", weight="700", extra='letter-spacing="4"')
    s += f'<rect x="124" y="84" width="64" height="24" rx="4" fill="#e0342a"/>' + t(156, 101, "LIVE", 13, "#ffffff", "middle", "700", 'letter-spacing="2"')
    s += t(200, 101, "BOOSTER CAM 2 · CHASE", 14, "#e6ecf8", extra='letter-spacing="3"')
    s += f'<line x1="660" y1="96" x2="1260" y2="96" stroke="url(#bannerline)" stroke-width="2"/>'
    s += f'<line x1="660" y1="96" x2="1260" y2="96" stroke="url(#bannerline)" stroke-width="8" stroke-opacity="0.35" filter="url(#glow)"/>'
    s += t(960, 74, "BOOSTBACK BURN", 34, "#ffffff", "middle", "700", 'letter-spacing="8"')
    s += t(960, 124, "STARTUP · 3 ENGINES · HEADING HOME", 14, "#ffe29a", "middle", "600", 'letter-spacing="5"')
    s += f'<rect x="1676" y="56" width="200" height="34" rx="17" fill="#000000" fill-opacity="0.45" stroke="#ff7a5a" stroke-opacity="0.8"/><circle cx="1700" cy="73" r="7" fill="#ff5a3a"/>'
    s += t(1716, 79, "REC 00:03:12", 15, "#ffd2c4", weight="600", extra='letter-spacing="2"')
    s += t(1876, 118, "DOWNRANGE  61.4 KM", 13, "#e6ecf8", "end", extra='letter-spacing="2"') + t(1876, 140, "ACCEL  2.3 G", 13, "#e6ecf8", "end", extra='letter-spacing="2"')

    # ---- bottom band
    s += f'<rect y="{H-330}" width="{W}" height="330" fill="url(#band)"/>'
    s += t(80, 812, "BOOSTER", 16, "#ffffff", weight="700", extra='letter-spacing="5"') + f'<rect x="190" y="798" width="60" height="18" rx="9" fill="#d6f53c"/>' + t(220, 812, "BURN", 10, "#0a1230", "middle", "700", 'letter-spacing="1"')
    s += gauge(150, 920, "SPEED", "1 248", "KM/H", 0.42) + engines(290, 920, 3) + gauge(430, 920, "ALTITUDE", "38.6", "KM", 0.3)
    s += props(530, 862, [("LOX", 0.25), ("CH4", 0.22)])
    s += t(1840, 812, "SHIP", 16, "#ffffff", "end", "700", 'letter-spacing="5"')
    s += gauge(1490, 920, "SPEED", "--", "KM/H", 0.86, True) + engines(1630, 920, 0, True) + gauge(1770, 920, "ALTITUDE", "--", "KM", 0.7, True)
    s += props(1360, 862, [("LOX", 0.6), ("CH4", 0.58)], True)
    s += f'<rect x="1596" y="796" width="140" height="22" rx="11" fill="#2a1216" stroke="#ff7a5a"/>' + t(1666, 812, "SIGNAL LOST", 11, "#ffb09a", "middle", "700", 'letter-spacing="2"')
    s += t(960, 836, "SSA · DUNA LANDER I", 14, "#c4d0ef", "middle", extra='letter-spacing="5"')
    s += t(960, 884, "T+ 00:03:12", 52, "#ffffff", "middle", "700", 'letter-spacing="4"')
    cx, cy, r = 960, 1500, 560
    s += f'<path d="{arc(cx, cy, r, 235, 305)}" fill="none" stroke="#ffffff" stroke-opacity="0.28" stroke-width="3"/>'
    names = ["LIFTOFF", "MAX-Q", "MECO", "STAGE SEP", "BOOSTBACK", "ENTRY BURN", "LANDING BURN", "TOUCHDOWN"]
    events = [(n, i / (len(names) - 1)) for i, n in enumerate(names)]
    now = 4 / 7
    s += f'<path d="{arc(cx, cy, r, 235, 235 + 70 * now)}" fill="none" stroke="#ffffff" stroke-width="3"/>'
    s += f'<path d="{arc(cx, cy, r, 235, 235 + 70 * now)}" fill="none" stroke="#7fe7ff" stroke-width="8" stroke-opacity="0.4" filter="url(#glow)"/>'
    for i, (e, f) in enumerate(events):
        a = math.radians(235 + 70 * f)
        x, y = cx + r * math.cos(a), cy + r * math.sin(a)
        done = f <= now
        cur = abs(f - now) < 1e-6
        if cur:
            s += f'<circle cx="{x:.1f}" cy="{y:.1f}" r="16" fill="none" stroke="#ffe29a" stroke-width="2" opacity="0.8"/><circle cx="{x:.1f}" cy="{y:.1f}" r="22" fill="#ffe29a" opacity="0.25" filter="url(#glow)"/>'
        s += f'<circle cx="{x:.1f}" cy="{y:.1f}" r="7" fill="{"#ffffff" if done else "#0a0d18"}" stroke="#ffffff" stroke-width="2"/>'
        deg = 235 + 70 * f + 90
        rr = r + 22 if (i % 2 == 0 or i == len(events) - 1) else r - 30
        lx, ly = cx + rr * math.cos(a), cy + rr * math.sin(a)
        col = "#ffe29a" if cur else ("#ffffff" if done else "#9aa6c4")
        s += f'<text x="0" y="0" transform="translate({lx:.1f} {ly:.1f}) rotate({deg:.1f})" font-size="11" fill="{col}" text-anchor="middle" letter-spacing="2" font-weight="{"700" if cur else "400"}">{e}</text>'
    s += t(W - 40, H - 24, "SkySpaceAgency · overlay concept · development build", 11, "#9aa6c4", "end", extra='letter-spacing="1"')
    return s + "\n</svg>\n"


for name, fn in [("orbit", orbit), ("rendezvous", rendezvous), ("return", ret), ("nodes", nodes), ("settings", settings), ("overlay", overlay_v2)]:
    with open(name + ".svg", "w", encoding="utf-8") as f:
        f.write(fn())
print("written")
