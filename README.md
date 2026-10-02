# SkadaDiscord

Отчёты **Skada** по убийствам боссов — сразу в **Discord**, картинкой, как окно Skada в игре.
Для **World of Warcraft 3.3.5a** (WotLK), сделано и проверено на WoW Circle.

**Автор:** Leanca Vladimir · **Discord:** `lyanka_v`

## ⬇️ [Скачать SkadaDiscord](https://github.com/LeancaVladimir/SkadaDiscord/releases/latest)

> [!IMPORTANT]
> Программа `SkadaDiscord.exe` лежит **только в архиве релиза**: откройте ссылку выше и в разделе **Assets** скачайте `SkadaDiscord-<версия>.zip`.
> Зелёная кнопка **Code → Download ZIP** и файлы «Source code» — это исходный код, **без** `.exe`.

---

## Что умеет

- После убийства босса отчёт сам уходит в нужный канал Discord: полосы цвета класса, иконки специализаций, проценты, итоги боя.
- Окна на выбор: урон (весь / по боссу / по выбранным целям / по трешу / полезный), исцеление и поглощения (только хилеры), оверхил, полученный урон, смерти, прерывания, диспелы, зелья, ошибки, раскол брони, активность.
- Свой канал на каждого босса, несколько вебхуков в одном канале.
- «Победа с 4-й попытки», вайпы не отправляются.
- Большой таймер по центру экрана (цифры DBM): после зачистки инста отчёты уходят сами; ждёт конца боя и раздачи лута.
- Все настройки — в игре (`/sd` → «Настройки»), профили на аккаунт, импорт/экспорт строкой.
- Общая база отчётов (Discord-сервер **Летопись Circle**) и поиск `/игрок` — один бой = один отчёт, даже если аддон у нескольких игроков рейда.
- Ярлык «Играть»: запускает WoW и программу, программа закрывается вместе с игрой.

## Установка

1. Скачайте `SkadaDiscord-<версия>.zip` из [последнего релиза](https://github.com/LeancaVladimir/SkadaDiscord/releases/latest) (раздел **Assets**, не «Source code»).
2. Распакуйте **в папку с игрой** (где `Wow.exe`):
   ```
   <игра>\Interface\AddOns\SkadaDiscord\   аддон
   <игра>\SkadaDiscord\SkadaDiscord.exe    программа
   ```
3. Запустите `SkadaDiscord\SkadaDiscord.exe` → «Программа» → «Создать ярлык «Играть»».
4. В игре включите аддон **Skada: Discord**, откройте `/sd` → «Настройки» → «Каналы Discord» и вставьте ссылки-вебхуки
   (в Discord: настройки канала → Интеграции → Вебхуки → Новый вебхук → Копировать URL).
5. В «Инсты и боссы» выберите для боссов окна и каналы.

Нужно: **Skada** (версия bkader «Skada Revisited» для 3.3.5), желательно **DBM**, Windows 10/11.
Подробная инструкция — в [`SkadaDiscord/README.txt`](SkadaDiscord/README.txt).

## Как это устроено

Аддоны WoW не могут выходить в интернет, поэтому:

```
WoW (аддон SkadaDiscord) ──/reload или выход──▶ WTF\...\SavedVariables\SkadaDiscord.lua
                                                         │
SkadaDiscord.exe (трей) ◀──────── читает ────────────────┘
        │ рисует картинку
        ├──▶ ваши вебхуки Discord
        └──▶ посредник Cloudflare Worker ──▶ сервер «Летопись Circle» (+ защита от дублей)
```

| Папка | Что это |
|---|---|
| `Interface/AddOns/SkadaDiscord` | аддон (Lua, WoW 3.3.5a) |
| `SkadaDiscord/src` | программа (C# WinForms, .NET Framework 4), сборка: `powershell -ExecutionPolicy Bypass -File SkadaDiscord\src\build.ps1` |
| `cloudflare/worker.js` | посредник для общей базы данных (Cloudflare Workers + D1) |

## Вопросы и идеи

Пишите в Discord: **lyanka_v**.
