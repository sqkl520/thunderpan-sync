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

            // 单实例：两个实例会抢调试端口（默认 9222，被占用时回退 9223-9233）
            bool createdNew;
            var mutex = new Mutex(true, "Global\\ThunderPanSync_SingleInstance", out createdNew);
            if (!createdNew)
            {
                // 只弹 "已经在运行" 会让人以为程序卡住了：本程序退出按钮是
                // "最小化到托盘"（--autostart 开机启动更是不显示窗口），
                // 用户看不到窗口时最需要的提示是"去哪找它"，而不是"它存在"。
                MessageBox.Show(
                    "ThunderPanSync 已在运行（可能已最小化在系统托盘，请看屏幕右下角的托盘图标）。\r\n\r\n"
                    + "请双击托盘图标打开主窗口；如果托盘里也没有，请按 Ctrl+Shift+Esc 打开任务管理器，"
                    + "结束 ThunderPanSync.exe 后重新启动。\r\n\r\n"
                    + "同一个程序只允许运行一个实例，否则两个实例会抢同一个浏览器调试端口。",
                    "ThunderPanSync 已在运行",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // DPI 缩放必须在创建任何窗口之前初始化。
            // Theme.S() 用这个值把逻辑像素换算成实际像素；不初始化的话
            // _scale 保持 0，S() 原样返回 —— 96 DPI 下看不出问题，
            // 但在 125%/150% 缩放下整个界面的尺寸就全是错的。
            try
            {
                using (var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero))
                {
                    Theme.InitScale(g.DpiX / 96f);
                }
            }
            catch (Exception) { /* 取不到 DPI 就用 1.0，不影响启动 */ }

            // 数据目录：%APPDATA%\ThunderPanSync（安装目录只读，绝不能往那里写）
            var dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ThunderPanSync");
            Directory.CreateDirectory(dataDir);

            var form = new MainForm(dataDir);

            // 用户拒绝 EULA 时构造函数会提前 return，此时窗体控件一个都没建。
            // 直接 Application.Run(form) 行为不确定（可能立刻退出，也可能弹异常框），
            // 必须显式判断后干净退出。
            if (form.EulaDeclined)
            {
                try { form.Dispose(); } catch { }
                return;
            }

            Application.Run(form);

            GC.KeepAlive(mutex);
        }
    }
}
