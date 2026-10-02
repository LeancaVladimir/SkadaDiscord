-- Большой таймер автоотправки по центру экрана (цифры из DBM, как у пулл-таймера).
-- По окончании делает /reload: WoW записывает отчёты на диск, программа их отправляет.
-- Не перезагружает во время боя и раздачи лута (EPGP Lootmaster, RaidRoll, окна ролла).

local _, ns = ...
if not _G.Skada then return end

local format, floor, fmod = string.format, math.floor, math.fmod

local DIGITS = "Interface\\AddOns\\DBM-Core\\textures\\Timer\\BigTimerNumbers"
local GLOW = "Interface\\AddOns\\DBM-Core\\textures\\Timer\\BigTimerNumbersGlow"
local SOUND = "Interface\\AddOns\\DBM-Core\\Sounds\\Corsica\\%d.ogg"
local FONT = "Interface\\AddOns\\DBM-Core\\Fonts\\Expressway.ttf"
-- раскладка текстуры DBM (DBM-TimerTracker.lua): цифра 256x170 в атласе 1024x512, 4 колонки
local CELL_W, CELL_H, TEX_W, TEX_H = 256, 170, 1024, 512
local HALF = {35, 14, 33, 32, 36, 32, 33, 29, 31, 31} -- полуширина цифр 0..9 (из 128)
local DIGIT_W, DIGIT_H = 128, 85 -- размер цифры на экране

-- окна, во время которых нельзя перезагружать интерфейс
local BUSY_FRAMES = {
	"LootFrame", "GroupLootFrame1", "GroupLootFrame2", "GroupLootFrame3", "GroupLootFrame4",
	"LootMasterMLMainFrame", "LootMasterUIFrame", "RR_RollFrame",
}

local frame, glyphs, colon, title, info
local remaining, total, label, lastSecond, blocked

local function Busy()
	if InCombatLockdown() then return "идёт бой" end
	for _, name in ipairs(BUSY_FRAMES) do
		local f = _G[name]
		if f and f:IsShown() then return "идёт раздача лута" end
	end
end

local function NewGlyph(parent)
	local g = {}
	g.digit = parent:CreateTexture(nil, "ARTWORK")
	g.digit:SetTexture(DIGITS)
	g.digit:SetWidth(DIGIT_W)
	g.digit:SetHeight(DIGIT_H)
	g.glow = parent:CreateTexture(nil, "OVERLAY")
	g.glow:SetTexture(GLOW)
	g.glow:SetBlendMode("ADD")
	g.glow:SetWidth(DIGIT_W)
	g.glow:SetHeight(DIGIT_H)
	return g
end

local function SetDigit(g, n, x)
	local col, row = fmod(n, 4), floor(n / 4)
	local l, t = col * CELL_W / TEX_W, row * CELL_H / TEX_H
	local r, b = l + CELL_W / TEX_W, t + CELL_H / TEX_H
	for _, tex in ipairs({g.digit, g.glow}) do
		tex:SetTexCoord(l, r, t, b)
		tex:ClearAllPoints()
		tex:SetPoint("CENTER", frame, "CENTER", x, 10)
		tex:Show()
	end
end

-- рисует "М:СС" (или "ММ:СС") по центру
local function Draw(seconds)
	seconds = math.max(0, math.ceil(seconds))
	local m, s = floor(seconds / 60), fmod(seconds, 60)
	local list = {}
	if m >= 10 then list[#list + 1] = floor(m / 10) end
	list[#list + 1] = fmod(m, 10)
	list[#list + 1] = ":"
	list[#list + 1] = floor(s / 10)
	list[#list + 1] = fmod(s, 10)

	-- ширина строки по полуширинам цифр, как у DBM
	local widths, sum = {}, 0
	for i, v in ipairs(list) do
		widths[i] = v == ":" and 22 or HALF[v + 1] * 2
		sum = sum + widths[i]
	end

	local x, gi = -sum / 2, 0
	for i, v in ipairs(list) do
		local center = x + widths[i] / 2
		if v == ":" then
			colon:ClearAllPoints()
			colon:SetPoint("CENTER", frame, "CENTER", center, 16)
		else
			gi = gi + 1
			SetDigit(glyphs[gi], v, center)
		end
		x = x + widths[i]
	end
	for i = gi + 1, #glyphs do
		glyphs[i].digit:Hide()
		glyphs[i].glow:Hide()
	end
end

local function Fire()
	local busy = Busy()
	if busy then
		info:SetText("|cffffcc00Ждём: " .. busy .. "...|r")
		return
	end
	info:SetText("|cff40ff40Отправляем...|r")
	ReloadUI()
end

local function CreateTimer()
	frame = CreateFrame("Frame", "SkadaDiscordTimer", UIParent)
	frame:SetWidth(460)
	frame:SetHeight(190)
	frame:SetPoint("CENTER", 0, 170)
	frame:SetFrameStrata("HIGH")
	frame:SetMovable(true)
	frame:EnableMouse(true)
	frame:RegisterForDrag("LeftButton")
	frame:SetScript("OnDragStart", frame.StartMoving)
	frame:SetScript("OnDragStop", frame.StopMovingOrSizing)
	frame:Hide()

	local bg = frame:CreateTexture(nil, "BACKGROUND")
	bg:SetAllPoints()
	bg:SetTexture(0, 0, 0, 0.45)

	title = frame:CreateFontString(nil, "OVERLAY")
	title:SetFont(FONT, 18, "OUTLINE")
	title:SetPoint("TOP", 0, -10)
	title:SetTextColor(0.45, 0.54, 0.85)

	glyphs = {}
	for i = 1, 4 do glyphs[i] = NewGlyph(frame) end

	colon = frame:CreateFontString(nil, "OVERLAY")
	colon:SetFont(FONT, 64, "THICKOUTLINE")
	colon:SetTextColor(1, 0.82, 0.1)
	colon:SetText(":")

	info = frame:CreateFontString(nil, "OVERLAY")
	info:SetFont(FONT, 14, "OUTLINE")
	info:SetPoint("BOTTOM", 0, 42)

	local function Button(name, text, width, onClick)
		local b = CreateFrame("Button", "SkadaDiscordTimer" .. name .. "Button", frame, "UIPanelButtonTemplate")
		b:SetWidth(width)
		b:SetHeight(24)
		b:SetText(text)
		b:SetScript("OnClick", onClick)
		return b
	end
	local now = Button("Now", "Отправить сейчас", 140, function()
		remaining = 0
		blocked = nil
		Fire()
	end)
	now:SetPoint("BOTTOM", -112, 10)
	local more = Button("More", "+1 мин", 80, function()
		remaining = math.max(0, remaining) + 60
		total = math.max(total, remaining)
		blocked = nil
	end)
	more:SetPoint("LEFT", now, "RIGHT", 6, 0)
	local cancel = Button("Cancel", "Отмена", 80, function() ns.StopTimer() end)
	cancel:SetPoint("LEFT", more, "RIGHT", 6, 0)

	frame:SetScript("OnUpdate", function(_, elapsed)
		if remaining > 0 then
			remaining = remaining - elapsed
			local sec = math.ceil(remaining)
			if sec ~= lastSecond then
				lastSecond = sec
				Draw(remaining)
				-- голос DBM на последних 5 секундах
				if sec >= 1 and sec <= 5 and ns.Config().sound ~= false then
					PlaySoundFile(format(SOUND, sec))
				end
			end
			-- свечение пульсирует на последних 10 секундах
			local glow = remaining <= 10 and (0.35 + 0.35 * math.sin(GetTime() * 6)) or 0
			for _, g in ipairs(glyphs) do g.glow:SetAlpha(glow) end
			if not blocked then
				local busy = Busy()
				info:SetText(busy and ("|cffffcc00Ждём: " .. busy .. "|r") or format("Отчётов к отправке: |cffffffff%d|r", ns.PendingCount()))
			end
		elseif not blocked then
			Fire()
		end
	end)

	-- если игра не дала перезагрузить интерфейс без нажатия
	frame:RegisterEvent("ADDON_ACTION_BLOCKED")
	frame:SetScript("OnEvent", function(_, _, addon, func)
		if func and tostring(func):find("ReloadUI") then
			blocked = true
			remaining = 0
			info:SetText("|cffff4040Нажмите «Отправить сейчас»|r")
		end
	end)
end

function ns.StartTimer(seconds, text)
	if not frame then CreateTimer() end
	total = seconds or 300
	remaining = total
	label = text
	lastSecond = nil
	blocked = nil
	title:SetText((label and (label .. "  ") or "") .. "Отправка отчётов в Discord")
	Draw(remaining)
	frame:Show()
	ns.Print(format("отчёты уйдут в Discord через %d:%02d. Отменить: /sd stop", floor(total / 60), fmod(total, 60)))
end

function ns.StopTimer()
	if frame and frame:IsShown() then
		frame:Hide()
		remaining = 0
		ns.Print("таймер отправки отменён. Отправить вручную: /sd send или кнопка в окне /sd.")
	end
end

function ns.TimerRunning()
	return frame and frame:IsShown()
end
