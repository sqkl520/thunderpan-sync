// make-icon.js -- 生成 build/icon.ico（多尺寸，256/128/64/48/32/16）
//
// 为什么自己生成而不是找现成图标：
//   1. electron-builder 的 win.icon 必须是真 .ico（含 256x256），否则构建直接失败；
//   2. 托盘图标是代码里画出来的蓝圆+上箭头，应用图标应与它同形，否则任务栏和托盘是两个东西；
//   3. 不引入第三方依赖（sharp/jimp），用纯 JS 画 + 手写 ICO 容器。
const fs = require('fs');
const path = require('path');

// ---- 画一帧 RGBA（抗锯齿用 4x 超采样）----
function render(size) {
  const SS = 4;
  const W = size * SS;
  const buf = Buffer.alloc(W * W * 4);
  const cx = W / 2, cy = W / 2, R = W * 0.47;

  const inCircle = (x, y) => {
    const dx = x - cx, dy = y - cy;
    return dx * dx + dy * dy <= R * R;
  };
  // 上传箭头：竖干 + 三角头（与托盘图标同形）
  const inArrow = (x, y) => {
    const nx = (x - cx) / W, ny = (y - cy) / W; // 归一化 (-0.5..0.5)
    // 竖干
    if (Math.abs(nx) < 0.075 && ny > -0.06 && ny < 0.30) return true;
    // 三角头（尖端朝上）
    if (ny >= -0.30 && ny <= -0.04) {
      const t = (ny + 0.30) / 0.26;       // 0 在尖端，1 在底
      const halfW = 0.22 * t;
      if (Math.abs(nx) <= halfW) return true;
    }
    return false;
  };

  for (let y = 0; y < W; y++) {
    for (let x = 0; x < W; x++) {
      const i = (y * W + x) * 4;
      if (!inCircle(x, y)) { buf[i + 3] = 0; continue; }
      // 背景：垂直渐变蓝，更像"应用"而不是纯色块
      const t = y / W;
      let r = Math.round(38 + t * 22);
      let g = Math.round(108 + t * 34);
      let b = Math.round(226 + t * 22);
      if (inArrow(x, y)) { r = 255; g = 255; b = 255; }
      buf[i] = r; buf[i + 1] = g; buf[i + 2] = b; buf[i + 3] = 255;
    }
  }

  // 4x 超采样降采样
  const out = Buffer.alloc(size * size * 4);
  for (let y = 0; y < size; y++) {
    for (let x = 0; x < size; x++) {
      let r = 0, g = 0, b = 0, a = 0;
      for (let dy = 0; dy < SS; dy++) {
        for (let dx = 0; dx < SS; dx++) {
          const si = ((y * SS + dy) * W + (x * SS + dx)) * 4;
          const av = buf[si + 3];
          r += buf[si] * av; g += buf[si + 1] * av; b += buf[si + 2] * av; a += av;
        }
      }
      const oi = (y * size + x) * 4;
      if (a === 0) { out[oi + 3] = 0; continue; }
      out[oi] = Math.round(r / a);
      out[oi + 1] = Math.round(g / a);
      out[oi + 2] = Math.round(b / a);
      out[oi + 3] = Math.round(a / (SS * SS));
    }
  }
  return out;
}

// ---- 32bit BMP-in-ICO（ICO 里的 PNG 只有 Vista+ 认，BMP 兼容性最好）----
function toBmpEntry(size, rgba) {
  const header = Buffer.alloc(40);
  header.writeUInt32LE(40, 0);            // biSize
  header.writeInt32LE(size, 4);           // biWidth
  header.writeInt32LE(size * 2, 8);       // biHeight（ICO 里是 XOR+AND 两倍）
  header.writeUInt16LE(1, 12);            // biPlanes
  header.writeUInt16LE(32, 14);           // biBitCount
  header.writeUInt32LE(0, 16);            // BI_RGB
  header.writeUInt32LE(size * size * 4, 20);

  // BMP 是自下而上、BGRA
  const pixels = Buffer.alloc(size * size * 4);
  for (let y = 0; y < size; y++) {
    const srcRow = size - 1 - y;
    for (let x = 0; x < size; x++) {
      const si = (srcRow * size + x) * 4;
      const di = (y * size + x) * 4;
      pixels[di] = rgba[si + 2];      // B
      pixels[di + 1] = rgba[si + 1];  // G
      pixels[di + 2] = rgba[si];      // R
      pixels[di + 3] = rgba[si + 3];  // A
    }
  }
  // AND 掩码（32bit 下可为全 0，但必须存在）
  const andMask = Buffer.alloc(Math.ceil(size / 32) * 4 * size, 0);

  return Buffer.concat([header, pixels, andMask]);
}

const sizes = [256, 128, 64, 48, 32, 16];
const images = sizes.map(s => ({ size: s, data: toBmpEntry(s, render(s)) }));

// ICONDIR
const dir = Buffer.alloc(6);
dir.writeUInt16LE(0, 0);
dir.writeUInt16LE(1, 2);
dir.writeUInt16LE(images.length, 4);

// ICONDIRENTRY x N
const entries = [];
let offset = 6 + 16 * images.length;
for (const img of images) {
  const e = Buffer.alloc(16);
  e.writeUInt8(img.size >= 256 ? 0 : img.size, 0);
  e.writeUInt8(img.size >= 256 ? 0 : img.size, 1);
  e.writeUInt8(0, 2);        // 调色板
  e.writeUInt8(0, 3);        // 保留
  e.writeUInt16LE(1, 4);     // planes
  e.writeUInt16LE(32, 6);    // bpp
  e.writeUInt32LE(img.data.length, 8);
  e.writeUInt32LE(offset, 12);
  entries.push(e);
  offset += img.data.length;
}

const outDir = path.join(__dirname, 'build');
fs.mkdirSync(outDir, { recursive: true });
const outFile = path.join(outDir, 'icon.ico');
const ico = Buffer.concat([dir, ...entries, ...images.map(i => i.data)]);
fs.writeFileSync(outFile, ico);

console.log(`icon.ico written: ${ico.length} B, sizes=${sizes.join('/')}`);
