// Следит за SavedVariables аддона, берёт оттуда отчёты и настройки (configJson), собирает сообщения и отправляет их в Discord.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace SkadaDiscord
{
    public enum LogKind { Info, Ok, Warn, Error }

    public class LogLine
    {
        public DateTime Time = DateTime.Now;
        public string Text;
        public LogKind Kind;
    }

    // одно сообщение в один канал (в каждый вебхук канала)
    public class Planned
    {
        public GameConfig Config;
        public Channel Channel;
        public List<Block> Blocks = new List<Block>();
        public List<Block> AllBlocks; // все окна босса (для "трeша" нужны выбранные цели соседних окон)
    }

    public class Engine
    {
        public AppConfig Config;
        public readonly string AppDir;
        public event Action<LogLine> Logged;
        public int SentCount;

        readonly List<LogLine> log = new List<LogLine>();
        readonly HashSet<string> sent = new HashSet<string>();
        readonly Dictionary<string, DateTime> seen = new Dictionary<string, DateTime>();
        readonly Dictionary<string, DateTime> retryAt = new Dictionary<string, DateTime>();
        readonly object sync = new object();
        Thread thread;
        volatile bool running;
        readonly AutoResetEvent wake = new AutoResetEvent(false);

        public Engine(string appDir, AppConfig config)
        {
            AppDir = appDir;
            Config = config;
            var sentPath = Path.Combine(AppDir, "sent.txt");
            if (File.Exists(sentPath))
                foreach (var l in File.ReadAllLines(sentPath, Encoding.UTF8)) if (l.Trim() != "") sent.Add(l.Trim());
            Directory.CreateDirectory(ReportsDir);
        }

        public string WowPath
        {
            get { return string.IsNullOrEmpty(Config.WowPath) ? Path.GetDirectoryName(AppDir) : Config.WowPath; }
        }

        public string ReportsDir { get { return Path.Combine(AppDir, "reports"); } }

        public string FontPath
        {
            get
            {
                foreach (var rel in new[] { @"Interface\AddOns\SharedMedia\Media\Fonts\Expressway.ttf", @"Interface\AddOns\DBM-Core\Fonts\Expressway.ttf", @"Interface\AddOns\ElvUI\Media\Fonts\Expressway.ttf" })
                {
                    var p = Path.Combine(WowPath, rel);
                    if (File.Exists(p)) return p;
                }
                var local = Path.Combine(AppDir, "Expressway.ttf");
                return File.Exists(local) ? local : null;
            }
        }

        public string IconsPath { get { return Path.Combine(WowPath, @"Interface\AddOns\Skada\Media\Textures\icons.blp"); } }

        // файлы SavedVariables аддона на всех аккаунтах
        public List<string> SavedVariablesFiles()
        {
            var list = new List<string>();
            var dir = Path.Combine(WowPath, @"WTF\Account");
            if (!Directory.Exists(dir)) return list;
            foreach (var acc in Directory.GetDirectories(dir))
            {
                var file = Path.Combine(acc, @"SavedVariables\SkadaDiscord.lua");
                if (File.Exists(file)) list.Add(file);
            }
            return list;
        }

        // ---------------------------------------------------------------------
        // журнал

        public void Log(string text, LogKind kind)
        {
            var line = new LogLine { Text = text, Kind = kind };
            lock (log)
            {
                log.Add(line);
                if (log.Count > 500) log.RemoveAt(0);
            }
            try
            {
                var path = Path.Combine(AppDir, "log.txt");
                if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024) File.Delete(path);
                File.AppendAllText(path, line.Time.ToString("yyyy-MM-dd HH:mm:ss") + "  " + text + "\r\n", Encoding.UTF8);
            }
            catch { }
            var h = Logged;
            if (h != null) h(line);
        }

        public List<LogLine> History()
        {
            lock (log) return new List<LogLine>(log);
        }

        // ---------------------------------------------------------------------
        // цикл

        public void Start()
        {
            if (running) return;
            running = true;
            thread = new Thread(Loop) { IsBackground = true, Name = "SkadaDiscord watcher" };
            thread.Start();
            Log("SkadaDiscord " + AppInfo.Version + ": слежу за игрой: " + WowPath, LogKind.Info);
        }

        public void Stop()
        {
            running = false;
            wake.Set();
        }

        public void PollNow() { wake.Set(); }

        void Loop()
        {
            bool first = true;
            while (running)
            {
                try { Poll(first); }
                catch (Exception e) { Log("Ошибка: " + e.Message, LogKind.Error); }
                first = false;
                wake.WaitOne(TimeSpan.FromSeconds(Math.Max(1, Config.PollSeconds)));
            }
        }

        // отправить всё, что ещё не отправлено (вызывается и при выходе из игры)
        public void Poll(bool force)
        {
            lock (sync)
            {
                foreach (var file in SavedVariablesFiles())
                {
                    var time = File.GetLastWriteTimeUtc(file);
                    DateTime old, retry;
                    bool changed = !seen.TryGetValue(file, out old) || old != time;
                    bool due = retryAt.TryGetValue(file, out retry) && DateTime.Now >= retry;
                    if (!changed && !due && !force) continue;
                    if (changed && !force) Thread.Sleep(700); // даём игре дописать файл
                    seen[file] = time;
                    retryAt.Remove(file);
                    if (!ProcessFile(file, changed))
                        retryAt[file] = DateTime.Now.AddSeconds(30);
                }
            }
        }

        // ---------------------------------------------------------------------
        // чтение SavedVariables

        public static string ReadText(string path)
        {
            for (int i = 0; i < 5; i++)
            {
                try { return File.ReadAllText(path, Encoding.UTF8); }
                catch (IOException) { Thread.Sleep(300); }
            }
            return null;
        }

        public static List<string> ReadReports(string path)
        {
            return ExtractReports(ReadText(path));
        }

        // отчёты - строки с JSON-объектом: "{...}", -- [n]
        public static List<string> ExtractReports(string text)
        {
            var list = new List<string>();
            if (text == null) return list;
            foreach (Match m in Regex.Matches(text, "(?m)^\\s*\"(\\{(?:[^\"\\\\]|\\\\.)*\\})\",\\s*(?:--.*)?$"))
                list.Add(LuaUnescape(m.Groups[1].Value));
            return list;
        }

        // настройки из игры: строка ["configJson"] = "{...}", (null - аддон их не записал)
        public static string ExtractConfigJson(string text)
        {
            if (text == null) return null;
            var m = Regex.Match(text, "(?m)^[ \\t]*\\[\"configJson\"\\][ \\t]*=[ \\t]*\"");
            if (!m.Success) return null;
            int start = m.Index + m.Length, i = start;
            while (i < text.Length)
            {
                char c = text[i];
                if (c == '\\') { i += 2; continue; }
                if (c == '"') break;
                i++;
            }
            if (i >= text.Length) return null; // файл дописан не до конца
            return LuaUnescape(text.Substring(start, i - start));
        }

        // строка Lua из SavedVariables: \" \\ \n \r \t \ddd (байты), \ + перевод строки
        public static string LuaUnescape(string s)
        {
            var sb = new StringBuilder(s.Length);
            var bytes = new List<byte>(); // \ddd выше 127 - байты UTF-8
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c != '\\' || i + 1 >= s.Length)
                {
                    FlushBytes(sb, bytes);
                    sb.Append(c);
                    continue;
                }
                char n = s[++i];
                if (n >= '0' && n <= '9')
                {
                    int code = 0, k = 0;
                    while (k < 3 && i < s.Length && s[i] >= '0' && s[i] <= '9') { code = code * 10 + (s[i] - '0'); i++; k++; }
                    i--;
                    if (code < 128) { FlushBytes(sb, bytes); sb.Append((char)code); }
                    else bytes.Add((byte)Math.Min(255, code));
                    continue;
                }
                FlushBytes(sb, bytes);
                switch (n)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case '\r':
                        if (i + 1 < s.Length && s[i + 1] == '\n') i++;
                        sb.Append('\n');
                        break;
                    default: sb.Append(n); break; // \" \\ \' и \ + перевод строки
                }
            }
            FlushBytes(sb, bytes);
            return sb.ToString();
        }

        static void FlushBytes(StringBuilder sb, List<byte> bytes)
        {
            if (bytes.Count == 0) return;
            sb.Append(Encoding.UTF8.GetString(bytes.ToArray()));
            bytes.Clear();
        }

        // настройки отчётов для файла: из игры, иначе старые из config.json, иначе null
        public GameConfig GameFromText(string text, bool logErrors)
        {
            var json = ExtractConfigJson(text);
            if (json != null)
            {
                try { return GameConfig.FromJson(json); }
                catch (Exception e)
                {
                    if (logErrors) Log("Настройки из игры не прочитаны (" + e.Message + ").", LogKind.Warn);
                }
            }
            if (Config.HasLegacy) return Config.Legacy();
            return null;
        }

        // настройки первого файла игры (для окна, снимков и проверок)
        public GameConfig CurrentGame()
        {
            foreach (var file in SavedVariablesFiles())
            {
                var json = ExtractConfigJson(ReadText(file));
                if (json == null) continue;
                try { return GameConfig.FromJson(json); } catch { }
            }
            return Config.HasLegacy ? Config.Legacy() : null;
        }

        bool ProcessFile(string file, bool announce)
        {
            var text = ReadText(file);
            if (text == null) return false; // файл занят игрой - попробуем позже
            var raws = ExtractReports(text);
            var game = GameFromText(text, announce);
            if (game != null && game.FromGame && game.ImportId != "")
            {
                // аддон забрал перенесённые настройки - убираем ссылки из Config.lua аддона
                try
                {
                    if (AppConfig.ClearImport(WowPath, game.ImportId))
                        Log("Настройки перенесены в игру (профиль «" + game.Profile + "»). Config.lua аддона очищен.", LogKind.Info);
                }
                catch (Exception e) { Log("Не удалось очистить Config.lua аддона: " + e.Message, LogKind.Warn); }
            }

            // база данных: выбор из настроек игры (даже если своих каналов ещё нет)
            var dbGame = game;
            // ни одной ссылки на вебхук (аддон только что установлен) - отчёты ждут, а не пропускаются
            if (game != null && !game.Channels.Any(c => c.ValidHooks().Count > 0)) game = null;

            int newOnes = 0, waiting = 0;
            bool ok = true;
            foreach (var raw in raws)
            {
                ReportData r;
                try { r = ReportData.Parse(raw); }
                catch { Log("Пропущен повреждённый отчёт", LogKind.Warn); continue; }

                if (r.Version < 2)
                {
                    if (sent.Add(r.Id)) AppendSent(r.Id); // старый формат: уже отправлялся прежней версией
                    continue;
                }
                Cache(r);
                if (game == null)
                {
                    if (!SendToDb(r, dbGame)) ok = false;
                    waiting++; // настроек ещё нет - отчёт подождёт
                    continue;
                }

                var plan = Plan(r, game);
                var fight = r.FightKey();
                foreach (var p in plan)
                {
                    var hooks = p.Channel.ValidHooks();
                    var todo = PendingHooks(r, p.Channel, hooks);
                    if (todo.Count == 0) continue;
                    newOnes++;

                    // картинки по наборам окон (у разных ссылок другие игроки могли уже отправить часть окон)
                    var images = new Dictionary<string, Tuple<RenderJob, byte[]>>();
                    foreach (var hook in todo)
                    {
                        var key = HookKey(r, p.Channel, hook);
                        var where = "#" + p.Channel.Name + (hooks.Count > 1 ? string.Format(" (ссылка {0} из {1})", hooks.IndexOf(hook) + 1, hooks.Count) : "");

                        // защита от дублей: окна, которые в эту ссылку уже отправил другой игрок рейда, пропускаем
                        var claimed = new List<string>();
                        var blocks = ClaimBlocks(hook, fight, p.Blocks, claimed);
                        if (blocks.Count == 0)
                        {
                            if (sent.Add(key)) AppendSent(key);
                            Log(string.Format("{0} → {1}: уже отправлено другим игроком рейда.", r.Boss, where), LogKind.Info);
                            continue;
                        }

                        var subset = string.Join(",", blocks.Select(b => p.Blocks.IndexOf(b).ToString()));
                        Tuple<RenderJob, byte[]> image;
                        if (!images.TryGetValue(subset, out image))
                        {
                            try
                            {
                                var job = Compose(r, blocks, p.AllBlocks);
                                image = Tuple.Create(job, Renderer.Render(job, FontPath, IconsPath, ScaleOf(game)));
                                images[subset] = image;
                            }
                            catch (Exception e)
                            {
                                ok = false;
                                ReleaseClaims(claimed);
                                Log(string.Format("Не отправлено: {0} → {1}: картинка не нарисована ({2})", r.Boss, where, e.Message), LogKind.Error);
                                continue;
                            }
                        }

                        try
                        {
                            SendJob(hook, game, r, image.Item1, image.Item2, "alt", null);
                            if (sent.Add(key)) AppendSent(key);
                            foreach (var b in blocks) RememberClaim(ClaimKey(hook, fight, b));
                            SentCount++;
                            var part = blocks.Count < p.Blocks.Count
                                ? string.Format(" (окон {0} из {1}, остальные уже отправил другой игрок рейда)", blocks.Count, p.Blocks.Count) : "";
                            Log(string.Format("Отправлено: {0} ({1}) → {2}{3}", r.Boss, Calc.DiffName(r.Diff), where, part), LogKind.Ok);
                        }
                        catch (Exception e)
                        {
                            ok = false;
                            ReleaseClaims(claimed); // пусть отправит другой игрок или мы при повторе
                            if (sent.Add(key + "|retry")) AppendSent(key + "|retry"); // эту ссылку повторим и после перезапуска
                            Log(string.Format("Не отправлено: {0} → {1}: {2}", r.Boss, where, e.InnerException != null ? e.InnerException.Message : e.Message), LogKind.Error);
                        }
                    }
                }
                if (!SendToDb(r, dbGame)) ok = false;
                if (plan.Count == 0 && sent.Add(r.Id + "|none"))
                {
                    AppendSent(r.Id + "|none");
                    if (WantsDb(r, dbGame))
                        Log(string.Format("{0}: своих каналов для этого босса нет, отчёт только для базы данных.", r.Boss), LogKind.Info);
                    else
                        Log(string.Format("{0}: для этого босса не настроено ни одного канала, пропускаю.", r.Boss), LogKind.Warn);
                }
            }
            if (waiting > 0 && announce)
                Log("Отчётов ждут настроек: " + waiting + ". В игре откройте /sd → «Настройки», укажите каналы и сделайте /reload.", LogKind.Warn);
            else if (announce && newOnes == 0)
                Log(raws.Count == 0 ? "Файл из игры прочитан: список отчётов пуст." : "Файл из игры прочитан: новых отчётов нет.", LogKind.Info);
            if (!ok) Log("Повторю через 30 секунд.", LogKind.Warn);
            return ok;
        }

        // ---------------------------------------------------------------------
        // что уже отправлено: "отчёт|канал|хэш ссылки" (старые записи "отчёт|канал" = отправлено во все ссылки канала)

        public static string HookHash(string hook)
        {
            return AppConfig.Hash((hook ?? "").Trim(), 10);
        }

        static string HookKey(ReportData r, Channel ch, string hook)
        {
            return r.Id + "|" + ch.Id + "|" + HookHash(hook);
        }

        // ссылки канала, куда этот отчёт ещё надо отправить
        public List<string> PendingHooks(ReportData r, Channel ch, List<string> hooks)
        {
            var todo = new List<string>();
            if (sent.Contains(r.Id + "|" + ch.Id)) return todo; // отправлено прежней версией
            bool any = hooks.Any(h => sent.Contains(HookKey(r, ch, h)));
            foreach (var h in hooks)
            {
                var key = HookKey(r, ch, h);
                if (sent.Contains(key)) continue;
                // отчёт уже ушёл в другие ссылки канала: повторяем только неудачные,
                // а ссылки, добавленные позже, старые отчёты не получают
                if (any && !sent.Contains(key + "|retry")) continue;
                todo.Add(h);
            }
            return todo;
        }

        void AppendSent(string key)
        {
            try { File.AppendAllText(Path.Combine(AppDir, "sent.txt"), key + "\r\n", Encoding.UTF8); } catch { }
        }

        // ---------------------------------------------------------------------
        // защита от дублей между игроками рейда (общие окна в один вебхук - один раз)

        // окно: метрика : цели : только лекари
        public static string BlockSignature(Block b)
        {
            bool healers = Metrics.IsHeal(b.Metric) && (b.Healers ?? true);
            return b.Metric + ":" + string.Join(",", b.Targets) + ":" + (healers ? "1" : "0");
        }

        // ключ на сервере: "W:<16 hex SHA1 ссылки>|<бой>|<окно>" (длинный - сокращается хэшем)
        public static string ClaimKey(string hook, string fight, Block b)
        {
            var hookHash = AppConfig.Hash((hook ?? "").Trim(), 16);
            var key = "W:" + hookHash + "|" + fight + "|" + BlockSignature(b);
            if (key.Length > Relay.MaxKeyLength) key = "W:" + hookHash + "|H:" + AppConfig.Hash(fight + "|" + BlockSignature(b), 40);
            return key;
        }

        // окна сообщения, которые отправляем мы; claimed - ключи, занятые сейчас (освободить при ошибке)
        public List<Block> ClaimBlocks(string hook, string fight, List<Block> blocks, List<string> claimed)
        {
            var keys = blocks.Select(b => ClaimKey(hook, fight, b)).ToList();
            ClaimResult res;
            try { res = Relay.Claim(keys); }
            catch (Exception e)
            {
                Log("Защита от дублей недоступна (" + e.Message + ") — отправляю без проверки.", LogKind.Warn);
                return new List<Block>(blocks);
            }
            var mine = new List<Block>();
            for (int i = 0; i < blocks.Count; i++)
            {
                var k = keys[i];
                // занято раньше нами же (отчёт добавлен заново) - тоже наше
                if (res.Taken.Contains(k) && !sent.Contains("claim|" + k)) continue;
                mine.Add(blocks[i]);
                if (res.Claimed.Contains(k) && !claimed.Contains(k)) claimed.Add(k);
            }
            return mine;
        }

        void RememberClaim(string key)
        {
            if (sent.Add("claim|" + key)) AppendSent("claim|" + key);
        }

        void ReleaseClaims(List<string> keys)
        {
            if (keys.Count == 0) return;
            try { Relay.Release(keys); }
            catch (Exception e) { Log("Не удалось освободить ключи защиты от дублей: " + e.Message, LogKind.Warn); }
        }

        // ---------------------------------------------------------------------
        // база данных: общий сервер отчётов, один отчёт на бой (дубли отсекает сервер по ключу боя)

        const float DbScale = 1.5f; // картинка базы не зависит от настроек игрока

        // окна картинки для базы: весь урон (25 строк) и исцеление + поглощения лекарей (10 строк)
        public static List<Block> DbBlocks()
        {
            var dmg = Metrics.NewBlock("damage", "");
            dmg.Rows = 25;
            var heal = Metrics.NewBlock("healing", "");
            heal.Rows = 10;
            heal.Healers = true;
            return new List<Block> { dmg, heal };
        }

        // отправлять ли отчёт в базу: только убийства, отчёты аддона 2.3+ (есть рейд-лидер), если игрок не выключил
        public static bool WantsDb(ReportData r, GameConfig game)
        {
            if (r.Version < 2 || !r.Success || string.IsNullOrEmpty(r.Leader)) return false;
            if (r.Db.HasValue && !r.Db.Value) return false;
            return game == null || game.SendDB;
        }

        // инст и номер босса в нём (порядок каналов на сервере базы): стандартные списки, иначе настройки игрока
        public static void DbPlace(ReportData r, GameConfig game, out string instance, out int index)
        {
            int i = Array.IndexOf(AppConfig.IccBosses, r.Boss);
            if (i >= 0) { instance = "ЦЛК"; index = i + 1; return; }
            i = Array.IndexOf(AppConfig.RsBosses, r.Boss);
            if (i >= 0) { instance = "РС"; index = i + 1; return; }
            instance = r.Instance ?? "";
            index = 0;
            if (game == null) return;
            Instance inst; Boss boss;
            game.Find(r.Instance, r.Boss, out inst, out boss);
            if (inst == null || boss == null) return;
            if (instance == "") instance = inst.Name;
            index = inst.Bosses.IndexOf(boss) + 1;
        }

        static readonly HashSet<string> NotPlayer = new HashSet<string> { "PET", "MONSTER", "ENEMY", "BOSS" };

        // meta для сервера базы: бой и игроки (урон/исцеление в секунду, места)
        public static string DbMeta(ReportData r, GameConfig game)
        {
            string instance; int index;
            DbPlace(r, game, out instance, out index);

            // только игроки (без питомцев и транспорта вроде "Мутировавшего поганища")
            var list = r.Players.Where(p => !NotPlayer.Contains(p.Class ?? "")).ToList();
            var dmgRank = new Dictionary<PlayerData, int>();
            int n = 0;
            foreach (var p in list.Where(p => p.Damage > 0).OrderByDescending(p => p.Damage)) dmgRank[p] = ++n;
            var healRank = new Dictionary<PlayerData, int>();
            n = 0;
            foreach (var p in list.Where(p => Calc.IsHealer(p) && p.Heal + p.Absorb > 0).OrderByDescending(p => p.Heal + p.Absorb)) healRank[p] = ++n;

            var players = new List<object>();
            foreach (var p in list)
            {
                int dr, hr;
                dmgRank.TryGetValue(p, out dr);
                healRank.TryGetValue(p, out hr);
                var role = Calc.IsHealer(p) ? "HEALER" : (string.IsNullOrEmpty(p.Role) ? "NONE" : p.Role);
                players.Add(new OrderedMap {
                    { "n", p.Name }, { "c", p.Class }, { "r", role },
                    { "dps", Math.Round(p.Damage / p.Time) }, { "hps", Math.Round((p.Heal + p.Absorb) / p.Time) },
                    { "dr", dr }, { "hr", hr },
                });
            }

            var meta = new OrderedMap();
            meta.Add("key", r.FightKey());
            meta.Add("instance", instance);
            meta.Add("boss", r.Boss);
            meta.Add("bossIndex", index);
            meta.Add("diff", r.Diff);
            meta.Add("start", r.Start);
            meta.Add("duration", Math.Round(r.Duration));
            meta.Add("success", r.Success);
            meta.Add("attempt", r.Attempt);
            meta.Add("player", r.Player);
            meta.Add("realm", r.Realm);
            meta.Add("guild", r.Guild);
            meta.Add("players", players);
            return Json.Write(meta);
        }

        public RenderJob DbJob(ReportData r)
        {
            var blocks = DbBlocks();
            return Compose(r, blocks, blocks);
        }

        // false - не отправлено (повторим позже)
        bool SendToDb(ReportData r, GameConfig game)
        {
            if (!WantsDb(r, game)) return true;
            var key = r.Id + "|db";
            if (sent.Contains(key)) return true;
            try
            {
                var job = DbJob(r);
                var png = Renderer.Render(job, FontPath, IconsPath, DbScale);
                string content, embed, alt;
                Message(r, job, "alt", null, out content, out embed, out alt);
                var payload = Discord.Payload("SkadaDiscord", content, embed, png, alt);
                var res = Relay.SendReport(DbMeta(r, game), payload, png, r.Raw);
                if (sent.Add(key)) AppendSent(key);
                if (res.Status == "duplicate")
                    Log(string.Format("{0} ({1}): уже в базе (отправил другой игрок).", r.Boss, Calc.DiffName(r.Diff)), LogKind.Info);
                else
                {
                    SentCount++;
                    Log(string.Format("Отправлено: {0} ({1}) → база данных", r.Boss, Calc.DiffName(r.Diff)), LogKind.Ok);
                }
                return true;
            }
            catch (Exception e)
            {
                Log(string.Format("Не отправлено в базу данных: {0}: {1}", r.Boss, e.InnerException != null ? e.InnerException.Message : e.Message), LogKind.Error);
                return false;
            }
        }

        // ---------------------------------------------------------------------
        // что и куда отправлять

        public List<Planned> Plan(ReportData r, GameConfig game)
        {
            var result = new List<Planned>();
            if (game == null) return result;
            Instance inst; Boss boss;
            game.Find(r.Instance, r.Boss, out inst, out boss);

            List<Block> blocks;
            if (boss != null)
            {
                if (!boss.Enabled && !r.Manual) return result;
                blocks = boss.Blocks;
            }
            else if (r.Manual || game.Others)
            {
                blocks = new List<Block> { Metrics.NewBlock("damage", game.DefaultDps), Metrics.NewBlock("healing", game.DefaultHps) };
            }
            else return result;

            foreach (var b in blocks)
            {
                var ch = game.FindChannel(b.Channel);
                if (ch == null || ch.ValidHooks().Count == 0) continue;
                var p = result.FirstOrDefault(x => x.Channel == ch);
                if (p == null) { p = new Planned { Config = game, Channel = ch, AllBlocks = blocks }; result.Add(p); }
                p.Blocks.Add(b);
            }
            return result;
        }

        public RenderJob Compose(ReportData r, IEnumerable<Block> blocks, IEnumerable<Block> all)
        {
            var job = new RenderJob { Title = r.Boss, Success = r.Success };
            var zone = r.Zone;
            job.Subtitle = string.Join("  ·  ", new[] { Calc.DiffName(r.Diff), zone }.Where(s => !string.IsNullOrEmpty(s)));
            job.Info = string.Format("{0}  ·  {1}  ·  {2:dd.MM.yyyy HH:mm}", Calc.Result(r), r.DurationText, r.EndTime);
            job.Footer = string.Join("  ·  ", new[] { "SkadaDiscord v" + AppInfo.Short, r.Player, r.Guild, r.Realm }.Where(s => !string.IsNullOrEmpty(s)));
            foreach (var b in blocks) job.Blocks.Add(Calc.Build(r, b, all));

            double dmg = r.Players.Sum(p => p.Damage), heal = r.Players.Sum(p => p.Heal + p.Absorb), deaths = r.Players.Sum(p => p.Deaths);
            job.Summary.Add(new[] { "Длительность", r.DurationText });
            if (r.Attempt > 0) job.Summary.Add(new[] { "Попытка", r.Attempt.ToString() });
            job.Summary.Add(new[] { "Урон рейда", string.Format("{0} ({1})", Calc.Short(dmg), Calc.Short(dmg / r.Duration)) });
            job.Summary.Add(new[] { "Исцеление", string.Format("{0} ({1})", Calc.Short(heal), Calc.Short(heal / r.Duration)) });
            job.Summary.Add(new[] { "Смертей", Math.Round(deaths).ToString(CultureInfo.InvariantCulture) });
            job.Summary.Add(new[] { "Игроков", r.Players.Count.ToString() });
            return job;
        }

        static float ScaleOf(GameConfig game)
        {
            return (float)Math.Max(0.5, Math.Min(3, game != null ? game.Scale : 1.5));
        }

        public byte[] RenderImage(ReportData r, Planned p)
        {
            return Renderer.Render(Compose(r, p.Blocks, p.AllBlocks), FontPath, IconsPath, ScaleOf(p.Config));
        }

        // текст для поиска Discord: "Урон: Имя 20.0K, Имя 18.4K | Исцеление и поглощения: ..."
        // ровно те строки, что нарисованы на картинке (с учётом «строк» и фильтра лекарей)
        public static string SearchText(RenderJob job)
        {
            return string.Join("  |  ", job.Blocks.Where(b => b.Entries.Count > 0).Select(b =>
                b.Title + ": " + string.Join(", ", b.Entries.Select(e => e.Name + " " + (b.Rate ? Calc.Short(e.PerSec) : e.Text)))));
        }

        // текст сообщения: embed (заголовок, итог, время) и текст для поиска (search: alt | footer | spoiler)
        public static void Message(ReportData r, RenderJob job, string search, string titlePrefix, out string content, out string embed, out string alt)
        {
            var desc = string.Format("**{0}** за **{1}**", Calc.Result(r), r.DurationText);
            if (!string.IsNullOrEmpty(r.Zone)) desc += "  ·  " + r.Zone;
            var title = titlePrefix + r.Boss + (string.IsNullOrEmpty(r.Diff) ? "" : " — " + Calc.DiffName(r.Diff));
            var footer = string.Join(" · ", new[] { r.Player, r.Realm }.Where(s => !string.IsNullOrEmpty(s)));
            var text = SearchText(job);
            content = null;
            alt = null;
            search = search ?? "";
            if (search.Contains("footer")) footer = text;
            if (search.Contains("alt")) alt = text;
            if (search.Contains("spoiler")) content = "||" + Discord.Cut(text, 1990) + "||";
            embed = Discord.Embed(title, desc, r.Success ? 0x2ECC71 : 0xE74C3C, true, footer, r.EndTime);
        }

        public void SendJob(string webhook, GameConfig game, ReportData r, RenderJob job, byte[] png, string search, string titlePrefix)
        {
            string content, embed, alt;
            Message(r, job, search, titlePrefix, out content, out embed, out alt);
            Discord.Send(webhook, game != null ? game.Username : "Skada", content, embed, png, alt);
        }

        // проверка: какой вариант текста находит поиск Discord (по сообщению на вариант, во все ссылки канала)
        public void SendSearchTest(GameConfig game, Channel ch, ReportData r)
        {
            Instance inst; Boss boss;
            game.Find(r.Instance, r.Boss, out inst, out boss);
            var blocks = AppConfig.DefaultBlocks(inst != null ? inst.Name : r.Instance, r.Boss, "", "");
            var job = Compose(r, blocks, blocks);
            var png = Renderer.Render(job, FontPath, IconsPath, ScaleOf(game));
            foreach (var hook in ch.ValidHooks())
            {
                SendJob(hook, game, r, job, png, "alt", "[Тест 1: скрытая подпись] ");
                SendJob(hook, game, r, job, png, "footer", "[Тест 2: строка внизу] ");
                SendJob(hook, game, r, job, png, "spoiler", "[Тест 3: спойлер] ");
            }
        }

        // ---------------------------------------------------------------------
        // кэш отчётов (для предпросмотра и проверок)

        static string Safe(string s)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }

        void Cache(ReportData r)
        {
            try
            {
                var path = Path.Combine(ReportsDir, string.Format("{0}_{1}_{2}.json", r.Start, Safe(r.Boss), Safe(r.Diff)));
                if (!File.Exists(path)) File.WriteAllText(path, r.Raw, new UTF8Encoding(false));
            }
            catch { }
        }

        // последние отчёты по боссу (новые первыми)
        public List<ReportData> CachedReports(string boss, int max)
        {
            var list = new List<ReportData>();
            if (!Directory.Exists(ReportsDir)) return list;
            var files = Directory.GetFiles(ReportsDir, "*.json")
                .Where(f => boss == null || Path.GetFileName(f).Contains("_" + Safe(boss) + "_"))
                .OrderByDescending(f => Path.GetFileName(f));
            foreach (var f in files)
            {
                try { list.Add(ReportData.Parse(File.ReadAllText(f, Encoding.UTF8))); } catch { }
                if (list.Count >= max) break;
            }
            return list;
        }
    }
}
