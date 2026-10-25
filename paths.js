// paths.js -- 唯一的数据路径来源（三种运行模式都要正确）
//
// 为什么必须有这个文件：
//   1. Electron 打包后 __dirname 在 app.asar 内 = 只读，写配置会抛异常
//   2. 原生 C# 前端 + node.exe 模式下，程序装在 Program Files，__dirname 同样只读
//   3. 三条入口（Electron 主进程 / node 宿主 / 测试脚本）必须看到同一个目录，
//      否则会出现"界面显示已上传、引擎却在另一个 state.json 里"这种分裂
//
// 优先级：
//   环境变量 PANSYNC_DATA_DIR  >  Electron userData  >  %APPDATA%\ThunderPanSync
const path = require('path');
const fs = require('fs');
const os = require('os');

function appDataRoot() {
  const ad = process.env.APPDATA || path.join(os.homedir(), 'AppData', 'Roaming');
  return path.join(ad, 'ThunderPanSync');
}

function resolveDataDir() {
  // 1) 显式指定（安装器/测试可覆盖）
  if (process.env.PANSYNC_DATA_DIR) return process.env.PANSYNC_DATA_DIR;

  // 2) Electron：打包后必须用 userData
  try {
    const electron = require('electron');
    const app = electron && electron.app;
    if (app && typeof app.getPath === 'function' && app.isPackaged !== undefined) {
      return app.isPackaged ? app.getPath('userData') : path.join(__dirname, 'data');
    }
  } catch (e) { /* 不在 Electron 里 */ }

  // 3) 纯 Node（原生前端模式）：开发时用项目目录，打包后由启动器设 PANSYNC_DATA_DIR
  if (process.env.PANSYNC_DEV === '1') return path.join(__dirname, 'data');
  if (fs.existsSync(path.join(__dirname, 'package.json'))) {
    // 有源码树但没设环境变量：说明是开发态直接跑
    return path.join(__dirname, 'data');
  }
  return appDataRoot();
}

const DATA_DIR = resolveDataDir();
try { fs.mkdirSync(DATA_DIR, { recursive: true }); } catch (e) {}

function profileDir() {
  const p = process.env.PANSYNC_PROFILE_DIR || path.join(DATA_DIR, 'browser-profile');
  try { fs.mkdirSync(p, { recursive: true }); } catch (e) {}
  return p;
}

// ---------- 日志按天分文件 ----------
// 为什么不再只写一个 sync.log：
//   常驻同步跑几个月后单文件会到几十 MB，用户/支持要定位"昨天那次失败"时
//   既打不开也搜不动。按天切分后，只要按日期找文件即可。
// Windows 不能建软链，所以另写一个 latest.log 指针文件，内容是"当前活动日志"的完整路径。
function pad2(n) { return n < 10 ? '0' + n : '' + n; }

// YYYY-MM-DD（本地时区：日志是给人看的，必须与用户看到的日期一致，不能用 UTC）
function dayStamp(d) {
  const t = d instanceof Date ? d : (d ? new Date(d) : new Date());
  if (isNaN(t.getTime())) return dayStamp(new Date());
  return t.getFullYear() + '-' + pad2(t.getMonth() + 1) + '-' + pad2(t.getDate());
}

// 当天的日志文件；传 Date/日期字符串可取任意一天
function getLogFile(d) {
  return path.join(DATA_DIR, 'sync-' + dayStamp(d) + '.log');
}

// 指针文件：内容为当前活动日志路径（每次跨天覆盖一次，不重复写）
function getLatestLogFile() {
  return path.join(DATA_DIR, 'latest.log');
}

function noteLatestLog(file) {
  const target = getLatestLogFile();
  const body = file + '\n';
  try {
    // 已经是同一路径就不重写，避免每条日志都产生一次磁盘写
    try { if (fs.readFileSync(target, 'utf8') === body) return; } catch (e) {}
    fs.mkdirSync(path.dirname(target), { recursive: true });
    fs.writeFileSync(target, body, 'utf8');
  } catch (e) { /* 指针文件写失败不影响日志本身 */ }
}

module.exports = {
  isPackaged() {
    try {
      const electron = require('electron');
      return !!(electron && electron.app && electron.app.isPackaged);
    } catch (e) { return false; }
  },
  DATA_DIR: DATA_DIR,
  PROFILE_DIR: profileDir,
  CONFIG_FILE: path.join(DATA_DIR, 'config.json'),
  STATE_FILE: path.join(DATA_DIR, 'state.json'),
  // 保留 LOG_FILE 是为了向后兼容（host.js 的 status 回复、engine 的 cfg.logFile）。
  // 注意它只是"模块加载那一天"的路径；跨天要用 getLogFile() 重新取。
  LOG_FILE: getLogFile(),
  // 按天分文件：sync-YYYY-MM-DD.log
  getLogFile: getLogFile,
  getLatestLogFile: getLatestLogFile,
  // latest.log 指针（内容为当前活动日志的完整路径；Windows 不能建软链）
  noteLatestLog: noteLatestLog,
  PROBE_FILE: path.join(DATA_DIR, '.probe.txt'),
};

