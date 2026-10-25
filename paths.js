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
  LOG_FILE: path.join(DATA_DIR, 'sync.log'),
  PROBE_FILE: path.join(DATA_DIR, '.probe.txt'),
};
