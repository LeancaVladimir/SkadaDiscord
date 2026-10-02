// Настройки: программы (config.json) и отчётов (из игры - configJson в SavedVariables аддона).
// Старые настройки отчётов из config.json работают как запасной вариант и переносятся в игру (--migrate).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SkadaDiscord
{
    public class Channel
    {
        public string Id = Guid.NewGuid().ToString("N").Substring(0, 8);
        public string Name = "";
        public List<string> Hooks = new List<string>(); // один канал - несколько вебхуков (разные серверы)

        // рабочие ссылки без повторов
        public List<string> ValidHooks()
        {
            var list = new List<string>();
            foreach (var h in Hooks)
            {
                var url = (h ?? "").Trim();
                if (Discord.IsWebhook(url) && !list.Contains(url)) list.Add(url);
            }
            return list;
        }
    }

    public class Block
    {
        public string Metric = "damage";
        public string Title = "";
        public int Rows = 25;
        public string Channel = ""; // Channel.Id
        public List<string> Targets = new List<string>();
        public bool? Healers; // окна исцеления: только лекари (нет значения = да)

        public Block Clone()
        {
            return new Block { Metric = Metric, Title = Title, Rows = Rows, Channel = Channel, Targets = new List<string>(Targets), Healers = Healers };
        }
    }

    public class Boss
    {
        public string Name = "";
        public bool Enabled = true;
        public List<Block> Blocks = new List<Block>();
    }

    public class Instance
    {
        public string Name = "";
        public List<string> Zones = new List<string>();
        public string Trigger = "all"; // all | last
        public string LastBoss = "";
        public List<Boss> Bosses = new List<Boss>();

        public Boss FindBoss(string name)
        {
            return Bosses.FirstOrDefault(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));
        }
    }

    // доступные метрики (окна на картинке)
    public class MetricDef
    {
        public string Id { get; set; }   // свойства - для ComboBox (DisplayMember / ValueMember)
        public string Name { get; set; }
        public string Title;
        public bool Rate;       // сумма + в секунду + %
        public bool Count;      // просто число (смерти, прерывания...)
        public bool Targets;    // нужен список целей
        public int Rows;

        public override string ToString() { return Name; }
    }

    public static class Metrics
    {
        public static readonly List<MetricDef> All = new List<MetricDef> {
            new MetricDef { Id = "damage", Name = "Урон (весь)", Title = "Урон", Rate = true, Rows = 25 },
            new MetricDef { Id = "damage_boss", Name = "Урон по боссу", Title = "Урон по боссу", Rate = true, Rows = 25 },
            new MetricDef { Id = "damage_targets", Name = "Урон по выбранным целям", Title = "Урон по целям", Rate = true, Targets = true, Rows = 25 },
            new MetricDef { Id = "damage_adds", Name = "Урон по трешу (всё, кроме босса и выбранных целей)", Title = "Урон по трешу", Rate = true, Rows = 25 },
            new MetricDef { Id = "damage_useful", Name = "Полезный урон (без оверкилла)", Title = "Полезный урон", Rate = true, Rows = 25 },
            new MetricDef { Id = "healing", Name = "Исцеление + поглощения", Title = "Исцеление и поглощения", Rate = true, Rows = 4 },
            new MetricDef { Id = "heal", Name = "Только исцеление", Title = "Исцеление", Rate = true, Rows = 4 },
            new MetricDef { Id = "absorb", Name = "Только поглощения (щиты)", Title = "Поглощения", Rate = true, Rows = 4 },
            new MetricDef { Id = "overheal", Name = "Оверхил", Title = "Оверхил", Rate = true, Rows = 4 },
            new MetricDef { Id = "damage_taken", Name = "Полученный урон", Title = "Полученный урон", Rate = true, Rows = 10 },
            new MetricDef { Id = "friendfire", Name = "Урон по своим", Title = "Урон по своим", Rate = true, Rows = 10 },
            new MetricDef { Id = "deaths", Name = "Смерти", Title = "Смерти", Count = true, Rows = 10 },
            new MetricDef { Id = "interrupts", Name = "Прерывания", Title = "Прерывания", Count = true, Rows = 10 },
            new MetricDef { Id = "dispels", Name = "Диспелы", Title = "Диспелы", Count = true, Rows = 10 },
            new MetricDef { Id = "potions", Name = "Зелья", Title = "Зелья", Count = true, Rows = 25 },
            new MetricDef { Id = "fails", Name = "Ошибки (Fails)", Title = "Ошибки", Count = true, Rows = 10 },
            new MetricDef { Id = "sunder", Name = "Раскол брони", Title = "Раскол брони", Count = true, Rows = 10 },
            new MetricDef { Id = "activity", Name = "Активность (% времени боя)", Title = "Активность", Rows = 25 },
        };

        public static MetricDef Get(string id)
        {
            return All.FirstOrDefault(m => m.Id == id) ?? All[0];
        }

        public static Block NewBlock(string id, string channel)
        {
            var m = Get(id);
            return new Block { Metric = m.Id, Title = m.Title, Rows = m.Rows, Channel = channel ?? "" };
        }

        // окна исцеления (для них действует фильтр «только лекари»)
        public static bool IsHeal(string id)
        {
            return id == "healing" || id == "heal" || id == "absorb" || id == "overheal";
        }
    }

    // ---------------------------------------------------------------------
    // настройки отчётов: каналы, инсты, боссы, окна (свои для каждого файла SavedVariables)

    public class GameConfig
    {
        public bool FromGame;           // true - из игры (configJson), false - старые из config.json
        public string Profile = "", AddonVersion = "", ImportId = "";
        public string Username = "Skada";
        public double Scale = 1.5;
        public string Search = "alt";   // текст для поиска в Discord: с 2.3 всегда alt (скрытая подпись картинки)
        public bool Others = false;
        public bool SendDB = true;      // отправлять отчёты в базу данных (общий сервер)
        public string DefaultDps = "";
        public string DefaultHps = "";
        public List<Channel> Channels = new List<Channel>();
        public List<Instance> Instances = new List<Instance>();

        public Channel FindChannel(string id)
        {
            return Channels.FirstOrDefault(c => c.Id == id);
        }

        // инст и босс для отчёта: сначала по названию инста из аддона, потом по имени босса
        public void Find(string instance, string boss, out Instance inst, out Boss b)
        {
            inst = null; b = null;
            foreach (var i in Instances)
            {
                var found = i.FindBoss(boss);
                if (found != null && (string.IsNullOrEmpty(instance) || i.Name == instance || inst == null))
                {
                    inst = i; b = found;
                    if (i.Name == instance) return;
                }
            }
        }

        public string Describe()
        {
            int hooks = Channels.Sum(c => c.ValidHooks().Count);
            var src = FromGame
                ? "из игры" + (Profile != "" ? ", профиль «" + Profile + "»" : "") + (AddonVersion != "" ? ", аддон " + AddonVersion : "")
                : "из config.json (старые, до переноса в игру)";
            return string.Format("Настройки {0}: каналов {1}, ссылок {2}, инстов {3}, база данных: {4}.", src, Channels.Count, hooks, Instances.Count, SendDB ? "да" : "нет");
        }

        // configJson из аддона
        public static GameConfig FromJson(string text)
        {
            var root = Json.Parse(text) as Dictionary<string, object>;
            if (root == null) throw new FormatException("configJson - не объект");
            var g = new GameConfig { FromGame = true };
            g.Profile = Json.Str(root, "profile", "");
            g.AddonVersion = Json.Str(root, "addonVersion", "");
            g.ImportId = Json.Str(root, "importId", "");
            g.Username = Json.Str(root, "username", "Skada");
            if (string.IsNullOrEmpty(g.Username)) g.Username = "Skada";
            g.Scale = Json.Num(root, "scale", 1.5);
            g.Search = "alt"; // "search" из игры больше не учитывается
            g.Others = Json.Bool(root, "others", false);
            g.SendDB = Json.Bool(root, "sendDB", true);
            g.DefaultDps = Json.Str(root, "defaultDps", "");
            g.DefaultHps = Json.Str(root, "defaultHps", "");
            foreach (var o in Json.Arr(root, "channels")) g.Channels.Add(ParseChannel(o));
            foreach (var io in Json.Arr(root, "instances")) g.Instances.Add(ParseInstance(io));
            return g;
        }

        // канал: hooks [..] (новый формат) или webhook "..." (старый config.json)
        public static Channel ParseChannel(object o)
        {
            var ch = new Channel { Id = Json.Str(o, "id", Guid.NewGuid().ToString("N").Substring(0, 8)), Name = Json.Str(o, "name", "") };
            foreach (var h in Json.Arr(o, "hooks"))
            {
                var url = Convert.ToString(h, CultureInfo.InvariantCulture);
                if (!string.IsNullOrEmpty(url)) ch.Hooks.Add(url.Trim());
            }
            var single = Json.Str(o, "webhook", "");
            if (single.Trim() != "" && !ch.Hooks.Contains(single.Trim())) ch.Hooks.Add(single.Trim());
            return ch;
        }

        public static Instance ParseInstance(object io)
        {
            var inst = new Instance { Name = Json.Str(io, "name", ""), Trigger = Json.Str(io, "trigger", "all"), LastBoss = Json.Str(io, "lastBoss", Json.Str(io, "last", "")) };
            foreach (var z in Json.Arr(io, "zones")) inst.Zones.Add(Convert.ToString(z));
            foreach (var bo in Json.Arr(io, "bosses"))
            {
                var boss = new Boss { Name = Json.Str(bo, "name", ""), Enabled = Json.Bool(bo, "enabled", true) };
                foreach (var blo in Json.Arr(bo, "blocks"))
                {
                    var bl = new Block {
                        Metric = Json.Str(blo, "metric", "damage"),
                        Title = Json.Str(blo, "title", ""),
                        Rows = (int)Json.Num(blo, "rows", 25),
                        Channel = Json.Str(blo, "channel", ""),
                    };
                    if (Json.Has(blo, "healers")) bl.Healers = Json.Bool(blo, "healers", true);
                    foreach (var t in Json.Arr(blo, "targets")) bl.Targets.Add(Convert.ToString(t));
                    boss.Blocks.Add(bl);
                }
                inst.Bosses.Add(boss);
            }
            return inst;
        }
    }

    // ---------------------------------------------------------------------
    // config.json: настройки самой программы (+ старые настройки отчётов, если они там остались)

    public class AppConfig
    {
        public string WowPath = "";
        public string WowExe = "Wow.exe";
        public int PollSeconds = 2;
        public bool ExitWithGame = true;
        public bool AutoUpdate = true; // ставить новые версии с GitHub самой (false - спрашивать)

        // старые настройки отчётов (до 2.2 они редактировались в программе) - запасной вариант и перенос в игру
        public string Username = "Skada";
        public double Scale = 1.5;
        public int TimerSeconds = 300;
        public bool TimerSound = true;
        public bool Others = false;
        public string Search = "alt";
        public string DefaultDps = "";
        public string DefaultHps = "";
        public List<Channel> Channels = new List<Channel>();
        public List<Instance> Instances = new List<Instance>();

        // есть ли что переносить / чем подменить отсутствующие настройки из игры
        public bool HasLegacy
        {
            get { return Channels.Count > 0 && Instances.Count > 0; }
        }

        public GameConfig Legacy()
        {
            return new GameConfig {
                FromGame = false, Username = string.IsNullOrEmpty(Username) ? "Skada" : Username, Scale = Scale, Search = "alt", Others = Others,
                DefaultDps = DefaultDps, DefaultHps = DefaultHps, Channels = Channels, Instances = Instances,
            };
        }

        // ---------------------------------------------------------------------
        // стандартные настройки (для перевода самого старого config.json с одним вебхуком)

        public static readonly string[] IccBosses = {
            "Лорд Ребрад", "Леди Смертный Шепот", "Бой на кораблях", "Саурфанг Смертоносный",
            "Тухлопуз", "Гниломорд", "Профессор Мерзоцид", "Совет Принцев Крови",
            "Кровавая королева Лана'тель", "Валитрия Сноходица", "Синдрагоса", "Король-лич"
        };
        public static readonly string[] RsBosses = {
            "Балтарус Рожденный в Битве", "Савиана Огненная Пропасть", "Генерал Заритриан", "Халион"
        };

        public static List<Block> DefaultBlocks(string instance, string boss, string dps, string hps)
        {
            var list = new List<Block>();
            if (instance == "РС")
            {
                list.Add(Metrics.NewBlock("damage_boss", dps));
                if (boss == "Халион")
                {
                    var big = Metrics.NewBlock("damage_targets", dps);
                    big.Title = "Урон по большому пламени";
                    big.Targets.Add("Живое адское пламя");
                    list.Add(big);
                }
                list.Add(Metrics.NewBlock("damage_adds", dps));
            }
            else
            {
                list.Add(Metrics.NewBlock("damage", dps));
            }
            var heal = Metrics.NewBlock("healing", hps);
            if (boss == "Валитрия Сноходица") heal.Rows = 10;
            list.Add(heal);
            return list;
        }

        public static Instance DefaultInstance(string name, string zone, string[] bosses, string last, string dps, string hps)
        {
            var inst = new Instance { Name = name, Trigger = "all", LastBoss = last };
            inst.Zones.Add(zone);
            foreach (var b in bosses)
                inst.Bosses.Add(new Boss { Name = b, Enabled = true, Blocks = DefaultBlocks(name, b, dps, hps) });
            return inst;
        }

        public static AppConfig CreateDefault(string legacyWebhook)
        {
            var c = new AppConfig();
            if (string.IsNullOrEmpty(legacyWebhook)) return c; // настройки отчётов теперь в игре
            var ch = new Channel { Name = "Основной" };
            ch.Hooks.Add(legacyWebhook.Trim());
            c.Channels.Add(ch);
            c.DefaultDps = ch.Id;
            c.DefaultHps = ch.Id;
            c.Instances.Add(DefaultInstance("ЦЛК", "Цитадель Ледяной Короны", IccBosses, "Король-лич", ch.Id, ch.Id));
            c.Instances.Add(DefaultInstance("РС", "Рубиновое святилище", RsBosses, "Халион", ch.Id, ch.Id));
            return c;
        }

        // ---------------------------------------------------------------------
        // загрузка / сохранение

        public static AppConfig Load(string path)
        {
            if (!File.Exists(path)) return CreateDefault(null);
            return FromJson(File.ReadAllText(path, Encoding.UTF8));
        }

        public AppConfig Clone() { return FromJson(ToJson()); }

        public static AppConfig FromJson(string text)
        {
            var root = Json.Parse(text);
            if (Json.Num(root, "version", 1) < 2)
                return CreateDefault(Json.Str(root, "webhook", "")); // самый старый config.json: один вебхук

            var c = new AppConfig();
            c.WowPath = Json.Str(root, "wowPath", "");
            c.WowExe = Json.Str(root, "wowExe", "Wow.exe");
            c.PollSeconds = (int)Json.Num(root, "pollSeconds", 2);
            c.ExitWithGame = Json.Bool(root, "exitWithGame", true);
            c.AutoUpdate = Json.Bool(root, "autoUpdate", true);
            c.Username = Json.Str(root, "username", "Skada");
            c.Scale = Json.Num(root, "scale", 1.5);
            c.TimerSeconds = (int)Json.Num(root, "timerSeconds", 300);
            c.TimerSound = Json.Bool(root, "timerSound", true);
            c.Others = Json.Bool(root, "others", false);
            c.Search = Json.Str(root, "search", "alt");
            c.DefaultDps = Json.Str(root, "defaultDps", "");
            c.DefaultHps = Json.Str(root, "defaultHps", "");
            foreach (var o in Json.Arr(root, "channels")) c.Channels.Add(GameConfig.ParseChannel(o));
            foreach (var io in Json.Arr(root, "instances")) c.Instances.Add(GameConfig.ParseInstance(io));
            return c;
        }

        public void Save(string path)
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, ToJson(), new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        public string ToJson()
        {
            var d = new OrderedMap();
            d.Add("version", 3);
            d.Add("wowPath", WowPath);
            d.Add("wowExe", WowExe);
            d.Add("pollSeconds", PollSeconds);
            d.Add("exitWithGame", ExitWithGame);
            d.Add("autoUpdate", AutoUpdate);
            if (Channels.Count > 0 || Instances.Count > 0)
            {
                // старые настройки отчётов храним, пока они не перенесены в игру (запасной вариант)
                d.Add("username", Username);
                d.Add("scale", Scale);
                d.Add("timerSeconds", TimerSeconds);
                d.Add("timerSound", TimerSound);
                d.Add("others", Others);
                d.Add("search", Search);
                d.Add("defaultDps", DefaultDps);
                d.Add("defaultHps", DefaultHps);
                d.Add("channels", Channels.Select(ch => (object)new OrderedMap { { "id", ch.Id }, { "name", ch.Name }, { "hooks", ch.Hooks.Cast<object>().ToList() } }).ToList());
                d.Add("instances", Instances.Select(i => (object)new OrderedMap {
                    { "name", i.Name }, { "zones", i.Zones.Cast<object>().ToList() }, { "trigger", i.Trigger }, { "lastBoss", i.LastBoss },
                    { "bosses", i.Bosses.Select(b => (object)new OrderedMap {
                        { "name", b.Name }, { "enabled", b.Enabled },
                        { "blocks", b.Blocks.Select(bl => BlockJson(bl)).ToList() } }).ToList() } }).ToList());
            }
            return Json.Write(d);
        }

        static object BlockJson(Block bl)
        {
            var m = new OrderedMap { { "metric", bl.Metric }, { "title", bl.Title }, { "rows", bl.Rows }, { "channel", bl.Channel }, { "targets", bl.Targets.Cast<object>().ToList() } };
            if (bl.Healers.HasValue) m.Add("healers", bl.Healers.Value);
            return m;
        }

        // ---------------------------------------------------------------------
        // перенос старых настроек в игру: Config.lua аддона с таблицей SkadaDiscordImport (аддон забирает её один раз)

        const string ImportName = "Перенесено из программы";

        public const string ConfigLuaEmpty =
            "-- SkadaDiscord: через этот файл программа один раз передаёт аддону свои старые настройки.\n" +
            "-- Сейчас он пуст: каналы, боссы и окна отчёта настраиваются в игре (/sd → «Настройки»).\n";

        const string ConfigLuaHeader =
            "-- SkadaDiscord: настройки, перенесённые из программы (config.json).\n" +
            "-- Аддон один раз заберёт их в профиль «" + ImportName + "» (при входе в игру или /reload),\n" +
            "-- после чего программа очистит этот файл, чтобы ссылки на вебхуки не лежали в папке аддона.\n";

        static string Lua(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char ch in s ?? "")
            {
                if (ch == '"' || ch == '\\') sb.Append('\\').Append(ch);
                else if (ch == '\n') sb.Append("\\n");
                else if (ch == '\r') { }
                else sb.Append(ch);
            }
            return sb.Append('"').ToString();
        }

        static string LuaList(IEnumerable<string> items)
        {
            return "{" + string.Join(", ", items.Select(Lua)) + "}";
        }

        static string LuaBool(bool b) { return b ? "true" : "false"; }

        // таблица профиля в формате аддона (Profile.lua: DefaultProfile)
        public string LuaProfile()
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.AppendFormat(inv, "\t\ttimer = {0},\n", TimerSeconds);
            sb.AppendFormat("\t\tsound = {0},\n", LuaBool(TimerSound));
            sb.AppendFormat("\t\tothers = {0},\n", LuaBool(Others));
            sb.AppendFormat("\t\tsearch = {0},\n", Lua(Search));
            sb.AppendFormat("\t\tusername = {0},\n", Lua(Username));
            sb.AppendFormat("\t\tscale = {0},\n", Scale.ToString("R", inv));
            sb.AppendFormat("\t\tdefaultDps = {0},\n", Lua(DefaultDps));
            sb.AppendFormat("\t\tdefaultHps = {0},\n", Lua(DefaultHps));
            sb.Append("\t\tchannels = {\n");
            foreach (var ch in Channels)
                sb.AppendFormat("\t\t\t{{id = {0}, name = {1}, hooks = {2}}},\n", Lua(ch.Id), Lua(ch.Name), LuaList(ch.Hooks.Where(h => !string.IsNullOrEmpty(h))));
            sb.Append("\t\t},\n");
            sb.Append("\t\tinstances = {\n");
            foreach (var i in Instances)
            {
                sb.Append("\t\t\t{\n");
                sb.AppendFormat("\t\t\t\tname = {0},\n", Lua(i.Name));
                sb.AppendFormat("\t\t\t\tzones = {0},\n", LuaList(i.Zones));
                sb.AppendFormat("\t\t\t\ttrigger = {0},\n", Lua(i.Trigger == "last" ? "last" : "all"));
                sb.AppendFormat("\t\t\t\tlast = {0},\n", Lua(i.LastBoss));
                sb.Append("\t\t\t\tbosses = {\n");
                foreach (var b in i.Bosses)
                {
                    sb.AppendFormat("\t\t\t\t\t{{name = {0}, enabled = {1}, blocks = {{\n", Lua(b.Name), LuaBool(b.Enabled));
                    foreach (var bl in b.Blocks)
                    {
                        sb.AppendFormat(inv, "\t\t\t\t\t\t{{metric = {0}, title = {1}, rows = {2}, channel = {3}, targets = {4}",
                            Lua(bl.Metric), Lua(bl.Title), bl.Rows, Lua(bl.Channel), LuaList(bl.Targets));
                        if (bl.Healers.HasValue) sb.AppendFormat(", healers = {0}", LuaBool(bl.Healers.Value));
                        sb.Append("},\n");
                    }
                    sb.Append("\t\t\t\t\t}},\n");
                }
                sb.Append("\t\t\t\t},\n\t\t\t},\n");
            }
            sb.Append("\t\t},\n\t}");
            return sb.ToString();
        }

        // одинаковые настройки - одинаковый id (аддон не импортирует одно и то же дважды)
        public static string Hash(string text, int length)
        {
            using (var sha = SHA1.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? ""));
                var sb = new StringBuilder();
                foreach (var b in bytes) sb.Append(b.ToString("x2"));
                return sb.ToString().Substring(0, length);
            }
        }

        public string ImportId()
        {
            return Hash(LuaProfile(), 12);
        }

        public string LuaImportText()
        {
            var profile = LuaProfile();
            var sb = new StringBuilder();
            sb.Append("SkadaDiscordImport = {\n");
            sb.AppendFormat("\tid = {0},\n", Lua(Hash(profile, 12)));
            sb.AppendFormat("\tname = {0},\n", Lua(ImportName));
            sb.Append("\tprofile = ").Append(profile).Append(",\n");
            sb.Append("}\n");
            return sb.ToString();
        }

        public static string AddonConfigPath(string wowPath)
        {
            return Path.Combine(wowPath, @"Interface\AddOns\SkadaDiscord\Config.lua");
        }

        // записать перенос в Config.lua аддона; возвращает id переноса
        public string WriteImport(string wowPath)
        {
            var path = AddonConfigPath(wowPath);
            var dir = Path.GetDirectoryName(path);
            if (!Directory.Exists(dir)) throw new DirectoryNotFoundException("не найден аддон: " + dir);
            File.WriteAllText(path, ConfigLuaHeader + LuaImportText(), new UTF8Encoding(false));
            return ImportId();
        }

        // id переноса, который сейчас лежит в Config.lua (или null)
        public static string PendingImportId(string wowPath)
        {
            try
            {
                var path = AddonConfigPath(wowPath);
                if (!File.Exists(path)) return null;
                var m = Regex.Match(File.ReadAllText(path, Encoding.UTF8), "SkadaDiscordImport\\s*=\\s*\\{\\s*id\\s*=\\s*\"([^\"]*)\"");
                return m.Success ? m.Groups[1].Value : null;
            }
            catch { return null; }
        }

        // аддон забрал перенос (его importId совпал) - убираем ссылки из папки аддона
        public static bool ClearImport(string wowPath, string importId)
        {
            if (string.IsNullOrEmpty(importId)) return false;
            var pending = PendingImportId(wowPath);
            if (pending == null || pending != importId) return false;
            File.WriteAllText(AddonConfigPath(wowPath), ConfigLuaEmpty, new UTF8Encoding(false));
            return true;
        }
    }

    // словарь, который помнит порядок ключей (для читаемого config.json)
    public class OrderedMap : System.Collections.Specialized.OrderedDictionary
    {
        public void Add(string key, object value) { base.Add(key, value); }
    }
}
