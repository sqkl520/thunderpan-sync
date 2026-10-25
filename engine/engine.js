// engine.js -- ThunderPanSync 同步引擎（v2：所有关键逻辑都已实测验证）
//
// 五条实测结论（写进代码，避免后人重踩）：
//   1. captcha 不可离线复刻（算法分片动态下发），必须从浏览器真实请求里截取。
//   2. 网页版目录切换是 SPA 行为：page.goto("?path=/X") 只改 URL，前端不切换
//      （面包屑仍显示"全部文件"）；必须点击 <a title="X"> 才真正进入。
//   3. 上传走网页版 file input，落点是"当前浏览目录"，所以必须先点击进入目标文件夹。
//   4. 创建文件夹需要 POST 专属 captcha → 触发一次页面真实上传来截取。
//   5. 0 字节文件云盘拒收（file_size_too_small），标为已知跳过而非失败。
// 原生 CDP 客户端替代 playwright：只用 5 个操作，不值得为它背 17.8 MB 依赖
// 接口与 playwright 保持一致，engine 其余逻辑无需改动
const { chromium } = require('./cdp');
const fs = require('fs');
const path = require('path');
const { EventEmitter } = require('events');
// 路径统一从 paths.js 取：打包后 asar 只读，绝不能写 __dirname
const P = require('../paths');

const API = 'https://api-pan.xunlei.com';
const PAN = 'https://pan.xunlei.com';
const CDP = 'http://127.0.0.1:9222';

class SyncEngine extends EventEmitter {
  constructor(opts = {}) {
    super();
    this.cfg = {
      watchDirs: [],
      remoteFolderName: 'ThunderPanSync',
      intervalMs: 60000,
      settleMs: 5000,
      maxSizeMB: 2048,
      stateFile: P.STATE_FILE,
      logFile: P.LOG_FILE,
      ...opts,
    };
    this.remoteFolderId = null;
    this.state = { uploaded: {}, failed: {} };
    this.browser = null;
    this.page = null;
    this.running = false;
    this.timer = null;
    this.currentRun = null;
    this._abortRequested = false;   // stop() 置位，runOnce 的循环检查它
    this._loadState();
  }

  _loadState() {
    try {
      if (fs.existsSync(this.cfg.stateFile)) {
        const s = JSON.parse(fs.readFileSync(this.cfg.stateFile, 'utf8'));
        this.state = { uploaded: s.uploaded || {}, failed: s.failed || {}, lastRun: s.lastRun };
        this.remoteFolderId = s.remoteFolderId || null;
      }
    } catch (e) {
      // 状态损坏：不能静默当空（那会导致全量重传）。
      // 备份损坏文件供诊断，然后从空开始。
      try {
        const bak = this.cfg.stateFile + '.corrupt-' + Date.now();
        fs.copyFileSync(this.cfg.stateFile, bak);
        this.log('warn', `状态文件损坏，已备份为 ${path.basename(bak)}，将从空状态开始（会重新上传已备份过的文件）`);
      } catch (e2) {
        this.log('warn', '状态文件损坏且无法备份，将从空状态开始');
      }
      this.state = { uploaded: {}, failed: {} };
      this.remoteFolderId = null;
    }
  }

  // 原子写：先写临时文件 → fsync → rename 覆盖。
  // 直接 writeFileSync 覆盖目标文件时，断电/强杀会留下 0 字节或半截 JSON，
  // 下次启动解析失败 → 状态清空 → 把用户几个 GB 全部重传（实测过的场景）。
  _saveState() {
    const target = this.cfg.stateFile;
    const tmp = target + '.tmp';
    try {
      fs.mkdirSync(path.dirname(target), { recursive: true });
      const data = JSON.stringify({
        uploaded: this.state.uploaded,
        failed: this.state.failed,
        lastRun: this.state.lastRun,
        remoteFolderId: this.remoteFolderId,
      }, null, 2);

      const fd = fs.openSync(tmp, 'w');
      try {
        fs.writeSync(fd, data, 0, 'utf8');
        fs.fsyncSync(fd);          // 确保落盘，否则 rename 后内容仍可能在缓存里
      } finally {
        fs.closeSync(fd);
      }
      fs.renameSync(tmp, target);  // rename 在 NTFS 上是原子的
    } catch (e) {
      // 写失败必须让人知道：状态不落盘意味着下次会重传
      this.log('error', `保存状态失败: ${e.message}（下次启动可能重复上传）`);
      try { if (fs.existsSync(tmp)) fs.unlinkSync(tmp); } catch (e2) { }
    }
  }

  log(level, msg) {
    const t = new Date().toISOString();
    this.emit('log', { level, msg, time: t });
    try {
      fs.mkdirSync(path.dirname(this.cfg.logFile), { recursive: true });
      fs.appendFileSync(this.cfg.logFile, `${t} [${level}] ${msg}\n`);
    } catch (e) {}
    const line = `[${new Date().toTimeString().slice(0, 8)}] ${msg}`;
    if (level === 'error') console.error(line);
    else console.log(line);
  }

  // ================= 浏览器 =================
  async connect() {
    if (this.page && !this.page.isClosed()) return this.page;
    try {
      this.browser = await chromium.connectOverCDP(CDP, { timeout: 8000 });
    } catch (e) {
      throw new Error('连不上浏览器。请先运行 start-browser.cmd 并登录迅雷云盘。');
    }
    const ctx = this.browser.contexts()[0];
    if (!ctx) throw new Error('浏览器没有 context');
    let page = ctx.pages().find(p => p.url().includes('pan.xunlei.com'));
    if (!page) {
      page = await ctx.newPage();
      await page.goto(PAN, { waitUntil: 'domcontentloaded', timeout: 60000 });
    }
    const cookies = await ctx.cookies(PAN);
    if (!cookies.find(c => c.name === 'sessionid')) {
      throw new Error('浏览器里没有登录态，请登录 pan.xunlei.com');
    }
    this.page = page;
    return page;
  }

  // 截取真实请求头 —— captcha 只能这样拿到
  //
  // 性能优化（修一个实测到的严重回归）：
  //   每次调用都会 page.goto 重新导航页面来等一次真实请求，约 2.5~8 秒。
  //   而 _verifyRemoteFile 内部要尝试 6 次 ⇒ 单文件核查就耗 80 秒，
  //   整轮同步慢到不可用（实测：6 个文件跑了 5 分钟还没完）。
  //   而 captcha/authorization 在一次会话内是可复用的（实测过 38 分钟仍有效），
  //   所以这里缓存 header，只在接近过期或明确失败时才重新截取。
  async _capHeader(method = 'GET', timeoutMs = 25000) {
    const cacheKey = method;
    const now = Date.now();

    // GET 头缓存 5 分钟；POST 头缓存 2 分钟（不同 action 的 captcha 可能不同）
    const ttl = method === 'GET' ? 5 * 60 * 1000 : 2 * 60 * 1000;
    if (this._hdrCache && this._hdrCache[cacheKey]
      && (now - this._hdrCache[cacheKey].at) < ttl) {
      return this._hdrCache[cacheKey].h;
    }

    const page = await this.connect();
    return new Promise((resolve, reject) => {
      let done = false;
      const handler = async (req) => {
        if (done) return;
        if (!req.url().startsWith(API) || req.method() !== method) return;
        try {
          const h = await req.allHeaders();
          if (h['x-captcha-token'] && h['authorization']) {
            done = true;
            page.off('request', handler);
            // 缓存供后续复用，避免每查一次就导航一次
            this._hdrCache = this._hdrCache || {};
            this._hdrCache[cacheKey] = { h, at: Date.now() };
            resolve(h);
          }
        } catch (e) {}
      };
      page.on('request', handler);
      if (method === 'GET') {
        page.goto(PAN + '/', { waitUntil: 'domcontentloaded' }).catch(() => {});
      }
      setTimeout(() => {
        if (!done) {
          done = true;
          page.off('request', handler);
          reject(new Error(`${method} 头截取超时`));
        }
      }, timeoutMs);
    });
  }

  _H(h) {
    return {
      authorization: h['authorization'],
      'x-captcha-token': h['x-captcha-token'],
      'x-client-id': h['x-client-id'],
      'x-device-id': h['x-device-id'],
      origin: PAN,
      referer: PAN + '/',
    };
  }

  // ================= 远程文件夹 =================
  async ensureRemoteFolder() {
    const name = this.cfg.remoteFolderName;
    const gh = await this._capHeader('GET');
    const r = await fetch(API + '/drive/v1/files?limit=100', { headers: this._H(gh) });
    const j = await r.json();
    const found = (j.files || []).find(f => f.kind === 'drive#folder' && f.name === name);
    if (found) {
      this.remoteFolderId = found.id;
      this.log('info', `远程文件夹已存在: ${name} (${found.id})`);
      return found.id;
    }

    this.log('info', `远程文件夹 "${name}" 不存在，创建中...`);
    const probe = P.PROBE_FILE;
    fs.mkdirSync(path.dirname(probe), { recursive: true });
    fs.writeFileSync(probe, 'probe ' + Date.now());
    const page = await this.connect();
    await page.goto(PAN + '/', { waitUntil: 'domcontentloaded' }).catch(() => {});
    await page.waitForTimeout(2000);
    const postP = this._capHeader('POST', 30000).catch(() => null);
    try { await page.locator('input[type=file]').first().setInputFiles(probe); } catch (e) {}
    const ph = await postP;
    try { fs.unlinkSync(probe); } catch (e) {}

    if (!ph) {
      this.log('error', '拿不到 POST captcha，无法创建文件夹');
      return null;
    }
    const cr = await fetch(API + '/drive/v1/files', {
      method: 'POST',
      headers: { ...this._H(ph), 'content-type': 'application/json' },
      body: JSON.stringify({ kind: 'drive#folder', parent_id: '', name }),
    });
    const ct = await cr.text();
    if (cr.status !== 200) {
      this.log('error', `创建文件夹失败 ${cr.status}: ${ct.slice(0, 200)}`);
      return null;
    }
    const cj = JSON.parse(ct);
    this.remoteFolderId = cj.file && cj.file.id;
    this._saveState();
    this.log('info', `✓ 已创建文件夹 ${name} (${this.remoteFolderId})`);
    return this.remoteFolderId;
  }

  // 进入文件夹 —— 必须用点击，URL 导航无效
  // force=true 时即使已经在目标目录也会重新导航（用于重建页面的 file input）
  async enterRemoteFolder(force) {
    const name = this.cfg.remoteFolderName;
    const page = await this.connect();
    const crumbsOf = async () => page.evaluate(() => {
      const c = [...document.querySelectorAll('[class*=breadcrumb],[class*=crumb]')]
        .map(e => (e.innerText || '').replace(/\s+/g, ' ').trim())
        .filter(Boolean).join(' ');
      return c;
    }).catch(() => '');

    for (let i = 1; i <= 4; i++) {
      if (!force && (await crumbsOf()).includes(name)) {
        this.log('info', `已在 "${name}" 目录`);
        return true;
      }
      await page.goto(PAN + '/', { waitUntil: 'domcontentloaded' }).catch(() => {});
      // 等待时间递增：页面刚上传完文件时列表刷新较慢，固定 2.5s 会偶发点不到
      await page.waitForTimeout(2500 + (i - 1) * 1500);

      // 找不到链接时先滚动/等待再试，不要立刻判失败
      const clicked = await page.evaluate((n) => {
        const a = [...document.querySelectorAll('a[title]')].find(x => x.getAttribute('title') === n);
        if (a) { a.scrollIntoView(); a.click(); return true; }
        return false;
      }, name).catch(() => false);

      if (!clicked) {
        this.log('warn', `第 ${i} 次未找到 "${name}" 链接，重试`);
        await page.waitForTimeout(1500);
        continue;
      }
      await page.waitForTimeout(2500);
      if ((await crumbsOf()).includes(name)) {
        this.log('info', `✓ 已进入 "${name}"`);
        return true;
      }
      this.log('warn', `第 ${i} 次点击后未确认进入，重试`);
    }
    this.log('error', `无法进入文件夹 "${name}"（已重试 4 次）`);
    return false;
  }

  // ================= 扫描 =================
  // 已知的坑（每一条都是实测/评审确认过的）：
  //   1. 未来 mtime：now - mtime 为负时 `< settleMs` 恒成立 ⇒ 文件永久"等待稳定"。
  //      从 zip 解压、跨机拷贝、robocopy /COPY:DAT 都会带未来时间戳。
  //      ⇒ 用 Math.max(0, ...) 把负值当 0（视为刚修改），并给它一个有限等待次数。
  //   2. junction/符号链接：Dirent 上 isDirectory/isFile 都是 false，
  //      原来的 if/else-if 没有 else 分支 ⇒ 整棵子树被静默丢弃、零日志。
  //   3. 同步递归无深度上限：10 万文件会阻塞事件循环数十秒。
  //   4. 无系统目录防护：用户选 C:\ 会把整个系统盘往云上传。
  scan() {
    const out = [];
    const now = Date.now();
    const MAX_DEPTH = 64;               // 防符号链接环/超深目录
    const settleMs = this.cfg.settleMs;

    // 未来时间戳容忍窗口：超过这个量就认为时钟/时间戳异常，按"已稳定"处理
    const FUTURE_TOLERANCE_MS = 60 * 1000;

    const blocked = this._blockedRoots();

    for (const dir of this.cfg.watchDirs) {
      let rootReal;
      try { rootReal = path.resolve(dir); } catch (e) { rootReal = dir; }

      if (!fs.existsSync(dir)) { this.log('warn', `目录不存在: ${dir}`); continue; }

      // 用户选到系统目录时明确拒绝（否则会尝试上传整个系统盘）
      const hit = blocked.find(b => rootReal.toLowerCase().startsWith(b.toLowerCase()));
      if (hit) {
        this.log('error', `拒绝备份系统目录 "${dir}"（命中保护路径 ${hit}）。请选择你自己的文档/照片等目录。`);
        continue;
      }

      let skippedLinks = 0;
      let skippedOther = 0;

      const walk = (d, depth) => {
        if (depth > MAX_DEPTH) {
          this.log('warn', `目录层级超过 ${MAX_DEPTH} 层，已跳过: ${d}`);
          return;
        }
        let ents;
        try { ents = fs.readdirSync(d, { withFileTypes: true }); }
        catch (e) {
          // 读不到目录必须记下来：否则用户以为备份了，实际整棵树被跳过
          this.log('warn', `无法读取目录 ${d}: ${e.code || e.message}`);
          return;
        }
        for (const ent of ents) {
          const full = path.join(d, ent.name);
          if (ent.isDirectory()) {
            if (ent.name.startsWith('.') || ent.name === 'node_modules') continue;
            walk(full, depth + 1);
          } else if (ent.isFile()) {
            let st;
            try { st = fs.statSync(full); }
            catch (e) {
              out.push({ full, st: { size: 0, mtimeMs: now }, skip: `无法读取（${e.code || e.message}）` });
              continue;
            }

            if (st.size === 0) { out.push({ full, st, skip: '空文件（云盘不接受 0 字节）' }); continue; }
            if (st.size > this.cfg.maxSizeMB * 1024 * 1024) {
              out.push({ full, st, skip: `超过 ${this.cfg.maxSizeMB} MB 上限` }); continue;
            }

            // ---- 稳定性判定（修未来时间戳）----
            const age = now - st.mtimeMs;              // 可能是负数
            if (age < -FUTURE_TOLERANCE_MS) {
              // 时间戳在未来且超出容差：按"已稳定"处理，不能永久挂起
              // （实测：mtime=2027 年时原逻辑永远 skip"等待稳定"）
              this.log('info', `${path.basename(full)} 的修改时间在未来（${new Date(st.mtimeMs).toLocaleString()}），按已稳定处理`);
            } else if (age >= 0 && age < settleMs) {
              out.push({ full, st, skip: '刚修改，等待稳定' }); continue;
            } else if (age < 0) {
              // 未来但在容差内：视为刚写入，再等一轮
              out.push({ full, st, skip: '刚修改，等待稳定' }); continue;
            }

            const fp = `${st.size}-${Math.floor(st.mtimeMs)}`;
            const prev = this.state.uploaded[full];
            if (prev && prev.fp === fp) continue;
            out.push({ full, st, fp, rel: path.relative(dir, full), skip: null });
          } else if (ent.isSymbolicLink()) {
            // 原来这里没有分支 ⇒ 静默丢弃。现在至少计数并汇总告警。
            skippedLinks++;
          } else {
            // 设备文件、管道等：不备份，但要记账
            skippedOther++;
          }
        }
      };
      walk(dir, 0);

      if (skippedLinks > 0) {
        this.log('warn', `"${path.basename(dir)}" 下有 ${skippedLinks} 个符号链接/junction 未跟随（云盘无法表达链接；如需备份其内容，请把目标目录直接加入监视列表）`);
      }
      if (skippedOther > 0) {
        this.log('warn', `"${path.basename(dir)}" 下有 ${skippedOther} 个非普通文件被跳过`);
      }
    }
    return out;
  }

  // 禁止备份的系统路径（大小写不敏感）。用户误选会毁掉配额并上传系统文件。
  _blockedRoots() {
    const sys = process.env.SystemRoot || 'C:\\Windows';
    const pf = process.env.ProgramFiles || 'C:\\Program Files';
    const pf86 = process.env['ProgramFiles(x86)'] || 'C:\\Program Files (x86)';
    const pd = process.env.ProgramData || 'C:\\ProgramData';
    const win = process.env.WINDIR || 'C:\\Windows';
    return [
      sys,
      win,
      sys + '\\System32',
      pf,
      pf86,
      pd,
      'C:\\$Recycle.Bin',
      'C:\\Recovery',
      'C:\\System Volume Information',
      // 引导/恢复分区等
      'C:\\Boot',
    ];
  }

  // ================= 上传 =================
  async uploadOne(item) {
    // 关键：每次上传前必须重新进入目标文件夹（不能复用上一轮的状态）。
    // 实测：第一个文件传成功后，网页版会刷新文件列表并重建 <input type=file>，
    // 旧元素句柄失效 ⇒ 后续 setInputFiles 写到了已脱离文档的元素上，
    // 事件不触发、POST 不发、文件传不上去（表现为"云端未找到该文件"）。
    // 重新进入文件夹会让页面重建到干净的初始状态，这个模式稳定可复现。
    const entered = await this.enterRemoteFolder(true);
    if (!entered) {
      return { ok: false, error: '无法进入目标文件夹' };
    }

    const page = await this.connect();
    const name = this._remoteNameFor(item);
    this.log('info', `上传 ${name} (${item.st.size} B)...`);

    // 上传前拍快照：判定成功时只认"新出现的 fileId"，
    // 否则云端已有同名同大小旧文件会被误判成本次成功（静默丢数据）
    const beforeIds = await this._snapshotIds();

    const done = new Promise((resolve) => {
      let settled = false;
      let timer = null;
      const finish = (v) => {
        if (settled) return;
        settled = true;
        if (timer) { clearTimeout(timer); timer = null; }   // 必须清掉，否则定时器一直挂着
        page.off('response', onResp);
        resolve(v);
      };
      const onResp = async (resp) => {
        if (settled) return;
        if (!resp.url().startsWith(API)) return;
        if (resp.request().method() !== 'POST') return;
        if (!resp.url().includes('/drive/v1/files')) return;
        const status = resp.status();
        if (status === 200 || status >= 400) {
          let body = '';
          try { body = (await resp.text()).slice(0, 300); } catch (e) {}
          // 注意：200 返回的是"上传票据"（STS 临时凭据），不是上传完成。
          // 这条响应只用来判断"服务端接受了这次上传请求"，成功与否必须靠云端核查。
          finish({ ok: status === 200, status, body });
        }
      };
      page.on('response', onResp);
      // 45s 只是"拿到票据"的合理上限；真正判定成功靠云端核查，不靠这个响应
      timer = setTimeout(() => {
        finish({ ok: false, timeout: true, body: '45s 未收到上传响应' });
      }, 45000);
    });

    try {
      await page.locator('input[type=file]').first().setInputFiles(item.full);
    } catch (e) {
      return { ok: false, error: '触发上传失败: ' + e.message };
    }

    // 判定成功的方式改了（原先是等 page 上的 POST 响应，不可靠）：
    //   迅雷上传是三段式：POST 申请票据 → PUT 到阿里云 OSS → 服务端回调入库。
    //   PUT 阶段可能几十秒到几分钟，且真正入库发生在服务端回调之后，
    //   浏览器侧的 POST 响应并不代表"文件已经在云盘里"。
    //   实测：靠响应判定会把成功的大文件判成"120s 无响应"（文件其实已入库）。
    // 改为：等一个较短时间让 POST 拿到票据，然后用 API 去查文件是否真的在目标文件夹。
    const res = await done;

    if (!res.ok && res.timeout) {
      this.log('info', `${name} 上传较慢，改为核对云端是否已入库...`);
    }

    // 无论响应是否拿到，都做一次事实核查（这才是"文件真的上去了"的证据）
    const verified = await this._verifyRemoteFile(name, item.st.size, beforeIds);
    if (verified) {
      this.log('info', `✓ ${name} 上传成功（云端已核实，新增 fileId）`);
      this.state.uploaded[item.full] = { fp: item.fp, size: item.st.size, at: new Date().toISOString() };
      delete this.state.failed[item.full];
      this._saveState();
      return { ok: true };
    }

    if (res.ok) {
      // 浏览器说 200，但云端查不到：可能是刚入库、列表还没刷新。再等一轮。
      await new Promise(r => setTimeout(r, 5000));
      const again = await this._verifyRemoteFile(name, item.st.size, beforeIds);
      if (again) {
        this.log('info', `✓ ${name} 上传成功（延迟入库已确认）`);
        this.state.uploaded[item.full] = { fp: item.fp, size: item.st.size, at: new Date().toISOString() };
        delete this.state.failed[item.full];
        this._saveState();
        return { ok: true };
      }
    }

    this.log('error', `✗ ${name} 失败: 云端未找到该文件（上传可能被限流，稍后重试）`);
    const err = res.status ? ('HTTP ' + res.status) : (res.timeout ? '未收到上传响应' : '云端未核实');
    const prev = this.state.failed[item.full] || { count: 0 };
    this.state.failed[item.full] = { fp: item.fp, count: prev.count + 1, lastError: err, at: new Date().toISOString() };
    this._saveState();
    return { ok: false, error: err };
  }

  // 决定文件在云端叫什么名字
  //
  // 重要前提（踩过一次坑）：上传走的是网页版的 <input type=file>，
  // 云端文件名由【浏览器读到的真实文件路径】决定，我们无法在上传时改名。
  // 也就是说：这个函数只影响"日志里显示什么"和"核查时按什么名字找"，
  // 不会改变云端实际名字。若拿改名后的名字去核查，就永远找不到 → 每个文件空等超时。
  //
  // 因此这里返回的是"用于识别"的名字，上传仍必须用真实路径。
  // 真正的目录结构问题（同名不同目录）改用 _resolveCollision() 处理：
  // 上传前若发现云端已有同名同大小但 id 不同的文件，先把它改名为 xxx(目录).ext，
  // 从而让本次上传得到原名 —— 但这需要额外的 rename API，暂不做，
  // 改为在日志与界面里明确提示"两个同名文件会变成 x.txt 与 x(1).txt"。
  _remoteNameFor(item) {
    return path.basename(item.full);
  }

  // 作废缓存的 header（服务端返回 401/403 或 captcha_invalid 时调用）
  _invalidateHeader(method) {
    try {
      if (this._hdrCache) {
        if (method) delete this._hdrCache[method];
        else this._hdrCache = {};
      }
    } catch (e) { }
  }

  // 带缓存的 GET：遇到鉴权/captcha 失效时自动作废缓存并重试一次
  async _apiGet(url) {
    for (let attempt = 0; attempt < 2; attempt++) {
      const gh = await this._capHeader('GET', 25000);
      const r = await fetch(url, { headers: this._H(gh) });
      if (r.status === 200) return r;
      // captcha 或 token 失效：清缓存后重试一次
      if (r.status === 400 || r.status === 401 || r.status === 403) {
        this._invalidateHeader('GET');
        if (attempt === 0) continue;
      }
      return r;
    }
    return null;
  }

  // 云端事实核查：判定"这次上传是否真的入库了"
  //
  // 原实现只比 name+size（engine.js 旧版 L374），有两个致命漏洞：
  //   1. 云端已有同名同大小的旧文件 ⇒ 本次上传失败也判成功 ⇒ 永久不再重试（静默丢数据）
  //   2. 只查 limit=200 第一页 ⇒ 目录超 200 项后，新文件查不到 ⇒ 已传成功也判失败 ⇒ 无限重试
  //
  // 新方案：上传前先给目标目录拍一份 fileId 快照，上传后只认"新出现的 id"。
  //   - 同名旧文件 id 早已在快照里 ⇒ 不会误判
  //   - 分页取全量（最多 20 页 = 4000 项）⇒ 不会漏
  async _snapshotIds() {
    const ids = new Set();
    try {
      if (!this.remoteFolderId) return ids;
      let token = '';
      for (let page = 0; page < 20; page++) {
        let url = API + '/drive/v1/files?parent_id=' + encodeURIComponent(this.remoteFolderId) + '&limit=200';
        if (token) url += '&page_token=' + encodeURIComponent(token);
        const r = await this._apiGet(url);   // 带 header 缓存，不再每页重截 captcha
        if (!r || r.status !== 200) break;
        const j = await r.json();
        for (const f of (j.files || [])) ids.add(f.id);
        token = j.next_page_token || '';
        if (!token) break;
      }
    } catch (e) {
      this.log('warn', `云端快照失败: ${e.message}`);
    }
    return ids;
  }

  // 等待 targetName 对应的、且 id 不在 before 集合里的文件出现
  // 同时校验大小（防止把同名的其他新文件误认成本次的）
  async _verifyRemoteFile(name, size, beforeIds) {
    for (let attempt = 0; attempt < 6; attempt++) {
      if (attempt > 0) await new Promise(r => setTimeout(r, 3000));
      try {
        if (!this.remoteFolderId) return false;

        let token = '';
        for (let page = 0; page < 20; page++) {
          let url = API + '/drive/v1/files?parent_id=' + encodeURIComponent(this.remoteFolderId) + '&limit=200';
          if (token) url += '&page_token=' + encodeURIComponent(token);
          const r = await this._apiGet(url);   // 带 header 缓存
          if (!r || r.status !== 200) break;
          const j = await r.json();
          const files = j.files || [];

          for (const f of files) {
            if (Number(f.size) !== Number(size)) continue;
            // 名称可能是原名，也可能被云盘改名成 "x(1).ext"（同名已存在时）
            const isSameName = f.name === name;
            const isRenamed = this._looksLikeRenamedCopy(f.name, name);
            if (!isSameName && !isRenamed) continue;

            // 核心判据：id 必须是本次新出现的
            if (beforeIds && beforeIds.has(f.id)) {
              // 这是上传前就存在的旧文件，不能算本次成功
              continue;
            }
            return true;
          }

          token = j.next_page_token || '';
          if (!token) break;
        }
      } catch (e) {
        if (attempt === 5) this.log('warn', `云端核查失败: ${e.message}`);
      }
    }
    return false;
  }

  // "report.txt" 被云盘重命名后是 "report(1).txt" / "report(2).txt"
  _looksLikeRenamedCopy(candidate, original) {
    if (!candidate || !original) return false;
    const ext = path.extname(original);
    const stem = ext ? original.slice(0, -ext.length) : original;
    const suffix = ext || '';
    if (stem.length === 0) return false;
    const re = new RegExp('^' + stem.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + '\\(\\d+\\)' + suffix.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + '$');
    return re.test(candidate);
  }

  // ================= 一轮 =================
  async runOnce() {
    if (this.currentRun) throw new Error('已有同步进行中');
    const runId = new Date().toISOString().replace(/[:.]/g, '-');
    this.currentRun = runId;
    const t0 = Date.now();
    const stats = { runId, scanned: 0, uploaded: 0, failed: 0, skipped: 0, errors: [] };
    try {
      await this.connect();
      if (!this.remoteFolderId) await this.ensureRemoteFolder();
      if (!this.remoteFolderId) throw new Error('没有可用的远程文件夹');

      const items = this.scan();
      stats.scanned = items.length;
      const ready = items.filter(i => !i.skip);
      stats.skipped = items.length - ready.length;
      this.log('info', `[${runId}] 扫描 ${items.length} 项，待上传 ${ready.length} 项`);

      // 同名冲突检测：云盘是单层目录，子目录里的同名文件会互相顶掉
      // （第二个会变成 xxx(1).ext）。这不是能静默忽略的事：
      // 用户以为备份了 a\report.txt 和 b\report.txt，云端只有 report.txt + report(1).txt，
      // 且无法从名字判断谁是谁。这里明确告警，并给出可操作建议。
      const byName = {};
      for (const it of ready) {
        const n = path.basename(it.full);
        (byName[n] = byName[n] || []).push(it);
      }
      for (const n of Object.keys(byName)) {
        const group = byName[n];
        if (group.length > 1) {
          const where = group.map(g => g.rel || path.basename(g.full)).join(' | ');
          this.log('warn', `发现 ${group.length} 个同名文件 "${n}"（${where}）—— 云盘是单层目录，它们会变成 "${n}" 和带 (1) 后缀的副本，无法区分来源。建议把不同目录分别加入监视列表并使用不同的远程文件夹名。`);
          stats.nameCollisions = (stats.nameCollisions || 0) + 1;
        }
      }

      if (ready.length === 0) {
        this.log('info', '无需同步');
      } else {
        // 不再做这里的一次预检：uploadOne 内部每个文件都会 enterRemoteFolder(true)，
        // 预检等于把导航（最易失败的一步）多跑一遍，实测多耗 18 秒且失败面翻倍。
        let idx = 0;
        for (const it of ready) {
          // "停止"必须真的停下来：原来 stop() 只 clearTimeout，
          // 当前文件仍会传完、后续循环继续跑，界面却已显示"已停止"（说谎）。
          if (this._abortRequested) {
            this.log('info', `已按用户请求停止，本轮剩余 ${ready.length - idx + 1} 个文件不再上传`);
            stats.aborted = true;
            break;
          }

          idx++;

          // 单文件最多试 2 次（网络抖动/页面慢时一次失败不代表真失败）。
          // 原来函数内零重试，失败只能等下一轮（60s 后），用户体验是"卡住不动"。
          let r = await this.uploadOne(it);
          if (!r.ok) {
            this.log('warn', `${path.basename(it.full)} 第 1 次失败（${r.error}），2 秒后重试`);
            await new Promise(r2 => setTimeout(r2, 2000));
            r = await this.uploadOne(it);
          }

          if (r.ok) stats.uploaded++;
          else { stats.failed++; stats.errors.push({ file: it.full, error: r.error }); }

          // 文件之间留间隔：网页版一次只能处理一个上传，连续触发会被服务端限流
          if (idx < ready.length) {
            await new Promise(r2 => setTimeout(r2, 1500));
          }
        }
      }
    } catch (e) {
      this.log('error', `[${runId}] 失败: ${e.message}`);
      stats.errors.push({ error: e.message });
    } finally {
      stats.durationMs = Date.now() - t0;
      this.currentRun = null;
      this.state.lastRun = stats;
      this._saveState();
      this.emit('run-complete', stats);
      this.log('info', `[${runId}] 完成: 上传 ${stats.uploaded}, 失败 ${stats.failed}, 跳过 ${stats.skipped}, ${stats.durationMs}ms`);
    }
    return stats;
  }

  start() {
    if (this.running) return;
    this.running = true;
    this._abortRequested = false;
    this.log('info', `启动常驻同步，间隔 ${this.cfg.intervalMs}ms`);
    const tick = async () => {
      if (!this.running) return;
      try { await this.runOnce(); } catch (e) { this.log('error', e.message); }
      if (this.running) this.timer = setTimeout(tick, this.cfg.intervalMs);
    };
    tick();
  }

  // 停止：既要停掉定时器，也要让正在跑的这一轮尽快收尾退出。
  // 只 clearTimeout 的话，当前文件会传完、后续循环继续，而界面已经显示"已停止" —— 那是撒谎。
  stop() {
    this.running = false;
    this._abortRequested = true;
    if (this.timer) { clearTimeout(this.timer); this.timer = null; }
    this.log('info', this.currentRun
      ? '已停止（本轮正在收尾，剩余文件不再上传）'
      : '已停止');
  }

  getStatus() {
    return {
      running: this.running,
      watchDirs: this.cfg.watchDirs,
      remoteFolderName: this.cfg.remoteFolderName,
      remoteFolderId: this.remoteFolderId,
      uploadedTotal: Object.keys(this.state.uploaded).length,
      failedTotal: Object.keys(this.state.failed).length,
      lastRun: this.state.lastRun || null,
      failedFiles: Object.entries(this.state.failed).slice(0, 50).map(([f, v]) => ({
        file: f, count: v.count, error: v.lastError, at: v.at,
      })),
    };
  }
}

module.exports = { SyncEngine };
