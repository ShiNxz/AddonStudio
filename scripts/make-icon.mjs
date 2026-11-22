// Draws the app icon source: a flat orange rounded square with a white isometric cube, 1024x1024 PNG.
// No dependencies (node's zlib only), 4x4 supersampling for smooth edges.
//
// usage: node scripts/make-icon.mjs [out.png]   (default app/src-tauri/icons/app-icon.png)
// then:  cd app && npx tauri icon src-tauri/icons/app-icon.png
import { deflateSync } from "node:zlib";
import { mkdirSync, writeFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const out = resolve(process.argv[2] ?? resolve(here, "../app/src-tauri/icons/app-icon.png"));

const SIZE = 1024;
const SS = 4; // samples per axis

const BG = [236, 116, 52]; // orange
const WHITE = [255, 255, 255];

// rounded square
const INSET = 40;
const RADIUS = 200;

// cube: pointy-top hexagon around (CX, CY), faces split by the three edges that meet in the middle
const CX = 512;
const CY = 520;
const R = 290;
const W = R * Math.cos(Math.PI / 6);
const top = [CX, CY - R];
const ur = [CX + W, CY - R / 2];
const lr = [CX + W, CY + R / 2];
const bottom = [CX, CY + R];
const ll = [CX - W, CY + R / 2];
const ul = [CX - W, CY - R / 2];
const mid = [CX, CY];

// white faces with a little shading so the cube reads as 3D, still flat colors
const faces = [
  { poly: [top, ur, mid, ul], alpha: 1.0 },
  { poly: [ul, mid, bottom, ll], alpha: 0.86 },
  { poly: [mid, ur, lr, bottom], alpha: 0.72 },
];
const GAP = 14; // half width of the orange seams between faces
const seams = [
  [mid, ul],
  [mid, ur],
  [mid, bottom],
];

function inRoundedSquare(x, y) {
  const lo = INSET + RADIUS;
  const hi = SIZE - INSET - RADIUS;
  const dx = x < lo ? lo - x : x > hi ? x - hi : 0;
  const dy = y < lo ? lo - y : y > hi ? y - hi : 0;
  if (x < INSET || x > SIZE - INSET || y < INSET || y > SIZE - INSET) return false;
  return dx * dx + dy * dy <= RADIUS * RADIUS;
}

function inPolygon(x, y, poly) {
  let inside = false;
  for (let i = 0, j = poly.length - 1; i < poly.length; j = i++) {
    const [xi, yi] = poly[i];
    const [xj, yj] = poly[j];
    if (yi > y !== yj > y && x < ((xj - xi) * (y - yi)) / (yj - yi) + xi) inside = !inside;
  }
  return inside;
}

function distToSegment(x, y, [ax, ay], [bx, by]) {
  const vx = bx - ax;
  const vy = by - ay;
  const t = Math.max(0, Math.min(1, ((x - ax) * vx + (y - ay) * vy) / (vx * vx + vy * vy)));
  const px = ax + t * vx - x;
  const py = ay + t * vy - y;
  return Math.sqrt(px * px + py * py);
}

// color and coverage of one sample
function sample(x, y) {
  if (!inRoundedSquare(x, y)) return null;
  if (seams.some((s) => distToSegment(x, y, s[0], s[1]) < GAP)) return BG;
  for (const face of faces) {
    if (inPolygon(x, y, face.poly)) {
      return BG.map((c, i) => Math.round(c + (WHITE[i] - c) * face.alpha));
    }
  }
  return BG;
}

const rgba = Buffer.alloc(SIZE * SIZE * 4);
const n = SS * SS;
for (let py = 0; py < SIZE; py++) {
  for (let px = 0; px < SIZE; px++) {
    let r = 0;
    let g = 0;
    let b = 0;
    let a = 0;
    for (let sy = 0; sy < SS; sy++) {
      for (let sx = 0; sx < SS; sx++) {
        const c = sample(px + (sx + 0.5) / SS, py + (sy + 0.5) / SS);
        if (c) {
          r += c[0];
          g += c[1];
          b += c[2];
          a++;
        }
      }
    }
    const o = (py * SIZE + px) * 4;
    if (a > 0) {
      // straight (not premultiplied) alpha: average color of the covered samples
      rgba[o] = Math.round(r / a);
      rgba[o + 1] = Math.round(g / a);
      rgba[o + 2] = Math.round(b / a);
      rgba[o + 3] = Math.round((a / n) * 255);
    }
  }
}

// PNG encoding: RGBA8, filter 0 on every row
const CRC_TABLE = Array.from({ length: 256 }, (_, k) => {
  let c = k;
  for (let i = 0; i < 8; i++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
  return c >>> 0;
});

function crc32(buffer) {
  let c = 0xffffffff;
  for (const byte of buffer) c = CRC_TABLE[(c ^ byte) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}

function chunk(type, data) {
  const length = Buffer.alloc(4);
  length.writeUInt32BE(data.length);
  const body = Buffer.concat([Buffer.from(type, "ascii"), data]);
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(body));
  return Buffer.concat([length, body, crc]);
}

const ihdr = Buffer.alloc(13);
ihdr.writeUInt32BE(SIZE, 0);
ihdr.writeUInt32BE(SIZE, 4);
ihdr[8] = 8; // bit depth
ihdr[9] = 6; // RGBA
ihdr[10] = 0;
ihdr[11] = 0;
ihdr[12] = 0;

const raw = Buffer.alloc(SIZE * (SIZE * 4 + 1));
for (let y = 0; y < SIZE; y++) {
  raw[y * (SIZE * 4 + 1)] = 0;
  rgba.copy(raw, y * (SIZE * 4 + 1) + 1, y * SIZE * 4, (y + 1) * SIZE * 4);
}

const png = Buffer.concat([
  Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
  chunk("IHDR", ihdr),
  chunk("IDAT", deflateSync(raw, { level: 9 })),
  chunk("IEND", Buffer.alloc(0)),
]);

mkdirSync(dirname(out), { recursive: true });
writeFileSync(out, png);
console.log(`wrote ${out} (${png.length} bytes)`);
