// Точка входа: значок в трее, запуск WoW ("--play"), одна копия программы.
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("SkadaDiscord")]
[assembly: AssemblyDescription("Отчёты Skada из WoW 3.3.5a в Discord")]
[assembly: AssemblyCompany("Leanca Vladimir")]
[assembly: AssemblyProduct("SkadaDiscord")]
[assembly: AssemblyCopyright("Leanca Vladimir")]
[assembly: AssemblyVersion(SkadaDiscord.AppInfo.Version)]
[assembly: AssemblyFileVersion(SkadaDiscord.AppInfo.Version)]

namespace SkadaDiscord
{
    public static class AppInfo
    {
        public const string Version = "2.3.0";
        public const string Short = "2.3";   // для подписи на картинке
        public const string Author = "Leanca Vladimir";
        public const string DiscordName = "lyanka_v";
    }

    static class Program
    {
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main(string[] args)
        {
            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool play = args.Contains("--play");
            string appDir = Path.GetDirectoryName(Application.ExecutablePath);
            if (Tools.Run(args, appDir)) return;
            string id = "SkadaDiscord_" + appDir.ToLowerInvariant().GetHashCode().ToString("x");

            bool created;
            var mutex = new Mutex(true, id, out created);
            var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, id + "_show");
            if (!created)
            {
                // программа уже работает: "Играть" просто запускает игру, иначе показываем окно
                if (play)
                {
                    var c = LoadConfig(appDir);
                    TrayApp.StartWow(c, appDir);
                }
                else showEvent.Set();
                return;
            }

            AppConfig config;
            try { config = LoadConfig(appDir); }
            catch (Exception e)
            {
                MessageBox.Show("Не удалось прочитать config.json: " + e.Message, "SkadaDiscord");
                return;
            }
            Application.Run(new TrayApp(appDir, config, play, showEvent));
            GC.KeepAlive(mutex);
        }

        static AppConfig LoadConfig(string appDir)
        {
            var path = Path.Combine(appDir, "config.json");
            var example = Path.Combine(appDir, "config.example.json");
            if (!File.Exists(path) && File.Exists(example)) File.Copy(example, path); // первый запуск: берём шаблон
            var config = AppConfig.Load(path);
            if (!File.Exists(path) || !File.ReadAllText(path).Contains("\"version\"")) config.Save(path); // перевод старого config.json
            return config;
        }
    }

    class TrayApp : ApplicationContext
    {
        readonly string appDir;
        readonly Engine engine;
        readonly NotifyIcon tray;
        readonly SynchronizationContext ui;
        MainForm form;
        Process wow;
        bool exiting;

        public TrayApp(string appDir, AppConfig config, bool play, EventWaitHandle showEvent)
        {
            this.appDir = appDir;
            ui = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(ui);

            engine = new Engine(appDir, config);
            engine.Logged += OnLogged;

            var menu = new ContextMenuStrip();
            menu.Items.Add("Открыть окно", null, (s, e) => ShowSettings());
            menu.Items.Add("Запустить игру", null, (s, e) => { wow = StartWow(engine.Config, appDir) ?? wow; });
            menu.Items.Add("Проверить игру сейчас", null, (s, e) => engine.PollNow());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Выход", null, (s, e) => Exit());

            Icon icon;
            try { icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { icon = SystemIcons.Application; }
            tray = new NotifyIcon { Icon = icon, Text = "SkadaDiscord " + AppInfo.Version + " — отчёты Skada в Discord", Visible = true, ContextMenuStrip = menu };
            tray.DoubleClick += (s, e) => ShowSettings();

            engine.Start();

            // вторая копия программы просит показать окно
            var t = new Thread(() =>
            {
                while (true)
                {
                    showEvent.WaitOne();
                    ui.Post(_ => ShowSettings(), null);
                }
            }) { IsBackground = true };
            t.Start();

            if (play)
            {
                wow = StartWow(config, appDir);
                if (wow != null)
                {
                    tray.ShowBalloonTip(3000, "SkadaDiscord", "Игра запущена. Отчёты будут уходить в Discord автоматически.", ToolTipIcon.Info);
                    WatchWow();
                }
            }
            else ShowSettings();
        }

        public static Process StartWow(AppConfig config, string appDir)
        {
            var dir = string.IsNullOrEmpty(config.WowPath) ? Path.GetDirectoryName(appDir) : config.WowPath;
            var exe = Path.Combine(dir, string.IsNullOrEmpty(config.WowExe) ? "Wow.exe" : config.WowExe);
            if (!File.Exists(exe))
            {
                MessageBox.Show("Не найден файл игры:\n" + exe + "\n\nУкажите папку игры в окне программы (страница «Программа»).", "SkadaDiscord");
                return null;
            }
            return Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = dir, UseShellExecute = true });
        }

        // когда игра закрыта: отправить последние отчёты (выход пишет файл) и закрыться
        void WatchWow()
        {
            var t = new Thread(() =>
            {
                try { wow.WaitForExit(); } catch { return; }
                Thread.Sleep(4000);
                engine.Log("Игра закрыта — отправляю последние отчёты.", LogKind.Info);
                try { engine.Poll(true); } catch { }
                if (engine.Config.ExitWithGame)
                    ui.Post(_ => { if (form == null || form.IsDisposed || !form.Visible) Exit(); }, null);
            }) { IsBackground = true };
            t.Start();
        }

        void ShowSettings()
        {
            if (exiting) return;
            if (form == null || form.IsDisposed)
            {
                form = new MainForm(engine, Path.Combine(appDir, "config.json"));
                form.FormClosed += (s, e) => { form = null; };
            }
            form.Show();
            if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
            form.Activate();
        }

        void OnLogged(LogLine l)
        {
            if (l.Kind == LogKind.Ok || l.Kind == LogKind.Error)
                ui.Post(_ =>
                {
                    if (!exiting) tray.ShowBalloonTip(3000, "SkadaDiscord", l.Text, l.Kind == LogKind.Error ? ToolTipIcon.Error : ToolTipIcon.Info);
                }, null);
        }

        void Exit()
        {
            exiting = true;
            if (form != null && !form.IsDisposed) form.Close();
            if (form != null && !form.IsDisposed) { exiting = false; return; } // отменили закрытие (несохранённые изменения)
            engine.Stop();
            tray.Visible = false;
            tray.Dispose();
            ExitThread();
        }
    }

    static class Shortcut
    {
        // ярлык через WScript.Shell (без лишних библиотек)
        public static void Create(string lnkPath, string target, string args, string workDir, string icon, string description)
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(type);
            try
            {
                object lnk = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
                var lt = lnk.GetType();
                lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { target });
                lt.InvokeMember("Arguments", BindingFlags.SetProperty, null, lnk, new object[] { args });
                lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { workDir });
                lt.InvokeMember("IconLocation", BindingFlags.SetProperty, null, lnk, new object[] { icon });
                lt.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { description });
                lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
                Marshal.ReleaseComObject(lnk);
            }
            finally { Marshal.ReleaseComObject(shell); }
        }
    }
}
