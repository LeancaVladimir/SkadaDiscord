// SkadaDiscord relay — Cloudflare Worker.
// Автор: Leanca Vladimir (Discord: lyanka_v)
//
// Зачем: ссылки базы данных и токен бота хранятся только здесь, у пользователей их нет;
// здесь же защита от дублей (один отчёт на рейд), общая для всех пользователей,
// оформление сервера базы данных, поиск /игрок и выбор ролей кнопками.
//
// Привязки (Settings -> Variables and Secrets / Bindings):
//   DB                 D1 database (таблицы создаются сами)
//   DISCORD_BOT_TOKEN  секрет: токен бота Discord (сервер базы данных)
//   GUILD_ID           необязательно: ID сервера (если бот только на одном сервере, берётся он)
//
// Программа шлёт заголовок X-SkadaDiscord-Client: SkadaDiscord. Это не секрет (он виден в программе),
// а фильтр от случайных запросов; от злоупотреблений защищают лимит запросов и защита от дублей.
//
// Эндпоинты:
//   GET  /                 проверка
//   GET  /v1/status        состояние: токен, сервер, каналы
//   POST /v1/claim         {"keys":[...]} -> {"claimed":[...],"taken":[...]}   (защита от дублей)
//   POST /v1/release       {"keys":[...]}                                         (отмена, если отправка не удалась)
//   POST /v1/report        multipart: meta (JSON), payload_json, files[0] (png), files[1] (json) -> {"status","url"}
//   POST /v1/setup         оформление сервера (роли, инфо-каналы, команда /игрок); повторно — обновляет
//   POST /interactions     Discord: команда /игрок, кнопки ролей

const DISCORD = "https://discord.com/api/v10";
const APP_ID = "1555359180142026893";
const PUBLIC_KEY = "44405158e89fdaee3a513b894aa5ad9d898b1a007915f619dd08e48528874ab3"; // публичный ключ приложения (не секрет)
const SELF_URL = "https://skadadiscord.leancavladimir.workers.dev";
const GITHUB_URL = "https://github.com/LeancaVladimir/SkadaDiscord";
const AUTHOR = "Leanca Vladimir";
const CONTACT = "lyanka_v";
const SETUP_VERSION = 3; // увеличить, чтобы /v1/setup обновил сообщения и роли
const RATE_PER_MINUTE = 60;
const CLAIM_TTL_DAYS = 30;
const ACCENT = 0x5865f2;

let schemaReady = false;

export default {
  async fetch(req, env, ctx) {
    const url = new URL(req.url);
    try {
      if (req.method === "GET" && url.pathname === "/") return json({ ok: true, name: "SkadaDiscord relay" });
      await schema(env);
      if (req.method === "POST" && url.pathname === "/interactions") return await interactions(req, env, ctx);

      if (req.headers.get("X-SkadaDiscord-Client") !== "SkadaDiscord") return json({ error: "forbidden" }, 403);
      if (await rateLimited(env, req)) return json({ error: "rate limit" }, 429);

      if (req.method === "GET" && url.pathname === "/v1/status") return await status(env);
      if (req.method === "POST" && url.pathname === "/v1/claim") return await claim(req, env);
      if (req.method === "POST" && url.pathname === "/v1/release") return await release(req, env);
      if (req.method === "POST" && url.pathname === "/v1/report") return await report(req, env, ctx);
      if (req.method === "POST" && url.pathname === "/v1/setup") return await setup(env);
      return json({ error: "not found" }, 404);
    } catch (e) {
      return json({ error: String((e && e.message) || e) }, 500);
    }
  },
};

// ---------------------------------------------------------------------------
// база (D1)

async function schema(env) {
  if (schemaReady) return;
  await env.DB.batch([
    env.DB.prepare("CREATE TABLE IF NOT EXISTS claims (key TEXT PRIMARY KEY, created INTEGER NOT NULL)"),
    env.DB.prepare("CREATE TABLE IF NOT EXISTS channels (key TEXT PRIMARY KEY, channel_id TEXT NOT NULL, webhook TEXT NOT NULL, created INTEGER NOT NULL)"),
    env.DB.prepare("CREATE TABLE IF NOT EXISTS rate (ip TEXT NOT NULL, minute INTEGER NOT NULL, n INTEGER NOT NULL, PRIMARY KEY (ip, minute))"),
    env.DB.prepare("CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT)"),
    env.DB.prepare(
      "CREATE TABLE IF NOT EXISTS entries (id INTEGER PRIMARY KEY AUTOINCREMENT, player TEXT NOT NULL, player_lc TEXT NOT NULL, cls TEXT, role TEXT, " +
      "boss TEXT, diff TEXT, instance TEXT, dps REAL, hps REAL, drank INTEGER, hrank INTEGER, ts INTEGER, link TEXT)"
    ),
    env.DB.prepare("CREATE INDEX IF NOT EXISTS entries_player ON entries (player_lc, ts)"),
  ]);
  schemaReady = true;
}

async function getMeta(env, key) {
  const row = await env.DB.prepare("SELECT value FROM meta WHERE key = ?").bind(key).first();
  return row ? row.value : null;
}

async function setMeta(env, key, value) {
  await env.DB.prepare("INSERT OR REPLACE INTO meta (key, value) VALUES (?, ?)").bind(key, String(value)).run();
}

async function rateLimited(env, req) {
  const ip = req.headers.get("CF-Connecting-IP") || "?";
  const minute = Math.floor(Date.now() / 60000);
  const row = await env.DB.prepare(
    "INSERT INTO rate (ip, minute, n) VALUES (?, ?, 1) ON CONFLICT(ip, minute) DO UPDATE SET n = n + 1 RETURNING n"
  ).bind(ip, minute).first();
  if (Math.random() < 0.02) {
    // уборка старых записей
    await env.DB.batch([
      env.DB.prepare("DELETE FROM rate WHERE minute < ?").bind(minute - 5),
      env.DB.prepare("DELETE FROM claims WHERE created < ?").bind(Date.now() - CLAIM_TTL_DAYS * 86400000),
    ]);
  }
  return row && row.n > RATE_PER_MINUTE;
}

// атомарно: true, если ключ занят этим запросом впервые
async function tryClaim(env, key) {
  const res = await env.DB.prepare("INSERT OR IGNORE INTO claims (key, created) VALUES (?, ?)").bind(key, Date.now()).run();
  return res.meta.changes === 1;
}

function cleanKeys(body) {
  const keys = Array.isArray(body && body.keys) ? body.keys : [];
  return keys.filter((k) => typeof k === "string" && k.length > 0 && k.length <= 300).slice(0, 100);
}

async function claim(req, env) {
  const keys = cleanKeys(await req.json());
  const claimed = [], taken = [];
  for (const k of keys) ((await tryClaim(env, k)) ? claimed : taken).push(k);
  return json({ claimed, taken });
}

async function release(req, env) {
  const keys = cleanKeys(await req.json());
  for (const k of keys) await env.DB.prepare("DELETE FROM claims WHERE key = ?").bind(k).run();
  return json({ released: keys.length });
}

// проверка настройки (без секретов): есть ли токен, какой сервер видит бот
async function status(env) {
  const out = { token: !!env.DISCORD_BOT_TOKEN, guildIdSet: !!env.GUILD_ID, setupVersion: Number(await getMeta(env, "setup")) || 0 };
  if (out.token) {
    try {
      const me = await bot(env, "GET", "/users/@me");
      out.bot = me.username;
      const guilds = await bot(env, "GET", "/users/@me/guilds");
      out.guilds = guilds.map((g) => ({ id: g.id, name: g.name }));
    } catch (e) {
      out.error = String(e.message || e);
    }
  }
  const row = await env.DB.prepare("SELECT COUNT(*) AS n FROM channels").first();
  out.channelsCached = row ? row.n : 0;
  const e = await env.DB.prepare("SELECT COUNT(*) AS n FROM entries").first();
  out.entries = e ? e.n : 0;
  return json(out);
}

// ---------------------------------------------------------------------------
// отчёт в базу данных

const DIFF = { "10n": "10 об.", "10h": "10 гер.", "25n": "25 об.", "25h": "25 гер.", "5n": "5 об.", "5h": "5 гер." };

async function report(req, env, ctx) {
  const form = await req.formData();
  const meta = JSON.parse(form.get("meta") || "{}");
  const payload = form.get("payload_json");
  if (!meta.key || !meta.boss || !payload) return json({ error: "bad request" }, 400);

  // один отчёт на бой: ID сохранения инста + босс + сложность (ключ собирает программа)
  const dbKey = "db:" + String(meta.key).slice(0, 250);
  if (!(await tryClaim(env, dbKey))) return json({ status: "duplicate" });

  try {
    const category = `⚔ ${meta.instance || "Другое"} ${DIFF[meta.diff] || meta.diff || ""}`.trim();
    const channel = slug(meta.boss);
    let target = await channelFor(env, category, channel, Number(meta.bossIndex) || 0);
    let res = await forward(target.webhook, form, payload);
    if (res.status === 404) {
      // вебхук или канал удалили вручную — создаём заново
      await env.DB.prepare("DELETE FROM channels WHERE key = ?").bind(category + "/" + channel).run();
      target = await channelFor(env, category, channel, Number(meta.bossIndex) || 0);
      res = await forward(target.webhook, form, payload);
    }
    if (!res.ok) throw new Error("Discord " + res.status + ": " + (await res.text()).slice(0, 300));
    const msg = await res.json().catch(() => null);
    const link = msg && msg.id ? `https://discord.com/channels/${await guildId(env)}/${msg.channel_id}/${msg.id}` : "";
    ctx.waitUntil(indexPlayers(env, meta, link));
    return json({ status: "sent", url: link });
  } catch (e) {
    await env.DB.prepare("DELETE FROM claims WHERE key = ?").bind(dbKey).run(); // пусть отправит другой
    throw e;
  }
}

// поисковый индекс для /игрок
async function indexPlayers(env, meta, link) {
  const players = Array.isArray(meta.players) ? meta.players.slice(0, 40) : [];
  const ts = Number(meta.start) ? Number(meta.start) * 1000 : Date.now();
  const stmts = players.filter((p) => p && p.n).map((p) =>
    env.DB.prepare(
      "INSERT INTO entries (player, player_lc, cls, role, boss, diff, instance, dps, hps, drank, hrank, ts, link) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?)"
    ).bind(String(p.n), String(p.n).toLowerCase(), p.c || "", p.r || "", meta.boss, meta.diff || "", meta.instance || "",
      Number(p.dps) || 0, Number(p.hps) || 0, Number(p.dr) || 0, Number(p.hr) || 0, ts, link)
  );
  if (stmts.length) await env.DB.batch(stmts);
}

async function forward(webhook, form, payload) {
  const out = new FormData();
  out.append("payload_json", payload);
  for (const name of ["files[0]", "files[1]"]) {
    const f = form.get(name);
    if (f) out.append(name, f, f.name || name);
  }
  for (let attempt = 0; attempt < 4; attempt++) {
    const res = await fetch(webhook + "?wait=true", { method: "POST", body: out });
    if (res.status !== 429) return res;
    const body = await res.json().catch(() => ({}));
    await sleep(Math.min(10, Number(body.retry_after) || 2) * 1000 + 200);
  }
  return new Response("rate limited", { status: 429 });
}

// категория без эмодзи-префикса (для сравнения)
function bare(name) {
  return String(name).replace(/^[^\p{L}\p{N}]+/u, "").trim().toLowerCase();
}

// канал (и категория) создаются ботом при первом отчёте, вебхук кэшируется в D1
async function channelFor(env, categoryName, channelName, position) {
  const key = categoryName + "/" + channelName;
  const cached = await env.DB.prepare("SELECT channel_id, webhook FROM channels WHERE key = ?").bind(key).first();
  if (cached) return { channelId: cached.channel_id, webhook: cached.webhook };

  // одновременно создаёт только один запрос
  const lock = "lock:" + key;
  if (!(await tryClaim(env, lock))) {
    for (let i = 0; i < 10; i++) {
      await sleep(1000);
      const row = await env.DB.prepare("SELECT channel_id, webhook FROM channels WHERE key = ?").bind(key).first();
      if (row) return { channelId: row.channel_id, webhook: row.webhook };
    }
    throw new Error("channel creation timeout");
  }
  try {
    const guild = await guildId(env);
    const channels = await bot(env, "GET", `/guilds/${guild}/channels`);
    let cat = channels.find((c) => c.type === 4 && bare(c.name) === bare(categoryName));
    if (!cat) {
      cat = await bot(env, "POST", `/guilds/${guild}/channels`, { name: categoryName, type: 4, permission_overwrites: lockedOverwrites(guild) });
    } else if (cat.name !== categoryName) {
      // категория из старой версии (без эмодзи и без запрета писать) — приводим к новому виду
      cat = await bot(env, "PATCH", `/channels/${cat.id}`, { name: categoryName, permission_overwrites: lockedOverwrites(guild) });
    }
    let ch = channels.find((c) => c.type === 0 && c.parent_id === cat.id && c.name === channelName);
    if (ch) {
      try {
        await bot(env, "PATCH", `/channels/${ch.id}`, { permission_overwrites: lockedOverwrites(guild) });
      } catch (e) {
        // старый канал, у которого бот не может поменять права, — пересоздаём
        await bot(env, "DELETE", `/channels/${ch.id}`);
        ch = null;
      }
    }
    if (!ch) {
      ch = await bot(env, "POST", `/guilds/${guild}/channels`, {
        name: channelName, type: 0, parent_id: cat.id, position: position || 0,
        topic: "Отчёты Skada по боссу. Поиск по игроку: команда /игрок в любом канале.",
        permission_overwrites: lockedOverwrites(guild),
      });
    }
    // имя вебхука не может содержать слово "discord"
    const hook = await bot(env, "POST", `/channels/${ch.id}/webhooks`, { name: "Skada" });
    const webhook = `https://discord.com/api/webhooks/${hook.id}/${hook.token}`;
    await env.DB.prepare("INSERT OR REPLACE INTO channels (key, channel_id, webhook, created) VALUES (?, ?, ?, ?)")
      .bind(key, ch.id, webhook, Date.now()).run();
    return { channelId: ch.id, webhook };
  } finally {
    await env.DB.prepare("DELETE FROM claims WHERE key = ?").bind(lock).run();
  }
}

// ---------------------------------------------------------------------------
// оформление сервера: роли, инфо-каналы, команда /игрок

const PERM = {
  VIEW_CHANNEL: 1024,                     // 1 << 10
  SEND_MESSAGES: 2048,                    // 1 << 11
  EMBED_LINKS: 16384,                     // 1 << 14
  ATTACH_FILES: 32768,                    // 1 << 15
  READ_MESSAGE_HISTORY: 65536,            // 1 << 16
  CREATE_PUBLIC_THREADS: 34359738368,     // 1 << 35
  CREATE_PRIVATE_THREADS: 68719476736,    // 1 << 36
  SEND_MESSAGES_IN_THREADS: 274877906944, // 1 << 38
};

// порядок = порядок сверху вниз в списке ролей
const ROLES = [
  { key: "dev", name: "👑 Разработчик", color: 0xf1c40f, hoist: true },
  { key: "admin", name: "🛡️ Админ", color: 0xe74c3c, hoist: true },
  { key: "logger", name: "📜 Логгер", color: 0x3498db, hoist: true, self: true },
  { key: "seeker", name: "🔍 Искатель", color: 0x99aab5, self: true },
];
// роли прошлой версии оформления: переименовать (ключ новой роли) или удалить (null)
const OLD_ROLES = {
  "Админ": "admin", "Рейд-лидер": null, "Танк": null, "Хил": null, "ДД": null,
  "Рыцарь смерти": null, "Друид": null, "Охотник": null, "Маг": null, "Паладин": null,
  "Жрец": null, "Разбойник": null, "Шаман": null, "Чернокнижник": null, "Воин": null,
};

const CLASSES = [
  { key: "DEATHKNIGHT", name: "Рыцарь смерти", color: 0xc41f3b, emoji: "💀" },
  { key: "DRUID", name: "Друид", color: 0xff7d0a, emoji: "🐻" },
  { key: "HUNTER", name: "Охотник", color: 0xabd473, emoji: "🏹" },
  { key: "MAGE", name: "Маг", color: 0x69ccf0, emoji: "🔮" },
  { key: "PALADIN", name: "Паладин", color: 0xf58cba, emoji: "🛡️" },
  { key: "PRIEST", name: "Жрец", color: 0xfefefe, emoji: "✨" },
  { key: "ROGUE", name: "Разбойник", color: 0xfff569, emoji: "🗡️" },
  { key: "SHAMAN", name: "Шаман", color: 0x0070de, emoji: "⚡" },
  { key: "WARLOCK", name: "Чернокнижник", color: 0x9482c9, emoji: "🔥" },
  { key: "WARRIOR", name: "Воин", color: 0xc79c6e, emoji: "⚔️" },
];

const INFO = [
  {
    key: "welcome", name: "👋・добро-пожаловать",
    embeds: () => [{
      title: "Летопись Circle — база рейдовых отчётов",
      color: ACCENT,
      description:
        "Здесь собираются отчёты **Skada** по убийствам боссов на WoW Circle x100: урон, исцеление, место каждого игрока.\n\n" +
        "Отчёты присылает аддон **SkadaDiscord** — автоматически, сразу после рейда. Один бой = один отчёт, даже если аддон стоит у нескольких игроков рейда.",
      fields: [
        { name: "📂 Где отчёты", value: "Категории «⚔ ЦЛК 25 гер.», «⚔ РС 10 об.» и т.д., внутри — канал на каждого босса." },
        { name: "🔍 Поиск", value: "Команда **/игрок** и ник — список последних боёв игрока со ссылками." },
        { name: "🎭 Роли", value: "Разработчик → Админ → Логгер → Искатель. Подробнее — в #🎭・роли." },
        { name: "✅ Начать", value: "Нажмите кнопку ниже — получите роль **🔍 Искатель**." },
      ],
      footer: { text: `SkadaDiscord · автор ${AUTHOR} · Discord: ${CONTACT}` },
    }],
    components: () => [
      { type: 1, components: [{ type: 2, style: 3, label: "Начать", emoji: { name: "✅" }, custom_id: "role:seeker" }] },
    ],
  },
  {
    key: "howto", name: "📖・как-пользоваться",
    embeds: () => [{
      title: "Как пользоваться",
      color: ACCENT,
      fields: [
        { name: "1. Найти себя", value: "Напишите в любом канале `/игрок` и ник персонажа. Бот покажет последние бои: босс, сложность, ДПС/ХПС, место и ссылку на отчёт. Ответ видите только вы." },
        { name: "2. Встроенный поиск Discord", value: "Ник можно искать и обычным поиском Discord (сверху справа) — в каждом отчёте есть скрытый список игроков." },
        { name: "3. Каналы отчётов", value: "Писать в них может только бот, чтобы отчёты не терялись. Обсуждать — в #обсуждение." },
        { name: "4. Хотите присылать отчёты сами?", value: "Скачайте аддон в #📥・скачать-аддон. Галочка «Отправлять отчёт в базу данных» включена по умолчанию." },
      ],
    }],
  },
  {
    key: "download", name: "📥・скачать-аддон",
    embeds: () => [{
      title: "Аддон SkadaDiscord",
      color: ACCENT,
      description:
        "Аддон для WoW 3.3.5a + программа для Windows: после убийства босса отчёт Skada сам уходит в Discord картинкой — как окно Skada в игре.\n\n" +
        (GITHUB_URL ? `**Скачать:** ${GITHUB_URL}/releases/latest\n**Исходный код:** ${GITHUB_URL}` : "**Скачать:** ссылка появится здесь в ближайшее время."),
      fields: [
        { name: "Что нужно", value: "Skada (версия bkader для 3.3.5), желательно DBM, Windows 10/11." },
        { name: "Вопросы и идеи", value: `Пишите автору в Discord: **${CONTACT}** (${AUTHOR}).` },
      ],
    }],
  },
  {
    key: "updates", name: "📢・обновления",
    embeds: () => [{
      title: "SkadaDiscord 2.2",
      color: 0x2ecc71,
      description:
        "• Все настройки — прямо в игре: `/sd` → «Настройки».\n" +
        "• Профили, импорт/экспорт строкой.\n" +
        "• Несколько вебхуков в одном канале.\n" +
        "• Окна «Раскол брони», «Урон по целям», «Урон по трешу» и др.\n" +
        "• В окне хила — только хилеры (по спеку).\n" +
        "• Общая база данных и защита от дублей отчётов.",
    }],
  },
  {
    key: "roles", name: "🎭・роли",
    embeds: () => [{
      title: "Роли сервера",
      color: ACCENT,
      fields: [
        { name: "👑 Разработчик", value: `Автор аддона — ${AUTHOR}.` },
        { name: "🛡️ Админ", value: "Помогают вести сервер." },
        { name: "📜 Логгер", value: "Ставят аддон и присылают отчёты своих рейдов. Нажмите кнопку ниже, если вы из них (ещё раз — снять)." },
        { name: "🔍 Искатель", value: "Все участники: смотрят отчёты и ищут игроков через /игрок. Выдаётся кнопкой «Начать» в #👋・добро-пожаловать." },
      ],
    }],
    components: () => [
      { type: 1, components: [
        { type: 2, style: 1, label: "Я отправляю отчёты", emoji: { name: "📜" }, custom_id: "role:logger" },
      ] },
    ],
  },
  {
    key: "search", name: "🔍・поиск", writable: true,
    embeds: () => [{
      title: "Поиск игрока",
      color: ACCENT,
      description: "Напишите здесь `/игрок` и ник — например `/игрок ник:Interc`. Ответ увидите только вы.",
    }],
  },
];

const BOT_ALLOW = () => PERM.VIEW_CHANNEL + PERM.SEND_MESSAGES + PERM.EMBED_LINKS + PERM.ATTACH_FILES + PERM.READ_MESSAGE_HISTORY;
const ALL_DENY = () => PERM.SEND_MESSAGES + PERM.CREATE_PUBLIC_THREADS + PERM.CREATE_PRIVATE_THREADS + PERM.SEND_MESSAGES_IN_THREADS;

// права при создании канала: всем только чтение, боту — писать
function lockedOverwrites(guild) {
  return [
    { id: guild, type: 0, allow: "0", deny: String(ALL_DENY()) },
    { id: APP_ID, type: 1, allow: String(BOT_ALLOW()), deny: "0" },
  ];
}

async function setup(env) {
  const done = Number(await getMeta(env, "setup")) || 0;
  if (done >= SETUP_VERSION) return json({ status: "already", version: done });
  const guild = await guildId(env);
  const log = [];

  // роли: старые переименовать/удалить, недостающие создать
  let roles = await bot(env, "GET", `/guilds/${guild}/roles`);
  for (const [oldName, key] of Object.entries(OLD_ROLES)) {
    const old = roles.find((x) => x.name === oldName);
    if (!old) continue;
    const def = ROLES.find((r) => r.key === key);
    if (def) await bot(env, "PATCH", `/guilds/${guild}/roles/${old.id}`, { name: def.name, color: def.color, hoist: !!def.hoist });
    else await bot(env, "DELETE", `/guilds/${guild}/roles/${old.id}`);
    log.push((def ? "переименована роль " : "удалена роль ") + oldName);
  }
  roles = await bot(env, "GET", `/guilds/${guild}/roles`);
  const ordered = [];
  for (const r of ROLES) {
    let role = roles.find((x) => x.name === r.name);
    if (!role) {
      role = await bot(env, "POST", `/guilds/${guild}/roles`, { name: r.name, color: r.color, hoist: !!r.hoist, mentionable: false, permissions: "0" });
      log.push("роль " + r.name);
    }
    ordered.push(role.id);
    await setMeta(env, "role:" + r.key, role.id);
  }

  // порядок ролей: сразу под ролью бота, сверху вниз как в ROLES
  try {
    const me = await bot(env, "GET", `/guilds/${guild}/members/${APP_ID}`);
    roles = await bot(env, "GET", `/guilds/${guild}/roles`);
    const top = Math.max(...roles.filter((x) => me.roles.includes(x.id)).map((x) => x.position));
    await bot(env, "PATCH", `/guilds/${guild}/roles`, ordered.map((id, i) => ({ id, position: Math.max(1, top - 1 - i) })));
  } catch (e) {
    log.push("порядок ролей не изменён: " + String(e.message || e).slice(0, 120));
  }

  // разработчик — владелец сервера
  try {
    const g = await bot(env, "GET", `/guilds/${guild}`);
    await bot(env, "PUT", `/guilds/${guild}/members/${g.owner_id}/roles/${await getMeta(env, "role:dev")}`);
    log.push("роль Разработчик — владельцу сервера");
  } catch (e) {
    log.push("роль Разработчик не выдана: " + String(e.message || e).slice(0, 120));
  }

  // инфо-каналы
  let channels = await bot(env, "GET", `/guilds/${guild}/channels`);
  let info = channels.find((c) => c.type === 4 && bare(c.name) === "инфо");
  if (info && done === 0) {
    // остатки неудачной первой попытки (создавались с запретом, который бот не может снять) — пересоздаём
    for (const c of channels.filter((x) => x.parent_id === info.id)) await bot(env, "DELETE", `/channels/${c.id}`);
    await bot(env, "DELETE", `/channels/${info.id}`);
    info = null;
    channels = await bot(env, "GET", `/guilds/${guild}/channels`);
  }
  if (!info) {
    info = await bot(env, "POST", `/guilds/${guild}/channels`, { name: "📌 Инфо", type: 4, position: 0 });
    log.push("категория Инфо");
  }
  let pos = 0;
  for (const item of INFO) {
    let ch = channels.find((c) => c.type === 0 && c.parent_id === info.id && bare(c.name) === bare(item.name));
    if (!ch) {
      // права сразу при создании: всем только чтение, боту — писать
      ch = await bot(env, "POST", `/guilds/${guild}/channels`, {
        name: item.name, type: 0, parent_id: info.id, position: pos,
        permission_overwrites: item.writable ? [] : lockedOverwrites(guild),
      });
      log.push("канал " + item.name);
    }
    pos++;
    const body = { embeds: item.embeds(), components: item.components ? item.components() : [] };
    const msgId = await getMeta(env, "msg:" + item.key);
    if (msgId) {
      try {
        await bot(env, "PATCH", `/channels/${ch.id}/messages/${msgId}`, body);
        continue;
      } catch (e) { /* сообщение удалили — отправим заново */ }
    }
    const msg = await bot(env, "POST", `/channels/${ch.id}/messages`, body);
    await setMeta(env, "msg:" + item.key, msg.id);
  }

  // общение
  let talk = channels.find((c) => c.type === 4 && bare(c.name) === "общение");
  if (!talk) {
    talk = await bot(env, "POST", `/guilds/${guild}/channels`, { name: "💬 Общение", type: 4 });
    await bot(env, "POST", `/guilds/${guild}/channels`, { name: "обсуждение", type: 0, parent_id: talk.id });
    log.push("категория Общение");
  }

  // команда /игрок и адрес для команд и кнопок
  await bot(env, "PUT", `/applications/${APP_ID}/guilds/${guild}/commands`, [{
    name: "игрок", description: "Последние бои игрока: босс, ДПС/ХПС, место, ссылка на отчёт", type: 1,
    options: [{ type: 3, name: "ник", description: "Ник персонажа", required: true }],
  }]);
  await bot(env, "PATCH", "/applications/@me", { interactions_endpoint_url: SELF_URL + "/interactions", description: `Отчёты Skada в Discord. Автор: ${AUTHOR} (Discord: ${CONTACT})` });
  log.push("команда /игрок");

  await setMeta(env, "setup", SETUP_VERSION);
  return json({ status: "done", version: SETUP_VERSION, created: log });
}

// ---------------------------------------------------------------------------
// Discord interactions: /игрок, кнопки ролей

function hexToBytes(hex) {
  const out = new Uint8Array(hex.length / 2);
  for (let i = 0; i < out.length; i++) out[i] = parseInt(hex.substr(i * 2, 2), 16);
  return out;
}

async function verify(req, body) {
  const sig = req.headers.get("X-Signature-Ed25519");
  const ts = req.headers.get("X-Signature-Timestamp");
  if (!sig || !ts) return false;
  const data = new TextEncoder().encode(ts + body);
  for (const algo of [{ name: "Ed25519" }, { name: "NODE-ED25519", namedCurve: "NODE-ED25519" }]) {
    try {
      const key = await crypto.subtle.importKey("raw", hexToBytes(PUBLIC_KEY), algo, false, ["verify"]);
      return await crypto.subtle.verify(algo.name, key, hexToBytes(sig), data);
    } catch (e) { /* пробуем другой вариант */ }
  }
  return false;
}

function ephemeral(content, embeds) {
  return json({ type: 4, data: { content: content || "", embeds: embeds || [], flags: 64 } });
}

async function interactions(req, env) {
  const body = await req.text();
  if (!(await verify(req, body))) return new Response("bad signature", { status: 401 });
  const it = JSON.parse(body);
  if (it.type === 1) return json({ type: 1 }); // PING

  if (it.type === 2 && it.data && it.data.name === "игрок") {
    const opt = (it.data.options || []).find((o) => o.name === "ник");
    return ephemeral("", [await playerEmbed(env, opt ? String(opt.value) : "")]);
  }

  if (it.type === 3 && it.data && it.member) {
    const guild = it.guild_id;
    const user = it.member.user.id;
    const have = new Set(it.member.roles || []);
    if (it.data.custom_id.startsWith("role:")) {
      const key = it.data.custom_id.slice(5);
      const def = ROLES.find((r) => r.key === key && r.self); // кнопками — только Логгер и Искатель
      const roleId = def && (await getMeta(env, "role:" + key));
      if (!roleId) return ephemeral("Эту роль кнопкой получить нельзя.");
      if (key === "seeker") {
        // «Начать» только выдаёт
        if (!have.has(roleId)) await bot(env, "PUT", `/guilds/${guild}/members/${user}/roles/${roleId}`);
        return ephemeral(`Добро пожаловать! У вас роль <@&${roleId}>.`);
      }
      const add = !have.has(roleId);
      await bot(env, add ? "PUT" : "DELETE", `/guilds/${guild}/members/${user}/roles/${roleId}`);
      return ephemeral(add ? `Роль <@&${roleId}> выдана.` : `Роль <@&${roleId}> снята.`);
    }
  }
  return ephemeral("Неизвестная команда.");
}

const CLASS_COLORS = Object.fromEntries(CLASSES.map((c) => [c.key, c.color]));

function short(n) {
  n = Number(n) || 0;
  if (n >= 1e6) return (n / 1e6).toFixed(2) + "M";
  if (n >= 1e3) return (n / 1e3).toFixed(1) + "K";
  return String(Math.round(n));
}

async function playerEmbed(env, name) {
  name = name.trim();
  if (!name) return { title: "Укажите ник", color: 0xe74c3c };
  const rows = (await env.DB.prepare(
    "SELECT player, cls, role, boss, diff, instance, dps, hps, drank, hrank, ts, link FROM entries WHERE player_lc = ? ORDER BY ts DESC LIMIT 12"
  ).bind(name.toLowerCase()).all()).results || [];
  if (rows.length === 0) {
    return { title: `🔍 ${name}`, color: 0x95a5a6, description: "Отчётов с этим игроком пока нет. Проверьте ник (как в игре)." };
  }
  const lines = rows.map((r) => {
    const date = new Date(r.ts).toLocaleDateString("ru-RU", { day: "2-digit", month: "2-digit", timeZone: "Europe/Moscow" });
    const parts = [];
    if (r.dps > 0) parts.push(`ДПС **${short(r.dps)}**${r.drank ? ` (#${r.drank})` : ""}`);
    if (r.hps > 0 && (r.role === "HEALER" || r.hrank)) parts.push(`ХПС **${short(r.hps)}**${r.hrank ? ` (#${r.hrank})` : ""}`);
    const title = `${r.boss} · ${r.instance} ${DIFF[r.diff] || r.diff}`;
    return `${r.link ? `[${title}](${r.link})` : title} — ${parts.join(", ") || "—"} · ${date}`;
  });
  return {
    title: `🔍 ${rows[0].player}`,
    color: CLASS_COLORS[rows[0].cls] || ACCENT,
    description: lines.join("\n"),
    footer: { text: `Последние ${rows.length} боёв · SkadaDiscord` },
  };
}

// ---------------------------------------------------------------------------
// Discord API от имени бота

// сервер базы данных: GUILD_ID или единственный сервер, куда приглашён бот
let cachedGuild = null;
async function guildId(env) {
  if (env.GUILD_ID) return env.GUILD_ID;
  if (cachedGuild) return cachedGuild;
  const guilds = await bot(env, "GET", "/users/@me/guilds");
  if (guilds.length !== 1) throw new Error(`бот на ${guilds.length} серверах — укажите GUILD_ID в настройках Worker`);
  cachedGuild = guilds[0].id;
  return cachedGuild;
}

async function bot(env, method, path, body) {
  for (let attempt = 0; attempt < 4; attempt++) {
    const res = await fetch(DISCORD + path, {
      method,
      headers: { Authorization: "Bot " + env.DISCORD_BOT_TOKEN, "Content-Type": "application/json", "User-Agent": "SkadaRelay (https://skadadiscord.leancavladimir.workers.dev, 1.1)" },
      body: body ? JSON.stringify(body) : undefined,
    });
    if (res.status === 429) {
      const b = await res.json().catch(() => ({}));
      await sleep(Math.min(10, Number(b.retry_after) || 2) * 1000 + 200);
      continue;
    }
    if (res.status === 204) return null;
    if (!res.ok) throw new Error(`Discord API ${method} ${path}: ${res.status} ${(await res.text()).slice(0, 300)}`);
    return res.json();
  }
  throw new Error("Discord API rate limit");
}

// ---------------------------------------------------------------------------

function slug(s) {
  // как Discord называет текстовые каналы: нижний регистр, пробелы -> дефис
  return cut(String(s).toLowerCase().replace(/['’"]/g, "").replace(/[^\p{L}\p{N}]+/gu, "-").replace(/^-+|-+$/g, ""), 100) || "boss";
}

function cut(s, n) { return s.length > n ? s.slice(0, n) : s; }
function sleep(ms) { return new Promise((r) => setTimeout(r, ms)); }
function json(obj, status = 200) {
  return new Response(JSON.stringify(obj), { status, headers: { "Content-Type": "application/json; charset=utf-8" } });
}
