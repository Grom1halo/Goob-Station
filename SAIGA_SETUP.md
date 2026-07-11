# Saiga MCP Agent — установка и запуск (SS14, client-only)

Клиент-онли ИИ-агент для SS14 (форк Goob-Station): управляет твоим персонажем через локальный
MCP-эндпоинт (JSON-RPC на `:1213`), «мозги» — локальная LLM (LM Studio / любой OpenAI-совместимый
сервер). Работает как отдельная сборка `Content.Saiga.Client.dll`, которую кладут в ресурсы клиента
ЦЕЛЕВОГО сервера — сетевые типы совпадают, серверный код/права хоста НЕ нужны.

Код агента: `Content.Saiga.Client/` (C#). Раннер и панель: `saiga/` (Python, без зависимостей кроме stdlib).

> ⚠️ **Про аккаунт и токен.** Токен авторизации берётся из ТВОЕЙ лаунчерной `settings.db` в момент
> запуска — он НИГДЕ не хардкодится. **Никогда** не выкладывай `settings.db`, `ROBUST_AUTH_TOKEN`,
> свой `UserId` — это доступ к твоему аккаунту. Ниже везде плейсхолдеры.
> ⚠️ Боты на чужих серверах могут нарушать их правила — на свой страх и риск.

## Что нужно
- Linux (тестировалось на Arch/CachyOS), **.NET 9 SDK**, git, Python 3, `sqlite3`, `unzip`.
- Официальный лаунчер SS14 + аккаунт (spacestation14.com).
- LM Studio (или аналог) + модель с tool-calling (напр. `saiga_llama3_8b_gguf` ~4.6ГБ). ~8ГБ VRAM под 8B.

## 1. Собрать DLL агента
```bash
git clone https://github.com/Grom1halo/Goob-Station.git
cd Goob-Station && git checkout saiga-agent
# .NET 9 SDK обязателен (global.json). Нет в системе — поставь без sudo:
#   curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 9.0 --install-dir $HOME/.dotnet
env DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$PATH \
  dotnet build Content.Saiga.Client/Content.Saiga.Client.csproj -c Release
# → bin/Content.Client/Content.Saiga.Client.dll
```

## 2. Клиент целевого сервера (Path B — реконструкция официального клиента)
Через лаунчер наш код не подгрузить (он запускает клиент из `content.db` без нашего кода). Поэтому
собираем клиент вручную из уже скачанного лаунчером контента и кладём туда наш DLL.

1. **Зайди на нужный сервер ОФИЦИАЛЬНЫМ ЛАУНЧЕРОМ** — он скачает контент в `~/.local/share/Space Station 14/launcher/content.db` и движок в `.../launcher/engines/<ver>.zip`.
2. Узнай версию контента сервера:
   ```bash
   sqlite3 -readonly "$HOME/.local/share/Space Station 14/launcher/content.db" \
     "SELECT Id,ForkId,ForkVersion,LastUsed FROM ContentVersion ORDER BY Id DESC LIMIT 5"
   ```
   Возьми `Id` последней (свежескачанной) версии.
3. Извлеки контент этой версии в `~/void-client/Resources/` — правь `VID` под свой Id:
   ```bash
   mkdir -p ~/void-client/Resources
   python3 saiga/extract_content.py <VID>   # VID из шага 2
   ```
4. Движок: распакуй нужный `<ver>.zip` в `~/void-client/` и СЛЕЙ его Resources в контентные
   (иначе `FileNotFound` на движковые шейдеры/шрифты):
   ```bash
   cd ~/void-client
   unzip -o "$HOME/.local/share/Space Station 14/launcher/engines/<ver>.zip" -d .
   # ресурсы движка (Shaders/Fonts/…) — в ту же Resources, не затирая контент:
   cp -rn "$HOME/void-client/Resources/"* "$HOME/void-client/Resources/" 2>/dev/null || true
   ```
   (Если движок кладёт свои ресурсы отдельно — убедись, что `Resources/Shaders/Internal/default-sprite.swsl` на месте.)
5. Положи собранный DLL агента:
   ```bash
   cp <Goob>/bin/Content.Client/Content.Saiga.Client.dll ~/void-client/Resources/Assemblies/
   ```
> При обновлении сервера повтори шаги 1-5 (переизвлечь контент + пересобрать DLL против свежего Goob),
> иначе рассинхрон сетевых типов → `MissingMetadataException`.

## 3. LM Studio
```bash
lms load saiga_llama3_8b_gguf   # РЕЗИДЕНТНО (не JIT, иначе выгрузится по TTL)
lms ps                          # проверить, что loaded
```
Сервер OpenAI-совместимый на `http://localhost:1234/v1` (по умолчанию у LM Studio).

## 4. Запуск клиента (с авторизацией)
Токен и UserId подтягиваются из ТВОЕЙ `settings.db` рантаймом — свои у каждого. Сохрани как `run.sh`:
```bash
#!/usr/bin/env bash
set -euo pipefail
cd "$HOME/void-client"
S="$HOME/.local/share/Space Station 14/launcher/settings.db"
TOK=$(sqlite3 "file:${S}?mode=ro&nolock=1&immutable=1" "SELECT Token FROM Login LIMIT 1")
UID_=$(sqlite3 "file:${S}?mode=ro&nolock=1&immutable=1" "SELECT UserId FROM Login LIMIT 1")
[ -n "$TOK" ] || { echo "пустой токен — зайди лаунчером"; exit 1; }
exec env DOTNET_ROOT="$HOME/.dotnet" ROBUST_DISABLE_SANDBOX=1 \
  SAIGA_MCP_CLIENT=1 SAIGA_MCP_TOKEN=devsecret \
  ROBUST_AUTH_TOKEN="$TOK" ROBUST_AUTH_USERID="$UID_" \
  ROBUST_AUTH_SERVER=https://auth.spacestation14.com/ \
  "$HOME/.dotnet/dotnet" Robust.Client.dll \
  --connect --connect-address udp://<SERVER_IP>:<PORT> --cvar net.connection_timeout=120
```
`<SERVER_IP>:<PORT>` — UDP-адрес сервера (узнать: `curl -s https://<хаб>/server/<имя>/info | grep connect_address`).
`SAIGA_MCP_TOKEN` — локальный токен MCP-эндпоинта (поставь любой свой, тот же указывается раннеру/GUI).
`ROBUST_DISABLE_SANDBOX=1` обязателен (песочница блокирует HttpListener MCP).

Запусти `bash run.sh` → зайди в игру персонажем.

## 5. Раннер и панель (из `saiga/`)
- **Автономный LLM-борг** (реагирует на речь рядом, шорткаты команд):
  ```bash
  python3 saiga/listen_agent.py
  ```
  (Правь `MCP_TOKEN`/`MODEL`/`LLM_URL` в шапке файла под себя.)
- **GUI-панель** — кнопки на все MCP-тулзы + ручной ввод боргу:
  ```bash
  python3 saiga/saiga_gui.py
  ```
  Клиента достаточно (MCP живёт в нём). Раннер нужен только для автономного борга и ввода через инбокс.

## Проверка, что MCP жив
```bash
curl -s http://127.0.0.1:1213/mcp -H 'Authorization: Bearer devsecret' \
  -H 'Content-Type: application/json' -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'
```
Вернулся список тулзов = агент загрузился и готов.
