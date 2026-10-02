-- SkadaDiscord: собирает данные по убийствам боссов из Skada и сохраняет их в SavedVariables,
-- откуда их забирает программа SkadaDiscord.exe и отправляет в Discord.
-- Аддоны WoW не имеют доступа к сети, поэтому передача идёт через файл
-- WTF\Account\<аккаунт>\SavedVariables\SkadaDiscord.lua (пишется при /reload или выходе).
-- Что и куда отправлять, настраивается в игре (/sd config, профили в Profile.lua, окно в Options.lua);
-- при /reload и выходе настройки выгружаются для программы в SkadaDiscordDB.configJson.

local AddOnName, ns = ...
local Skada = _G.Skada
if not Skada then return end

local format, floor, max = string.format, math.floor, math.max
local tinsert, tremove, tconcat = table.insert, table.remove, table.concat
local pairs, ipairs, type, tostring, tonumber, pcall, next = pairs, ipairs, type, tostring, tonumber, pcall, next

local MAX_REPORTS = 30 -- сколько последних отчётов хранить в файле (полный ЦЛК = 12)
local NEW_RAID_GAP = 12 * 3600 -- вайпы старше этого не считаются попытками текущего рейда

ns.MAX_REPORTS = MAX_REPORTS
ns.pending = {} -- id отчётов, созданных в этой сессии (ещё не записаны на диск)

function ns.Print(msg)
	DEFAULT_CHAT_FRAME:AddMessage("|cff7289daSkadaDiscord|r: " .. tostring(msg))
end
local Print = ns.Print

-- ns.Config() (активный профиль) определён в Profile.lua

-------------------------------------------------------------------------------
-- JSON (данные передаются строкой, чтобы программе не нужен был парсер Lua)

local json_escape_map = {['"'] = '\\"', ["\\"] = "\\\\", ["\n"] = "\\n", ["\r"] = "\\r", ["\t"] = "\\t"}
local function json_string(s)
	s = tostring(s):gsub('[%c"\\]', function(c)
		return json_escape_map[c] or format("\\u%04x", c:byte())
	end)
	return '"' .. s .. '"'
end

local function json_encode(v)
	local t = type(v)
	if t == "string" then
		return json_string(v)
	elseif t == "number" then
		if v ~= v or v == math.huge or v == -math.huge then return "0" end
		if v == floor(v) then return format("%.0f", v) end
		return format("%.2f", v)
	elseif t == "boolean" then
		return v and "true" or "false"
	elseif t == "table" then
		local out = {}
		if #v > 0 or next(v) == nil then
			for i = 1, #v do out[i] = json_encode(v[i]) end
			return "[" .. tconcat(out, ",") .. "]"
		end
		for k, val in pairs(v) do
			out[#out + 1] = json_string(k) .. ":" .. json_encode(val)
		end
		return "{" .. tconcat(out, ",") .. "}"
	end
	return "null"
end
ns.JsonEncode = json_encode

-- краткие сведения об отчёте для списка в окне
function ns.ReportInfo(s)
	local id = s:match('"id":"([^"]*)"') or ""
	local _, players = s:gsub('"col":', "")
	return {
		id = id,
		key = s:match('"key":"([^"]*)"') or id,
		success = not s:find('"success":false', 1, true),
		boss = s:match('"boss":"([^"]*)"') or "?",
		diff = s:match('"diff":"([^"]*)"') or "",
		duration = s:match('"durationText":"([^"]*)"') or "",
		start = tonumber(s:match('"start":(%d+)')) or 0,
		attempt = tonumber(s:match('"attempt":(%d+)')),
		players = players,
	}
end

-------------------------------------------------------------------------------
-- настройки боссов (из активного профиля)

-- размер рейда из сложности Skada: "25h" -> 25
local function DiffSize(diff)
	return tonumber((diff or ""):match("^(%d+)")) or 0
end

-- инст и босс из настроек по имени босса
function ns.FindBoss(name)
	if not name then return end
	for _, inst in ipairs(ns.Config().instances or {}) do
		for _, boss in ipairs(inst.bosses or {}) do
			if boss.name == name then return inst, boss end
		end
	end
end

-- инст из настроек по названию зоны
function ns.FindInstance(zone)
	for _, inst in ipairs(ns.Config().instances or {}) do
		for _, z in ipairs(inst.zones or {}) do
			if z == zone then return inst end
		end
	end
end

-------------------------------------------------------------------------------
-- построение отчёта: все данные по игрокам, программа сама выбирает, что показать

local function ClassColor(class)
	local ok, color = pcall(Skada.classcolors, class)
	if ok and color and color.colorStr then
		return color.colorStr:sub(-6)
	end
	return "ffffff"
end

function ns.SetKey(set)
	return format("%s-%s", tostring(set.starttime), set.mobname or set.name or "?")
end

-------------------------------------------------------------------------------
-- кто убил босса: ID сохранения рейда и рейд-лидер (программа по ним узнаёт один и тот же бой
-- у разных игроков рейда: база данных и общие окна в одном вебхуке отправляются один раз)

-- ID сохранения рейда (строкой) для зоны из списка zones и размера рейда; "" - не сохранён
local function FindLockout(zones, size)
	if not size or size == 0 or not zones or not next(zones) then return "" end
	for i = 1, GetNumSavedInstances() do
		local name, id, _, _, locked, _, _, _, maxPlayers = GetSavedInstanceInfo(i)
		if name and zones[name] and locked and maxPlayers == size and id then
			return format("%.0f", id)
		end
	end
	return ""
end

-- зоны инста босса (из настроек) и текущая зона
local function LockoutZones(inst, withCurrent)
	local zones = {}
	if inst then
		for _, z in ipairs(inst.zones or {}) do zones[z] = true end
	end
	if withCurrent then
		local z = GetRealZoneText()
		if z and z ~= "" then zones[z] = true end
	end
	return zones
end

-- рейд-лидер (лидер группы; сам игрок, если без группы)
local function GroupLeader()
	local n = GetNumRaidMembers()
	if n > 0 then
		for i = 1, n do
			local name, rank = GetRaidRosterInfo(i)
			if name and rank == 2 then return name end
		end
	elseif GetNumPartyMembers() > 0 then
		local index = GetPartyLeaderIndex()
		if index and index > 0 then
			local name = UnitName("party" .. index)
			if name then return name end
		end
	end
	return UnitName("player") or ""
end

local function BuildReport(set, manual, attempt)
	local abs = Skada.profile.absdamage
	local settime = Skada:GetSetTime(set)
	local players, enemies = {}, {}

	for name, actor in pairs(set.actors) do
		if actor.fake then
			-- группы Skada ("Рейдовый мусор" и т.п.) пропускаем
		elseif actor.enemy then
			enemies[#enemies + 1] = {n = name, b = actor.class == "BOSS"}
		else
			local p = {
				n = Skada:FormatName(name),
				c = actor.class or "UNKNOWN",
				s = actor.spec or 0,
				col = ClassColor(actor.class),
				t = Skada:GetActiveTime(set, actor), -- как в Skada: время активности или боя
				dmg = (abs and actor.totaldamage or actor.damage) or 0,
				ok = actor.overkill or 0,
				heal = actor.heal or 0,
				abs = actor.absorb or 0,
				oh = actor.overheal or 0,
				dt = (abs and actor.totaldamaged or actor.damaged) or 0,
				death = actor.death or 0,
				intr = actor.interrupt or 0,
				disp = actor.dispel or 0,
				pot = actor.potion or 0,
				fail = actor.fail or 0,
				ff = actor.friendfire or 0,
				r = actor.role or "NONE", -- роль Skada: HEALER, DAMAGER, TANK, NONE
				sun = actor.sunder or 0, -- модуль Skada "Sunder Counter"
			}
			-- урон по каждой цели (для "урон по боссу", "по аддам" и т.п.)
			if actor.damagespells then
				local tg = {}
				for _, spell in pairs(actor.damagespells) do
					if spell.targets then
						for tname, info in pairs(spell.targets) do
							local amount = abs and info.total or info.amount
							if amount and amount > 0 then
								tg[tname] = (tg[tname] or 0) + amount
							end
						end
					end
				end
				if next(tg) then p.tg = tg end
			end
			players[#players + 1] = p
		end
	end

	local inst = ns.FindBoss(set.mobname)
	local zone = inst and inst.zones and inst.zones[1] or (manual and "" or GetRealZoneText()) or ""
	local key = ns.SetKey(set)

	-- ID сохранения: для только что убитого босса; для боя из истории - только если он недавний
	-- и мы всё ещё в этом инсте (иначе сохранение может быть уже от другого КД)
	local lockZones = {}
	if not manual then
		lockZones = LockoutZones(inst, true)
	elseif inst and set.starttime and time() - set.starttime < 4 * 3600 and LockoutZones(inst)[GetRealZoneText() or ""] then
		lockZones = LockoutZones(inst)
	end
	local size = DiffSize(set.diff)

	return {
		v = 2,
		addonVersion = ns.VERSION,
		key = key, -- один и тот же бой
		id = format("%s-%s", key, tostring(time())), -- каждое добавление уникально: программа отправит его заново
		manual = manual and true or false,
		success = set.success and true or false,
		boss = set.mobname or set.name or "?",
		instance = inst and inst.name or "",
		zone = zone,
		diff = set.diff or "",
		start = set.starttime,
		duration = settime,
		durationText = Skada:FormatTime(settime),
		attempt = attempt or 0,
		player = UnitName("player"),
		realm = GetRealmName(),
		guild = GetGuildInfo("player") or "",
		lockout = FindLockout(lockZones, size), -- "" - ещё не известен (см. PatchLockouts)
		leader = GroupLeader(),
		db = ns.Config().sendDB ~= false, -- отправлять ли в базу данных
		players = players,
		enemies = enemies,
	}, lockZones, size
end

-------------------------------------------------------------------------------
-- ID сохранения после первого убийства в КД появляется не сразу: отчёт сразу встаёт в очередь,
-- а когда игра пришлёт UPDATE_INSTANCE_INFO (ждём до 30 сек), строка отчёта пересобирается с ID

local LOCKOUT_WAIT = 30
local lockoutWait = {} -- id отчёта -> {report, zones, size, deadline}
local lastRaidInfo = 0

local function PatchLockouts()
	for id, w in pairs(lockoutWait) do
		local lockout = FindLockout(w.zones, w.size)
		if lockout ~= "" then
			lockoutWait[id] = nil
			w.report.lockout = lockout
			local needle = '"id":' .. json_string(id)
			local reports = ns.DB.reports
			for i = 1, #reports do
				if reports[i]:find(needle, 1, true) then
					reports[i] = json_encode(w.report)
					break
				end
			end
		end
	end
end

-- раз в секунду: забыть просроченные, раз в 5 сек переспросить сервер
local function TickLockouts(now)
	if not next(lockoutWait) then return end
	for id, w in pairs(lockoutWait) do
		if now > w.deadline then lockoutWait[id] = nil end
	end
	if next(lockoutWait) and now - lastRaidInfo >= 5 then
		lastRaidInfo = now
		RequestRaidInfo()
	end
end

-------------------------------------------------------------------------------
-- очередь отчётов

function ns.IsQueued(set)
	local needle = '"key":' .. json_string(ns.SetKey(set))
	for _, s in ipairs(ns.DB.reports) do
		if s:find(needle, 1, true) then return true end
	end
	return false
end

-- бои из истории Skada (новые первыми)
function ns.GetHistory()
	local list = {}
	local sets = Skada.sets
	if not sets then return list end
	for i = 1, #sets do
		local set = sets[i]
		if set and set.actors then
			list[#list + 1] = set
		end
	end
	return list
end

function ns.QueueSet(set, manual, attempt)
	if ns.IsQueued(set) then
		if manual then Print("этот бой уже в списке. Чтобы отправить заново, удалите его из списка и добавьте снова.") end
		return
	end

	local ok, report, lockZones, size = pcall(BuildReport, set, manual, attempt)
	if not ok then
		Print("|cffff0000ошибка при создании отчёта:|r " .. tostring(report))
		return
	end
	if #report.players == 0 then
		Print("в бою с " .. tostring(report.boss) .. " нет данных об игроках, отчёт не создан.")
		return
	end

	local reports = ns.DB.reports
	tinsert(reports, json_encode(report))
	while #reports > MAX_REPORTS do tremove(reports, 1) end
	if report.lockout == "" and report.success and lockZones and next(lockZones) then
		-- ID сохранения ещё нет: допишем, когда игра его пришлёт
		lockoutWait[report.id] = {report = report, zones = lockZones, size = size, deadline = GetTime() + LOCKOUT_WAIT}
		lastRaidInfo = GetTime()
		RequestRaidInfo()
	end
	ns.pending[report.id] = true
	ns.lastAdded = report.id
	local extra = (attempt and attempt > 1) and format(", с %d-й попытки", attempt) or ""
	Print(format("отчёт по |cffffbb00%s|r готов (%d игроков%s).", report.boss, #report.players, extra))
	ns.Refresh()
	return report
end

function ns.QueueLast()
	for _, set in ipairs(ns.GetHistory()) do
		if set.gotboss and set.success then
			return ns.QueueSet(set, true)
		end
	end
	Print("в истории Skada нет убитых боссов.")
end

function ns.PendingCount()
	local n = 0
	for _ in pairs(ns.pending) do n = n + 1 end
	return n
end

function ns.Remove(index)
	local s = ns.DB.reports[index]
	if not s then return end
	ns.pending[ns.ReportInfo(s).id] = nil
	tremove(ns.DB.reports, index)
	ns.Refresh()
end

function ns.Clear()
	wipe(ns.DB.reports)
	wipe(ns.pending)
	ns.Refresh()
	Print("список очищен.")
end

-- отправка = перезагрузка интерфейса (только так WoW записывает файл на диск)
function ns.Send()
	ReloadUI()
end

-- перерисовка интерфейса (UI.lua заменяет её своей)
function ns.Refresh() end

-------------------------------------------------------------------------------
-- попытки (вайпы до убийства)

local function AttemptKey(set)
	return format("%s|%s|%d", UnitName("player") or "?", set.mobname or "?", DiffSize(set.diff))
end

local function CountWipe(set)
	local key = AttemptKey(set)
	local a = ns.DB.attempts[key]
	if not a or time() - a.last > NEW_RAID_GAP then
		a = {wipes = 0}
		ns.DB.attempts[key] = a
	end
	a.wipes = a.wipes + 1
	a.last = time()
end

-- номер попытки, на которой убит босс (и сброс счётчика)
local function TakeAttempt(set)
	local key = AttemptKey(set)
	local a = ns.DB.attempts[key]
	ns.DB.attempts[key] = nil
	if a and time() - a.last <= NEW_RAID_GAP then
		return a.wipes + 1
	end
	return 1
end

-------------------------------------------------------------------------------
-- прогресс рейда (для таймера "после всех боссов")

local function ProgressKey(inst, size)
	return format("%s|%s|%d", UnitName("player") or "?", inst.name, size)
end

function ns.GetProgress(inst, size)
	local key = ProgressKey(inst, size)
	local p = ns.DB.progress[key]
	if not p then
		p = {kills = {}}
		ns.DB.progress[key] = p
	end
	return p, key
end

function ns.ResetProgress(key)
	if key then
		ns.DB.progress[key] = nil
	else
		wipe(ns.DB.progress)
	end
	ns.Refresh()
end

-- сколько боссов убито и сколько всего
function ns.CountKills(inst, p)
	local done, total = 0, 0
	for _, boss in ipairs(inst.bosses or {}) do
		total = total + 1
		if p.kills[boss.name] then done = done + 1 end
	end
	return done, total
end

-- последний активный прогресс этого персонажа (для окна)
function ns.CurrentProgress()
	local me = (UnitName("player") or "?") .. "|"
	local best, bestKey
	for key, p in pairs(ns.DB.progress) do
		if key:sub(1, #me) == me and p.last and (not best or p.last > best.last) then
			best, bestKey = p, key
		end
	end
	if not best then return end
	local _, instName, size = strsplit("|", bestKey)
	for _, inst in ipairs(ns.Config().instances or {}) do
		if inst.name == instName then
			return inst, best, tonumber(size), bestKey
		end
	end
end

-- новый КД рейда: сравниваем ID сохранения с запомненным
local function CheckLockout()
	local zone = GetRealZoneText()
	local inst = ns.FindInstance(zone)
	if not inst then return end
	local _, itype, _, _, maxPlayers = GetInstanceInfo()
	if itype ~= "raid" or not maxPlayers then return end

	local p, key = ns.GetProgress(inst, maxPlayers)
	local savedId
	for i = 1, GetNumSavedInstances() do
		local name, id, _, _, locked, _, _, _, maxP = GetSavedInstanceInfo(i)
		if name == zone and maxP == maxPlayers and locked then savedId = id end
	end
	if savedId then
		if p.id and p.id ~= savedId then
			ns.DB.progress[key] = {kills = {}, id = savedId}
			Print(format("%s %d: новое КД, прогресс сброшен.", inst.name, maxPlayers))
		else
			p.id = savedId
		end
	elseif p.id then
		-- КД закончилось, а в этом рейде ещё никого не убили
		ns.DB.progress[key] = {kills = {}}
	end
	ns.Refresh()
end

-------------------------------------------------------------------------------
-- убийство босса

local function OnKill(set)
	set.success = true -- заодно чиним отметку в истории Skada
	local attempt = TakeAttempt(set)
	local cfg = ns.Config()
	local inst, boss = ns.FindBoss(set.mobname)

	if inst then
		local p = ns.GetProgress(inst, DiffSize(set.diff))
		p.kills[set.mobname] = time()
		p.last = time()
	end

	if inst and boss and not boss.enabled then
		Print(format("|cffffbb00%s|r убит, отчёт выключен в настройках.", set.mobname))
	elseif inst and boss and not ns.BossSends(boss) and cfg.sendDB == false then
		Print(format("|cffffbb00%s|r убит, но не выбран канал — отчёт не создан.", set.mobname))
	elseif inst and boss and not ns.BossSends(boss) then
		-- своих каналов нет, но отчёт нужен для базы данных
		Print(format("|cffffbb00%s|r убит: канал не выбран, отчёт уйдёт только в базу данных.", set.mobname))
		ns.QueueSet(set, false, attempt)
	elseif not inst and not cfg.others then
		-- босс не из настроенных инстов (например, "Прелесть")
	else
		ns.QueueSet(set, false, attempt)
		if ns.DB.openOnKill and ns.ShowWindow then ns.ShowWindow() end
	end

	-- таймер автоотправки
	if inst then
		local fire
		if inst.trigger == "last" then
			fire = set.mobname == inst.last
		else
			local done, total = ns.CountKills(inst, ns.GetProgress(inst, DiffSize(set.diff)))
			fire = total > 0 and done >= total
		end
		if fire then
			if ns.PendingCount() > 0 and ns.StartTimer then
				ns.StartTimer(cfg.timer or 300, format("%s зачищен!", inst.name))
			else
				Print(format("%s зачищен! Новых отчётов для отправки нет.", inst.name))
			end
		end
	end
	RequestRaidInfo() -- после первого убийства в КД появится ID сохранения (UPDATE_INSTANCE_INFO)
	ns.Refresh()
end

-- Skada не всегда видит убийство (Совет Принцев, Валитрия...), поэтому слушаем и DBM.
local lastDBMKill = 0
local recentWipes = {} -- set -> время, вайп засчитывается, если DBM не скажет "убит" за 20 сек
local lastBoss, lastTime = nil, 0
local handler = {}

function handler:OnSetComplete(_, set)
	if not set or not set.gotboss then return end

	-- сегменты фаз приходят сразу после основного: берём только основной
	local now = GetTime()
	if lastBoss == set.mobname and now - lastTime < 15 then return end
	lastBoss, lastTime = set.mobname, now

	if set.success or now - lastDBMKill < 60 then
		OnKill(set)
	else
		recentWipes[set] = now
	end
end

local function OnDBMKill()
	lastDBMKill = GetTime()
	-- Skada закрыла бой раньше, чем DBM сообщил об убийстве
	for set, t in pairs(recentWipes) do
		if GetTime() - t < 20 then
			recentWipes[set] = nil
			OnKill(set)
		end
	end
end

-------------------------------------------------------------------------------
-- события

local frame = CreateFrame("Frame")
local elapsedTotal = 0

frame:SetScript("OnUpdate", function(_, elapsed)
	elapsedTotal = elapsedTotal + elapsed
	if elapsedTotal < 1 then return end
	elapsedTotal = 0
	local now = GetTime()
	for set, t in pairs(recentWipes) do
		if now - t >= 20 then
			recentWipes[set] = nil
			CountWipe(set)
		end
	end
	TickLockouts(now)
end)

-- настройки для программы: JSON активного профиля в SavedVariables
local function SaveProgramConfig()
	if not ns.DB or not ns.DB.profiles or not ns.ProgramConfig then return end
	local ok, s = pcall(function() return json_encode(ns.ProgramConfig()) end)
	if ok then
		ns.DB.configJson = s
	else
		Print("|cffff0000ошибка при сохранении настроек для программы:|r " .. tostring(s))
	end
end

frame:RegisterEvent("ADDON_LOADED")
frame:RegisterEvent("PLAYER_ENTERING_WORLD")
frame:RegisterEvent("UPDATE_INSTANCE_INFO")
frame:RegisterEvent("PLAYER_LOGOUT") -- и при /reload, до записи SavedVariables
frame:SetScript("OnEvent", function(self, event, name)
	if event == "ADDON_LOADED" and name == AddOnName then
		self:UnregisterEvent("ADDON_LOADED")
		SkadaDiscordDB = SkadaDiscordDB or {}
		local DB = SkadaDiscordDB
		DB.reports = DB.reports or {}
		DB.attempts = DB.attempts or {}
		DB.progress = DB.progress or {}
		DB.minimap = DB.minimap or {}
		DB.auto = nil -- старая настройка, заменена таймером
		if DB.openOnKill == nil then DB.openOnKill = false end
		ns.DB = DB
		ns.InitProfiles()
		SaveProgramConfig() -- чтобы configJson был в файле всегда
		Skada.RegisterCallback(handler, "Skada_SetComplete", "OnSetComplete")
		if DBM and DBM.RegisterCallback then
			DBM:RegisterCallback("DBM_Kill", OnDBMKill)
		end
		if ns.InitOptions then ns.InitOptions() end
		if ns.InitUI then ns.InitUI() end
	elseif event == "PLAYER_LOGOUT" then
		SaveProgramConfig()
	elseif event == "PLAYER_ENTERING_WORLD" then
		RequestRaidInfo()
	elseif event == "UPDATE_INSTANCE_INFO" and ns.DB then
		PatchLockouts()
		CheckLockout()
	end
end)

-------------------------------------------------------------------------------
-- команды: /sd

SLASH_SKADADISCORD1 = "/sd"
SLASH_SKADADISCORD2 = "/skadadiscord"
SlashCmdList.SKADADISCORD = function(msg)
	local cmd = (msg or ""):lower():match("^%s*(%S*)")
	if cmd == "" then
		if ns.ToggleWindow then ns.ToggleWindow() end
	elseif cmd == "send" then
		ns.Send()
	elseif cmd == "history" or cmd == "h" then
		if ns.ShowHistory then ns.ShowHistory() end
	elseif cmd == "last" then
		if ns.QueueLast() and ns.ShowWindow then ns.ShowWindow() end
	elseif cmd == "timer" then
		if ns.StartTimer then ns.StartTimer(ns.Config().timer or 300, "Отправка по команде") end
	elseif cmd == "stop" then
		if ns.StopTimer then ns.StopTimer() end
	elseif cmd == "clear" then
		ns.Clear()
	elseif cmd == "config" or cmd == "settings" then
		if ns.OpenSettings then ns.OpenSettings() end
	else
		Print("/sd - окно, /sd config - настройки, /sd history - история боёв, /sd send - отправить, /sd timer - запустить таймер, /sd stop - остановить таймер, /sd clear - очистить")
	end
end
