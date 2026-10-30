// Writes the curved ring text of icon.svg letter by letter, between <!--RING-START--> and
// <!--RING-END-->. ImageMagick's SVG renderer ignores <textPath>, so each glyph is placed and
// rotated on its own. Advances are estimates for a bold sans font (Bahnschrift), in em.
// Usage: node ring-text.mjs icon.svg
import { readFileSync, writeFileSync } from "node:fs";

const file = process.argv[2] ?? "icon.svg";
const CX = 512, CY = 512;
const FONT = "Bahnschrift, Segoe UI, Arial, sans-serif";

const advance = (ch) => {
  if (ch === " ") return 0.32;
  if (ch === "·") return 0.34;
  if ("IJ1".includes(ch)) return 0.34;
  if ("MW".includes(ch)) return 0.82;
  if ("AVYTXKOQGCD".includes(ch)) return 0.64;
  return 0.58;
};

// Lays text along a circle of radius r, centred on the top (or the bottom).
function ring(text, { r, size, spacing, fill, weight, bottom }) {
  const widths = [...text].map((ch) => advance(ch) * size + spacing);
  const total = widths.reduce((a, b) => a + b, 0) - spacing;
  let s = -total / 2;
  const out = [];
  [...text].forEach((ch, i) => {
    const mid = s + (widths[i] - spacing) / 2;
    s += widths[i];
    if (ch === " ") return;
    const a = mid / r; // radians along the arc
    // Top: left to right over the top. Bottom: left to right under the bottom, glyphs upright.
    const x = CX + r * Math.sin(a);
    const y = bottom ? CY + r * Math.cos(a) : CY - r * Math.cos(a);
    const deg = (bottom ? -a : a) * 180 / Math.PI;
    out.push(`    <text x="0" y="0" transform="translate(${x.toFixed(1)} ${y.toFixed(1)}) rotate(${deg.toFixed(2)})" text-anchor="middle">${ch}</text>`);
  });
  return `  <g font-family="${FONT}" font-size="${size}" font-weight="${weight}" fill="${fill}">\n${out.join("\n")}\n  </g>`;
}

const top = ring("SKYSPACEAGENCY", { r: 444, size: 46, spacing: 9, fill: "#f4f7ff", weight: 700, bottom: false });
// Bottom glyphs point to the centre, so the baseline sits on the outer radius.
const bottom = ring("FLY FURTHER · KSP2 REDUX", { r: 478, size: 30, spacing: 7, fill: "#ffd27a", weight: 600, bottom: true });

const svg = readFileSync(file, "utf8");
const next = svg.replace(/<!--RING-START-->[\s\S]*?<!--RING-END-->/, `<!--RING-START-->\n${top}\n${bottom}\n  <!--RING-END-->`);
if (next === svg) throw new Error("ring markers not found");
writeFileSync(file, next);
console.log("ring text written");
