// Program.cs -- 入口点：单实例、隐藏浏览器、启动主窗口
//
// 浏览器策略（用户选择：完全隐藏，只在首次登录时弹窗）：
//   1. 检查 9222 是否已有登录态 → 有则完全静默，用户看不到浏览器
//   2. 没有 → 用最小化的 Edge 启动（带调试端口），提示用户登录一次
//   3. 登录完成后（检测到 sessionid）自动把浏览器窗口隐藏到后台
//
// 为什么浏览器必须存在：captcha 由浏览器 JS 实时计算（算法分片动态下发），
// 实测无法离线复刻。但用户不需要知道这件事——它只是一个后台凭证来源。
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace ThunderPanSync
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            bool autostart = false;
            foreach (var a in args) { if (a == "--autostart") autostart = true; }

            // 单实例：两个实例会抢 9222 调试端口
            bool createdNew;
            var mutex = new Mutex(true, "Global\\ThunderPanSync_SingleInstance", out createdNew);
            if (!createdNew)
            {
                MessageBox.Show("ThunderPanSync 已经在运行（请查看系统托盘）。", "ThunderPanSync",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 数据目录：%APPDATA%\ThunderPanSync（安装目录只读，绝不能往那里写）
            var dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ThunderPanSync");
            Directory.CreateDirectory(dataDir);

            var form = new MainForm(dataDir);
            Application.Run(form);

            GC.KeepAlive(mutex);
        }
    }
}
