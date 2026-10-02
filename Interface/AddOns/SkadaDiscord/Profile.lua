-- Профили настроек (на аккаунт): каналы, инсты, боссы, окна отчёта, таймер.
-- Импорт/экспорт строкой, импорт из программы (Config.lua), выгрузка настроек для программы (JSON).

local AddOnName, ns = ...
if not _G.Skada then return end

local format, tinsert, tremove = string.format, table.insert, table.remove
local pairs, ipairs, type, tostring = pairs, ipairs, type, tostring

ns.VERSION = GetAddOnMetadata(AddOnName, "Version") or "?"
ns.AUTHOR = "Leanca Vladimir"
ns.CONTACT = "Discord: lyanka_v"

-------------------------------------------------------------------------------
-- метрики (окна на картинке) - те же, что понимает программа

ns.METRICS = {
	{id = "damage", name = "Урон (весь)", title = "Урон", rows = 25},
	{id = "damage_boss", name = "Урон по боссу", title = "Урон по боссу", rows = 25},
	{id = "damage_targets", name = "Урон по выбранным целям", title = "Урон по целям", rows = 25},
	{id = "damage_adds", name = "Урон по трешу (кроме босса и выбранных целей)", title = "Урон по трешу", rows = 25},
	{id = "damage_useful", name = "Полезный урон (без оверкилла)", title = "Полезный урон", rows = 25},
	{id = "healing", name = "Исцеление + поглощения", title = "Исцеление и поглощения", rows = 4},
	{id = "heal", name = "Только исцеление", title = "Исцеление", rows = 4},
	{id = "absorb", name = "Только поглощения (щиты)", title = "Поглощения", rows = 4},
	{id = "overheal", name = "Оверхил", title = "Оверхил", rows = 4},
	{id = "damage_taken", name = "Полученный урон", title = "Полученный урон", rows = 10},
	{id = "friendfire", name = "Урон по своим", title = "Урон по своим", rows = 10},
	{id = "deaths", name = "Смерти", title = "Смерти", rows = 10},
	{id = "interrupts", name = "Прерывания", title = "Прерывания", rows = 10},
	{id = "dispels", name = "Диспелы", title = "Диспелы", rows = 10},
	{id = "potions", name = "Зелья", title = "Зелья", rows = 25},
	{id = "fails", name = "Ошибки (Fails)", title = "Ошибки", rows = 10},
	{id = "sunder", name = "Раскол брони", title = "Раскол брони", rows = 10},
	{id = "activity", name = "Активность (% времени боя)", title = "Активность", rows = 25},
}

function ns.Metric(id)
	for _, m in ipairs(ns.METRICS) do
		if m.id == id then return m end
	end
	return ns.METRICS[1]
end

-- окна исцеления (в них можно оставить только хилеров)
function ns.IsHealMetric(id)
	return id == "healing" or id == "heal" or id == "absorb" or id == "overheal"
end

function ns.NewBlock(id, channel)
	local m = ns.Metric(id)
	local b = {metric = m.id, title = m.title, rows = m.rows, channel = channel or "", targets = {}}
	if ns.IsHealMetric(m.id) then b.healers = true end
	return b
end

-------------------------------------------------------------------------------
-- стандартный профиль

local ICC = {
	"Лорд Ребрад", "Леди Смертный Шепот", "Бой на кораблях", "Саурфанг Смертоносный",
	"Тухлопуз", "Гниломорд", "Профессор Мерзоцид", "Совет Принцев Крови",
	"Кровавая королева Лана'тель", "Валитрия Сноходица", "Синдрагоса", "Король-лич",
}
local RS = {"Балтарус Рожденный в Битве", "Савиана Огненная Пропасть", "Генерал Заритриан", "Халион"}

function ns.DefaultBlocks(instName, bossName, dps, hps)
	local list = {}
	if instName == "РС" then
		tinsert(list, ns.NewBlock("damage_boss", dps))
		if bossName == "Халион" then
			local big = ns.NewBlock("damage_targets", dps)
			big.title = "Урон по большому пламени"
			big.targets = {"Живое адское пламя"}
			tinsert(list, big)
		end
		tinsert(list, ns.NewBlock("damage_adds", dps))
	else
		tinsert(list, ns.NewBlock("damage", dps))
	end
	local heal = ns.NewBlock("healing", hps)
	if bossName == "Валитрия Сноходица" then heal.rows = 10 end
	tinsert(list, heal)
	return list
end

local function NewInstance(name, zone, bosses, last)
	local inst = {name = name, zones = {zone}, trigger = "all", last = last, bosses = {}}
	for _, b in ipairs(bosses) do
		tinsert(inst.bosses, {name = b, enabled = true, blocks = ns.DefaultBlocks(name, b, "", "")})
	end
	return inst
end

function ns.DefaultProfile()
	return {
		timer = 300,
		sound = true,
		others = false,
		sendDB = true, -- отправлять отчёт в базу данных (общий сервер отчётов)
		search = "alt",
		username = "Skada",
		scale = 1.5,
		defaultDps = "",
		defaultHps = "",
		channels = {},
		instances = {
			NewInstance("ЦЛК", "Цитадель Ледяной Короны", ICC, "Король-лич"),
			NewInstance("РС", "Рубиновое святилище", RS, "Халион"),
		},
	}
end

-------------------------------------------------------------------------------
-- доступ к профилям

local function Copy(t)
	if type(t) ~= "table" then return t end
	local c = {}
	for k, v in pairs(t) do c[k] = Copy(v) end
	return c
end
ns.Copy = Copy

-- недостающие поля (после обновления аддона)
local function Fix(p)
	local d = ns.DefaultProfile()
	for k, v in pairs(d) do
		if p[k] == nil then p[k] = v end
	end
	for _, ch in ipairs(p.channels) do
		ch.hooks = ch.hooks or {}
		if ch.webhook then
			if ch.webhook ~= "" then tinsert(ch.hooks, ch.webhook) end
			ch.webhook = nil
		end
	end
	for _, inst in ipairs(p.instances) do
		inst.zones = inst.zones or {}
		inst.bosses = inst.bosses or {}
		inst.trigger = inst.trigger or "all"
		inst.last = inst.last or ""
		for _, boss in ipairs(inst.bosses) do
			boss.blocks = boss.blocks or {}
			for _, b in ipairs(boss.blocks) do
				local m = ns.Metric(b.metric)
				b.metric = b.metric or m.id -- незнакомый id (новее аддона) не трогаем
				b.title = b.title or m.title
				b.rows = tonumber(b.rows) or m.rows
				b.channel = b.channel or ""
				b.targets = b.targets or {}
				-- окна исцеления: по умолчанию только хилеры
				if b.healers == nil and ns.IsHealMetric(b.metric) then b.healers = true end
			end
		end
	end
	return p
end

function ns.InitProfiles()
	local DB = ns.DB
	DB.profiles = DB.profiles or {}
	if not next(DB.profiles) then
		DB.profiles["Основной"] = ns.DefaultProfile()
	end
	for _, p in pairs(DB.profiles) do Fix(p) end
	if not DB.profileName or not DB.profiles[DB.profileName] then
		DB.profileName = next(DB.profiles)
	end
	ns.ImportFromProgram()
end

function ns.Profile()
	return ns.DB.profiles[ns.DB.profileName]
end
-- активный профиль (до загрузки SavedVariables - стандартный)
ns.Config = function()
	return ns.DB and ns.DB.profiles and ns.Profile() or ns.DefaultProfile()
end

function ns.SetProfile(name)
	if ns.DB.profiles[name] then
		ns.DB.profileName = name
		ns.Print("профиль: |cffffbb00" .. name .. "|r")
		ns.Refresh()
	end
end

function ns.NewProfile(name, copyCurrent)
	name = strtrim(name or "")
	if name == "" then return end
	if ns.DB.profiles[name] then
		ns.Print("профиль «" .. name .. "» уже есть.")
		return
	end
	ns.DB.profiles[name] = copyCurrent and Copy(ns.Profile()) or ns.DefaultProfile()
	ns.SetProfile(name)
end

function ns.RenameProfile(name)
	name = strtrim(name or "")
	if name == "" or ns.DB.profiles[name] then return end
	ns.DB.profiles[name] = ns.Profile()
	ns.DB.profiles[ns.DB.profileName] = nil
	ns.DB.profileName = name
	ns.Refresh()
end

function ns.DeleteProfile()
	local count = 0
	for _ in pairs(ns.DB.profiles) do count = count + 1 end
	if count <= 1 then
		ns.Print("нельзя удалить единственный профиль.")
		return
	end
	ns.DB.profiles[ns.DB.profileName] = nil
	ns.DB.profileName = next(ns.DB.profiles)
	ns.Refresh()
end

-------------------------------------------------------------------------------
-- каналы

function ns.FindChannel(id)
	for _, ch in ipairs(ns.Profile().channels) do
		if ch.id == id then return ch end
	end
end

function ns.FindChannelByName(name)
	for _, ch in ipairs(ns.Profile().channels) do
		if ch.name == name then return ch end
	end
end

function ns.IsWebhook(url)
	return type(url) == "string" and url:match("^https://[%w%.]*discord[%w]*%.com/api/webhooks/%d+/[%w_%-]+$") ~= nil
end

function ns.NewChannel(name)
	local p = ns.Profile()
	local id
	repeat id = format("c%06x", math.random(0, 0xffffff)) until not ns.FindChannel(id)
	local ch = {id = id, name = name, hooks = {}}
	tinsert(p.channels, ch)
	return ch
end

function ns.DeleteChannel(id)
	local p = ns.Profile()
	for i, ch in ipairs(p.channels) do
		if ch.id == id then tremove(p.channels, i) break end
	end
	for _, inst in ipairs(p.instances) do
		for _, boss in ipairs(inst.bosses) do
			for _, b in ipairs(boss.blocks) do
				if b.channel == id then b.channel = "" end
			end
		end
	end
	if p.defaultDps == id then p.defaultDps = "" end
	if p.defaultHps == id then p.defaultHps = "" end
end

-- строки "Название: https://discord.com/api/webhooks/..." (канал с тем же названием получает ссылку)
function ns.PasteChannels(text)
	local added, updated = 0, 0
	for line in (text or ""):gmatch("[^\r\n]+") do
		local name, url = line:match("^%s*(.-)[%s:=%-]*(https://%S+)%s*$")
		if url and ns.IsWebhook(url) then
			name = strtrim(name or ""):gsub(":$", "")
			if name == "" then name = "Канал " .. (#ns.Profile().channels + 1) end
			local ch = ns.FindChannelByName(name)
			if ch then
				local exists = false
				for _, h in ipairs(ch.hooks) do if h == url then exists = true end end
				if not exists then tinsert(ch.hooks, url) updated = updated + 1 end
			else
				ch = ns.NewChannel(name)
				tinsert(ch.hooks, url)
				added = added + 1
			end
		end
	end
	ns.Print(format("каналов добавлено: %d, ссылок добавлено в существующие: %d.", added, updated))
end

-- босс отправляет отчёт, если включён и хотя бы одно окно ведёт в канал со ссылкой
function ns.BossSends(boss)
	if not boss or not boss.enabled then return false end
	for _, b in ipairs(boss.blocks) do
		local ch = ns.FindChannel(b.channel)
		if ch and #ch.hooks > 0 then return true end
	end
	return false
end

-------------------------------------------------------------------------------
-- экспорт / импорт строкой (AceSerializer + LibDeflate из Skada)

local PREFIX = "!SKD1!"

local function StripLinks(p)
	for _, ch in ipairs(p.channels) do ch.hooks = {} end
	return p
end

function ns.ExportProfile(withLinks)
	local AceSerializer = LibStub("AceSerializer-3.0", true)
	local LibDeflate = LibStub("LibDeflate", true)
	if not AceSerializer or not LibDeflate then return "нет библиотек Skada (AceSerializer / LibDeflate)" end
	local p = Copy(ns.Profile())
	if not withLinks then StripLinks(p) end
	local data = AceSerializer:Serialize({name = ns.DB.profileName, profile = p, v = 1})
	return PREFIX .. LibDeflate:EncodeForPrint(LibDeflate:CompressDeflate(data))
end

function ns.ImportProfile(text)
	local AceSerializer = LibStub("AceSerializer-3.0", true)
	local LibDeflate = LibStub("LibDeflate", true)
	text = strtrim(text or ""):gsub("%s", "")
	if text:sub(1, #PREFIX) ~= PREFIX or not AceSerializer or not LibDeflate then
		ns.Print("|cffff4040это не строка профиля SkadaDiscord.|r")
		return
	end
	local decoded = LibDeflate:DecodeForPrint(text:sub(#PREFIX + 1))
	local data = decoded and LibDeflate:DecompressDeflate(decoded)
	local ok, t = false, nil
	if data then ok, t = AceSerializer:Deserialize(data) end
	if not ok or type(t) ~= "table" or type(t.profile) ~= "table" then
		ns.Print("|cffff4040строка профиля повреждена.|r")
		return
	end
	local name = t.name or "Импорт"
	while ns.DB.profiles[name] do name = name .. " (2)" end
	ns.DB.profiles[name] = Fix(t.profile)
	ns.SetProfile(name)
	ns.Print("профиль «" .. name .. "» импортирован и включён.")
end

-- программа может передать профиль через Config.lua (SkadaDiscordImport), один раз
function ns.ImportFromProgram()
	local imp = _G.SkadaDiscordImport
	if type(imp) ~= "table" or type(imp.profile) ~= "table" or not imp.id then return end
	if ns.DB.importId == imp.id then return end
	ns.DB.importId = imp.id
	local name = imp.name or "Импорт"
	ns.DB.profiles[name] = Fix(Copy(imp.profile))
	ns.DB.profileName = name
	ns.Print("настройки из программы импортированы в профиль «" .. name .. "».")
end

-------------------------------------------------------------------------------
-- настройки для программы: JSON в SavedVariables (пишется при /reload и выходе)

function ns.ProgramConfig()
	local p = ns.Profile()
	local instances = {}
	for _, inst in ipairs(p.instances) do
		local bosses = {}
		for _, boss in ipairs(inst.bosses) do
			tinsert(bosses, {name = boss.name, enabled = boss.enabled and true or false, blocks = boss.blocks})
		end
		tinsert(instances, {name = inst.name, zones = inst.zones, trigger = inst.trigger or "all", lastBoss = inst.last or "", bosses = bosses})
	end
	return {
		version = 3,
		addonVersion = ns.VERSION,
		profile = ns.DB.profileName,
		importId = ns.DB.importId or "",
		username = p.username,
		scale = p.scale,
		search = "alt", -- ники для поиска Discord - всегда в скрытой подписи картинки
		others = p.others,
		sendDB = p.sendDB ~= false,
		defaultDps = p.defaultDps,
		defaultHps = p.defaultHps,
		channels = p.channels,
		instances = instances,
	}
end
