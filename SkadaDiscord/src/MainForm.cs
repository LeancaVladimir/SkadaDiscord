// Окно программы: журнал, настройки самой программы, о программе.
// Каналы, боссы и окна отчёта с версии 2.2 настраиваются в игре (/sd → «Настройки»).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace SkadaDiscord
{
    public class MainForm : Form
    {
        readonly Engine engine;
        readonly string configPath;
        AppConfig cfg; // редактируемая копия, в работу уходит после "Сохранить"
        bool dirty, loading;

        Label statusLabel, dirtyLabel;
        Panel content;
        readonly Dictionary<string, Control> pages = new Dictionary<string, Control>();
        readonly Dictionary<string, Button> nav = new Dictionary<string, Button>();

        // Программа
        TextBox wowPath, wowExe;
        CheckBox exitWithGame;

        // О программе
        Label gameStatus;

        // Журнал
        RichTextBox logBox;

        public MainForm(Engine engine, string configPath)
        {
            this.engine = engine;
            this.configPath = configPath;
            cfg = engine.Config.Clone();

            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = "SkadaDiscord " + AppInfo.Version;
            BackColor = Theme.Back;
            ForeColor = Theme.Text;
            Font = Theme.Font;
            ClientSize = new Size(1000, 640);
            MinimumSize = new Size(860, 540);
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            BuildShell();
            pages["log"] = BuildLog();
            pages["program"] = BuildProgram();
            pages["about"] = BuildAbout();
            foreach (var p in pages.Values) { p.Dock = DockStyle.Fill; p.Visible = false; content.Controls.Add(p); }
            ShowPage("log");

            LoadProgram();

            engine.Logged += OnLogged;
            foreach (var l in engine.History()) AppendLog(l);
            var t = new System.Windows.Forms.Timer { Interval = 1500 };
            t.Tick += (s, e) => UpdateStatus();
            t.Start();
            UpdateStatus();
        }

        // ---------------------------------------------------------------------
        // каркас

        static void Stack(Control parent, params Control[] controls)
        {
            // Dock раскладывается в обратном z-порядке: BringToFront по порядку = сверху вниз, Fill - последним
            foreach (var c in controls) { parent.Controls.Add(c); c.BringToFront(); }
        }

        void BuildShell()
        {
            var side = new Panel { Dock = DockStyle.Left, Width = 210, BackColor = Theme.Panel, Padding = new Padding(14, 18, 14, 14) };
            var logo = Theme.Label("SkadaDiscord", Theme.H1, Color.White);
            logo.Dock = DockStyle.Top;
            statusLabel = Theme.Label("", Theme.Small, Theme.Green);
            statusLabel.Dock = DockStyle.Top;
            statusLabel.Padding = new Padding(2, 4, 0, 18);
            var navPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Color.Transparent };
            foreach (var item in new[] { new[] { "log", "Журнал" }, new[] { "program", "Программа" }, new[] { "about", "О программе" } })
            {
                var key = item[0];
                var b = Theme.Button(item[1], false);
                b.AutoSize = false;
                b.Size = new Size(180, 38);
                b.TextAlign = ContentAlignment.MiddleLeft;
                b.Margin = new Padding(0, 0, 0, 4);
                b.BackColor = Theme.Panel;
                b.Click += (s, e) => ShowPage(key);
                nav[key] = b;
                navPanel.Controls.Add(b);
            }
            var version = Theme.Label("версия " + AppInfo.Version, Theme.Small, Theme.Muted);
            version.Dock = DockStyle.Bottom;
            Stack(side, logo, statusLabel, version, navPanel);

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 56, BackColor = Theme.Panel, Padding = new Padding(20, 11, 20, 11) };
            dirtyLabel = Theme.Label("", Theme.Font, Theme.Muted);
            dirtyLabel.Dock = DockStyle.Fill;
            dirtyLabel.AutoSize = false;
            dirtyLabel.TextAlign = ContentAlignment.MiddleLeft;
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, FlowDirection = FlowDirection.RightToLeft, BackColor = Color.Transparent };
            var save = Theme.Button("Сохранить", true);
            save.Margin = new Padding(8, 0, 0, 0);
            save.Click += (s, e) => Save();
            var sendNow = Theme.Button("Проверить игру сейчас", false);
            sendNow.Click += (s, e) => { engine.PollNow(); ShowPage("log"); };
            buttons.Controls.Add(save);
            buttons.Controls.Add(sendNow);
            Stack(bottom, buttons, dirtyLabel);

            content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(22, 18, 22, 12) };
            Stack(this, side, bottom, content);
        }

        void ShowPage(string key)
        {
            foreach (var kv in pages) kv.Value.Visible = kv.Key == key;
            foreach (var kv in nav)
            {
                kv.Value.BackColor = kv.Key == key ? Theme.Input : Theme.Panel;
                kv.Value.ForeColor = kv.Key == key ? Color.White : Theme.Muted;
            }
            if (key == "about") RefreshGameStatus();
        }

        void MarkDirty()
        {
            if (loading) return;
            dirty = true;
            dirtyLabel.ForeColor = Theme.Yellow;
            dirtyLabel.Text = "● Есть несохранённые изменения";
        }

        void UpdateStatus()
        {
            statusLabel.Text = "● Работает · отправлено: " + engine.SentCount;
        }

        Control PageHeader(string title, string hint)
        {
            var p = new Panel { Dock = DockStyle.Top, Height = 62, BackColor = Color.Transparent };
            var t = Theme.Label(title, Theme.H2, Color.White);
            t.Location = new Point(0, 0);
            var h = Theme.Label(hint, Theme.Small, Theme.Muted);
            h.Location = new Point(1, 30);
            p.Controls.Add(t);
            p.Controls.Add(h);
            return p;
        }

        // текст, который можно выделить и скопировать
        static TextBox Selectable(string text, Font font, Color color, int width)
        {
            return new TextBox {
                Text = text, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Theme.Back, ForeColor = color,
                Font = font, TabStop = false, Width = width, Margin = new Padding(3, 0, 0, 6),
            };
        }

        // ---------------------------------------------------------------------
        // Программа

        Control BuildProgram()
        {
            var page = new Panel { AutoScroll = true };
            var header = PageHeader("Программа", "Настройки самой программы. Каналы, боссы, окна отчёта и таймер — в игре: /sd → «Настройки».");
            var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, BackColor = Color.Transparent };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 240));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            Action<string, Control> row = (label, control) =>
            {
                var l = Theme.Label(label, null, Theme.Text);
                l.Margin = new Padding(0, 8, 10, 8);
                control.Margin = new Padding(0, 4, 0, 4);
                grid.Controls.Add(l);
                grid.Controls.Add(control);
            };

            var pathRow = Theme.Row();
            wowPath = Theme.TextBox("");
            wowPath.Width = 420;
            var browse = Theme.Button("Обзор…", false);
            browse.Margin = new Padding(6, 0, 0, 0);
            browse.Click += (s, e) =>
            {
                using (var d = new FolderBrowserDialog { Description = "Папка игры (где лежит Wow.exe)", SelectedPath = engine.WowPath })
                    if (d.ShowDialog(this) == DialogResult.OK) wowPath.Text = d.SelectedPath;
            };
            pathRow.Controls.Add(wowPath);
            pathRow.Controls.Add(browse);
            row("Папка игры (пусто = авто)", pathRow);
            wowExe = Theme.TextBox("");
            wowExe.Width = 200;
            row("Файл запуска игры", wowExe);
            exitWithGame = Theme.Check("закрываться вместе с игрой (при запуске через «Играть»)", true);
            row("Закрытие", exitWithGame);

            foreach (var c in new Control[] { wowPath, wowExe }) c.TextChanged += (s, e) => MarkDirty();
            exitWithGame.CheckedChanged += (s, e) => MarkDirty();

            var actions = Theme.Row();
            actions.Dock = DockStyle.Top;
            actions.Padding = new Padding(0, 18, 0, 0);
            var shortcut = Theme.Button("Создать ярлык «Играть» на рабочем столе", true);
            shortcut.Click += (s, e) => CreatePlayShortcut();
            var folder = Theme.Button("Открыть папку программы", false);
            folder.Click += (s, e) => Process.Start("explorer.exe", "\"" + engine.AppDir + "\"");
            actions.Controls.AddRange(new Control[] { shortcut, folder });

            var how = Theme.Label(
                "• Ярлык «Играть» запускает WoW вместе с этой программой (она сидит в трее, у часов).\n" +
                "• Папка игры определяется сама, если программа лежит в папке SkadaDiscord внутри папки игры.", Theme.Small, Theme.Muted);
            how.Dock = DockStyle.Top;
            how.Padding = new Padding(0, 18, 0, 0);

            Stack(page, header, grid, actions, how);
            return page;
        }

        void LoadProgram()
        {
            loading = true;
            wowPath.Text = cfg.WowPath;
            wowExe.Text = cfg.WowExe;
            exitWithGame.Checked = cfg.ExitWithGame;
            loading = false;
        }

        void ReadProgram()
        {
            cfg.WowPath = wowPath.Text.Trim();
            cfg.WowExe = wowExe.Text.Trim() == "" ? "Wow.exe" : wowExe.Text.Trim();
            cfg.ExitWithGame = exitWithGame.Checked;
        }

        void CreatePlayShortcut()
        {
            try
            {
                var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                var wowDir = string.IsNullOrEmpty(wowPath.Text.Trim()) ? engine.WowPath : wowPath.Text.Trim();
                var exe = Path.Combine(wowDir, string.IsNullOrEmpty(wowExe.Text.Trim()) ? "Wow.exe" : wowExe.Text.Trim());
                Shortcut.Create(Path.Combine(desktop, "Играть.lnk"), Application.ExecutablePath, "--play", engine.AppDir,
                    File.Exists(exe) ? exe + ",0" : Application.ExecutablePath + ",0", "World of Warcraft + отправка отчётов Skada в Discord");
                MessageBox.Show(this, "Ярлык «Играть» создан на рабочем столе.", "SkadaDiscord");
            }
            catch (Exception e)
            {
                MessageBox.Show(this, "Не удалось создать ярлык: " + e.Message, "SkadaDiscord");
            }
        }

        // ---------------------------------------------------------------------
        // О программе

        Control BuildAbout()
        {
            var page = new Panel();
            var header = PageHeader("О программе", "Отчёты Skada из WoW 3.3.5a в Discord.");
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Color.Transparent };

            var name = Selectable("SkadaDiscord " + AppInfo.Version, Theme.H1, Color.White, 420);
            name.Margin = new Padding(3, 4, 0, 10);
            var author = Selectable("Автор: " + AppInfo.Author, Theme.Font, Theme.Text, 420);
            var contact = Selectable("Discord: " + AppInfo.DiscordName, Theme.Font, Theme.Text, 420);

            var howTitle = Theme.Label("Как это работает", Theme.Bold, Color.White);
            howTitle.Margin = new Padding(0, 22, 0, 6);
            var how = Theme.Label(
                "• Все настройки отчётов — каналы Discord (ссылки на вебхуки), боссы, окна на картинке, таймер, звук,\n" +
                "   имя бота и размер картинки — редактируются в игре: команда /sd → кнопка «Настройки».\n" +
                "• После каждого убийства аддон сохраняет отчёт. Игра записывает отчёты и настройки в файл при /reload\n" +
                "   и при выходе (таймер после зачистки инста делает /reload сам).\n" +
                "• Программа видит новый файл, рисует картинки как окна Skada и отправляет их в нужные каналы.\n" +
                "   У канала может быть несколько ссылок — сообщение уйдёт в каждую.\n" +
                "• Защита от дублей: если у нескольких игроков рейда одинаковое окно ведёт в один вебхук,\n" +
                "   оно отправится один раз. Убийства уходят и в общую базу данных (один бой — один отчёт),\n" +
                "   это выключается в игре: /sd → «Настройки» → «Общие».\n" +
                "• Здесь, в программе, остались только папка игры, ярлык «Играть» и журнал отправки.", Theme.Font, Theme.Text);
            how.Margin = new Padding(0, 0, 0, 0);

            var nowTitle = Theme.Label("Сейчас", Theme.Bold, Color.White);
            nowTitle.Margin = new Padding(0, 22, 0, 6);
            gameStatus = Theme.Label("", Theme.Font, Theme.Muted);

            flow.Controls.AddRange(new Control[] { name, author, contact, howTitle, how, nowTitle, gameStatus });
            Stack(page, header, flow);
            return page;
        }

        void RefreshGameStatus()
        {
            if (gameStatus == null) return;
            GameConfig game = null;
            try { game = engine.CurrentGame(); } catch { }
            if (game == null)
            {
                gameStatus.Text = "Настройки из игры ещё не получены. Обновите аддон, откройте /sd → «Настройки», добавьте каналы и сделайте /reload.";
                gameStatus.ForeColor = Theme.Yellow;
            }
            else
            {
                gameStatus.Text = game.Describe();
                gameStatus.ForeColor = game.FromGame ? Theme.Green : Theme.Yellow;
            }
        }

        // ---------------------------------------------------------------------
        // Журнал

        Control BuildLog()
        {
            var page = new Panel();
            var header = PageHeader("Журнал", "Что программа прочитала из игры и что отправила.");
            logBox = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BackColor = Theme.Panel, ForeColor = Theme.Text, BorderStyle = BorderStyle.None, Font = new Font("Consolas", 9.5f) };
            var bottom = Theme.Row();
            bottom.Dock = DockStyle.Bottom;
            bottom.Padding = new Padding(0, 10, 0, 0);
            var poll = Theme.Button("Проверить игру сейчас", true);
            poll.Click += (s, e) => engine.PollNow();
            var reports = Theme.Button("Папка с отчётами", false);
            reports.Click += (s, e) => Process.Start("explorer.exe", "\"" + engine.ReportsDir + "\"");
            bottom.Controls.AddRange(new Control[] { poll, reports });
            Stack(page, header, bottom, logBox);
            return page;
        }

        void OnLogged(LogLine l)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(new Action(() => AppendLog(l))); } catch { }
        }

        void AppendLog(LogLine l)
        {
            logBox.SelectionStart = logBox.TextLength;
            logBox.SelectionColor = Theme.Muted;
            logBox.AppendText(l.Time.ToString("dd.MM HH:mm:ss") + "  ");
            logBox.SelectionColor = l.Kind == LogKind.Ok ? Theme.Green : l.Kind == LogKind.Error ? Theme.Red : l.Kind == LogKind.Warn ? Theme.Yellow : Theme.Text;
            logBox.AppendText(l.Text + "\n");
            logBox.ScrollToCaret();
        }

        // снимки страниц (режим --snapshot, для проверки вёрстки)
        public void Snapshot(string dir)
        {
            foreach (var key in new[] { "log", "program", "about" })
            {
                ShowPage(key);
                Application.DoEvents();
                using (var bmp = new Bitmap(Width, Height))
                {
                    DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
                    bmp.Save(Path.Combine(dir, "page_" + key + ".png"));
                }
            }
        }

        // ---------------------------------------------------------------------
        // сохранение / закрытие

        public bool Save()
        {
            Validate();
            ReadProgram();
            try
            {
                cfg.Save(configPath);
            }
            catch (Exception e)
            {
                MessageBox.Show(this, "Не удалось сохранить настройки: " + e.Message, "SkadaDiscord");
                return false;
            }
            engine.Config = cfg.Clone();
            dirty = false;
            dirtyLabel.ForeColor = Theme.Green;
            dirtyLabel.Text = "✓ Сохранено.";
            engine.Log("Настройки программы сохранены.", LogKind.Info);
            return true;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (dirty && e.CloseReason == CloseReason.UserClosing)
            {
                var r = MessageBox.Show(this, "Сохранить изменения?", "SkadaDiscord", MessageBoxButtons.YesNoCancel);
                if (r == DialogResult.Cancel) { e.Cancel = true; return; }
                if (r == DialogResult.Yes && !Save()) { e.Cancel = true; return; }
                if (r == DialogResult.No) { cfg = engine.Config.Clone(); dirty = false; }
            }
            engine.Logged -= OnLogged;
            base.OnFormClosing(e);
        }
    }
}
