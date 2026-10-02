// Отчёт из аддона (JSON версии 2) и расчёт окон для картинки.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SkadaDiscord
{
    public class PlayerData
    {
        public string Name, Class, Color;
        public string Role = "";  // HEALER | DAMAGER | TANK | NONE (из аддона, если известна)
        public int Spec;
        public double Time, Damage, Overkill, Heal, Absorb, Overheal, DamageTaken;
        public double Deaths, Interrupts, Dispels, Potions, Fails, FriendFire, Sunder;
        public Dictionary<string, double> Targets = new Dictionary<string, double>();
    }

    public class ReportData
    {
        public int Version;
        public string Id, Key, Boss, Instance, Zone, Diff, DurationText, Player, Realm, Guild, AddonVersion, Raw;
        public string Lockout = "", Leader = ""; // ID сохранения рейда и рейд-лидер (аддон 2.3+)
        public bool? Db;                          // отправлять ли в базу данных (нет значения - да)
        public bool Manual, Success;
        public long Start;
        public double Duration;
        public int Attempt;
        public List<PlayerData> Players = new List<PlayerData>();
        public List<string> BossTargets = new List<string>();
        public List<string> Enemies = new List<string>();

        public DateTime EndTime
        {
            get { return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(Start + Duration).ToLocalTime(); }
        }

        public static ReportData Parse(string json)
        {
            var o = Json.Parse(json);
            var r = new ReportData { Raw = json };
            r.Version = (int)Json.Num(o, "v", 1);
            r.Id = Json.Str(o, "id", "");
            r.Key = Json.Str(o, "key", r.Id);
            r.Boss = Json.Str(o, "boss", "?");
            r.Instance = Json.Str(o, "instance", "");
            r.Zone = Json.Str(o, "zone", "");
            r.Diff = Json.Str(o, "diff", "");
            r.DurationText = Json.Str(o, "durationText", "");
            r.Player = Json.Str(o, "player", "");
            r.Realm = Json.Str(o, "realm", "");
            r.Guild = Json.Str(o, "guild", "");
            r.AddonVersion = Json.Str(o, "addonVersion", "");
            r.Manual = Json.Bool(o, "manual", false);
            r.Success = Json.Bool(o, "success", true);
            r.Start = (long)Json.Num(o, "start", 0);
            r.Duration = Math.Max(1, Json.Num(o, "duration", 1));
            r.Attempt = (int)Json.Num(o, "attempt", 0);
            r.Lockout = Json.Str(o, "lockout", "").Trim();
            r.Leader = Json.Str(o, "leader", "").Trim();
            if (Json.Has(o, "db")) r.Db = Json.Bool(o, "db", true);

            foreach (var p in Json.Arr(o, "players"))
            {
                var pd = new PlayerData {
                    Name = Json.Str(p, "n", "?"), Class = Json.Str(p, "c", "UNKNOWN"), Color = Json.Str(p, "col", "ffffff"),
                    Spec = (int)Json.Num(p, "s", 0), Time = Math.Max(1, Json.Num(p, "t", r.Duration)),
                    Damage = Json.Num(p, "dmg", 0), Overkill = Json.Num(p, "ok", 0),
                    Heal = Json.Num(p, "heal", 0), Absorb = Json.Num(p, "abs", 0), Overheal = Json.Num(p, "oh", 0),
                    DamageTaken = Json.Num(p, "dt", 0), Deaths = Json.Num(p, "death", 0), Interrupts = Json.Num(p, "intr", 0),
                    Dispels = Json.Num(p, "disp", 0), Potions = Json.Num(p, "pot", 0), Fails = Json.Num(p, "fail", 0),
                    FriendFire = Json.Num(p, "ff", 0), Sunder = Json.Num(p, "sun", 0), Role = Json.Str(p, "r", ""),
                };
                var tg = Json.Obj(p, "tg");
                if (tg != null)
                    foreach (var kv in tg) pd.Targets[kv.Key] = Convert.ToDouble(kv.Value, CultureInfo.InvariantCulture);
                r.Players.Add(pd);
            }
            foreach (var e in Json.Arr(o, "enemies"))
            {
                var name = Json.Str(e, "n", "");
                if (name == "") continue;
                if (!r.Enemies.Contains(name)) r.Enemies.Add(name);
                if (Json.Bool(e, "b", false) && !r.BossTargets.Contains(name)) r.BossTargets.Add(name);
            }
            // запасной вариант: цель с именем босса
            if (r.BossTargets.Count == 0 && r.Enemies.Contains(r.Boss)) r.BossTargets.Add(r.Boss);
            return r;
        }

        // один и тот же бой у всех игроков рейда (база данных и защита от дублей):
        // "L:<ID сохранения>|босс|сложность", без ID - "R:<рейд-лидер>|босс|сложность|<дата убийства UTC>"
        // вайпы (отправляются только вручную, в базу не идут) - каждый свой: "...|wipe:<начало боя>"
        public string FightKey()
        {
            string key;
            if (!string.IsNullOrEmpty(Lockout)) key = "L:" + Lockout + "|" + Boss + "|" + Diff;
            else
            {
                var day = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(Start + Duration).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                var leader = string.IsNullOrEmpty(Leader) ? Player : Leader; // отчёты до 2.3: без лидера - свой ник (без общих дублей)
                key = "R:" + leader + "|" + Boss + "|" + Diff + "|" + day;
            }
            return Success ? key : key + "|wipe:" + Start.ToString(CultureInfo.InvariantCulture);
        }

        // все цели, по которым кто-то бил
        public List<string> AllTargets()
        {
            var set = new List<string>(Enemies);
            foreach (var p in Players)
                foreach (var t in p.Targets.Keys)
                    if (!set.Contains(t)) set.Add(t);
            return set;
        }
    }

    // ---------------------------------------------------------------------
    // расчёт одного окна

    public static class Calc
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // как Skada (формат чисел 1): 1.23M, 12.3K
        public static string Short(double n)
        {
            double a = Math.Abs(n);
            if (a >= 1e9) return (n * 1e-9).ToString("0.00", Inv) + "B";
            if (a >= 1e6) return (n * 1e-6).ToString("0.00", Inv) + "M";
            if (a >= 1e3) return (n * 1e-3).ToString("0.0", Inv) + "K";
            return Math.Round(n).ToString(Inv);
        }

        public static string Pct(double v, double total)
        {
            return (100 * v / Math.Max(1, total)).ToString("0.0", Inv) + "%";
        }

        static double SumTargets(PlayerData p, Func<string, bool> filter)
        {
            double s = 0;
            foreach (var kv in p.Targets) if (filter(kv.Key)) s += kv.Value;
            return s;
        }

        static double Value(string metric, PlayerData p, ReportData r, Block block, HashSet<string> named)
        {
            switch (metric)
            {
                case "damage": return p.Damage;
                case "damage_boss": return SumTargets(p, t => r.BossTargets.Contains(t));
                case "damage_targets": return SumTargets(p, t => block.Targets.Contains(t));
                case "damage_adds": return SumTargets(p, t => !r.BossTargets.Contains(t) && !named.Contains(t));
                case "damage_useful": return Math.Max(0, p.Damage - p.Overkill);
                case "healing": return p.Heal + p.Absorb;
                case "heal": return p.Heal;
                case "absorb": return p.Absorb;
                case "overheal": return p.Overheal;
                case "damage_taken": return p.DamageTaken;
                case "friendfire": return p.FriendFire;
                case "deaths": return p.Deaths;
                case "interrupts": return p.Interrupts;
                case "dispels": return p.Dispels;
                case "potions": return p.Potions;
                case "fails": return p.Fails;
                case "sunder": return p.Sunder;
                case "activity": return Math.Min(100, 100 * p.Time / r.Duration);
            }
            return 0;
        }

        // специализации лекарей: ХПал, Рдру, ДЦ, ХПриест, Ршам
        static readonly HashSet<int> HealerSpecs = new HashSet<int> { 65, 105, 256, 257, 264 };

        // лекарь: по специализации, а если она неизвестна - по роли из аддона
        public static bool IsHealer(PlayerData p)
        {
            if (HealerSpecs.Contains(p.Spec)) return true;
            return p.Spec == 0 && string.Equals(p.Role, "HEALER", StringComparison.OrdinalIgnoreCase);
        }

        public static BlockView Build(ReportData r, Block block, IEnumerable<Block> siblings)
        {
            var m = Metrics.Get(block.Metric);
            bool healersOnly = Metrics.IsHeal(m.Id) && (block.Healers ?? true);
            // цели, выбранные в окнах "урон по целям" этого босса, не считаются трешем
            var named = new HashSet<string>();
            foreach (var b in siblings)
                if (b.Metric == "damage_targets") foreach (var t in b.Targets) named.Add(t);

            var view = new BlockView { Title = string.IsNullOrEmpty(block.Title) ? m.Title : block.Title, Rate = m.Rate };
            var rows = new List<Entry>();
            double total = 0;
            foreach (var p in r.Players)
            {
                if (healersOnly && !IsHealer(p)) continue;
                double v = Value(m.Id, p, r, block, named);
                if (v <= 0) continue;
                total += v;
                rows.Add(new Entry { Name = p.Name, Class = p.Class, Spec = p.Spec, Color = p.Color, Value = v, PerSec = v / p.Time });
            }
            rows.Sort((a, b) => b.Value.CompareTo(a.Value));

            foreach (var e in rows)
            {
                if (m.Rate) e.Text = string.Format("{0} ({1}, {2})", Short(e.Value), Short(e.PerSec), Pct(e.Value, total));
                else if (m.Count) e.Text = Math.Round(e.Value).ToString(Inv);
                else e.Text = e.Value.ToString("0.0", Inv) + "%";
            }
            view.Entries = rows.Take(Math.Max(1, block.Rows)).ToList();
            view.Value = total;
            if (m.Rate) view.Total = total > 0 ? string.Format("{0} ({1})", Short(total), Short(total / r.Duration)) : "";
            else if (m.Count) view.Total = total > 0 ? Math.Round(total).ToString(Inv) : "";
            else view.Total = "";
            return view;
        }

        public static string DiffName(string diff)
        {
            switch (diff)
            {
                case "10n": return "10 об.";
                case "10h": return "10 гер.";
                case "25n": return "25 об.";
                case "25h": return "25 гер.";
                case "5n": return "5 об.";
                case "5h": return "5 гер.";
            }
            return diff ?? "";
        }

        public static string Result(ReportData r)
        {
            if (!r.Success) return "Вайп";
            if (r.Attempt == 1) return "Победа с первой попытки";
            if (r.Attempt > 1) return string.Format("Победа с {0}-й попытки", r.Attempt);
            return "Победа";
        }
    }
}
