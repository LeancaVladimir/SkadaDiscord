// Служебные режимы запуска (без окна):
//   --migrate                     перенести старые настройки из config.json в игру (Config.lua аддона, один раз)
//   --search-test [босс]          отправить в канал "Тест" три варианта текста для поиска
//   --db-test [босс]              отправить в базу данных последний отчёт из файла игры (заново: без учёта sent.txt и защиты от дублей)
//   --snapshot <папка>           сохранить снимки окна программы и картинки отчётов из reports\
//   --check <папка> [файл .lua]   проверка без отправки: какие сообщения ушли бы, их текст для поиска и payload
//                                 (настройки - из указанного SavedVariables, иначе из игры / config.json)
//   --check-update [download]     что видит автообновление (релиз, архив, SHA256) - в журнал, без установки;
//                                 download - ещё скачать архив во временную папку, проверить SHA256 и распаковать
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace SkadaDiscord
{
    static class Tools
    {
        public static bool Run(string[] args, string appDir)
        {
            if (args.Length == 0) return false;
            var mode = args[0];
            if (mode != "--migrate" && mode != "--search-test" && mode != "--db-test" && mode != "--snapshot" && mode != "--check" && mode != "--check-update") return false;

            var config = AppConfig.Load(Path.Combine(appDir, "config.json"));
            var engine = new Engine(appDir, config);
            try
            {
                if (mode == "--migrate")
                {
                    if (!config.HasLegacy)
                        engine.Log("Перенос: в config.json нет каналов и боссов — переносить нечего.", LogKind.Warn);
                    else
                    {
                        var id = config.WriteImport(engine.WowPath);
                        engine.Log("Перенос: настройки из config.json записаны в Config.lua аддона (id " + id + "). " +
                            "Зайдите в игру или сделайте /reload — аддон заберёт их в профиль «Перенесено из программы».", LogKind.Info);
                    }
                }
                else if (mode == "--search-test")
                {
                    var game = engine.CurrentGame();
                    if (game == null) throw new Exception("нет настроек отчётов (ни из игры, ни в config.json)");
                    var ch = game.Channels.FirstOrDefault(c => c.Name == "Тест");
                    if (ch == null || ch.ValidHooks().Count == 0) throw new Exception("нет канала «Тест» со ссылкой");
                    var r = engine.CachedReports(args.Length > 1 ? args[1] : null, 1).FirstOrDefault();
                    if (r == null) throw new Exception("нет отчётов в reports\\");
                    engine.SendSearchTest(game, ch, r);
                    engine.Log("Тест поиска отправлен в #Тест (" + r.Boss + ").", LogKind.Ok);
                }
                else if (mode == "--db-test")
                {
                    engine.ResendToDb(args.Length > 1 ? args[1] : null);
                }
                else if (mode == "--snapshot")
                {
                    var dir = args.Length > 1 ? args[1] : Path.Combine(appDir, "snapshot");
                    Directory.CreateDirectory(dir);
                    var game = engine.CurrentGame();
                    foreach (var r in engine.CachedReports(null, 10))
                    {
                        foreach (var p in engine.Plan(r, game))
                            File.WriteAllBytes(Path.Combine(dir, "msg_" + r.Start + "_" + Safe(p.Channel.Name) + ".png"), engine.RenderImage(r, p));
                        // все окна босса на одной картинке (даже без каналов)
                        if (game == null) continue;
                        Instance inst; Boss boss;
                        game.Find(r.Instance, r.Boss, out inst, out boss);
                        if (boss != null)
                            File.WriteAllBytes(Path.Combine(dir, "all_" + r.Start + ".png"),
                                Renderer.Render(engine.Compose(r, boss.Blocks, boss.Blocks), engine.FontPath, engine.IconsPath, 1f));
                    }
                    var form = new MainForm(engine, Path.Combine(appDir, "config.json"));
                    form.StartPosition = FormStartPosition.Manual;
                    form.Location = new Point(-4000, -4000);
                    form.ShowInTaskbar = false;
                    form.Show();
                    form.Snapshot(dir);
                    form.Close();
                    // окна обновления
                    var sample = "## Что нового\n- **Автообновление** программы и аддона\n- Исправлена отправка в базу данных\n\nSHA256: " + new string('0', 64);
                    Shot(new UpdateForm("Доступна версия 9.9.9", "Сейчас у вас " + AppInfo.Version + ". Обновятся программа и аддон (игра должна быть закрыта). Что нового:", Updater.MarkdownToText(sample), true), Path.Combine(dir, "update_offer.png"));
                    Shot(new UpdateForm("SkadaDiscord обновлён до " + AppInfo.Version, "Предыдущая версия: 2.3.0. Что нового:", Updater.MarkdownToText(sample), false), Path.Combine(dir, "update_done.png"));
                }
                else if (mode == "--check-update")
                {
                    CheckUpdate(engine, args.Length > 1 && args[1] == "download");
                }
                else if (mode == "--check")
                {
                    Check(engine, config, args.Length > 1 ? args[1] : Path.Combine(appDir, "check"), args.Length > 2 ? args[2] : null);
                }
            }
            catch (Exception e)
            {
                engine.Log("Ошибка (" + mode + "): " + e.Message, LogKind.Error);
            }
            return true;
        }

        static void Shot(Form f, string path)
        {
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(-4000, -4000);
            f.ShowInTaskbar = false;
            f.Show();
            Application.DoEvents();
            using (var bmp = new Bitmap(f.Width, f.Height))
            {
                f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                bmp.Save(path);
            }
            f.Close();
        }

        // что видит автообновление - без установки
        static void CheckUpdate(Engine engine, bool download)
        {
            var current = Updater.Current;
            var r = Updater.FetchLatest();
            if (r == null)
            {
                engine.Log("Проверка обновлений: версия " + Updater.VersionText(current) + ", релизов пока нет.", LogKind.Info);
                return;
            }
            var sb = new StringBuilder("Проверка обновлений: версия " + Updater.VersionText(current) + "; последний релиз " + r.Tag);
            if (r.Version == null) sb.Append(" (тег не похож на версию - пропускается)");
            else sb.Append(Updater.IsNewer(r.Version, current) ? " - новее, будет установлен" : " - не новее, ничего не делается");
            sb.Append("; архив: " + (r.AssetName == "" ? "НЕТ .zip" : r.AssetName + " (" + r.AssetSize + " байт), ссылка " +
                (Updater.IsAllowedUrl(r.AssetUrl) ? "допустима" : "НЕДОПУСТИМА: " + r.AssetUrl)));
            sb.Append("; SHA256 в описании: " + (r.Sha256 ?? "НЕТ - установка будет отклонена"));
            sb.Append("; игра запущена: " + (Updater.WowRunning(engine.Config) ? "да" : "нет"));
            sb.Append("; автообновление: " + (engine.Config.AutoUpdate ? "включено" : "выключено (спросит)"));
            sb.Append("; изменения: " + Updater.MarkdownToText(r.Body).Replace("\n", " / "));
            engine.Log(sb.ToString(), LogKind.Info);
            if (!download) return;

            var dir = Path.Combine(Path.GetTempPath(), @"SkadaDiscord-update\check");
            var zip = Updater.Download(r, dir);
            var files = Updater.ExtractZip(zip, Path.Combine(dir, "files"));
            var list = Directory.GetFiles(files, "*", SearchOption.AllDirectories).Select(f => f.Substring(files.Length).TrimStart('\\').Replace('\\', '/')).ToList();
            var exe = Path.Combine(files, @"SkadaDiscord\SkadaDiscord.exe");
            string exeVersion = "нет";
            if (File.Exists(exe))
            {
                try { exeVersion = Updater.VersionText(Updater.Normalize(System.Reflection.AssemblyName.GetAssemblyName(exe).Version)); }
                catch (Exception e) { exeVersion = "не прочитана (" + e.Message + ")"; }
            }
            engine.Log("Проверка обновлений: архив скачан, SHA256 совпадает: " + zip + "; версия exe в архиве: " + exeVersion +
                "; файлов " + list.Count + ": " + string.Join(", ", list), LogKind.Ok);
        }

        static string Safe(string s)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }

        static string MaskHooks(string s)
        {
            return Regex.Replace(s, @"(discord(app)?\.com/api/webhooks/)[^""\s]+", "$1…");
        }

        // проверка без отправки: собирает сообщения так же, как при настоящей отправке, но в Discord ничего не уходит
        static void Check(Engine engine, AppConfig config, string dir, string savedVariables)
        {
            Discord.DryRun = new List<string>(); // до любой "отправки"!
            Directory.CreateDirectory(dir);
            var sb = new StringBuilder();
            GameConfig game;
            if (savedVariables != null)
            {
                var text = Engine.ReadText(savedVariables);
                if (text == null) throw new Exception("не прочитан " + savedVariables);
                sb.AppendLine("Файл: " + savedVariables);
                sb.AppendLine("configJson: " + (Engine.ExtractConfigJson(text) != null ? "есть" : "нет") + ", отчётов в файле: " + Engine.ExtractReports(text).Count);
                game = engine.GameFromText(text, true);
            }
            else game = engine.CurrentGame();
            sb.AppendLine(game != null ? game.Describe() : "Настроек нет.");
            if (game != null)
            {
                sb.AppendLine("Имя бота: " + game.Username + ", масштаб: " + game.Scale + ", поиск: " + game.Search + ", другие рейды: " + game.Others + ", importId: " + game.ImportId);
                foreach (var ch in game.Channels)
                    sb.AppendLine("  канал " + ch.Id + " #" + ch.Name + ": ссылок " + ch.Hooks.Count + ", рабочих " + ch.ValidHooks().Count +
                        (ch.ValidHooks().Count > 0 ? " (" + string.Join(", ", ch.ValidHooks().Select(Engine.HookHash)) + ")" : ""));
            }
            sb.AppendLine();

            foreach (var r in engine.CachedReports(null, 20))
            {
                sb.AppendLine("=== " + r.Boss + " (" + Calc.DiffName(r.Diff) + ")  " + r.Id + (r.AddonVersion != "" ? "  аддон " + r.AddonVersion : ""));
                var plan = engine.Plan(r, game);
                if (plan.Count == 0) sb.AppendLine("  не отправляется (босс выключен или окнам не выбран канал)");
                foreach (var p in plan)
                {
                    var job = engine.Compose(r, p.Blocks, p.AllBlocks);
                    var png = engine.RenderImage(r, p);
                    var name = "check_" + r.Start + "_" + Safe(p.Channel.Name) + ".png";
                    File.WriteAllBytes(Path.Combine(dir, name), png);
                    sb.AppendLine("  → #" + p.Channel.Name + "  (картинка " + name + ")");
                    for (int i = 0; i < p.Blocks.Count; i++)
                    {
                        var b = p.Blocks[i];
                        var v = job.Blocks[i];
                        sb.AppendLine(string.Format("    окно «{0}» ({1}, строк {2}{3}): на картинке {4}: {5}", v.Title, b.Metric, b.Rows,
                            Metrics.IsHeal(b.Metric) ? ", только лекари: " + ((b.Healers ?? true) ? "да" : "нет") : "",
                            v.Entries.Count, string.Join(", ", v.Entries.Select(e => e.Name))));
                    }
                    var search = Engine.SearchText(job);
                    int names = job.Blocks.Sum(b => b.Entries.Count);
                    sb.AppendLine("    текст для поиска (" + names + " имён): " + search);
                    var hooks = engine.PendingHooks(r, p.Channel, p.Channel.ValidHooks());
                    sb.AppendLine("    ещё не отправлено в ссылки: " + (hooks.Count == 0 ? "— (уже отправлено)" : string.Join(", ", hooks.Select(Engine.HookHash))));
                    int before = Discord.DryRun.Count;
                    foreach (var hook in p.Channel.ValidHooks())
                        engine.SendJob(hook, game, r, job, png, game.Search, null);
                    foreach (var payload in Discord.DryRun.Skip(before))
                        sb.AppendLine("    payload " + payload.Replace("\n", "\n      "));
                    sb.AppendLine("    ключи защиты от дублей (ссылка 1): " + string.Join("; ", p.Blocks.Select(b => Engine.ClaimKey(p.Channel.ValidHooks()[0], r.FightKey(), b).Substring(19))));
                }
                sb.AppendLine("  ключ боя: " + r.FightKey());
                if (!Engine.WantsDb(r, game))
                    sb.AppendLine("  база данных: не отправляется" + (r.Success ? (string.IsNullOrEmpty(r.Leader) ? " (отчёт аддона до 2.3)" : " (выключено)") : " (вайп)"));
                else
                {
                    var dbPng = Renderer.Render(engine.DbJob(r), engine.FontPath, engine.IconsPath, 1.5f);
                    var dbName = "check_" + r.Start + "_db.png";
                    File.WriteAllBytes(Path.Combine(dir, dbName), dbPng);
                    sb.AppendLine("  база данных: картинка " + dbName + ", meta " + Engine.DbMeta(r, game).Replace("\n", "\n    "));
                }
                sb.AppendLine();
            }

            if (config.HasLegacy)
            {
                File.WriteAllText(Path.Combine(dir, "import_preview.lua"), MaskHooks(config.LuaImportText()), new UTF8Encoding(false));
                sb.AppendLine("Перенос (--migrate) запишет в Config.lua аддона таблицу с id " + config.ImportId() + " (пример без ссылок: import_preview.lua).");
            }
            File.WriteAllText(Path.Combine(dir, "check.txt"), sb.ToString(), new UTF8Encoding(true));
            engine.Log("Проверка без отправки записана: " + Path.Combine(dir, "check.txt"), LogKind.Info);
        }
    }
}
