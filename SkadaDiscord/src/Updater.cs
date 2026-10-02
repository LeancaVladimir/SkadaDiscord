// Автообновление: релизы на GitHub (LeancaVladimir/SkadaDiscord), скачивание архива, проверка SHA256,
// установка аддона и замена SkadaDiscord.exe (старый файл -> SkadaDiscord.old.exe, перезапуск с --updated).
// Ничего не ставится, пока запущена игра.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SkadaDiscord
{
    // релиз на GitHub
    public class ReleaseInfo
    {
        public string Tag = "";
        public Version Version;          // null - тег не похож на версию (такой релиз пропускается)
        public string Body = "";         // список изменений (Markdown)
        public string Sha256;            // из строки "SHA256: ..." в описании (null - нет строки)
        public string AssetName = "", AssetUrl = "";
        public long AssetSize;
    }

    // обновление отклонено (контрольная сумма, ссылка, содержимое архива): эту версию больше не пробуем до перезапуска
    public class UpdateRejected : Exception
    {
        public UpdateRejected(string message) : base(message) { }
    }

    // установленные файлы (для отката): что заменено и где лежит прежняя копия
    public class InstallTxn
    {
        readonly string backupDir;
        readonly List<string[]> files = new List<string[]>(); // { файл, прежняя копия или null (файла не было) }
        public string ExePath;
        public bool ExeSwapped;
        public List<string> Notes = new List<string>();      // что сделано (для журнала)

        public InstallTxn(string backupDir) { this.backupDir = backupDir; }

        public void CopyFile(string src, string dest)
        {
            string backup = null;
            if (File.Exists(dest))
            {
                backup = Path.Combine(backupDir, files.Count.ToString() + "_" + Path.GetFileName(dest));
                File.Copy(dest, backup, true);
            }
            files.Add(new[] { dest, backup }); // до копирования: откат вернёт и наполовину записанный файл
            Directory.CreateDirectory(Path.GetDirectoryName(dest));
            File.Copy(src, dest, true);
        }

        public void Rollback(Action<string> warn)
        {
            if (ExeSwapped)
            {
                try { Updater.UndoSwap(ExePath); }
                catch (Exception e) { warn("не удалось вернуть прежний " + Path.GetFileName(ExePath) + ": " + e.Message); }
                ExeSwapped = false;
            }
            for (int i = files.Count - 1; i >= 0; i--)
            {
                var dest = files[i][0];
                var backup = files[i][1];
                try
                {
                    if (backup == null) { if (File.Exists(dest)) File.Delete(dest); }
                    else File.Copy(backup, dest, true);
                }
                catch (Exception e) { warn("не удалось вернуть " + dest + ": " + e.Message); }
            }
            files.Clear();
        }
    }

    public class Updater
    {
        public const string Repo = "LeancaVladimir/SkadaDiscord";
        public const string ApiBase = "https://api.github.com/repos/" + Repo + "/releases/";
        const long MaxZip = 100L * 1024 * 1024;
        static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(30);
        static readonly HttpClient Client;
        static readonly Regex ShaLine = new Regex(@"SHA-?256\b[^\r\n]*?(?<![0-9a-fA-F])([0-9a-fA-F]{64})(?![0-9a-fA-F])", RegexOptions.IgnoreCase);

        static Updater()
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2 (GitHub)
            var handler = new HttpClientHandler { AllowAutoRedirect = true, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate };
            Client = new HttpClient(handler);
            Client.Timeout = Timeout.InfiniteTimeSpan; // время ограничивается на каждый запрос
            Client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "SkadaDiscord/" + AppInfo.Version);
        }

        // ---------------------------------------------------------------------
        // версии

        public static Version Current
        {
            get { return Normalize(Assembly.GetExecutingAssembly().GetName().Version); }
        }

        // 2.4 и 2.4.0.0 - одно и то же (у Version недостающие части = -1, и 2.4.0 < 2.4.0.0)
        public static Version Normalize(Version v)
        {
            if (v == null) return null;
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
        }

        // "v2.4.0" -> 2.4.0.0; не версия -> null
        public static Version ParseTag(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return null;
            var t = tag.Trim();
            if (t.StartsWith("v", StringComparison.OrdinalIgnoreCase)) t = t.Substring(1);
            if (!Regex.IsMatch(t, @"^\d+(\.\d+){1,3}$")) return null;
            Version v;
            return Version.TryParse(t, out v) ? Normalize(v) : null;
        }

        public static bool IsNewer(Version latest, Version current)
        {
            return latest != null && current != null && Normalize(latest) > Normalize(current);
        }

        // 2.4.0 (четвёртая часть - только если не 0)
        public static string VersionText(Version v)
        {
            if (v == null) return "?";
            var s = v.Major + "." + v.Minor + "." + Math.Max(0, v.Build);
            if (v.Revision > 0) s += "." + v.Revision;
            return s;
        }

        // ---------------------------------------------------------------------
        // описание релиза

        // строка "SHA256: <64 hex>" (можно в `кавычках`, с именем файла) -> hex в нижнем регистре или null
        public static string ParseSha256(string body)
        {
            var m = ShaLine.Match(body ?? "");
            return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
        }

        // скачиваем только с GitHub
        public static bool IsAllowedUrl(string url)
        {
            Uri u;
            if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out u)) return false;
            if (u.Scheme != Uri.UriSchemeHttps) return false;
            var host = u.Host.ToLowerInvariant();
            return host == "github.com" || host.EndsWith(".githubusercontent.com");
        }

        // Markdown -> простой текст: без #, **, `, ссылки - только текст, пункты списка - "• "
        public static string MarkdownToText(string md)
        {
            var sb = new StringBuilder();
            bool blank = true;
            foreach (var raw in (md ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                var line = raw.TrimEnd();
                if (ShaLine.IsMatch(line)) continue; // контрольная сумма - не для чтения
                if (Regex.IsMatch(line, @"^\s*([-*_])(\s*\1){2,}\s*$")) line = ""; // ---
                var h = Regex.Match(line, @"^\s{0,3}#{1,6}\s*(.*?)\s*#*$");
                if (h.Success) line = h.Groups[1].Value;
                else
                {
                    var b = Regex.Match(line, @"^(\s*)[-*+]\s+(.*)$");
                    if (b.Success) line = new string(' ', Math.Min(b.Groups[1].Length, 8)) + "• " + b.Groups[2].Value;
                }
                line = Regex.Replace(line, @"!?\[([^\]]*)\]\([^)]*\)", "$1");
                line = line.Replace("**", "").Replace("__", "").Replace("`", "");
                if (line.Trim() == "")
                {
                    if (!blank) sb.Append('\n');
                    blank = true;
                    continue;
                }
                sb.Append(line).Append('\n');
                blank = false;
            }
            return sb.ToString().Trim('\n');
        }

        // ответ GitHub API (один релиз)
        public static ReleaseInfo ParseRelease(string json)
        {
            var root = Json.Parse(json) as Dictionary<string, object>;
            if (root == null) throw new FormatException("ответ GitHub - не объект");
            var r = new ReleaseInfo { Tag = Json.Str(root, "tag_name", ""), Body = Json.Str(root, "body", "") };
            r.Version = ParseTag(r.Tag);
            r.Sha256 = ParseSha256(r.Body);
            var expected = "SkadaDiscord-" + (r.Version != null ? VersionText(r.Version) : "") + ".zip";
            foreach (var a in Json.Arr(root, "assets"))
            {
                var name = Json.Str(a, "name", "");
                if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                if (r.AssetName != "" && !string.Equals(name, expected, StringComparison.OrdinalIgnoreCase)) continue;
                r.AssetName = name;
                r.AssetUrl = Json.Str(a, "browser_download_url", "");
                r.AssetSize = (long)Json.Num(a, "size", 0);
            }
            return r;
        }

        // ---------------------------------------------------------------------
        // сеть

        public static string Message(Exception e)
        {
            while (e is AggregateException && e.InnerException != null) e = e.InnerException;
            if (e is TaskCanceledException || e is OperationCanceledException) return "GitHub не ответил вовремя";
            if (e is HttpRequestException && e.InnerException != null) return e.Message + " " + e.InnerException.Message;
            return e.Message;
        }

        static Exception Unwrap(Exception e)
        {
            while (e is AggregateException && e.InnerException != null) e = e.InnerException;
            if (e is UpdateRejected) return e;
            return new Exception(Message(e), e);
        }

        static string Get(string url, int timeoutSeconds, out int code)
        {
            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
            {
                req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
                try
                {
                    using (var resp = Client.SendAsync(req, cts.Token).Result)
                    {
                        code = (int)resp.StatusCode;
                        return resp.Content.ReadAsStringAsync().Result;
                    }
                }
                catch (Exception e) { throw Unwrap(e); }
            }
        }

        static ReleaseInfo Fetch(string url)
        {
            int code;
            var text = Get(url, 30, out code);
            if (code == 404) return null; // релизов пока нет
            if (code == 403 || code == 429) throw new Exception("GitHub ограничил число запросов (" + code + "), попробую позже");
            if (code < 200 || code >= 300) throw new Exception("GitHub ответил " + code + ": " + Discord.Cut(text ?? "", 200));
            return ParseRelease(text);
        }

        // последний релиз (null - релизов пока нет)
        public static ReleaseInfo FetchLatest() { return Fetch(ApiBase + "latest"); }

        // релиз по тегу "v2.4.0" (null - нет такого)
        public static ReleaseInfo FetchByTag(string tag) { return Fetch(ApiBase + "tags/" + Uri.EscapeDataString(tag)); }

        public static string FileSha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var f = File.OpenRead(path))
            {
                var sb = new StringBuilder();
                foreach (var b in sha.ComputeHash(f)) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        // контрольная сумма обязательна: нет или не совпала - не ставим
        public static void VerifySha256(string path, string expected)
        {
            if (string.IsNullOrEmpty(expected)) throw new UpdateRejected("в описании релиза нет строки SHA256 — обновление не установлено");
            var hash = FileSha256(path);
            if (!string.Equals(hash, expected.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new UpdateRejected("контрольная сумма архива не совпала (SHA256 " + hash + ", в релизе " + expected + ")");
        }

        // скачать архив релиза в dir и проверить SHA256; возвращает путь к архиву
        public static string Download(ReleaseInfo r, string dir)
        {
            if (string.IsNullOrEmpty(r.Sha256)) throw new UpdateRejected("в описании релиза нет строки SHA256 — обновление не установлено");
            if (string.IsNullOrEmpty(r.AssetUrl)) throw new UpdateRejected("в релизе " + r.Tag + " нет архива .zip");
            if (!IsAllowedUrl(r.AssetUrl)) throw new UpdateRejected("недопустимая ссылка на архив: " + r.AssetUrl);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "SkadaDiscord-" + VersionText(r.Version) + ".zip");
            var tmp = path + ".part";
            using (var req = new HttpRequestMessage(HttpMethod.Get, r.AssetUrl))
            using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5)))
            {
                req.Headers.TryAddWithoutValidation("Accept", "application/octet-stream");
                HttpResponseMessage resp;
                try { resp = Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).Result; }
                catch (Exception e) { throw Unwrap(e); }
                using (resp)
                {
                    var final = resp.RequestMessage != null ? resp.RequestMessage.RequestUri : null;
                    if (final != null && !IsAllowedUrl(final.AbsoluteUri)) throw new UpdateRejected("архив перенаправлен на недопустимый адрес: " + final.Host);
                    if (!resp.IsSuccessStatusCode) throw new Exception("архив не скачан: GitHub ответил " + (int)resp.StatusCode);
                    if (resp.Content.Headers.ContentLength > MaxZip) throw new UpdateRejected("архив слишком большой");
                    try
                    {
                        using (var input = resp.Content.ReadAsStreamAsync().Result)
                        using (cts.Token.Register(() => { try { input.Dispose(); } catch { } }))
                        using (var output = File.Create(tmp))
                        {
                            var buf = new byte[81920];
                            long total = 0;
                            int n;
                            while ((n = input.Read(buf, 0, buf.Length)) > 0)
                            {
                                total += n;
                                if (total > MaxZip) throw new UpdateRejected("архив слишком большой");
                                output.Write(buf, 0, n);
                            }
                        }
                        if (cts.IsCancellationRequested) throw new OperationCanceledException();
                    }
                    catch (Exception e)
                    {
                        try { File.Delete(tmp); } catch { }
                        if (cts.IsCancellationRequested) throw new Exception("архив не скачан: GitHub не ответил вовремя");
                        throw Unwrap(e);
                    }
                }
            }
            try { VerifySha256(tmp, r.Sha256); }
            catch { try { File.Delete(tmp); } catch { } throw; }
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
            return path;
        }

        // ---------------------------------------------------------------------
        // установка (без сети и без окон - проверяется на временных папках)

        // распаковать архив в пустую папку (пути вне папки - отказ)
        public static string ExtractZip(string zipPath, string destDir)
        {
            if (Directory.Exists(destDir)) Directory.Delete(destDir, true);
            Directory.CreateDirectory(destDir);
            var root = Path.GetFullPath(destDir).TrimEnd('\\') + "\\";
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                foreach (var e in zip.Entries)
                {
                    var name = e.FullName.Replace('\\', '/');
                    if (name.EndsWith("/")) continue; // папка
                    string full;
                    try { full = Path.GetFullPath(Path.Combine(root, name.Replace('/', '\\'))); }
                    catch { throw new UpdateRejected("в архиве недопустимый путь: " + e.FullName); }
                    if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new UpdateRejected("в архиве недопустимый путь: " + e.FullName);
                    Directory.CreateDirectory(Path.GetDirectoryName(full));
                    e.ExtractToFile(full, true);
                }
            }
            return destDir;
        }

        public static string OldExePath(string exePath)
        {
            return Path.Combine(Path.GetDirectoryName(exePath), Path.GetFileNameWithoutExtension(exePath) + ".old.exe");
        }

        // работающий exe можно переименовать, но не перезаписать: exe -> .old.exe, новый - на его место
        public static void SwapExe(string exePath, string newExe)
        {
            var old = OldExePath(exePath);
            if (File.Exists(old)) File.Delete(old); // не вышло - ничего ещё не тронуто
            File.Move(exePath, old);
            try { File.Copy(newExe, exePath, false); }
            catch
            {
                try
                {
                    if (File.Exists(exePath)) File.Delete(exePath);
                    File.Move(old, exePath);
                }
                catch { }
                throw;
            }
        }

        public static void UndoSwap(string exePath)
        {
            var old = OldExePath(exePath);
            if (!File.Exists(old)) throw new FileNotFoundException("нет файла " + old);
            if (File.Exists(exePath)) File.Delete(exePath);
            File.Move(old, exePath);
        }

        // распакованный релиз -> аддон в папку игры (Config.lua не трогаем, если он есть), README и шаблон настроек, exe.
        // ошибка - всё возвращается как было
        public static InstallTxn Apply(string extractDir, string wowPath, string exePath, string backupDir, Action<string> warn)
        {
            var pkg = Path.Combine(extractDir, "SkadaDiscord");
            var newExe = Path.Combine(pkg, "SkadaDiscord.exe");
            if (!File.Exists(newExe)) throw new UpdateRejected("в архиве нет SkadaDiscord/SkadaDiscord.exe");
            if (Directory.Exists(backupDir)) Directory.Delete(backupDir, true);
            Directory.CreateDirectory(backupDir);
            var txn = new InstallTxn(backupDir) { ExePath = exePath };
            try
            {
                // аддон
                var addonSrc = Path.GetFullPath(Path.Combine(extractDir, @"Interface\AddOns\SkadaDiscord")).TrimEnd('\\');
                if (!Directory.Exists(addonSrc)) warn("в архиве нет аддона");
                else
                {
                    var addons = string.IsNullOrEmpty(wowPath) ? "" : Path.Combine(wowPath, @"Interface\AddOns");
                    if (addons == "" || !Directory.Exists(addons))
                        warn("аддон не обновлён: не найдена папка " + (addons == "" ? "игры" : addons));
                    else
                    {
                        var dest = Path.Combine(addons, "SkadaDiscord");
                        int n = 0;
                        bool keptConfig = false;
                        foreach (var file in Directory.GetFiles(addonSrc, "*", SearchOption.AllDirectories))
                        {
                            var rel = file.Substring(addonSrc.Length).TrimStart('\\');
                            var target = Path.Combine(dest, rel);
                            // Config.lua может ждать переноса настроек в игру - не затираем
                            if (string.Equals(rel, "Config.lua", StringComparison.OrdinalIgnoreCase) && File.Exists(target)) { keptConfig = true; continue; }
                            txn.CopyFile(file, target);
                            n++;
                        }
                        txn.Notes.Add("аддон: файлов " + n + (keptConfig ? ", Config.lua оставлен" : ""));
                    }
                }

                // файлы рядом с программой
                var appDir = Path.GetDirectoryName(exePath);
                foreach (var name in new[] { "README.txt", "config.example.json" })
                {
                    var src = Path.Combine(pkg, name);
                    if (File.Exists(src)) txn.CopyFile(src, Path.Combine(appDir, name));
                }

                // сама программа
                SwapExe(exePath, newExe);
                txn.ExeSwapped = true;
                txn.Notes.Add("программа");
                return txn;
            }
            catch
            {
                txn.Rollback(warn);
                throw;
            }
        }

        // игра запущена? (файл запуска из настроек и на всякий случай Wow)
        public static bool WowRunning(AppConfig config)
        {
            var names = new List<string> { "Wow" };
            try
            {
                var exe = config != null ? Path.GetFileNameWithoutExtension(config.WowExe ?? "") : "";
                if (!string.IsNullOrEmpty(exe) && !names.Contains(exe, StringComparer.OrdinalIgnoreCase)) names.Add(exe);
            }
            catch { }
            foreach (var n in names)
            {
                Process[] list;
                try { list = Process.GetProcessesByName(n); } catch { continue; }
                bool any = list.Length > 0;
                foreach (var p in list) p.Dispose();
                if (any) return true;
            }
            return false;
        }

        // ---------------------------------------------------------------------
        // работа в программе: проверка при запуске и каждые 30 минут, установка, когда игра закрыта

        readonly Engine engine;
        readonly SynchronizationContext ui;
        readonly object gate = new object();
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        readonly HashSet<Version> dismissed = new HashSet<Version>(); // «Позже» - не спрашивать до перезапуска
        readonly HashSet<Version> rejected = new HashSet<Version>();  // не установилось - не пробовать до перезапуска
        readonly HashSet<string> said = new HashSet<string>();        // уже написано в журнал
        ReleaseInfo pending;  // ставим, как только можно (игра закрыта)
        ReleaseInfo offer;    // предложить (автообновление выключено), когда игра закрыта
        bool installing, prompting;
        UpdateForm offerForm;
        volatile bool stopped;

        public Func<bool> CanInstall;              // (окно) можно ли сейчас закрыть программу (нет несохранённых изменений)
        public Func<string, string, bool> Restart; // (окно) запустить exe с аргументами и закрыться; false - не вышло

        public Updater(Engine engine, SynchronizationContext ui)
        {
            this.engine = engine;
            this.ui = ui;
        }

        public void Start()
        {
            var t = new Thread(Loop) { IsBackground = true, Name = "SkadaDiscord updater" };
            t.Start();
        }

        public void Stop()
        {
            stopped = true;
            wake.Set();
        }

        public void Kick() { wake.Set(); }

        void Loop()
        {
            wake.WaitOne(TimeSpan.FromSeconds(5));
            var next = DateTime.MinValue;
            while (!stopped)
            {
                try
                {
                    if (DateTime.Now >= next)
                    {
                        next = DateTime.Now + CheckEvery;
                        CheckAuto();
                    }
                    Tick();
                }
                catch (Exception e) { LogOnce("Обновление: " + Message(e), LogKind.Warn); }
                wake.WaitOne(TimeSpan.FromMinutes(1));
            }
        }

        void LogOnce(string text, LogKind kind)
        {
            lock (said) if (!said.Add(text)) return;
            engine.Log(text, kind);
        }

        void CheckAuto()
        {
            ReleaseInfo r;
            try { r = FetchLatest(); }
            catch (Exception e)
            {
                LogOnce("Проверка обновлений не удалась: " + Message(e), LogKind.Warn);
                return;
            }
            if (r == null || !IsNewer(r.Version, Current)) return;
            lock (gate)
            {
                if (rejected.Contains(r.Version)) return;
                if (engine.Config.AutoUpdate)
                {
                    if (pending == null || r.Version > pending.Version)
                    {
                        pending = r;
                        LogOnce("Доступна версия " + VersionText(r.Version) + " — установлю автоматически, когда игра будет закрыта.", LogKind.Info);
                    }
                }
                else if (!dismissed.Contains(r.Version) && pending == null) offer = r;
            }
        }

        void Tick()
        {
            ReleaseInfo show = null;
            lock (gate)
            {
                // окно с предложением - не поверх игры
                if (offer != null && !prompting && !WowRunning(engine.Config))
                {
                    show = offer;
                    offer = null;
                    prompting = true;
                }
            }
            if (show != null) ui.Post(_ => Offer(show, null), null);
            InstallIfPending();
        }

        // (окно) предложить обновление: «Обновить» / «Позже»
        public void Offer(ReleaseInfo r, IWin32Window owner)
        {
            if (offerForm != null && !offerForm.IsDisposed)
            {
                offerForm.Activate();
                return;
            }
            lock (gate) prompting = true;
            var f = new UpdateForm("Доступна версия " + VersionText(r.Version),
                "Сейчас у вас " + VersionText(Current) + ". Обновятся программа и аддон (игра должна быть закрыта). Что нового:",
                r.Body.Trim() != "" ? MarkdownToText(r.Body) : "Список изменений не указан.", true);
            offerForm = f;
            f.FormClosed += (s, e) =>
            {
                offerForm = null;
                lock (gate) prompting = false;
                if (f.Accepted) Accept(r);
                else lock (gate) dismissed.Add(r.Version);
            };
            if (owner != null) f.Show(owner); else f.Show();
            f.Activate();
        }

        // пользователь согласился: ставим сейчас или после закрытия игры
        public void Accept(ReleaseInfo r)
        {
            lock (gate)
            {
                rejected.Remove(r.Version);
                pending = r;
            }
            if (WowRunning(engine.Config))
                engine.Log("Обновление " + VersionText(r.Version) + " установится, когда игра будет закрыта.", LogKind.Info);
            Kick();
        }

        // поставить ожидающее обновление, если можно; true - программа перезапускается новой версией (или уже ставится)
        public bool InstallIfPending()
        {
            ReleaseInfo r;
            lock (gate)
            {
                if (installing) return true;
                if (pending == null) return false;
                if (WowRunning(engine.Config))
                {
                    LogOnce("Игра запущена — обновление " + VersionText(pending.Version) + " установится после её закрытия.", LogKind.Info);
                    return false;
                }
                installing = true;
                r = pending;
            }
            bool restarted = false;
            try { restarted = Install(r); }
            catch (Exception e) { Reject(r, Message(e)); }
            finally { lock (gate) installing = false; }
            return restarted;
        }

        void Reject(ReleaseInfo r, string why)
        {
            lock (gate)
            {
                rejected.Add(r.Version);
                if (pending == r) pending = null;
            }
            engine.Log("Обновление " + VersionText(r.Version) + " не установлено: " + why, LogKind.Error);
        }

        bool OnUi(Func<bool> f, bool def)
        {
            if (f == null) return def;
            bool result = def;
            ui.Send(_ =>
            {
                try { result = f(); }
                catch (Exception e) { engine.Log("Обновление: " + e.Message, LogKind.Error); result = false; }
            }, null);
            return result;
        }

        bool Install(ReleaseInfo r)
        {
            var v = VersionText(r.Version);
            if (!OnUi(CanInstall, true))
            {
                LogOnce("Обновление " + v + " ждёт: в окне программы есть несохранённые изменения.", LogKind.Warn);
                return false;
            }

            var dir = Path.Combine(Path.GetTempPath(), "SkadaDiscord-update");
            string extracted;
            try
            {
                engine.Log("Скачиваю обновление " + v + "…", LogKind.Info);
                var zip = Download(r, dir);
                extracted = ExtractZip(zip, Path.Combine(dir, v));
                var newExe = Path.Combine(extracted, @"SkadaDiscord\SkadaDiscord.exe");
                if (!File.Exists(newExe)) throw new UpdateRejected("в архиве нет SkadaDiscord/SkadaDiscord.exe");
                Version exeVersion = null;
                try { exeVersion = Normalize(AssemblyName.GetAssemblyName(newExe).Version); } catch { }
                if (exeVersion == null || exeVersion != r.Version)
                    throw new UpdateRejected("версия программы в архиве (" + VersionText(exeVersion) + ") не совпадает с релизом " + v);
            }
            catch (UpdateRejected e)
            {
                Reject(r, e.Message);
                return false;
            }
            catch (Exception e)
            {
                // сеть: попробуем при следующей проверке
                lock (gate) if (pending == r) pending = null;
                engine.Log("Обновление " + v + " не скачано: " + Message(e) + ". Попробую позже.", LogKind.Warn);
                return false;
            }

            if (WowRunning(engine.Config)) return false; // игру успели запустить - подождём
            // дождаться конца текущей отправки отчётов и не начинать новых
            if (!engine.TryLockForUpdate(120000))
            {
                LogOnce("Обновление " + v + " ждёт конца отправки отчётов.", LogKind.Info);
                return false;
            }
            bool restarted = false;
            InstallTxn txn = null;
            try
            {
                var exePath = Application.ExecutablePath;
                var warnings = new List<string>();
                txn = Apply(extracted, engine.WowPath, exePath, Path.Combine(dir, "backup"), w => warnings.Add(w));
                foreach (var w in warnings) engine.Log("Обновление: " + w, LogKind.Warn);
                engine.Log("Обновление " + v + " установлено (" + string.Join(", ", txn.Notes) + "). Перезапускаю программу.", LogKind.Info);
                var args = "--updated " + VersionText(Current) + " --tray";
                restarted = OnUi(() => Restart != null && Restart(exePath, args), false);
                if (!restarted) throw new Exception("новая версия не запустилась");
                return true;
            }
            catch (Exception e)
            {
                if (txn != null) txn.Rollback(w => engine.Log("Откат обновления: " + w, LogKind.Warn));
                Reject(r, Message(e) + " (всё возвращено как было)");
                return false;
            }
            finally
            {
                if (!restarted) engine.UnlockForUpdate();
            }
        }
    }

    // окно «Доступна версия» (Обновить / Позже) и «Программа обновлена» (Закрыть)
    public class UpdateForm : Form
    {
        public bool Accepted;
        readonly TextBox text;

        public UpdateForm(string title, string hint, string body, bool offer)
        {
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = "SkadaDiscord — " + (offer ? "обновление" : "обновлено");
            BackColor = Theme.Back;
            ForeColor = Theme.Text;
            Font = Theme.Font;
            ClientSize = new Size(620, 470);
            MinimumSize = new Size(440, 320);
            StartPosition = FormStartPosition.CenterScreen;
            Padding = new Padding(20, 16, 20, 14);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var head = new Panel { Dock = DockStyle.Top, Height = 66, BackColor = Color.Transparent };
            var t = Theme.Label(title, Theme.H2, Color.White);
            t.Location = new Point(0, 0);
            var h = Theme.Label(hint, Theme.Small, Theme.Muted);
            h.Location = new Point(1, 32);
            h.MaximumSize = new Size(580, 0);
            head.Controls.Add(t);
            head.Controls.Add(h);

            text = new TextBox {
                Dock = DockStyle.Fill, ReadOnly = true, Multiline = true, WordWrap = true, ScrollBars = ScrollBars.Vertical,
                BackColor = Theme.Panel, ForeColor = Theme.Text, BorderStyle = BorderStyle.None, Font = Theme.Font, TabStop = false,
            };
            SetBody(body);
            var box = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Panel, Padding = new Padding(12, 10, 4, 10) };
            box.Controls.Add(text);

            var bottom = new FlowLayoutPanel {
                Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false, BackColor = Color.Transparent, Padding = new Padding(0, 12, 0, 0),
            };
            if (offer)
            {
                var ok = Theme.Button("Обновить", true);
                ok.Margin = new Padding(8, 0, 0, 0);
                ok.Click += (s, e) => { Accepted = true; Close(); };
                var later = Theme.Button("Позже", false);
                later.Click += (s, e) => Close();
                bottom.Controls.Add(ok);
                bottom.Controls.Add(later);
                AcceptButton = ok;
                CancelButton = later;
            }
            else
            {
                var close = Theme.Button("Закрыть", true);
                close.Margin = new Padding(0);
                close.Click += (s, e) => Close();
                bottom.Controls.Add(close);
                AcceptButton = close;
                CancelButton = close;
            }

            // Dock: сверху вниз, Fill - последним
            foreach (var c in new Control[] { head, bottom, box }) { Controls.Add(c); c.BringToFront(); }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            var b = AcceptButton as Control;
            if (b != null) b.Focus(); // не текст: иначе он весь выделен
        }

        public void SetBody(string body)
        {
            text.Text = (body ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n"); // TextBox понимает только \r\n
            text.Select(0, 0);
        }
    }
}
