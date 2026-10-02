-- Окно настроек в игре (AceConfig из Skada): общие, профили, каналы, инсты -> боссы -> окна отчёта.

local _, ns = ...
if not _G.Skada then return end

local format, tinsert, tremove, tconcat = string.format, table.insert, table.remove, table.concat
local ipairs = ipairs

local APP = "SkadaDiscord"
local ACD, ACR
local exportLinks = false

local function P() return ns.Profile() end

local function Changed()
	ns.Refresh()
	if ACR then ACR:NotifyChange(APP) end
end

local function Trim(v) return strtrim(v or "") end

-------------------------------------------------------------------------------
-- значения для списков

local function ChannelValues(none)
	local t = {[""] = none or "|cffff4040— не выбран —|r"}
	for _, ch in ipairs(P().channels) do
		t[ch.id] = ch.name .. (#ch.hooks == 0 and " |cffff4040(нет ссылок)|r" or "")
	end
	return t
end

local function MetricValues()
	local t = {}
	for _, m in ipairs(ns.METRICS) do t[m.id] = m.name end
	return t
end

local function Usage(id)
	local list = {}
	for _, inst in ipairs(P().instances) do
		for _, boss in ipairs(inst.bosses) do
			for _, b in ipairs(boss.blocks) do
				if b.channel == id then tinsert(list, boss.name) break end
			end
		end
	end
	return #list > 0 and tconcat(list, ", ") or "|cff888888не используется|r"
end

local function BossStatus(boss)
	if not boss.enabled then return "|cff888888Выключен: отчёт не отправляется.|r" end
	if not ns.BossSends(boss) then
		if P().sendDB ~= false then return "|cffffcc00Ни одно окно не ведёт в канал со ссылкой — отчёт уйдёт только в базу данных.|r" end
		return "|cffff4040Включён, но ни одно окно не ведёт в канал со ссылкой — отчёт никуда не уйдёт.|r"
	end
	local names, seen = {}, {}
	for _, b in ipairs(boss.blocks) do
		local ch = ns.FindChannel(b.channel)
		if ch and #ch.hooks > 0 and not seen[ch.id] then
			seen[ch.id] = true
			tinsert(names, "#" .. ch.name)
		end
	end
	return "|cff40ff40Отправляется в: " .. tconcat(names, ", ") .. "|r  (окна с одним каналом — одна картинка)"
end

local function BossLabel(boss)
	local color = not boss.enabled and "888888" or (ns.BossSends(boss) and "40ff40" or "ff4040")
	return format("|cff%s%s|r", color, boss.name)
end

local function IsHeal(metric)
	return ns.IsHealMetric(metric)
end

-------------------------------------------------------------------------------
-- Общие

local function GeneralGroup()
	return {
		type = "group", name = "Общие", order = 1,
		get = function(info) return P()[info[#info]] end,
		-- здесь есть ползунки: без NotifyChange (окно ACD обновит само после изменения)
		set = function(info, v) P()[info[#info]] = v; ns.Refresh() end,
		args = {
			intro = {
				type = "description", order = 0, fontSize = "medium",
				name = format("|cff7289daSkadaDiscord|r  v%s\nАвтор: |cffffffff%s|r  ·  %s\n\n" ..
					"Настройки сохраняются при /reload и выходе из игры; программа SkadaDiscord на компьютере берёт их оттуда.\n",
					ns.VERSION, ns.AUTHOR, ns.CONTACT),
			},
			timer = {type = "range", order = 1, name = "Таймер автоотправки (сек)", desc = "Сколько ждать после зачистки инста перед отправкой (300 = 5 минут).", min = 10, max = 3600, step = 10, bigStep = 30, width = "double"},
			sound = {type = "toggle", order = 2, name = "Голос DBM на последних 5 секундах таймера", width = "full"},
			openOnKill = {
				type = "toggle", order = 3, name = "Открывать окно /sd после каждого убийства", width = "full",
				get = function() return ns.DB.openOnKill end,
				set = function(_, v) ns.DB.openOnKill = v end,
			},
			minimap = {
				type = "toggle", order = 4, name = "Кнопка у миникарты", width = "full",
				get = function() return not ns.DB.minimap.hide end,
				set = function(_, v)
					ns.DB.minimap.hide = not v
					if ns.DBI then
						if v then ns.DBI:Show(APP) else ns.DBI:Hide(APP) end
					end
				end,
			},
			others = {type = "toggle", order = 5, name = "Отправлять боссов, которых нет в списках (другие рейды)", width = "full"},
			defaultDps = {type = "select", order = 6, name = "Канал ДПС для боссов без настроек и боёв из истории", values = function() return ChannelValues("— никуда —") end, width = "double"},
			defaultHps = {type = "select", order = 7, name = "Канал ХПС для боссов без настроек и боёв из истории", values = function() return ChannelValues("— никуда —") end, width = "double"},
			hdr = {type = "header", order = 10, name = "Картинка и сообщение"},
			username = {type = "input", order = 11, name = "Имя бота в Discord"},
			scale = {type = "range", order = 12, name = "Размер картинки", min = 1, max = 3, step = 0.25},
			hdrDB = {type = "header", order = 20, name = "База данных"},
			sendDB = {
				type = "toggle", order = 21, name = "Отправлять отчёт в базу данных", width = "full",
				desc = "Общий сервер отчётов DB-Circle-x100; один бой = один отчёт, даже если аддон у нескольких игроков рейда.",
			},
			dbInfo = {
				type = "description", order = 22,
				name = "|cffaaaaaaОбщий сервер отчётов DB-Circle-x100: картинка урона и исцеления по каждому убийству (вайпы не отправляются), " ..
					"поиск по нику — команда /игрок на сервере. Один бой = один отчёт, даже если аддон у нескольких игроков рейда.\n" ..
					"В свои каналы: если у нескольких игроков рейда одинаковое окно ведёт в один и тот же вебхук, оно отправится один раз.|r\n",
			},
		},
	}
end

-------------------------------------------------------------------------------
-- Профили

local function ProfilesGroup()
	return {
		type = "group", name = "Профили", order = 2,
		args = {
			intro = {type = "description", order = 0, name = "Профили общие для всех персонажей аккаунта. Экспорт — строка, которую можно отправить другу; импорт — вставить такую строку.\n"},
			current = {
				type = "select", order = 1, name = "Активный профиль", width = "double",
				values = function()
					local t = {}
					for k in pairs(ns.DB.profiles) do t[k] = k end
					return t
				end,
				get = function() return ns.DB.profileName end,
				set = function(_, v) ns.SetProfile(v); Changed() end,
			},
			new = {type = "input", order = 2, name = "Новый профиль (стандартный)", get = function() return "" end, set = function(_, v) ns.NewProfile(v, false); Changed() end},
			copy = {type = "input", order = 3, name = "Копия текущего под именем", get = function() return "" end, set = function(_, v) ns.NewProfile(v, true); Changed() end},
			rename = {type = "input", order = 4, name = "Переименовать текущий", get = function() return ns.DB.profileName end, set = function(_, v) ns.RenameProfile(v); Changed() end},
			delete = {type = "execute", order = 5, name = "Удалить текущий", confirm = true, confirmText = "Удалить текущий профиль?", func = function() ns.DeleteProfile(); Changed() end},
			hdr = {type = "header", order = 10, name = "Экспорт / импорт"},
			links = {
				type = "toggle", order = 11, width = "full",
				name = "Включить в экспорт ссылки-вебхуки |cffff4040(кто получит строку, сможет писать в ваши каналы)|r",
				get = function() return exportLinks end,
				set = function(_, v) exportLinks = v; Changed() end,
			},
			export = {
				type = "input", order = 12, multiline = 6, width = "full",
				name = "Экспорт: выделите (Ctrl+A) и скопируйте (Ctrl+C)",
				get = function() return ns.ExportProfile(exportLinks) end,
				set = function() end,
			},
			import = {
				type = "input", order = 13, multiline = 6, width = "full",
				name = "Импорт: вставьте строку (Ctrl+V) и нажмите «Принять»",
				get = function() return "" end,
				set = function(_, v) ns.ImportProfile(v); Changed() end,
			},
		},
	}
end

-------------------------------------------------------------------------------
-- Каналы

local function ChannelGroup(i, ch)
	local args = {
		name = {
			type = "input", order = 1, name = "Название канала",
			get = function() return ch.name end,
			set = function(_, v) v = Trim(v); if v ~= "" then ch.name = v; Changed() end end,
		},
		used = {type = "description", order = 2, name = function() return "\nИспользуется: " .. Usage(ch.id) .. "\n" end},
		hdr = {type = "header", order = 3, name = "Ссылки (вебхуки) — отчёт уходит по всем"},
	}
	for h, url in ipairs(ch.hooks) do
		args["h" .. h] = {
			type = "input", order = 10 + h, width = "full",
			name = format("Ссылка %d%s  (очистить поле = удалить)", h, ns.IsWebhook(url) and "" or " |cffff4040— неправильная ссылка|r"),
			get = function() return ch.hooks[h] end,
			set = function(_, v)
				v = Trim(v)
				if v == "" then tremove(ch.hooks, h) else ch.hooks[h] = v end
				Changed()
			end,
		}
	end
	args.add = {
		type = "input", order = 60, width = "full",
		name = "Добавить ссылку: вставьте (Ctrl+V) и нажмите «Принять»",
		get = function() return "" end,
		set = function(_, v)
			v = Trim(v)
			if ns.IsWebhook(v) then
				tinsert(ch.hooks, v)
				Changed()
			else
				ns.Print("|cffff4040это не ссылка на вебхук Discord.|r")
			end
		end,
	}
	args.delete = {
		type = "execute", order = 70, name = "Удалить канал", confirm = true,
		confirmText = "Удалить канал «" .. ch.name .. "»? В окнах, где он выбран, канал станет «не выбран».",
		func = function() ns.DeleteChannel(ch.id); Changed() end,
	}
	return {type = "group", order = 10 + i, name = ch.name .. (#ch.hooks == 0 and " |cffff4040•|r" or ""), args = args}
end

local function ChannelsGroup()
	local args = {
		intro = {
			type = "description", order = 0,
			name = "Канал — это одна или несколько ссылок-вебхуков (отчёт уходит по всем).\n" ..
				"В Discord: настройки канала → Интеграции → Вебхуки → Новый вебхук → Копировать URL. В игре ссылка вставляется Ctrl+V.\n",
		},
		add = {type = "input", order = 1, name = "Новый канал (название)", get = function() return "" end, set = function(_, v) v = Trim(v); if v ~= "" then ns.NewChannel(v); Changed() end end},
		paste = {
			type = "input", order = 2, multiline = 6, width = "full",
			name = "Вставить список: по строке «Название: https://discord.com/api/webhooks/...»",
			get = function() return "" end,
			set = function(_, v) ns.PasteChannels(v); Changed() end,
		},
	}
	for i, ch in ipairs(P().channels) do
		args["c" .. i] = ChannelGroup(i, ch)
	end
	return {type = "group", name = "Каналы Discord", order = 3, childGroups = "tree", args = args}
end

-------------------------------------------------------------------------------
-- Инсты -> боссы -> окна

local function BlockGroup(inst, boss, k, b)
	return {
		type = "group", inline = true, order = 10 + k,
		name = format("Окно %d: %s", k, b.title ~= "" and b.title or ns.Metric(b.metric).title),
		args = {
			metric = {
				type = "select", order = 1, name = "Что показывать", width = "double", values = MetricValues,
				get = function() return b.metric end,
				set = function(_, v)
					local old, new = ns.Metric(b.metric), ns.Metric(v)
					if b.title == "" or b.title == old.title then b.title = new.title end
					b.metric = v
					-- "только хилеры" есть только у окон исцеления (по умолчанию включено)
					if IsHeal(v) then
						if b.healers == nil then b.healers = true end
					else
						b.healers = nil
					end
					Changed()
				end,
			},
			channel = {
				type = "select", order = 2, name = "Канал", values = function() return ChannelValues() end,
				get = function() return b.channel end,
				set = function(_, v) b.channel = v; Changed() end,
			},
			title = {type = "input", order = 3, name = "Заголовок окна", get = function() return b.title end, set = function(_, v) b.title = Trim(v); Changed() end},
			-- ползунок: без NotifyChange, иначе окно перестраивается посреди перетаскивания (ACD обновит само при отпускании)
			rows = {type = "range", order = 4, name = "Строк", min = 1, max = 40, step = 1, get = function() return b.rows end, set = function(_, v) b.rows = v; ns.Refresh() end},
			targets = {
				type = "input", order = 5, width = "full", name = "Цели через «;» (имена как в Skada, например: Живое адское пламя)",
				hidden = function() return b.metric ~= "damage_targets" end,
				get = function() return tconcat(b.targets, "; ") end,
				set = function(_, v)
					b.targets = {}
					for t in (v or ""):gmatch("[^;]+") do
						t = Trim(t)
						if t ~= "" then tinsert(b.targets, t) end
					end
					Changed()
				end,
			},
			healers = {
				type = "toggle", order = 6, width = "double", name = "Только хилеры (по спеку/роли)",
				desc = "В окно попадут только игроки с ролью или спеком лекаря (по данным Skada). Выключите, чтобы видеть всех.",
				hidden = function() return not IsHeal(b.metric) end,
				get = function() return b.healers ~= false end,
				set = function(_, v) b.healers = v and true or false; Changed() end,
			},
			up = {type = "execute", order = 7, name = "Выше", width = "half", disabled = function() return k == 1 end,
				func = function() boss.blocks[k], boss.blocks[k - 1] = boss.blocks[k - 1], boss.blocks[k]; Changed() end},
			down = {type = "execute", order = 8, name = "Ниже", width = "half", disabled = function() return k == #boss.blocks end,
				func = function() boss.blocks[k], boss.blocks[k + 1] = boss.blocks[k + 1], boss.blocks[k]; Changed() end},
			del = {type = "execute", order = 9, name = "Удалить", width = "half", confirm = true, confirmText = "Удалить это окно?",
				func = function() tremove(boss.blocks, k); Changed() end},
		},
	}
end

local function CopyToAll(inst, boss)
	for _, other in ipairs(inst.bosses) do
		if other ~= boss then
			local old = other.blocks
			other.blocks = {}
			for _, b in ipairs(boss.blocks) do
				local nb = ns.Copy(b)
				-- канал берём из окна того же типа (урон/хил) у этого босса
				nb.channel = ""
				for _, o in ipairs(old) do
					if IsHeal(o.metric) == IsHeal(nb.metric) and ns.FindChannel(o.channel) then
						nb.channel = o.channel
						break
					end
				end
				tinsert(other.blocks, nb)
			end
		end
	end
end

local function BossGroup(inst, j, boss)
	local args = {
		enabled = {
			type = "toggle", order = 1, width = "full", name = "Отправлять отчёт по этому боссу",
			get = function() return boss.enabled end,
			set = function(_, v) boss.enabled = v; Changed() end,
		},
		status = {type = "description", order = 2, name = function() return BossStatus(boss) .. "\n" end},
	}
	for k, b in ipairs(boss.blocks) do
		args["k" .. k] = BlockGroup(inst, boss, k, b)
	end
	args.add = {type = "execute", order = 100, name = "+ Окно",
		func = function()
			local p = P()
			local heal = #boss.blocks > 0
			tinsert(boss.blocks, ns.NewBlock(heal and "healing" or "damage", heal and p.defaultHps or p.defaultDps))
			Changed()
		end}
	args.defaults = {type = "execute", order = 101, name = "Стандартные окна", confirm = true, confirmText = "Заменить окна на стандартные (каналы по умолчанию)?",
		func = function()
			local p = P()
			boss.blocks = ns.DefaultBlocks(inst.name, boss.name, p.defaultDps, p.defaultHps)
			Changed()
		end}
	args.copy = {type = "execute", order = 102, name = "Копировать окна на всех", width = "double", confirm = true,
		confirmText = "Окна этого босса заменят окна всех боссов инста (каналы у каждого останутся свои). Продолжить?",
		func = function() CopyToAll(inst, boss); Changed() end}
	args.hdr = {type = "header", order = 110, name = "Босс"}
	args.rename = {type = "input", order = 111, name = "Имя босса (точно как в Skada)",
		get = function() return boss.name end,
		set = function(_, v)
			v = Trim(v)
			if v == "" then return end
			if inst.last == boss.name then inst.last = v end
			boss.name = v
			Changed()
		end}
	args.up = {type = "execute", order = 112, name = "Выше", width = "half", disabled = function() return j == 1 end,
		func = function() inst.bosses[j], inst.bosses[j - 1] = inst.bosses[j - 1], inst.bosses[j]; Changed() end}
	args.down = {type = "execute", order = 113, name = "Ниже", width = "half", disabled = function() return j == #inst.bosses end,
		func = function() inst.bosses[j], inst.bosses[j + 1] = inst.bosses[j + 1], inst.bosses[j]; Changed() end}
	args.delete = {type = "execute", order = 114, name = "Удалить босса", confirm = true, confirmText = "Удалить босса «" .. boss.name .. "»?",
		func = function() tremove(inst.bosses, j); Changed() end}
	return {type = "group", order = 10 + j, name = BossLabel(boss), args = args}
end

local function InstanceGroup(i, inst)
	local args = {
		name = {type = "input", order = 1, name = "Название инста", get = function() return inst.name end,
			set = function(_, v) v = Trim(v); if v ~= "" then inst.name = v; Changed() end end},
		zones = {type = "input", order = 2, name = "Зона в игре (несколько — через ;)", width = "double",
			get = function() return tconcat(inst.zones, "; ") end,
			set = function(_, v)
				inst.zones = {}
				for z in (v or ""):gmatch("[^;]+") do
					z = Trim(z)
					if z ~= "" then tinsert(inst.zones, z) end
				end
				Changed()
			end},
		trigger = {type = "select", order = 3, name = "Таймер автоотправки запускается", width = "double",
			values = {all = "после убийства всех боссов", last = "после убийства выбранного босса"},
			get = function() return inst.trigger end, set = function(_, v) inst.trigger = v; Changed() end},
		last = {type = "select", order = 4, name = "Выбранный босс", disabled = function() return inst.trigger ~= "last" end,
			values = function()
				local t = {}
				for _, b in ipairs(inst.bosses) do t[b.name] = b.name end
				return t
			end,
			get = function() return inst.last end, set = function(_, v) inst.last = v; Changed() end},
		addBoss = {type = "input", order = 5, name = "Добавить босса (имя как в Skada)", get = function() return "" end,
			set = function(_, v)
				v = Trim(v)
				if v == "" then return end
				local p = P()
				tinsert(inst.bosses, {name = v, enabled = true, blocks = ns.DefaultBlocks(inst.name, v, p.defaultDps, p.defaultHps)})
				Changed()
			end},
		delete = {type = "execute", order = 6, name = "Удалить инст", confirm = true, confirmText = "Удалить инст «" .. inst.name .. "» со всеми боссами?",
			func = function() tremove(P().instances, i); Changed() end},
		help = {type = "description", order = 7, name = "\nБоссы слева: |cff40ff40зелёный|r — отправляется, |cffff4040красный|r — не выбран канал, |cff888888серый|r — выключен.\n"},
	}
	for j, boss in ipairs(inst.bosses) do
		args["b" .. j] = BossGroup(inst, j, boss)
	end
	return {type = "group", order = 10 + i, name = inst.name, childGroups = "tree", args = args}
end

local function InstancesGroup()
	local args = {
		intro = {type = "description", order = 0, name = "Инсты и боссы. Выберите босса слева и настройте окна отчёта: что показывать, сколько строк и в какой канал.\n"},
		add = {type = "input", order = 1, name = "Новый инст (название)", get = function() return "" end,
			set = function(_, v)
				v = Trim(v)
				if v ~= "" then
					tinsert(P().instances, {name = v, zones = {}, trigger = "all", last = "", bosses = {}})
					Changed()
				end
			end},
	}
	for i, inst in ipairs(P().instances) do
		args["i" .. i] = InstanceGroup(i, inst)
	end
	return {type = "group", name = "Инсты и боссы", order = 4, childGroups = "tree", args = args}
end

-------------------------------------------------------------------------------

local function Options()
	return {
		type = "group",
		name = format("SkadaDiscord v%s — профиль: %s", ns.VERSION, ns.DB.profileName or "?"),
		childGroups = "tree",
		args = {
			general = GeneralGroup(),
			profiles = ProfilesGroup(),
			channels = ChannelsGroup(),
			instances = InstancesGroup(),
		},
	}
end

function ns.InitOptions()
	local AceConfig = LibStub("AceConfig-3.0", true)
	ACD = LibStub("AceConfigDialog-3.0", true)
	ACR = LibStub("AceConfigRegistry-3.0", true)
	if not AceConfig or not ACD then return end
	AceConfig:RegisterOptionsTable(APP, Options)
	ACD:SetDefaultSize(APP, 900, 660)
end

function ns.OpenSettings(...)
	if not ACD then
		ns.Print("окно настроек недоступно (нет AceConfig в Skada).")
		return
	end
	ACD:Open(APP)
	if select("#", ...) > 0 then ACD:SelectGroup(APP, ...) end
end

function ns.SettingsChanged()
	if ACR then ACR:NotifyChange(APP) end
end
