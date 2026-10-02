// Связь с сервером SkadaDiscord (Cloudflare Worker): база данных отчётов (общий сервер Discord)
// и защита от дублей между игроками рейда (ключи "занять" / "освободить").
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SkadaDiscord
{
    public class ClaimResult
    {
        public HashSet<string> Claimed = new HashSet<string>(); // заняты этим запросом
        public HashSet<string> Taken = new HashSet<string>();   // уже заняты раньше (другим игроком или нами)
    }

    public class RelayResult
    {
        public string Status = ""; // sent | duplicate
        public string Url = "";
    }

    public static class Relay
    {
        public const string BaseUrl = "https://skadadiscord.leancavladimir.workers.dev";
        const string ClientHeader = "X-SkadaDiscord-Client";
        const string ClientName = "SkadaDiscord";
        public const int MaxKeys = 100;
        public const int MaxKeyLength = 300;

        static readonly HttpClient Client;

        // для проверок: вместо сети - (путь, тело запроса или meta) -> ответ JSON
        public static Func<string, string, string> Fake;

        static Relay()
        {
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; // TLS 1.2
            Client = new HttpClient();
            Client.Timeout = TimeSpan.FromSeconds(120);
        }

        static Exception Unwrap(Exception e)
        {
            while (e is AggregateException && e.InnerException != null) e = e.InnerException;
            if (e is TaskCanceledException || e is OperationCanceledException) return new Exception("сервер не ответил вовремя");
            if (e is HttpRequestException && e.InnerException != null) return new Exception(e.Message + " " + e.InnerException.Message);
            return e;
        }

        static string Send(string path, HttpContent content, int timeoutSeconds, out int code)
        {
            using (var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + path))
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
            {
                req.Headers.Add(ClientHeader, ClientName);
                req.Content = content;
                try
                {
                    var resp = Client.SendAsync(req, cts.Token).Result;
                    code = (int)resp.StatusCode;
                    return resp.Content.ReadAsStringAsync().Result;
                }
                catch (Exception e) { throw Unwrap(e); }
            }
        }

        static string PostJson(string path, string body)
        {
            var fake = Fake;
            if (fake != null) return fake(path, body);
            int code;
            var text = Send(path, new StringContent(body, Encoding.UTF8, "application/json"), 20, out code);
            if (code < 200 || code >= 300) throw new Exception("сервер ответил " + code + ": " + Discord.Cut(text ?? "", 200));
            return text;
        }

        static string KeysJson(IEnumerable<string> keys)
        {
            var sb = new StringBuilder("{\"keys\":[");
            bool first = true;
            foreach (var k in keys)
            {
                if (!first) sb.Append(',');
                first = false;
                Json.WriteString(sb, k);
            }
            return sb.Append("]}").ToString();
        }

        // занять ключи (атомарно на сервере: кто первый, тот и отправляет)
        public static ClaimResult Claim(IEnumerable<string> keys)
        {
            var list = keys.Where(k => !string.IsNullOrEmpty(k)).Distinct().ToList();
            var result = new ClaimResult();
            if (list.Count == 0) return result;
            if (Fake == null && Discord.DryRun != null)
            {
                // проверка без отправки: ключи на сервере не занимаем
                foreach (var k in list) result.Claimed.Add(k);
                return result;
            }
            for (int i = 0; i < list.Count; i += MaxKeys)
            {
                var part = list.Skip(i).Take(MaxKeys).ToList();
                var resp = Json.Parse(PostJson("/v1/claim", KeysJson(part))) as Dictionary<string, object>;
                if (resp == null || !resp.ContainsKey("claimed") || !resp.ContainsKey("taken")) throw new Exception("неожиданный ответ сервера");
                foreach (var k in Json.Arr(resp, "claimed")) result.Claimed.Add(Convert.ToString(k));
                foreach (var k in Json.Arr(resp, "taken")) result.Taken.Add(Convert.ToString(k));
            }
            return result;
        }

        // освободить ключи (отправка не удалась - пусть отправит другой игрок или мы при повторе)
        public static void Release(IEnumerable<string> keys)
        {
            var list = keys.Where(k => !string.IsNullOrEmpty(k)).Distinct().ToList();
            if (list.Count == 0) return;
            if (Fake == null && Discord.DryRun != null) return;
            for (int i = 0; i < list.Count; i += MaxKeys)
                PostJson("/v1/release", KeysJson(list.Skip(i).Take(MaxKeys)));
        }

        static RelayResult ParseResult(string text)
        {
            var o = Json.Parse(text);
            var r = new RelayResult { Status = Json.Str(o, "status", ""), Url = Json.Str(o, "url", "") };
            if (r.Status != "sent" && r.Status != "duplicate")
                throw new Exception("неожиданный ответ сервера: " + Discord.Cut(text ?? "", 200));
            return r;
        }

        // отчёт в базу данных: meta (JSON), сообщение Discord, картинка и сам отчёт
        public static RelayResult SendReport(string metaJson, string payloadJson, byte[] png, string reportJson)
        {
            var dry = Discord.DryRun;
            var fake = Fake;
            if (dry != null || fake != null)
            {
                // проверка: на сервер ничего не уходит
                if (dry != null)
                    lock (dry) dry.Add("relay /v1/report" + (png != null ? " +png " + png.Length + " байт" : "") + "\nmeta " + metaJson + "\npayload " + payloadJson);
                return ParseResult(fake != null ? fake("/v1/report", metaJson) : "{\"status\":\"sent\",\"url\":\"\"}");
            }

            for (int attempt = 1; attempt <= 3; attempt++)
            {
                using (var form = new MultipartFormDataContent())
                {
                    form.Add(new StringContent(metaJson, Encoding.UTF8, "application/json"), "meta");
                    form.Add(new StringContent(payloadJson, Encoding.UTF8, "application/json"), "payload_json");
                    if (png != null)
                    {
                        var file = new ByteArrayContent(png);
                        file.Headers.ContentType = MediaTypeHeaderValue.Parse("image/png");
                        form.Add(file, "files[0]", "skada.png");
                    }
                    if (!string.IsNullOrEmpty(reportJson))
                    {
                        var json = new ByteArrayContent(new UTF8Encoding(false).GetBytes(reportJson));
                        json.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");
                        form.Add(json, "files[1]", "report.json");
                    }
                    int code;
                    var text = Send("/v1/report", form, 90, out code);
                    if (code == 429 && attempt < 3)
                    {
                        Thread.Sleep(TimeSpan.FromSeconds(5 * attempt));
                        continue;
                    }
                    if (code < 200 || code >= 300) throw new Exception("сервер базы ответил " + code + ": " + Discord.Cut(text ?? "", 200));
                    return ParseResult(text);
                }
            }
            throw new Exception("сервер базы: лимит запросов");
        }
    }
}
