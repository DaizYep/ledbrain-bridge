# LEDBRAIN Bridge

Программа для Windows: принимает **NDI** и **Spout** из Resolume напрямую и отдаёт картинку 3D-виду «Сцены» LEDBRAIN —
в родном размере композиции, без виртуальных веб-камер и обрезки до 16:9.

Один файл `LEDBRAIN Bridge.exe` (~60 КБ), без Node.js и установки. Нужен .NET Framework 4.8 — он есть в Windows 10 и 11.

## Пользователю

1. Скачайте [`LEDBRAIN-Bridge.exe`](https://github.com/DaizYep/ledbrain-bridge/releases/latest/download/LEDBRAIN-Bridge.exe) из [релизов](https://github.com/DaizYep/ledbrain-bridge/releases/latest) и запустите. Windows может показать SmartScreen — «Подробнее → Выполнить в любом случае».
2. Для NDI поставьте [NDI Tools](https://ndi.video/tools/) (из них берётся библиотека NDI). Для Spout ничего не нужно.
3. В Resolume включите выход композиции в NDI или Spout.
4. LEDBRAIN → «Сцена» → 3D → **Bridge · NDI / Spout**, в списке «Источник» — нужный (`NDI · …` или `Spout · …`).

Окно закрывается в трей — Bridge продолжает работать. Клик по значку — окно, правый клик — меню: открыть LEDBRAIN,
автозапуск с Windows, язык (русский / English, по умолчанию — язык Windows), выход.

## Сборка

`src\build.cmd` — компилятор C# из .NET Framework 4.8 (`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`), exe появляется в корне репозитория.

- `src/App.cs` — окно и трей (WinForms, C# 5).
- `src/Core.cs` — ядро: NDI, Spout, JPEG, сервер HTTP + WebSocket.
- `src/icon.ico` — иконка.

## Как устроено

- `http://127.0.0.1:47800/` — список источников: `NDI · <имя>` и `Spout · <имя>`; `/status` — состояние для окна.
- `ws://127.0.0.1:47800/ws?src=<имя>` — кадры JPEG; текстом — размер и частота источника.
- Слушает только `127.0.0.1`, принимает страницы `led.gladkovegor.ru` и `localhost`. Ничего никуда не отправляет.
- **NDI** — `Processing.NDI.Lib.x64.dll` из NDI Tools / NDI Runtime (её нельзя класть в программу, поэтому NDI Tools ставятся отдельно).
- **Spout** — без SDK: имена и описание текстуры из общей памяти Windows (`SpoutSenderNames`), кадр — общая текстура DirectX 11,
  копия на процессор ~30 раз в секунду. Видеокарта выбирается та, где рисует отправитель (ноутбуки с двумя видеокартами).
- Кадр уменьшается до 2048 px по ширине (среднее 2×2) и сжимается в JPEG встроенным кодеком Windows; до трёх кадров одновременно.
  Медленный клиент пропускает кадры, а не копит очередь.

---

Часть [LEDBRAIN](https://led.gladkovegor.ru) — планировщика LED-экранов NovaStar. © 2026 Egor Gladkov. NDI® — зарегистрированный товарный знак Vizrt NDI AB.
