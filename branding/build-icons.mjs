#!/usr/bin/env node
// Renders logo.svg / logo-16.svg with headless Chrome and writes highbridge.ico (16, 20, 24, 32,
// 40, 48, 64 and 256 px) plus the sample page's copy of the logo. No npm dependencies.
//
//   node branding/build-icons.mjs [--png <dir>]     (CHROME=/path/to/chrome to pick a browser)
//
// Sizes up to 40 px (tray icon at 100-250% scaling) come from logo-16.svg, which is drawn on a
// 16 px pixel grid so they stay sharp; 48 px and up come from logo.svg (a 48 px grid). Frames up to 64 px are stored as 32-bit BMP and
// the 256 px frame as PNG, which is what Windows Explorer, the shell and Windows Installer expect.
import { spawn } from 'node:child_process';
import { copyFileSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { deflateSync } from 'node:zlib';

const here = dirname(fileURLToPath(import.meta.url));
const root = join(here, '..');
const sizes = [16, 20, 24, 32, 40, 48, 64, 256];
const fromSmallGrid = new Set([16, 20, 24, 32, 40]);
const pngDir = process.argv.includes('--png') ? process.argv[process.argv.indexOf('--png') + 1] : null;

const sources = {
  small: readFileSync(join(here, 'logo-16.svg'), 'utf8'),
  large: readFileSync(join(here, 'logo.svg'), 'utf8'),
};

const frames = await renderWithChrome(sizes.map((size) => ({ size, svg: fromSmallGrid.has(size) ? sources.small : sources.large })));
writeFileSync(join(here, 'highbridge.ico'), buildIco(frames));
copyFileSync(join(here, 'logo.svg'), join(root, 'docs', 'logo.svg'));
if (pngDir) {
  mkdirSync(pngDir, { recursive: true });
  for (const f of frames) writeFileSync(join(pngDir, `highbridge-${f.size}.png`), encodePng(f));
}
console.log(`Wrote branding/highbridge.ico (${frames.map((f) => f.size).join(', ')} px) and docs/logo.svg`);

async function renderWithChrome(jobs) {
  const work = mkdtempSync(join(tmpdir(), 'highbridge-icons-'));
  try {
    const page = join(work, 'render.html');
    const items = jobs.map(({ size, svg }) => ({
      size,
      url: 'data:image/svg+xml;base64,' + Buffer.from(svg.replace(/<svg\b([^>]*?)\swidth="[^"]*"\s+height="[^"]*"/, `<svg$1 width="${size}" height="${size}"`)).toString('base64'),
    }));
    writeFileSync(page, `<!doctype html><pre id="out"></pre><script>
      const items = ${JSON.stringify(items)};
      Promise.all(items.map(({ size, url }) => new Promise((resolve, reject) => {
        const img = new Image();
        img.onload = () => {
          const c = document.createElement('canvas');
          c.width = c.height = size;
          const ctx = c.getContext('2d');
          ctx.drawImage(img, 0, 0, size, size);
          const data = ctx.getImageData(0, 0, size, size).data;
          let bin = '';
          for (let i = 0; i < data.length; i++) bin += String.fromCharCode(data[i]);
          resolve({ size, rgba: btoa(bin) });
        };
        img.onerror = reject;
        img.src = url;
      }))).then((r) => { document.getElementById('out').textContent = JSON.stringify(r); });
    </script>`);
    const dom = await dumpDom(page, join(work, 'profile'));
    const json = dom.match(/<pre id="out">([\s\S]*?)<\/pre>/)?.[1];
    if (!json) throw new Error('Chrome did not render the icons');
    return JSON.parse(json.replaceAll('&quot;', '"')).map(({ size, rgba }) => ({ size, rgba: Buffer.from(rgba, 'base64') }));
  } finally {
    rmSync(work, { recursive: true, force: true });
  }
}

// Some Chrome builds keep running after --dump-dom, so stop reading at </html>.
function dumpDom(page, profile) {
  const chrome = spawn(process.env.CHROME ?? 'google-chrome', [
    '--headless', '--disable-gpu', '--no-sandbox', `--user-data-dir=${profile}`,
    '--virtual-time-budget=10000', '--dump-dom', pathToFileURL(page).href,
  ], { stdio: ['ignore', 'pipe', 'ignore'] });
  return new Promise((resolve, reject) => {
    let out = '';
    const timer = setTimeout(() => { chrome.kill('SIGKILL'); reject(new Error('Chrome timed out')); }, 60_000);
    const finish = () => { clearTimeout(timer); chrome.kill('SIGKILL'); resolve(out); };
    chrome.stdout.setEncoding('utf8');
    chrome.stdout.on('data', (d) => { out += d; if (out.includes('</html>')) finish(); });
    chrome.on('error', (err) => { clearTimeout(timer); reject(err); });
    chrome.on('exit', finish);
  });
}

function buildIco(list) {
  const images = list.map((f) => (f.size >= 256 ? encodePng(f) : encodeDib(f)));
  const header = Buffer.alloc(6 + 16 * list.length);
  header.writeUInt16LE(0, 0);
  header.writeUInt16LE(1, 2);
  header.writeUInt16LE(list.length, 4);
  let offset = header.length;
  list.forEach((f, i) => {
    const e = 6 + 16 * i;
    header.writeUInt8(f.size >= 256 ? 0 : f.size, e);
    header.writeUInt8(f.size >= 256 ? 0 : f.size, e + 1);
    header.writeUInt16LE(1, e + 4);
    header.writeUInt16LE(32, e + 6);
    header.writeUInt32LE(images[i].length, e + 8);
    header.writeUInt32LE(offset, e + 12);
    offset += images[i].length;
  });
  return Buffer.concat([header, ...images]);
}

// BITMAPINFOHEADER + bottom-up BGRA pixels + 1-bit AND mask (set where fully transparent).
function encodeDib({ size, rgba }) {
  const maskStride = Math.ceil(size / 32) * 4;
  const header = Buffer.alloc(40);
  header.writeUInt32LE(40, 0);
  header.writeInt32LE(size, 4);
  header.writeInt32LE(size * 2, 8);
  header.writeUInt16LE(1, 12);
  header.writeUInt16LE(32, 14);
  header.writeUInt32LE(size * size * 4 + maskStride * size, 20);
  const pixels = Buffer.alloc(size * size * 4);
  const mask = Buffer.alloc(maskStride * size);
  for (let y = 0; y < size; y++) {
    const row = size - 1 - y;
    for (let x = 0; x < size; x++) {
      const s = (y * size + x) * 4;
      const d = (row * size + x) * 4;
      pixels[d] = rgba[s + 2];
      pixels[d + 1] = rgba[s + 1];
      pixels[d + 2] = rgba[s];
      pixels[d + 3] = rgba[s + 3];
      if (rgba[s + 3] === 0) mask[row * maskStride + (x >> 3)] |= 0x80 >> (x & 7);
    }
  }
  return Buffer.concat([header, pixels, mask]);
}

function encodePng({ size, rgba }) {
  const raw = Buffer.alloc((size * 4 + 1) * size);
  for (let y = 0; y < size; y++) rgba.copy(raw, y * (size * 4 + 1) + 1, y * size * 4, (y + 1) * size * 4);
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(size, 0);
  ihdr.writeUInt32BE(size, 4);
  ihdr.set([8, 6, 0, 0, 0], 8);
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', ihdr), chunk('IDAT', deflateSync(raw, { level: 9 })), chunk('IEND', Buffer.alloc(0)),
  ]);
}

function chunk(type, data) {
  const out = Buffer.alloc(12 + data.length);
  out.writeUInt32BE(data.length, 0);
  out.write(type, 4, 'ascii');
  data.copy(out, 8);
  out.writeUInt32BE(crc32(out.subarray(4, 8 + data.length)), 8 + data.length);
  return out;
}

function crc32(buf) {
  let c = ~0;
  for (const b of buf) {
    c ^= b;
    for (let k = 0; k < 8; k++) c = (c >>> 1) ^ (0xedb88320 & -(c & 1));
  }
  return ~c >>> 0;
}
