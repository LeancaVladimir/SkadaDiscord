// Точка входа: значок в трее, запуск WoW ("--play"), одна копия программы.
//   --tray               без окна (только значок в трее)
//   --updated <версия>   запуск после автообновления: окно «SkadaDiscord обновлён» (прежняя копия ещё закрывается)
//   --quit               (с --updated) закрыться, когда окно «обновлён» закрыто (прежняя копия закрывалась вместе с игрой)
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
        public const string Version = "2.4.0";
        public const string Short = "2.4";   // для подписи на картинке
        public const string Author = "Leanca Vladimir";
        public const string DiscordName = "lyanka_v";
    }

    static class Program
    {
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        static Mutex instance; // одна копия программы на папку
        static bool ownsInstance;

        [STAThread]
        static void Main(string[] args)
        {
            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool play = args.Contains("--play");
            bool trayOnly = args.Contains("--tray");
            bool quit = args.Contains("--quit");
            string updatedFrom = null;
            int ui = Array.IndexOf(args, "--updated");
            if (ui >= 0) updatedFrom = ui + 1 < args.Length && !args[ui + 1].StartsWith("--") ? Discord.Cut(args[ui + 1], 20) : "?";
            string appDir = Path.GetDirectoryName(Application.ExecutablePath);
            if (Tools.Run(args, appDir)) return;
            string id = "SkadaDiscord_" + appDir.ToLowerInvariant().GetHashCode().ToString("x");

            bool created;
            instance = new Mutex(true, id, out created);
            if (!created && updatedFrom != null)
            {
                // после обновления прежняя копия ещё закрывается - ждём её
                try { created = instance.WaitOne(TimeSpan.FromSeconds(10)); }
                catch (AbandonedMutexException) { created = true; }
            }
            ownsInstance = created;
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
            Application.Run(new TrayApp(appDir, config, play, trayOnly, updatedFrom, quit, showEvent));
            GC.KeepAlive(instance);
        }

        // перед запуском новой версии (вызывать из главного потока - он владеет мьютексом)
        public static void ReleaseInstance()
        {
            if (!ownsInstance) return;
            try { instance.ReleaseMutex(); } catch { }
            ownsInstance = false;
        }

        // новая версия не запустилась - остаёмся единственной копией
        public static void ReacquireInstance()
        {
            if (ownsInstance) return;
            try { ownsInstance = instance.WaitOne(0); }
            catch (AbandonedMutexException) { ownsInstance = true; }
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
        readonly Updater updater;
        readonly NotifyIcon tray;
        readonly SynchronizationContext ui;
        readonly bool play;
        MainForm form;
        Process wow;
        bool exiting;

        public TrayApp(string appDir, AppConfig config, bool play, bool trayOnly, string updatedFrom, bool quit, EventWaitHandle showEvent)
        {
            this.appDir = appDir;
            this.play = play;
            ui = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(ui);

            engine = new Engine(appDir, config);
            engine.Logged += OnLogged;
            updater = new Updater(engine, ui);
            updater.CanInstall = () => form == null || form.IsDisposed || !form.IsDirty;
            updater.Restart = RestartForUpdate;

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

            DeleteOldExe();
            if (updatedFrom != null) ShowUpdated(updatedFrom, quit);

            if (play)
            {
                wow = StartWow(config, appDir);
                if (wow != null)
                {
                    tray.ShowBalloonTip(3000, "SkadaDiscord", "Игра запущена. Отчёты будут уходить в Discord автоматически.", ToolTipIcon.Info);
                    WatchWow();
                }
            }
            else if (!trayOnly) ShowSettings();

            updater.Start(); // проверка обновлений через несколько секунд и каждые 30 минут
        }

        // SkadaDiscord.old.exe после обновления (прежняя копия может ещё закрываться - пробуем несколько раз)
        void DeleteOldExe()
        {
            var old = Updater.OldExePath(Application.ExecutablePath);
            if (!File.Exists(old)) return;
            var t = new Thread(() =>
            {
                for (int i = 0; i < 30 && File.Exists(old); i++)
                {
                    try { File.Delete(old); }
                    catch { Thread.Sleep(1000); }
                }
                if (File.Exists(old)) engine.Log("Не удалось удалить " + Path.GetFileName(old) + " — удалите его вручную.", LogKind.Warn);
            }) { IsBackground = true };
            t.Start();
        }

        // запуск после обновления: окно со списком изменений и подсказка у часов
        void ShowUpdated(string from, bool quit)
        {
            var v = Updater.VersionText(Updater.Current);
            engine.Log("Программа обновлена: " + from + " → " + v + ".", LogKind.Info);
            tray.ShowBalloonTip(5000, "SkadaDiscord обновлён до " + v, "Что нового — в открывшемся окне.", ToolTipIcon.Info);
            var f = new UpdateForm("SkadaDiscord обновлён до " + v, "Предыдущая версия: " + from + ". Что нового:", "Загружаю список изменений…", false);
            if (quit) f.FormClosed += (s, e) => { if (form == null || form.IsDisposed || !form.Visible) Exit(); };
            f.Show();
            var t = new Thread(() =>
            {
                string body = null;
                try
                {
                    var r = Updater.FetchByTag("v" + v);
                    if (r != null && r.Body.Trim() != "") body = Updater.MarkdownToText(r.Body);
                }
                catch { }
                if (body == null) body = "Версия " + v + ". Список изменений сейчас недоступен.";
                ui.Post(_ => { if (!f.IsDisposed) f.SetBody(body); }, null);
            }) { IsBackground = true };
            t.Start();
        }

        // (главный поток) обновление установлено: запустить новую версию и закрыться
        bool RestartForUpdate(string exePath, string args)
        {
            // запускали через «Играть» и игра уже закрыта: программа и так закрывалась бы вместе с ней
            bool wowClosed = false;
            try { wowClosed = wow != null && wow.HasExited; } catch { }
            if (play && wowClosed && engine.Config.ExitWithGame) args += " --quit";
            Program.ReleaseInstance(); // новая копия ждёт мьютекс до 10 секунд
            try
            {
                Process.Start(new ProcessStartInfo(exePath, args) { WorkingDirectory = appDir, UseShellExecute = false });
            }
            catch (Exception e)
            {
                Program.ReacquireInstance();
                engine.Log("Не удалось запустить новую версию: " + e.Message, LogKind.Error);
                return false;
            }
            exiting = true;
            engine.Stop();
            updater.Stop();
            if (form != null && !form.IsDisposed) form.CloseForUpdate();
            tray.Visible = false;
            tray.Dispose();
            ExitThread();
            return true;
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
                if (updater.InstallIfPending()) return; // ждало обновление - программа перезапускается новой версией
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
                form = new MainForm(engine, Path.Combine(appDir, "config.json"), updater);
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
            updater.Stop();
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
