-- Окно SkadaDiscord, панель истории боёв и кнопка у миникарты.

local _, ns = ...
local Skada = _G.Skada
if not Skada then return end

local format = string.format

local W, H = 400, 476
local ROWS, ROW_H = 8, 30
local HW, HROWS = 330, 12 -- панель истории

local diffNames = {["10n"] = "10 об.", ["10h"] = "10 гер.", ["25n"] = "25 об.", ["25h"] = "25 гер.", ["5n"] = "5 об.", ["5h"] = "5 гер."}

local frame, scroll, status, empty, sendBtn, historyBtn, progress
local history, hscroll, hempty
local rows, hrows, checks = {}, {}, {}

StaticPopupDialogs["SKADADISCORD_CLEAR"] = {
	text = "Удалить все отчёты из списка?",
	button1 = YES,
	button2 = NO,
	OnAccept = function() ns.Clear() end,
	timeout = 0,
	whileDead = 1,
	hideOnEscape = 1,
	preferredIndex = 3,
}

local function DiffText(diff) return diffNames[diff or ""] or diff or "" end

-------------------------------------------------------------------------------
-- обновление

local function UpdateHistory()
	if not history or not history:IsShown() then return end

	local sets = ns.GetHistory()
	local n = #sets
	FauxScrollFrame_Update(hscroll, n, HROWS, ROW_H)
	local offset = FauxScrollFrame_GetOffset(hscroll)

	for i = 1, HROWS do
		local row = hrows[i]
		local set = sets[offset + i]
		if set then
			row.set = set
			row.name:SetText(set.name or set.mobname or "?")
			local parts = {}
			if set.diff and set.diff ~= "" then parts[#parts + 1] = DiffText(set.diff) end
			parts[#parts + 1] = Skada:FormatTime(Skada:GetSetTime(set)) or ""
			parts[#parts + 1] = date("%d.%m %H:%M", set.starttime or 0)
			row.sub:SetText(table.concat(parts, "  -  "))

			if ns.IsQueued(set) then
				row.state:SetText("|cff7289daв списке|r")
			elseif set.gotboss and set.success then
				row.state:SetText("|cff40ff40убит|r")
			elseif set.gotboss then
				row.state:SetText("|cffff4040вайп|r")
			else
				row.state:SetText("|cff888888треш|r")
			end
			row.bg:SetTexture(1, 1, 1, i % 2 == 0 and 0.04 or 0)
			row:Show()
		else
			row:Hide()
		end
	end
	if n == 0 then hempty:Show() else hempty:Hide() end
end

local function Update()
	if ns.ldb then
		local p = ns.PendingCount()
		ns.ldb.text = p > 0 and format("Discord: %d", p) or "Discord"
	end
	if not frame or not frame:IsShown() then return end

	local reports = ns.DB.reports
	local n = #reports
	FauxScrollFrame_Update(scroll, n, ROWS, ROW_H)
	local offset = FauxScrollFrame_GetOffset(scroll)

	for i = 1, ROWS do
		local row = rows[i]
		local index = n - (offset + i) + 1 -- новые сверху
		if index >= 1 then
			local info = ns.ReportInfo(reports[index])
			row.index = index
			local suffix = ""
			if not info.success then
				suffix = " |cffff4040(вайп)|r"
			elseif info.attempt and info.attempt > 1 then
				suffix = format(" |cffaaaaaa(%d-я попытка)|r", info.attempt)
			end
			row.boss:SetText(info.boss .. suffix)
			row.sub:SetText(format("%s  -  %s  -  %s  -  %d игр.", DiffText(info.diff), info.duration, date("%d.%m %H:%M", info.start), info.players))
			if ns.pending[info.id] then
				row.state:SetText("|cffffcc00ждёт отправки|r")
			else
				row.state:SetText("|cff40ff40записан|r")
			end
			if info.id == ns.lastAdded then
				row.bg:SetTexture(0.2, 0.55, 0.25, 0.35)
			else
				row.bg:SetTexture(1, 1, 1, i % 2 == 0 and 0.04 or 0)
			end
			row:Show()
		else
			row:Hide()
		end
	end

	if n == 0 then empty:Show() else empty:Hide() end

	local p = ns.PendingCount()
	if p > 0 then
		status:SetText(format("|cffffcc00Ждут отправки: %d|r", p))
		sendBtn:SetText(format("Отправить (%d)", p))
		sendBtn:LockHighlight()
	else
		status:SetText(n > 0 and "|cff40ff40Новых отчётов нет, всё записано|r" or "|cffaaaaaaОтчётов пока нет|r")
		sendBtn:SetText("Отправить сейчас")
		sendBtn:UnlockHighlight()
	end

	for _, cb in ipairs(checks) do
		cb:SetChecked(cb.get())
	end

	local inst, p, size = ns.CurrentProgress()
	if inst then
		local done, totalBosses = ns.CountKills(inst, p)
		local color = done >= totalBosses and "40ff40" or "ffcc00"
		progress:SetText(format("Прогресс: %s %d - |cff%s%d/%d|r", inst.name, size, color, done, totalBosses))
	else
		progress:SetText("Прогресс: |cffaaaaaaнет убийств в настроенных инстах|r")
	end

	UpdateHistory()
end
ns.Refresh = Update

-------------------------------------------------------------------------------
-- элементы

local function Flat(parent, layer, r, g, b, a)
	local t = parent:CreateTexture(nil, layer)
	t:SetTexture(r, g, b, a or 1)
	return t
end

local function Panel(f, alpha)
	f:SetBackdrop({bgFile = "Interface\\Buttons\\WHITE8X8", edgeFile = "Interface\\Buttons\\WHITE8X8", edgeSize = 1})
	f:SetBackdropColor(0.06, 0.06, 0.08, alpha or 0.95)
	f:SetBackdropBorderColor(0, 0, 0, 1)
end

local function TitleBar(f, text, closeName)
	local bg = Flat(f, "ARTWORK", 0.16, 0.18, 0.24, 1)
	bg:SetPoint("TOPLEFT", 1, -1)
	bg:SetPoint("TOPRIGHT", -1, -1)
	bg:SetHeight(26)
	local accent = Flat(f, "OVERLAY", 0.45, 0.54, 0.85, 1)
	accent:SetPoint("TOPLEFT", bg, "BOTTOMLEFT")
	accent:SetPoint("TOPRIGHT", bg, "BOTTOMRIGHT")
	accent:SetHeight(2)
	local title = f:CreateFontString(nil, "OVERLAY", "GameFontNormal")
	title:SetPoint("LEFT", bg, "LEFT", 10, 0)
	title:SetText(text)
	local close = CreateFrame("Button", closeName, f, "UIPanelCloseButton")
	close:SetPoint("TOPRIGHT", 3, 3)
end

local function ListBox(parent, top, height)
	local list = CreateFrame("Frame", nil, parent)
	list:SetPoint("TOPLEFT", 10, top)
	list:SetPoint("TOPRIGHT", -10, top)
	list:SetHeight(height)
	list:SetBackdrop({bgFile = "Interface\\Buttons\\WHITE8X8", edgeFile = "Interface\\Buttons\\WHITE8X8", edgeSize = 1})
	list:SetBackdropColor(0, 0, 0, 0.45)
	list:SetBackdropBorderColor(0.2, 0.2, 0.25, 1)
	return list
end

local function Tooltip(widget, title, text)
	widget:SetScript("OnEnter", function(self)
		GameTooltip:SetOwner(self, "ANCHOR_TOP")
		GameTooltip:SetText(title)
		if text then GameTooltip:AddLine(text, 1, 1, 1, true) end
		GameTooltip:Show()
	end)
	widget:SetScript("OnLeave", GameTooltip_Hide)
end

local function CreateButton(name, text, width, onClick)
	local b = CreateFrame("Button", "SkadaDiscord" .. name .. "Button", frame, "UIPanelButtonTemplate")
	b:SetWidth(width)
	b:SetHeight(24)
	b:SetText(text)
	b:SetScript("OnClick", onClick)
	return b
end

local function CreateCheck(name, label, get, set)
	local cb = CreateFrame("CheckButton", "SkadaDiscord" .. name .. "Check", frame, "UICheckButtonTemplate")
	cb:SetWidth(24)
	cb:SetHeight(24)
	_G[cb:GetName() .. "Text"]:SetText(label)
	cb.get, cb.set = get, set
	cb:SetScript("OnClick", function(self)
		self.set(self:GetChecked() and true or false)
		Update()
	end)
	checks[#checks + 1] = cb
	return cb
end

-- строка: заголовок сверху, подпись снизу, статус справа
local function RowTexts(row, rightAnchor)
	row.bg = Flat(row, "BACKGROUND", 0, 0, 0, 0)
	row.bg:SetAllPoints()

	row.state = row:CreateFontString(nil, "OVERLAY", "GameFontNormalSmall")
	row.state:SetPoint("RIGHT", rightAnchor or row, rightAnchor and "LEFT" or "RIGHT", -6, 0)

	row.boss = row:CreateFontString(nil, "OVERLAY", "GameFontNormal")
	row.boss:SetPoint("TOPLEFT", 6, -3)
	row.boss:SetPoint("RIGHT", row.state, "LEFT", -6, 0)
	row.boss:SetJustifyH("LEFT")

	row.sub = row:CreateFontString(nil, "OVERLAY", "GameFontHighlightSmall")
	row.sub:SetPoint("BOTTOMLEFT", 6, 3)
	row.sub:SetPoint("RIGHT", row.state, "LEFT", -6, 0)
	row.sub:SetJustifyH("LEFT")
end

local function CreateRow(i, list)
	local row = CreateFrame("Frame", nil, frame)
	row:SetHeight(ROW_H)
	row:SetPoint("TOPLEFT", list, "TOPLEFT", 4, -4 - (i - 1) * ROW_H)
	row:SetPoint("RIGHT", list, "RIGHT", -26, 0)

	row.del = CreateFrame("Button", nil, row)
	row.del:SetWidth(18)
	row.del:SetHeight(18)
	row.del:SetPoint("RIGHT", -4, 0)
	row.del:SetNormalTexture("Interface\\Buttons\\UI-GroupLoot-Pass-Up")
	row.del:SetHighlightTexture("Interface\\Buttons\\UI-GroupLoot-Pass-Highlight", "ADD")
	row.del:SetScript("OnClick", function(self) ns.Remove(self:GetParent().index) end)
	Tooltip(row.del, "Удалить из списка")

	RowTexts(row, row.del)
	rows[i] = row
end

local function CreateHistoryRow(i, list)
	local row = CreateFrame("Button", nil, history)
	row:SetHeight(ROW_H)
	row:SetPoint("TOPLEFT", list, "TOPLEFT", 4, -4 - (i - 1) * ROW_H)
	row:SetPoint("RIGHT", list, "RIGHT", -26, 0)
	row:SetHighlightTexture("Interface\\Buttons\\WHITE8X8")
	row:GetHighlightTexture():SetVertexColor(0.45, 0.54, 0.85, 0.25)

	RowTexts(row)
	row.name = row.boss

	row:SetScript("OnClick", function(self)
		if self.set then ns.QueueSet(self.set, true) end
	end)
	Tooltip(row, "Добавить в список", "Отчёт по этому бою появится в списке отправки.")
	hrows[i] = row
end

-------------------------------------------------------------------------------
-- панель истории боёв

local function CreateHistory()
	history = CreateFrame("Frame", "SkadaDiscordHistory", frame)
	history:SetWidth(HW)
	history:SetPoint("TOPLEFT", frame, "TOPRIGHT", 4, 0)
	history:SetPoint("BOTTOMLEFT", frame, "BOTTOMRIGHT", 4, 0)
	history:EnableMouse(true)
	Panel(history)
	TitleBar(history, "История боёв Skada", "SkadaDiscordHistoryCloseButton")
	history:SetScript("OnShow", UpdateHistory)
	history:Hide()

	local hint = history:CreateFontString(nil, "OVERLAY", "GameFontHighlightSmall")
	hint:SetPoint("TOPLEFT", 12, -38)
	hint:SetText("Нажмите на бой, чтобы добавить его в список.")

	local list = ListBox(history, -58, HROWS * ROW_H + 8)

	hscroll = CreateFrame("ScrollFrame", "SkadaDiscordHistoryScroll", history, "FauxScrollFrameTemplate")
	hscroll:SetPoint("TOPLEFT", list, "TOPLEFT", 0, -4)
	hscroll:SetPoint("BOTTOMRIGHT", list, "BOTTOMRIGHT", -26, 4)
	hscroll:SetScript("OnVerticalScroll", function(self, offset)
		FauxScrollFrame_OnVerticalScroll(self, offset, ROW_H, UpdateHistory)
	end)

	for i = 1, HROWS do CreateHistoryRow(i, list) end

	hempty = history:CreateFontString(nil, "OVERLAY", "GameFontDisable")
	hempty:SetPoint("CENTER", list, "CENTER")
	hempty:SetText("В истории Skada нет боёв.")
end

-------------------------------------------------------------------------------
-- главное окно

local function CreateWindow()
	frame = CreateFrame("Frame", "SkadaDiscordFrame", UIParent)
	frame:SetWidth(W)
	frame:SetHeight(H)
	frame:SetFrameStrata("DIALOG")
	frame:SetClampedToScreen(true)
	frame:SetMovable(true)
	frame:EnableMouse(true)
	frame:RegisterForDrag("LeftButton")
	frame:SetScript("OnDragStart", frame.StartMoving)
	frame:SetScript("OnDragStop", function(self)
		self:StopMovingOrSizing()
		local point, _, relPoint, x, y = self:GetPoint()
		ns.DB.pos = {point, relPoint, x, y}
	end)
	frame:SetScript("OnShow", Update)
	local pos = ns.DB.pos
	if pos then
		frame:SetPoint(pos[1], UIParent, pos[2], pos[3], pos[4])
	else
		frame:SetPoint("CENTER")
	end
	tinsert(UISpecialFrames, "SkadaDiscordFrame") -- закрытие по Esc

	Panel(frame)
	TitleBar(frame, "Skada |cff7289daDiscord|r |cffaaaaaav" .. tostring(ns.VERSION) .. "|r", "SkadaDiscordCloseButton")

	status = frame:CreateFontString(nil, "OVERLAY", "GameFontHighlight")
	status:SetPoint("TOPLEFT", 12, -38)

	-- список отчётов
	local list = ListBox(frame, -58, ROWS * ROW_H + 8)

	scroll = CreateFrame("ScrollFrame", "SkadaDiscordScroll", frame, "FauxScrollFrameTemplate")
	scroll:SetPoint("TOPLEFT", list, "TOPLEFT", 0, -4)
	scroll:SetPoint("BOTTOMRIGHT", list, "BOTTOMRIGHT", -26, 4)
	scroll:SetScript("OnVerticalScroll", function(self, offset)
		FauxScrollFrame_OnVerticalScroll(self, offset, ROW_H, Update)
	end)

	for i = 1, ROWS do CreateRow(i, list) end

	empty = frame:CreateFontString(nil, "OVERLAY", "GameFontDisable")
	empty:SetPoint("CENTER", list, "CENTER")
	empty:SetWidth(W - 60)
	empty:SetText("Пока нет отчётов.\nУбейте босса или выберите бой в \"Истории боёв\".")

	-- кнопки
	sendBtn = CreateButton("Send", "Отправить сейчас", 140, function() ns.Send() end)
	sendBtn:SetPoint("TOPLEFT", list, "BOTTOMLEFT", 0, -10)
	Tooltip(sendBtn, "Отправить в Discord", "Интерфейс перезагрузится: только так WoW записывает данные на диск.")

	historyBtn = CreateButton("History", "История боёв >>", 140, function()
		if history:IsShown() then history:Hide() else history:Show() end
	end)
	historyBtn:SetPoint("LEFT", sendBtn, "RIGHT", 6, 0)
	Tooltip(historyBtn, "История боёв", "Выбрать любой сохранённый бой Skada и добавить его в список.")

	local clearBtn = CreateButton("Clear", "Очистить", 86, function() StaticPopup_Show("SKADADISCORD_CLEAR") end)
	clearBtn:SetPoint("LEFT", historyBtn, "RIGHT", 6, 0)

	-- прогресс рейда и таймер
	progress = frame:CreateFontString(nil, "OVERLAY", "GameFontHighlight")
	progress:SetPoint("TOPLEFT", sendBtn, "BOTTOMLEFT", 2, -14)
	progress:SetJustifyH("LEFT")

	local resetBtn = CreateButton("Reset", "Сбросить", 86, function()
		local _, _, _, key = ns.CurrentProgress()
		ns.ResetProgress(key)
	end)
	resetBtn:SetPoint("TOPRIGHT", list, "BOTTOMRIGHT", 0, -44)
	Tooltip(resetBtn, "Сбросить прогресс", "Начать отсчёт убитых боссов заново (новое КД сбрасывается само).")

	local timerBtn = CreateButton("Timer", "Таймер", 86, function()
		if ns.TimerRunning() then ns.StopTimer() else ns.StartTimer(ns.Config().timer or 300, "Вручную:") end
	end)
	timerBtn:SetPoint("RIGHT", resetBtn, "LEFT", -6, 0)
	Tooltip(timerBtn, "Таймер отправки", "Запустить или остановить таймер автоотправки (он сам запускается после зачистки инста).")

	-- настройки
	local c2 = CreateCheck("Open", "Открывать это окно после убийства",
		function() return ns.DB.openOnKill end,
		function(v) ns.DB.openOnKill = v end)
	c2:SetPoint("TOPLEFT", sendBtn, "BOTTOMLEFT", -2, -40)

	local c3 = CreateCheck("Minimap", "Кнопка у миникарты",
		function() return not ns.DB.minimap.hide end,
		function(v)
			ns.DB.minimap.hide = not v
			if ns.DBI then
				if v then ns.DBI:Show("SkadaDiscord") else ns.DBI:Hide("SkadaDiscord") end
			end
		end)
	c3:SetPoint("TOPLEFT", c2, "BOTTOMLEFT", 0, 2)

	-- окно настроек (справа от галочек, по центру между ними)
	local settingsBtn = CreateButton("Settings", "Настройки", 120, function() ns.OpenSettings() end)
	settingsBtn:SetPoint("TOPRIGHT", list, "BOTTOMRIGHT", 0, -85)
	Tooltip(settingsBtn, "Настройки", "Каналы Discord, инсты, боссы, окна отчёта, профили. Команда: /sd config")

	local hint = frame:CreateFontString(nil, "OVERLAY", "GameFontDisableSmall")
	hint:SetPoint("BOTTOMLEFT", 12, 10)
	hint:SetPoint("BOTTOMRIGHT", -12, 10)
	hint:SetJustifyH("LEFT")
	hint:SetText("Автор: " .. (ns.AUTHOR or "Leanca Vladimir") .. " · " .. (ns.CONTACT or "Discord: lyanka_v"))

	CreateHistory()
end

-------------------------------------------------------------------------------
-- доступ из ядра и команд

function ns.ShowWindow()
	if not frame then CreateWindow() end
	frame:Show()
	Update()
end

function ns.ToggleWindow()
	if frame and frame:IsShown() then
		frame:Hide()
	else
		ns.ShowWindow()
	end
end

function ns.ShowHistory()
	ns.ShowWindow()
	history:Show()
end

-- кнопка у миникарты (библиотеки идут вместе со Skada)
function ns.InitUI()
	local LDB = LibStub and LibStub("LibDataBroker-1.1", true)
	if not LDB then return end
	ns.ldb = LDB:NewDataObject("SkadaDiscord", {
		type = "launcher",
		text = "Discord",
		icon = "Interface\\Icons\\INV_Misc_Note_01",
		OnClick = function(_, button)
			if button == "RightButton" then
				ns.Send()
			elseif IsShiftKeyDown() then
				ns.OpenSettings()
			else
				ns.ToggleWindow()
			end
		end,
		OnTooltipShow = function(tt)
			tt:AddLine("Skada |cff7289daDiscord|r |cffaaaaaav" .. tostring(ns.VERSION) .. "|r")
			local p = ns.PendingCount()
			tt:AddLine(p > 0 and format("Ждут отправки: %d", p) or "Новых отчётов нет", 1, 1, 1)
			tt:AddLine(" ")
			tt:AddLine("ЛКМ: открыть окно", 0.7, 0.7, 0.7)
			tt:AddLine("Shift+ЛКМ: настройки", 0.7, 0.7, 0.7)
			tt:AddLine("ПКМ: отправить сейчас", 0.7, 0.7, 0.7)
		end,
	})
	local DBI = LibStub("LibDBIcon-1.0", true)
	if DBI then
		DBI:Register("SkadaDiscord", ns.ldb, ns.DB.minimap)
		ns.DBI = DBI
	end
end
