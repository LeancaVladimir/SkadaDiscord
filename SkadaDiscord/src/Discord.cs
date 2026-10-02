// Отправка сообщений в Discord через вебхук (с картинкой и embed).
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace SkadaDiscord
{
    public static class Discord
    {
        static readonly HttpClient Client;
        static readonly Dictionary<string, DateTime> LastSend = new Dictionary<string, DateTime>();

        // проверка без отправки (--check): если задан, сообщения складываются сюда, в Discord ничего не уходит
        public static List<string> DryRun;
        // для проверок: в режиме DryRun "отправка" в эту ссылку завершается ошибкой
        public static Predicate<string> DryRunFail;

        static Discord()
        {
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; // TLS 1.2
            Client = new HttpClient();
            Client.Timeout = TimeSpan.FromSeconds(60);
        }

        public static bool IsWebhook(string url)
        {
            return !string.IsNullOrEmpty(url) && Regex.IsMatch(url.Trim(), @"^https://(ptb\.|canary\.)?discord(app)?\.com/api/webhooks/\d+/[\w-]+$");
        }

        // embedJson - JSON одного embed, png - картинка (или null), content - текст сообщения, alt - подпись картинки
        public static void Send(string webhook, string username, string embedJson, byte[] png)
        {
            Send(webhook, username, null, embedJson, png, null);
        }

        public static void Send(string webhook, string username, string content, string embedJson, byte[] png, string alt)
        {
            webhook = webhook.Trim();
            if (!IsWebhook(webhook)) throw new Exception("неправильный адрес вебхука");
            string payload = Payload(username, content, embedJson, png, alt);

            var dry = DryRun;
            if (dry != null)
            {
                var fail = DryRunFail;
                if (fail != null && fail(webhook)) throw new Exception("проверка: ошибка отправки");
                lock (dry) dry.Add("hook " + AppConfig.Hash(webhook, 10) + (png != null ? " +png " + png.Length + " байт" : "") + "\n" + payload);
                return;
            }

            // не чаще ~1 сообщения в секунду в один вебхук
            lock (LastSend)
            {
                DateTime last;
                if (LastSend.TryGetValue(webhook, out last))
                {
                    var wait = last.AddMilliseconds(1100) - DateTime.Now;
                    if (wait > TimeSpan.Zero) Thread.Sleep(wait);
                }
                LastSend[webhook] = DateTime.Now;
            }

            for (int attempt = 1; attempt <= 5; attempt++)
            {
                using (var form = WebhookForm(payload, png))
                {
                    var resp = Client.PostAsync(webhook, form).Result;
                    var body = resp.Content.ReadAsStringAsync().Result;
                    if (resp.IsSuccessStatusCode) return;
                    if ((int)resp.StatusCode == 429)
                    {
                        double wait = 2;
                        try { wait = Json.Num(Json.Parse(body), "retry_after", 2); } catch { }
                        Thread.Sleep(TimeSpan.FromSeconds(Math.Max(0.5, Math.Min(60, wait)) + 0.2));
                        continue;
                    }
                    if ((int)resp.StatusCode == 404) throw new Exception("вебхук не найден (удалён в Discord?)");
                    throw new Exception("Discord ответил " + (int)resp.StatusCode + ": " + body);
                }
            }
            throw new Exception("Discord не принял сообщение (лимит запросов)");
        }

        // часть multipart/form-data с именем в кавычках: name="..." (и filename="...").
        // form.Add(content, name) пишет name=meta без кавычек - FormData в Cloudflare Workers такое не принимает
        public static void AddPart(MultipartFormDataContent form, HttpContent content, string name, string fileName)
        {
            var cd = new ContentDispositionHeaderValue("form-data") { Name = "\"" + name + "\"" };
            if (fileName != null) cd.FileName = "\"" + fileName + "\"";
            content.Headers.ContentDisposition = cd;
            form.Add(content);
        }

        // сообщение вебхука: payload_json и картинка
        public static MultipartFormDataContent WebhookForm(string payload, byte[] png)
        {
            var form = new MultipartFormDataContent();
            AddPart(form, new StringContent(payload, Encoding.UTF8, "application/json"), "payload_json", null);
            if (png != null)
            {
                var file = new ByteArrayContent(png);
                file.Headers.ContentType = MediaTypeHeaderValue.Parse("image/png");
                AddPart(form, file, "files[0]", "skada.png");
            }
            return form;
        }

        // Discord не принимает имя вебхука со словом "discord" (ошибка 400) - вырезаем его
        public static string SafeUsername(string username)
        {
            var name = Regex.Replace(username ?? "", "discord", "", RegexOptions.IgnoreCase).Trim();
            return name == "" ? "Skada" : name;
        }

        // payload_json сообщения
        public static string Payload(string username, string content, string embedJson, byte[] png, string alt)
        {
            var sb = new StringBuilder("{\"username\":");
            Json.WriteString(sb, SafeUsername(username));
            if (!string.IsNullOrEmpty(content))
            {
                sb.Append(",\"content\":");
                Json.WriteString(sb, Cut(content, 2000));
            }
            if (embedJson != null) sb.Append(",\"embeds\":[").Append(embedJson).Append("]");
            if (png != null && !string.IsNullOrEmpty(alt))
            {
                sb.Append(",\"attachments\":[{\"id\":0,\"filename\":\"skada.png\",\"description\":");
                Json.WriteString(sb, Cut(alt, 1024));
                sb.Append("}]");
            }
            sb.Append('}');
            return sb.ToString();
        }

        public static string Cut(string s, int max)
        {
            return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
        }

        public static string Embed(string title, string description, int color, bool withImage, string footer, DateTime? time)
        {
            var sb = new StringBuilder("{\"title\":");
            Json.WriteString(sb, title);
            sb.Append(",\"description\":");
            Json.WriteString(sb, description);
            sb.Append(",\"color\":").Append(color);
            if (withImage) sb.Append(",\"image\":{\"url\":\"attachment://skada.png\"}");
            if (!string.IsNullOrEmpty(footer))
            {
                sb.Append(",\"footer\":{\"text\":");
                Json.WriteString(sb, Cut(footer, 2048));
                sb.Append('}');
            }
            if (time.HasValue) sb.Append(",\"timestamp\":\"").Append(time.Value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")).Append('"');
            sb.Append('}');
            return sb.ToString();
        }
    }
}
