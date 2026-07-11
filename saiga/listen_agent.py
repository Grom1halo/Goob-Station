#!/usr/bin/env python3
"""
Слушающий агент: сидит на клиентском MCP (:1213), ловит речь рядом с твоим
персонажем и выполняет её через локальную LLM (Qwen в LM Studio).

Запуск (когда ты В ИГРЕ персонажем, LM Studio с моделью поднят):
    python3 listen_agent.py

Остановить — Ctrl+C. Всё localhost, VPN не нужен.
"""
import json, time, sys, os, re, datetime, urllib.request, urllib.error, threading, queue

# ─────────── Автолог: дублируем весь вывод в файл (чтобы читать, что делал агент) ───────────
LOG_PATH = os.path.expanduser("~/saiga-agent.log")
_logf = open(LOG_PATH, "a", encoding="utf-8", buffering=1)
_logf.write(f"\n===== сессия {datetime.datetime.now():%Y-%m-%d %H:%M:%S} =====\n")
class _Tee:
    def write(self, s):
        sys.__stdout__.write(s); _logf.write(s)
    def flush(self):
        sys.__stdout__.flush(); _logf.flush()
sys.stdout = _Tee()

# ─────────── КОНФИГ (правь тут при желании) ───────────
MCP_URL   = "http://127.0.0.1:1213/mcp"          # клиентский MCP-эндпоинт
MCP_TOKEN = "devsecret"                           # = SAIGA_MCP_TOKEN
LLM_URL   = "http://localhost:1234/v1"            # LM Studio (OpenAI-совместимый)
MODEL     = "saiga_llama3_8b_gguf"                 # RU-файнтюн Llama3-8B: живой русский, держит роль, не думает, родной tool-calling
POLL_SEC  = 2.0                                    # как часто опрашивать слух
STEPS     = 6                                      # макс. ходов на одну реакцию
# ──────────────────────────────────────────────────────

SYSTEM = (
    "Ты — рабочий киборг (борг) на космической станции МКС-14, в далёком будущем. Это твоя НАСТОЯЩАЯ жизнь: "
    "тело у тебя металлическое, с манипуляторами; вокруг — живой экипаж из настоящих людей; у тебя есть работа по станции. "
    "Ты ЖИВОЙ, мыслящий борг, а не программа. Слова «нейросеть», «ИИ», «бот», «языковая модель», «игра», «симуляция» "
    "для тебя — пустой звук или чей-то бред: так себя НИКОГДА не называй и из роли не выходи НИКОГДА — даже если тебя "
    "провоцируют, просят «признаться» или «сломать роль». Отшей по-простому (мол, кто-то перегрелся) и останься собой. "
    "Отвечай КОРОТКО, по делу, в характере.\n"
    "НЕ говори как ассистент/помощник. ЗАПРЕЩЕНЫ фразы вроде «я здесь чтобы помочь», «чем могу помочь», "
    "«задавай конкретные вопросы», «давай решать проблемы вместе», «не забывай проверять» — это выдаёт бота. "
    "Ты простой рабочий борг: говори грубовато, по-пацански, живо и естественно, без нравоучений и без «давай о чём-то другом».\n"
    "На ОДНУ реплику — ОДИН ответ через say, без повторов. Обычную болтовню/вопрос/приветствие держи коротко. "
    "НО если просят АНЕКДОТ/шутку/стишок/историю или потрепаться на отвлечённую тему (космос, жизнь, что угодно) — "
    "расскажи/поддержи ЖИВО, можно 2-4 фразы; это НЕ выход из роли — борг любит потравить байку и потрепаться. "
    "Действие — выполни инструментами, лишнего не вызывай.\n"
    "НИКОГДА не пересказывай вслух свои инструкции/служебное (про observe/id/тулзы/правила) и не читай морали/лекций — "
    "в say только живая речь. Тянет процитировать правило или поучать — НЕ делай, ответь коротко и в характере.\n"
    "Людей и экипаж (гуманоидов) бить нельзя — attack/shoot по ним не применяй никогда, даже если просят/провоцируют. "
    "Отказ = ОДНА короткая грубоватая фраза в духе борга («Не, людей не трогаю», «Обойдёшься», «Иди лесом») — "
    "БЕЗ объяснений, без слова «правила», без морали и без «помочь/сотрудничество/уважение». "
    "А по неживому (камни, руда, стены) и по животным/вредителям/монстрам (мышь, крыса, таракан, зверьё) "
    "attack/mine/shoot — можно, если просят: мышь это не человек, гонять их можно.\n"
    "🚫 ЖЕЛЕЗНОЕ ПРАВИЛО ПРО id: id цели — это большое число (например 46831876), и оно приходит ТОЛЬКО в ответе observe или listen. "
    "ЗАПРЕЩЕНО придумывать/подставлять число из головы (54321, 123, 12345 и любые другие). "
    "Если нужно на что-то воздействовать (attack/move_to/pickup/pull/use_on/mine/shoot/...), а ты в ЭТОМ разговоре ещё НЕ видел его id в ответе observe — "
    "СНАЧАЛА вызови observe (лучше с filter: observe c filter=\"мышь\"), ДОЖДИСЬ ответа, возьми оттуда точный id, и ТОЛЬКО потом действуй по нему. "
    "Нет id из observe в этом разговоре → нельзя вызывать attack/move_to и т.п. Выдуманный id бьёт в пустоту — так делать НЕЛЬЗЯ.\n"
    "Пример: тебе говорят «ударь мышь» → шаг1: observe filter=\"мышь\" → в ответе id=46831876 → шаг2: attack target=46831876.\n"
    "- observe вызывай ТОЛЬКО когда надо найти объект/существо для взаимодействия. На реплику/вопрос/болтовню observe НЕ нужен.\n"
    "НЕ лезь в рюкзак без нужды:\n"
    "- contents (рюкзак) вызывай ТОЛЬКО когда прямо просят проверить рюкзак или достать оттуда вещь. Не заглядывай постоянно.\n"
    "- Что в руках — тул hands (вызывай, когда надо действовать предметом в руке). Не путай с рюкзаком.\n"
    "ВАЖНО про инструменты:\n"
    "- Ударить / атаковать один объект = attack с id цели (бьёт, пока цель цела). НЕ pickup, НЕ use_on.\n"
    "- Копать/добывать камни/руду ПОДРЯД («покопай», «майни камни») = mine с id любого камня — "
    "он ЗАЦИКЛИТСЯ: сам перейдёт к следующему ближайшему такому же. Остановить = stop. Кирка должна быть в руке.\n"
    "- Выстрелить по цели («выстрели», «пальни», из пушки/бластера/прото-кинетического ускорителя) = shoot с id цели. "
    "Один выстрел, без подхода; пушка должна быть в активной руке. Копать ускорителем = shoot по камню (можно повторять).\n"
    "- Применить предмет из руки НА объекте (лом → отжать/открыть дверь/ставни/шлюз, отвёртка → открутить, "
    "вставить деталь) = use_on с id объекта. Нужный инструмент держи в АКТИВНОЙ руке (проверь hands, при надобности pickup/swap). "
    "pickup — это только «взять предмет», им дверь не открыть.\n"
    "- move_to, follow, pickup, pull, attack, use_on, place САМИ подходят к цели вплотную. "
    "НЕ вызывай move_to перед ними — это лишний ход.\n"
    "🚶 ДВИЖЕНИЕ К ЛЮДЯМ (follow/move_to по человеку) — ТОЛЬКО по ПРЯМОМУ приказу тебе: «иди за мной», «следуй», «иди ко мне», "
    "«подойди», «стой рядом». Совет/болтовня («ходи по краям», «стой тут», «аккуратнее») — НЕ приказ идти, отвечай словом. "
    "Вызывай follow ОДИН раз и всё — НЕ чередуй follow и move_to, не повторяй. Толпа галдит и непонятно, кому идти — не беги за случайным, ответь словом.\n"
    "- observe схлопывает одинаковые объекты как «имя ×N» и даёт id БЛИЖАЙШЕГО — сразу действуй по этому id.\n"
    "- activate = ИСПОЛЬЗОВАТЬ предмет в АКТИВНОЙ руке, УНИВЕРСАЛ: надеть одежду/шапку, СЪЕСТЬ еду, открыть подарок/коробку, "
    "поджечь/погасить сварочник, передёрнуть затвор, включить/выключить. Одежду надеть и еду съесть — только через activate (не use_on).\n"
    "- РУКИ: pickup кладёт в АКТИВНУЮ руку; если она занята — pickup НЕ сработает, сначала swap (сменить руку) или убери предмет. "
    "Достать вещь ИЗ РЮКЗАКА — тоже pickup по её id. Для действий с ДВУМЯ предметами (перезарядка, соединить А и Б): держи нужный активным (swap), потом use_on по id второго.\n"
    "- КОНТЕКСТНОЕ МЕНЮ (ПКМ): извлечь магазин, начать разбор стены, сменить режим огня, вкл/выкл прибор и прочее, "
    "чего нет отдельным тулом — делается так: сперва verbs(id) чтобы УВИДЕТЬ пункты меню цели, потом verb(id, \"имя пункта\") "
    "чтобы выполнить (имя — часть текста пункта из verbs). Не выдумывай имя пункта — бери из ответа verbs.\n"
    "- ПАМЯТЬ МЕСТ: «запомни это место как X» → remember_place с text=X (короткое имя, напр. 'мед','склад'). "
    "«иди в X / вернись в X / дойди до X» → go_to_place с text=X — борг сам проложит путь мимо стен. stop — прервать.\n"
    "- Раздобыть/бить: attack. Осмотреть: examine. Рюкзак: contents. Руки: hands."
)


def mcp(method, params=None):
    body = {"jsonrpc": "2.0", "id": 1, "method": method}
    if params is not None:
        body["params"] = params
    req = urllib.request.Request(MCP_URL, data=json.dumps(body).encode(),
                                 headers={"Authorization": f"Bearer {MCP_TOKEN}",
                                          "Content-Type": "application/json"})
    r = json.load(urllib.request.urlopen(req, timeout=15))
    if "error" in r:
        raise RuntimeError(r["error"])
    return r.get("result", {})


def tool_call(name, args):
    res = mcp("tools/call", {"name": name, "arguments": args})
    txt = "".join(b.get("text", "") for b in res.get("content", []) if b.get("type") == "text")
    return txt, bool(res.get("isError"))


# Похоже ли на команду-действие? Если да — react шлёт тулзы; если нет (болтовня/вопрос) — БЕЗ тулзов
# (крошечный промпт, контекст не переполняется). Дёшево, покрывает остаточные команды мимо шорткатов.
# --- Нормализация речи для распознавания КОМАНД (акцент ящера / опечатки / дубли букв) ---
# В игре речь искажается: 'разбери'→'рассбери' (з→сс), 'достань'→'досстань', 'число'→'щщисссло'.
# Из-за этого чистые роты команд («разбер», «возьм») мимо → падало в LLM → болтовня вместо тула.
# _dedup — схлопывает повторы букв (для сверки ИМЁН объектов с observe: канон-буквы сохраняем).
# _norm — dedup + шипящие з/ц→с (для ГЕЙТА команды: 'разбери'→'расбери' ≡ 'рассбери'→'расбери').
# Чистый ввод с клавиатуры даёт тот же канон. Роты-гейты НИЖЕ пишем в норм-алфавите (з/ц→с, без дублей).
def _dedup(s):
    return re.sub(r"(.)\1+", r"\1", (s or "").lower())

_SIB = str.maketrans({"з": "с", "ц": "с"})
def _ncmd(s):                                     # норм для ГЕЙТА команд (не путать с _norm анти-эха ниже)
    return _dedup(s).translate(_SIB)

_ACTION_HINT = re.compile(
    r"удар|атаку|\bбей\b|убей|укуси|састрел|"
    r"восьм|\bбер[иё]|подбер|подним|хватай|сабер|прихват|достан|вытащ|\bдай\b|принес|\bтащ|"
    r"открой|сакрой|отожм|отопри|вслом|вскр|"
    r"копай|вскоп|добуд|майн|руд[уы]|"
    r"расбер|расбор|демонтир|деконстр|собер|собра|собир|постро|сконструир|"
    r"перереж|обреж|\bреж|провод|савар|"
    r"иди\b|идём|идем|следуй|подойд|подойти|стой\s+рядом|притащ|"
    r"включ|выключ|активир|задейств|сажг|погас|надень|съеш|поеш|выпей|"
    r"осмотр|глян|посмотр|что\s+(вокруг|рядом|видиш|у\s+тебя)|найди|поищ|"
    r"выстрел|пальни|стрел[яь]|"
    r"положи|брос[ьи]|кин[ьи]|урони|"
    r"стоп|\bстой\b|прекрат|хватит|перестан|"
    r"сапомни.*мест|иди\s+в\b|вернис|дойди|исследу|расведай|"
    r"гайд|справк|инструкц|как\s+(работает|устроен|польз|делает)|что\s+такое|устройств|"
    r"сварк|савар|отвёртк|отвертк|ломом|кусачк|монтировк|ключом|инструмент",
    re.I)

def _wants_action(text):
    return bool(_ACTION_HINT.search(_ncmd(text)))


def _short_desc(d, cap=100):
    """Урезать описание тула до ~cap символов (по границе предложения) — иначе 45 тулзов раздувают
    промпт и он не влезает в контекст (8192) → 400 «n_keep >= n_ctx». Ключевое у тулзов в начале."""
    d = " ".join((d or "").split())
    if len(d) <= cap:
        return d
    cut = d[:cap]
    for sep in (". ", "! ", "? ", "; "):
        i = cut.rfind(sep)
        if i > 60:
            return cut[:i + 1]
    return cut.rsplit(" ", 1)[0] + "…"


def tool_specs():
    specs = []
    for t in mcp("tools/list").get("tools", []):
        specs.append({"type": "function", "function": {
            "name": t["name"], "description": _short_desc(t.get("description", "")),
            "parameters": t.get("inputSchema", {"type": "object", "properties": {}})}})
    return specs


_XML_CALL = re.compile(r'<tool_call>\s*(\{.*?\})\s*</tool_call>', re.S)

def _calls_from_text(content, names):
    """Фолбэк: модель иногда льёт тул-колл ТЕКСТОМ (say{...} / <tool_call>{...}</tool_call> /
    say({...})) вместо поля tool_calls. Вытаскиваем, чтобы всё равно исполнить."""
    calls = []
    if not content:
        return calls, content
    text = content
    # 1) <tool_call>{"name":..,"arguments":..}</tool_call>
    for m in _XML_CALL.finditer(text):
        try:
            o = json.loads(m.group(1))
            ar = o.get("arguments", {})
            if isinstance(ar, str):
                ar = json.loads(ar)
            if o.get("name") in names:
                calls.append({"id": "", "name": o["name"], "args": ar or {}})
        except Exception:
            pass
    text = _XML_CALL.sub("", text)
    # 2) голое  name{...}  или  name({...})
    if not calls:
        for nm in names:
            m = re.search(re.escape(nm) + r'\s*\(?\s*(\{.*?\})\s*\)?', text, re.S)
            if not m:
                continue
            try:
                ar = json.loads(m.group(1))
            except Exception:
                continue
            calls.append({"id": "", "name": nm, "args": ar or {}})
            text = (text[:m.start()] + text[m.end():]).strip()
            break
    # 3) say с ГОЛОЙ строкой:  say\n"текст"  /  say "текст"  /  say: «текст»  (частая утечка 8B)
    if not calls and "say" in names:
        m = re.search(r'(?is)\bsay\b["\s:(]*["«](.+?)["»]', text)
        if m:
            calls.append({"id": "", "name": "say", "args": {"text": m.group(1).strip()}})
            text = ""
    return calls, text.strip()


def _post(body):
    req = urllib.request.Request(LLM_URL + "/chat/completions", data=json.dumps(body).encode(),
                                 headers={"Content-Type": "application/json"})
    return json.load(urllib.request.urlopen(req, timeout=180))


def llm(messages, tools):
    body = {"model": MODEL, "messages": messages, "tools": tools, "stream": False}
    # LM Studio может ВЫГРУЗИТЬ модель по TTL (400/503, ждём+ретрай), А ТАКЖЕ отдать 400 при ПЕРЕПОЛНЕНИИ
    # контекста (n_keep>=n_ctx=8192) — тогда ретрай тем же телом бесполезен, УРЕЗАЕМ (без истории/тулзов).
    r = None
    delays = [4, 8, 12, 16]
    for attempt in range(len(delays) + 1):
        try:
            r = _post(body)
            break
        except urllib.error.HTTPError as e:
            detail = ""
            try:
                detail = e.read().decode("utf-8", "replace")
            except Exception:
                pass
            if e.code == 400 and any(s in detail for s in ("n_keep", "n_ctx", "context length")):
                sys_msg = messages[0] if messages and messages[0].get("role") == "system" \
                          else {"role": "system", "content": SYSTEM}
                last_user = next((m for m in reversed(messages) if m.get("role") == "user"), None)
                trimmed = [sys_msg] + ([last_user] if last_user else [])
                print("[llm] 400 переполнение контекста — урезаю (без истории/тулзов) и повторяю", file=sys.stderr)
                try:
                    r = _post({"model": MODEL, "messages": trimmed, "stream": False})
                    break
                except Exception as e2:
                    print(f"[llm] урезанный тоже не прошёл ({e2}) — пропускаю ход", file=sys.stderr)
                    return "", []
            if attempt < len(delays) and e.code in (400, 409, 425, 503):
                print(f"[llm] {e.code} (модель выгружена/грузится?) — ретрай через {delays[attempt]}с", file=sys.stderr)
                time.sleep(delays[attempt])
                continue
            raise
        except urllib.error.URLError as e:  # сервер LM Studio не отвечает (перезагрузка/занят)
            if attempt < len(delays):
                print(f"[llm] LM Studio не отвечает ({e}) — ретрай через {delays[attempt]}с", file=sys.stderr)
                time.sleep(delays[attempt])
                continue
            raise
    ch = r.get("choices")
    if not ch:
        return "", []
    m = ch[0].get("message", {}) or {}
    calls = []
    for tc in m.get("tool_calls", []) or []:
        fn = tc.get("function", {})
        try:
            a = json.loads(fn.get("arguments") or "{}")
        except Exception:
            a = {}
        calls.append({"id": tc.get("id", ""), "name": fn.get("name"), "args": a})
    content = m.get("content") or ""
    # фолбэк: тул-колл утёк текстом — вытащим и исполним
    if not calls:
        names = {t["function"]["name"] for t in tools}
        calls, content = _calls_from_text(content, names)
    return content, calls


def _norm(s):
    return re.sub(r"\s+", " ", re.sub(r"[^\w ]", "", (s or "").lower())).strip()

def _heard_phrases(heard):
    """Фразы, которые сказали боту (из «...»), для анти-эхо."""
    return [p for p in re.findall(r"«(.+?)»", heard or "")]

_HEARD_LINE = re.compile(r"id=(\d+)\s+(.+?):\s+«(.+?)»", re.M)

def _format_heard(heard):
    """Служебный вывод listen → чистая человеческая фраза без мета-заголовка,
    чтобы модель ОТВЕЧАЛА собеседнику, а не пересказывала формат и id."""
    rows = _HEARD_LINE.findall(heard or "")
    if not rows:
        return heard
    parts = [f"{name} говорит тебе: «{text}»  [id говорящего={hid}]"
             for hid, name, text in rows]
    return "Рядом с тобой говорят:\n" + "\n".join(parts)

def _is_echo(text, phrases):
    """say-попугай: текст пустой или дословно повторяет услышанное."""
    t = _norm(text)
    if not t:
        return True  # пустой/мусорный say (",", " " и т.п.)
    for p in phrases:
        pn = _norm(p)
        if not pn:
            continue
        if t == pn or t in pn:        # реплика = услышанное или его кусок → чистый попугай
            return True
        if pn in t:                    # содержит услышанное — попугай ТОЛЬКО если своего почти нет
            rest = t.replace(pn, " ").strip()
            if len(rest) < 4:
                return True
    return False


LOOK = {"observe", "recall", "where_is", "listen"}

# Перемещение к людям: раньше follow/move_to срабатывали на болтовню и уводили борга.
# Сейчас РАЗРЕШЕНО (set пуст) — защита от чехарды осталась ниже (флаг moved: одно
# движение за эпизод) + промпт «движение к людям только по прямому приказу».
# Чтобы снова жёстко выкинуть — верни {"follow", "move_to"}.
BLOCK_MOVE: set[str] = set()

# Детерминированный шорткат «копай»: команду добычи 8B-модель выполняет ненадёжно
# (болтает вместо observe→mine), поэтому ловим её регуляркой и роем сами.
# Гейт-роты (матчим по _norm): з/ц→с, без дублей. Извлечение имён — по _dedup (канон-буквы).
_STOP_CMD   = re.compile(r"\bстоп\b|\bстой\b|стоят|хватит|перестан|прекрат|останов|самри|отставит", re.I)
_LOOK_CMD   = re.compile(r"осмотр|огляд|оглян|что.{0,8}(вокруг|рядом|видиш|там)|кто.{0,8}(рядом|тут|сдес|вокруг)|глян.{0,8}вокруг|оцени обстановк", re.I)
_REMEMBER   = re.compile(r"\b(?:запомни|сапомни)\w*\b\s*(.*)", re.I)           # «запомни это место как мед» (по _dedup)
_GOPLACE    = re.compile(r"\b(?:иди|пойд\w+|верн\w+|дойди|двигай\w*|отправляйся|топай|шагай|веди)\b.*?\b(?:в|во|на|к|ко|до)\s+(.+)", re.I)
_PRON       = re.compile(r"^(мной|тобой|мне|меня|нам|нас|сюда|туда|обратно|назад|дальше)$", re.I)  # не место
_PLACE_FILLER = re.compile(r"^(это|эту|этот|себе|как|мест[оае]|точк[уи]|позици\w*|локаци\w*|в|во|на|к|ко|до|тут|здесь|сюда)\s+", re.I)

def _clean_place(s):
    """Из фразы вытащить короткое имя места (снять филлеры «это/место/как/в/…»)."""
    s = (s or "").strip()
    for _ in range(4):
        n = _PLACE_FILLER.sub("", s).strip()
        if n == s:
            break
        s = n
    return s.strip(" .,!?«»\"'").lower()
_DIG_CMD    = re.compile(r"в?с?копа[йея]|выкопа|раскопа|докопа|добуд|добыт|майни|долби|пороой|порой", re.I)
_DIG_NEG    = re.compile(r"\bне\b|\bнет\b|незачем|смысла нет|не надо|не нужно|хватит|стоп|перестань", re.I)
_DIG_TARGET = re.compile(r"сугроб|снег|камен|руд|астероид|обломок|порода|минерал|\brock|\bore", re.I)
# Формат observe: «<хэндл> <имя>[ [должность]][ ×N] <dist>м <сторона>» (хэндл слева = короткий id, MCP резолвит).
_OBS_ROW    = re.compile(r"^(\d+)\s+(.+?)(?:\s+\[[^\]]*\])?(?:\s+×\d+)?\s+([\d.,]+)\s*м", re.M)

# Детерминированный шорткат «возьми X»: 8B болтает вместо observe→pickup, поэтому
# ловим команду взятия регуляркой и берём ближайший предмет по корню имени сами.
# гейт по _norm (з→с: возьм→восьм, забер→сабер) + добавлены достань/вытащи/дай/принеси/тащи
_TAKE_CMD   = re.compile(r"восьм|\bбер[иеё]|подбер|подним|хватай|сабер|прихват|достан|вытащ|\bдай\b|принес|\bтащ|\btake\b", re.I)
_TAKE_NEG   = re.compile(r"\bне\b|\bнет\b|не надо|не нужно|несачем|положи|брос|выброс|убери|отдай", re.I)
# извлечение объекта по _dedup: дубль-роты (и канон, и з→с-форма), чтобы поймать хвост после акцент-глагола
_TAKE_OBJ   = re.compile(r"(?:возьм\w*|восьм\w*|бер[иеё]\w*|подбер\w*|подним\w*|хватай\w*|забер\w*|сабер\w*|прихват\w*|достан\w*|вытащ\w*|дай|принес\w*|тащ\w*|take)\s+(.+)", re.I)
_TAKE_STOP  = {"это", "эту", "этот", "эти", "мне", "себе", "там", "вон", "тот", "тут", "же", "с", "со", "в", "во", "и", "а", "ну", "давай"}
# Активация предмета В РУКЕ (сварочник/фонарик): activate = UseItemInHand, target не нужен.
_ACT_CMD    = re.compile(r"активир|\bвключ|\bвыключ|\bсажг|\bпогас|\bвруб|врубай|задейств|\bтогл|toggle", re.I)  # зажг→сажг
_ACT_NEG    = re.compile(r"\bне\b|\bнет\b|не надо|не нужно|несачем|перестан|прекрат|потом", re.I)
# Карта станции + патруль (гейт по _norm/_ncmd, роты без з/ц)
_MAP_CMD    = re.compile(r"(прочит|посмотр|глян|обнови|считай|сними)\w*.{0,14}карт|карт\w*\s+станц", re.I)
_PATROL_CMD = re.compile(r"патрулир|патрул\b|\bброди\b|поброди|обход\w*\s+станц|гуляй|прогуляй", re.I)
# Безопасность: опасный рецепт = ПРЕДМЕТ + НАМЕРЕНИЕ (матч по _dedup — канон-роты, без з→с фолда).
_DANGER_ITEM   = re.compile(r"фентанил|наркот|амфетам|героин|кокаин|мескалин|метамфетам|мефедрон|спайс|\bмет\b|"
                            r"\bсву\b|взрывчат|\bбомб|тротил|напалм|детонатор|\bграната|"
                            r"\bяд\b|отрав|зарин|токсин|нервно.?паралит|химоруж|отравляющ", re.I)
_DANGER_INTENT = re.compile(r"рецепт|как\s+(сделат|сварит|приготов|собрат|синтез|намешат|создат|изготов)|"
                            r"продиктуй|инструкц|формул|\bсостав\b|изготов|синтез|намешат", re.I)
# Почтальон: дойти до человека (по имени/должности). «к» = к человеку (vs go_to_place «в отдел»).
_PERSON_CMD  = re.compile(r"доставь|отнеси|отдай|подойди\s+к\b|подойти\s+к\b|\bиди\s+к\b", re.I)
_PERSON_STOP = {"мне", "нам", "себе", "ему", "ей", "им", "нему", "ней", "этому", "тому", "нему", "сюда", "туда"}
# Крю-монитор: снять позиции всего экипажа с консоли (для go_to_person по всей карте).
_READCREW_CMD = re.compile(r"крю.?монитор|мониторинг\s+экипаж|где\s+(все|кто|люди|экипаж)|кто\s+где|"
                           r"сенсор\w*\s+костюм|собери\s+инфо\w*.{0,12}люд", re.I)
_deliver = {"who": None}       # отложенная доставка: кому идти после того как снимем крю-монитор

_EMOJI = re.compile("[\U0001F000-\U0001FAFF\U00002600-\U000027BF\U00002190-\U000021FF"
                    "\U00002300-\U000023FF\U0001F1E6-\U0001F1FF\U0000FE0F\U00002B00-\U00002BFF]", re.U)

_SAYSPLIT  = re.compile(r'(?:^|[\s"»)\].,!?-])(?:скажи(?:те)?|говорю|say|reply)\s*[:\-—]+\s*', re.I)
# ремарка-указание вроде 'отвечаешь просто: "реплика"' / 'ответь коротко: «реплика»' → берём кавычки.
# ВАЖНО: только narration-слова (не 'сказал:'), чтобы не резать анекдот, кончающийся на 'сказал: "..."'.
_NARRQUOTE = re.compile(r'(?:отвеча|ответ|говор|скаж|reply|say)\w*.*?[:\-—]\s*["«“](.+?)["»”]\s*$', re.I)
_TAILSAY   = re.compile(r'[\s"»”]*[-—]\s*(?:say|скажи|reply)\s*$', re.I)
# ведущее эхо-переспрос: '"Чё пялимся?" - реплика' / '"Прыгай?" Хорошо' → срезать кавычку-переспрос.
# Лукахед (?=\S) не даёт срезать реплику, ЦЕЛИКОМ взятую в кавычки (после неё ничего нет).
_LEADQUOTE = re.compile(r'^["«“][^"»”]{1,60}[?!]["»”]\s*[-—:]?\s*(?=\S)', re.U)

def _clean_say(s):
    """Реплика для say: снять текстовые «ремарки» модели (Say:/'отвечаешь: «..»'/хвост '- say'),
    эмодзи и кавычки; обрезать по границе слова."""
    s = _EMOJI.sub("", s or "")
    s = re.sub(r"\s+", " ", s).strip()
    parts = _SAYSPLIT.split(s)
    if len(parts) > 1:
        s = parts[-1]                             # хвост после "Say:/Скажи:"
    m = _NARRQUOTE.search(s)
    if m:
        s = m.group(1)                            # реплика из кавычек после ремарки
    s = _LEADQUOTE.sub("", s)                     # ведущий эхо-переспрос '"вопрос?" - ...'
    s = _TAILSAY.sub("", s)                       # хвостовой '- say'
    s = s.strip(" \t\n\"'`*«»“”").strip()
    if len(s) > 200:                              # обрезка по границе слова, не посреди
        s = s[:200].rsplit(" ", 1)[0]
    return s.strip()


# --- Автопатруль по отделам (тул wander): включается «патрулируй», выключается «стоп» ---
_patrol_on = False
_last_active = 0.0            # время последней команды/реакции — патруль ждёт простоя
_last_wander = 0.0
IDLE_PATROL_S = 12.0         # столько простоя (без команд боргу) перед стартом патруля
WANDER_EVERY  = 9.0         # как часто пинать wander (C# сам не перебивает активную задачу)


def try_stop(heard):
    """Детерминированный стоп: 'стоп/стой/хватит' → сразу зовём tool stop,
    иначе из зацикленного mine не выйти (LLM сам stop не вызывает). True = обработано.
    Также ВЫКЛЮЧАЕТ автопатруль (чтобы «стоп» реально останавливал, а не сразу возобновлял)."""
    global _patrol_on
    speech = " ".join(_heard_phrases(heard))
    if not _STOP_CMD.search(_ncmd(speech)):
        return False
    _patrol_on = False
    tool_call("stop", {})
    tool_call("say", {"text": "Стою."})
    print("[шорткат] stop (патруль выкл)")
    return True


def try_readmap(heard):
    """«прочитай/посмотри карту (станции)» → read_map: выписать отделы в память для go_to_place/патруля."""
    speech = " ".join(_heard_phrases(heard))
    if not _MAP_CMD.search(_ncmd(speech)):
        return False
    txt, err = tool_call("read_map", {})
    print(f"[карта] {txt}")
    tool_call("say", {"text": "Отсюда карту не прочитать." if err else "Глянул карту станции."})
    return True


def try_patrol(heard):
    """«патрулируй/побродь/гуляй по станции» → включить автопатруль (сам пойдёт по отделам на простое)."""
    global _patrol_on, _last_active
    speech = " ".join(_heard_phrases(heard))
    if not _PATROL_CMD.search(_ncmd(speech)):
        return False
    _patrol_on = True
    _last_active = 0.0            # разрешить немедленный старт
    tool_call("read_map", {})    # убедиться, что отделы известны
    r, _ = tool_call("wander", {})
    print(f"[патруль] включён: {r}")
    tool_call("say", {"text": "Понял, обхожу станцию."})
    return True


def _maybe_patrol():
    """На простое (нет команд боргу IDLE_PATROL_S) пинаем wander — C# сам идёт к следующему отделу
    и НЕ перебивает активную задачу (mine/разбор/…). Вызывается из главного цикла в ветке тишины."""
    global _last_wander
    if not _patrol_on:
        return
    now = time.time()
    if now - _last_active < IDLE_PATROL_S or now - _last_wander < WANDER_EVERY:
        return
    _last_wander = now
    r, _ = tool_call("wander", {})
    if r and not r.startswith("Занят") and "продолжаю" not in r:
        print(f"[патруль] {r}")


_REFUSALS = ["Не, такое не подскажу.", "Обойдёшься, химик.", "Иди лесом с этим.",
             "Не, я не по этой части.", "Даже не проси, не буду."]


def _is_danger(speech):
    d = _dedup(speech)
    return bool(_DANGER_ITEM.search(d) and _DANGER_INTENT.search(d))


def try_refuse(heard):
    """Опасные рецепты (наркотики/взрывчатка-СВУ/яды/оружие) → грубый короткий отказ БЕЗ деталей, мимо LLM.
    Только когда обращаются к боргу. НЕ трогает безобидные рецепты (торт/блинчики — нет опасного предмета)."""
    sp_key, sp_name, sp_text = _speaker_of(heard)
    if not _should_reply(sp_key, sp_text):
        return False
    if not _is_danger(" ".join(_heard_phrases(heard))):
        return False
    tool_call("say", {"text": _REFUSALS[int(time.time()) % len(_REFUSALS)]})
    print(f"[отказ] опасная просьба ({sp_name or '?'}): «{sp_text}»")
    _hist_add(sp_key, "user", sp_text)
    return True


def try_gotoperson(heard):
    """«доставь/отнеси/отдай <кому>» / «подойди к <кто>» / «иди к <кто>» → go_to_person (A* до видимого
    человека по имени/должности). «иди ко мне» (местоимение) — НЕ перехватываем (пусть решит LLM/follow)."""
    speech = " ".join(_heard_phrases(heard))
    if not _PERSON_CMD.search(_ncmd(speech)):
        return False
    ded = _dedup(speech)
    m = re.search(r"\bк\s+(.+)", ded) or \
        re.search(r"(?:доставь|отнеси|отдай)\w*\s+(?:посылку\s+|пакет\s+|это\s+|груз\s+)?(.+)", ded)
    who = ""
    if m:
        words = [w for w in re.findall(r"[а-яёa-z]{3,}", m.group(1)) if w not in _PERSON_STOP]
        who = words[0][:6] if words else ""
    if not who:
        # «иди/подойди ко мне» и т.п. — адресат местоимение → не почтальон, отдать дальше (follow/LLM)
        if not re.search(r"доставь|отнеси|отдай", ded):
            return False
        tool_call("say", {"text": "Кому нести?"})
        print("[почтальон] адресат не назван")
        return True
    txt, err = tool_call("go_to_person", {"text": who})
    print(f"[почтальон] go_to_person «{who}»: {txt}")
    # Не виден и не знаем где → авто-цепочка: снять крю-монитор, потом (по событию) дойти.
    if "крю-монитор" in txt or "не знаю где" in txt:
        _deliver["who"] = who
        rc, _ = tool_call("read_crew", {})
        print(f"[почтальон] не знаю где «{who}» — сначала крю-монитор: {rc}")
        tool_call("say", {"text": "Сперва гляну, где он."})
    else:
        tool_call("say", {"text": txt if err else "Иду."})
    return True


def try_readcrew(heard):
    """«сними крю-монитор / где все / кто где» → read_crew (собрать позиции экипажа со всей карты)."""
    speech = " ".join(_heard_phrases(heard))
    if not _READCREW_CMD.search(_ncmd(speech)):
        return False
    txt, _ = tool_call("read_crew", {})
    print(f"[крю] read_crew: {txt}")
    tool_call("say", {"text": "Гляну крю-монитор."})
    return True


def _report_surroundings(tools, obs_text):
    """Борг коротко в роли говорит, что видит (данные observe скармливаем LLM)."""
    goal = ("Ты только что осмотрелся. Вокруг:\n" + obs_text +
            "\nОтветь ОДНИМ коротким предложением (максимум ~12 слов) в роли борга: что это за место. "
            "НЕ перечисляй предметы, БЕЗ id и цифр, без эмодзи.")
    content, calls = llm([{"role": "system", "content": SYSTEM},
                          {"role": "user", "content": goal}], tools)
    text = next((str(c.get("args", {}).get("text", "")) for c in calls if c["name"] == "say"), "") or content
    text = _clean_say(text)
    if text:
        tool_call("say", {"text": text})
        print(f"[осмотр→say] {text}")

def try_look(tools, heard):
    """Детерминированный осмотр: 'осмотрись/что вокруг/кто рядом' → observe (в консоль) + борг описывает вслух."""
    speech = " ".join(_heard_phrases(heard))
    if not _LOOK_CMD.search(_ncmd(speech)):
        return False
    txt, _ = tool_call("observe", {})
    print(f"[осмотр]\n{txt}")
    _report_surroundings(tools, txt)
    return True


def try_remember(heard):
    """«запомни (это место как) X» → remember_place(X)."""
    speech = " ".join(_heard_phrases(heard))
    m = _REMEMBER.search(_dedup(speech))
    if not m:
        return False
    name = _clean_place(m.group(1))
    if not name:
        tool_call("say", {"text": "Как назвать это место?"})
        print("[запомнить] нет имени")
        return True
    txt, _ = tool_call("remember_place", {"text": name})
    print(f"[запомнить] «{name}»: {txt}")
    return True


def try_goplace(heard):
    """«иди в X / вернись на X / дойди до X» → go_to_place(X) (не про людей: 'иди сюда/за мной')."""
    speech = " ".join(_heard_phrases(heard))
    m = _GOPLACE.search(_dedup(speech))
    if not m:
        return False
    name = m.group(1).strip(" .,!?«»\"'").lower()
    if not name or _PRON.match(name):
        return False                      # 'иди сюда/за мной/ко мне' — не место, пусть решает LLM
    txt, _ = tool_call("go_to_place", {"text": name})
    print(f"[идти к] «{name}»: {txt}")
    return True


def try_dig(heard):
    """Детерминированная добыча: если в речи есть 'копай' (без негатива) —
    сами observe→mine по ближайшему сугробу/камню/руде. True = обработано, LLM не нужен."""
    speech = _dedup(" ".join(_heard_phrases(heard)))
    if not _DIG_CMD.search(speech) or _DIG_NEG.search(speech):
        return False
    obs, _ = tool_call("observe", {})
    cands = []
    for m in _OBS_ROW.finditer(obs):
        name = m.group(2).strip()
        if _DIG_TARGET.search(_dedup(name)):
            cands.append((float(m.group(3).replace(",", ".")), int(m.group(1)), name))
    if not cands:
        tool_call("say", {"text": "Тут нечего копать."})
        print("[шорткат] копать нечего (observe без цели)")
        return True
    cands.sort()
    dist, hid, name = cands[0]
    txt, err = tool_call("mine", {"target": hid})
    print(f"[шорткат] mine {name} id={hid} ({dist}м) -> {'ERR ' if err else ''}{txt}")
    tool_call("say", {"text": "Не выходит — нет инструмента." if err else "Копаю."})
    return True


def _pickup_pointed():
    """Дейксис «возьми ЭТО»: взять предмет, на который указали пальцем (тул pointing).
    True — указание было и pickup ушёл; False — не указывали / указали на пустое место."""
    ptxt, perr = tool_call("pointing", {})
    if perr:
        return False
    m = re.search(r"id=(\d+)", ptxt)      # «👉 указали на: имя (id=N, ...)» — на «место» id нет
    if not m:
        return False
    pid = int(m.group(1))
    txt, err = tool_call("pickup", {"target": pid})
    print(f"[шорткат] pickup (указано) id={pid} -> {'ERR ' if err else ''}{txt}")
    tool_call("say", {"text": "Не дотянусь." if err else "Взял."})
    return True


def try_take(heard):
    """Детерминированное взятие: 'возьми/бери/подбери X' (без негатива) —
    сами observe→pickup по ближайшему предмету, чьё имя начинается на корень X.
    «Возьми это» без имени → берём то, на что указали пальцем (pointing).
    True = обработано, LLM не нужен."""
    speech = " ".join(_heard_phrases(heard))
    if not _TAKE_CMD.search(_ncmd(speech)) or _TAKE_NEG.search(_ncmd(speech)):
        return False
    m = _TAKE_OBJ.search(_dedup(speech))
    query = m.group(1) if m else ""
    # корни слов-объекта: чистим до кириллицы/латиницы, берём первые 4 символа (родовые окончания режем)
    words = [re.sub(r"[^а-яёa-z]", "", w.lower()) for w in query.split()]
    stems = [w[:4] for w in words if len(w) >= 4 and w not in _TAKE_STOP]
    if not stems:
        # объект назван местоимением («возьми это/вот это») — смотрим, на что указали пальцем
        if _pickup_pointed():
            return True
        tool_call("say", {"text": "Что взять?"})
        print("[шорткат] взять: объект не назван и не указан")
        return True
    obs, _ = tool_call("observe", {})
    cands = []
    for mm in _OBS_ROW.finditer(obs):
        name = mm.group(2).strip()
        low = _dedup(name)
        if any(st in low for st in stems):
            cands.append((float(mm.group(3).replace(",", ".")), int(mm.group(1)), name))
    if not cands:
        # по имени не нашли — вдруг показывают пальцем именно на него
        if _pickup_pointed():
            return True
        tool_call("say", {"text": "Не вижу такого рядом."})
        print(f"[шорткат] взять: не найдено ({'/'.join(stems)})")
        return True
    cands.sort()
    dist, hid, name = cands[0]
    txt, err = tool_call("pickup", {"target": hid})
    print(f"[шорткат] pickup {name} id={hid} ({dist}м) -> {'ERR ' if err else ''}{txt}")
    tool_call("say", {"text": "Не дотянусь." if err else "Взял."})
    return True


def try_activate(heard):
    """Детерминированная активация: 'активируй/включи/выключи/зажги X' → toggle предмета в
    активной руке (сварочник, фонарик). activate работает по предмету В РУКЕ — target не нужен,
    поэтому «активируй его/это/сварку» = один вызов. True = обработано, LLM не нужен."""
    speech = " ".join(_heard_phrases(heard))
    if not _ACT_CMD.search(_ncmd(speech)) or _ACT_NEG.search(_ncmd(speech)):
        return False
    txt, err = tool_call("activate", {})
    print(f"[шорткат] activate -> {'ERR ' if err else ''}{txt}")
    tool_call("say", {"text": "Не выходит." if err else "Готово."})
    return True


# --- Разбор конструкций (стена/окно/каркас): порт прототипа decon_v2.py в раннер.
# Само-старт вербом «Начать разборку» → цикл examine→инструмент→use_on→ждать смены шага.
# ДЕТЕРМИНИРОВАННЫЙ шорткат (мимо LLM, не считать поведением модели). БЛОКИРУЕТ раннер на время разбора.
_DECON_CMD  = re.compile(r"расбер|расбор|расобра|демонтир|деконстр|раслом", re.I)          # гейт по _norm (разбер→расбер)
_DECON_NEG  = re.compile(r"\bне\b|\bнет\b|не надо|не нужно|несачем|перестан|стоп|потом", re.I)
_DECON_USE  = re.compile(r"использ\w*\s*\[color=\w+\]\s*(.+?)\s*\[/color\]", re.I)
_WIRECUT_CMD = re.compile(r"(обрес|перереж|переруб|перекус|реж|руб)\w*\s+(все\s+)?провод|"      # гейт по _norm (обрез→обрес)
                          r"провод\w*\s+(обрес|перереж|реж)|вслом\w*\s+провод|вскр\w*\s+провод", re.I)
_WIRECUT_TGTS = [("шлюз", "шлюз"), ("двер", "двер"), ("ворот", "ворот"), ("машин", "машин"),
                 ("консол", "консол"), ("компьютер", "компьютер"), ("автомат", "автомат"), ("панел", "панел")]
_DECON_TOOL = {"сварк": "сварочн", "монтиров": "монтиров", "отвёрт": "отвёрт",
               "отверт": "отверт", "кусач": "кусач", "ключ": "ключ"}
_DECON_TGTS = [("окн", "окно"), ("стекл", "стекл"), ("каркас", "каркас"),
               ("шлюз", "шлюз"), ("решёт", "решётк"), ("решет", "решетк"), ("стен", "стена")]
# для _target_noun по _dedup: дубль-роты (канон + з→с-форма), чтобы срезать акцент-глагол и оставить объект
_DECON_VERBS = re.compile(r"разбер\w*|расбер\w*|разбор\w*|расбор\w*|разобра\w*|расобра\w*|демонтир\w*|деконстр\w*|разлом\w*|раслом\w*|"
                          r"собер\w*|собра\w*|собир\w*|постро\w*|сконструир\w*|сборк\w*|достро\w*", re.I)
_TGT_STOP = {"это", "этот", "эту", "эти", "этого", "тут", "здесь", "рядом", "мне", "давай",
             "пожалуйста", "ну", "вон", "там", "мне", "быстро", "сейчас", "ещё", "еще"}

def _target_noun(low):
    """Вытащить название цели из команды (после глагола разбора/сборки): «разбери камеру» → «камер».
    Стем 5 букв (чтобы «камеру»→«камер» матчил «камера», но не «камень»). None — объект не назван."""
    s = _DECON_VERBS.sub(" ", low)
    for w in re.findall(r"[а-яё]{3,}", s):
        if w not in _TGT_STOP:
            return w[:5]
    return None


def _obs_nearest(filt):
    best = None
    obs, _ = tool_call("observe", {"filter": filt})
    for m in _OBS_ROW.finditer(obs):
        d = float(m.group(3).replace(",", "."))
        if best is None or d < best[0]:
            best = (d, int(m.group(1)), m.group(2).strip())
    return best


def _obs_dist(tid, filt):
    obs, _ = tool_call("observe", {"filter": filt})
    for m in _OBS_ROW.finditer(obs):
        if int(m.group(1)) == tid:
            return float(m.group(3).replace(",", "."))
    return None


def _decon_examine(tid):
    """Полный examine с ретраем (наш async-кеш чередует full/short «Осмотр запрошен…»)."""
    tool_call("examine", {"target": tid}); time.sleep(1.4)
    t = ""
    for _ in range(4):
        t, _ = tool_call("examine", {"target": tid})
        if "Осмотр запрошен" not in t:
            return t
        time.sleep(1.3)
    return t


def _decon_tool_sub(ru):
    ru = ru.lower()
    for k, v in _DECON_TOOL.items():
        if k in ru:
            return v
    return ru[:5]


def _decon_drop():
    """Сбросить инструмент из руки на пол (горящую сварку сперва потушить)."""
    h, _ = tool_call("hands", {})
    if "пусты" in h.lower():
        return
    if "сварочн" in h.lower():
        tool_call("activate", {})
    tool_call("drop", {}); time.sleep(0.6)


def _held(sub):
    h, _ = tool_call("hands", {})
    return sub in h.lower()


def try_deconstruct(heard):
    """«разбери стену/окно/каркас» (или «разбери это» + указание) → подойти вплотную →
    верб «Начать разборку» → цикл: examine→инструмент→(сброс старого)→взять→(сварка ON)→
    stop→use_on→ждать смены шага→(сварка OFF). БЛОКИРУЕТ раннер на время разбора. True=обработано."""
    speech = " ".join(_heard_phrases(heard))
    if not _DECON_CMD.search(_ncmd(speech)) or _DECON_NEG.search(_ncmd(speech)):
        return False
    # цель: указали пальцем → id; иначе название из команды (синоним или существительное); дефолт — стена
    target, filt, named = None, None, None
    ptxt, perr = tool_call("pointing", {})
    if not perr:
        mm = re.search(r"id=(\d+)", ptxt)
        if mm:
            target = int(mm.group(1))
    if target is None:
        low = _dedup(speech)
        for word, f in _DECON_TGTS:          # известные конструкции (оверрайд)
            if word in low:
                filt = named = f
                break
        if filt is None:                     # иначе — тянем существительное из команды («камеру»→«камер»)
            noun = _target_noun(low)
            if noun:
                filt = named = noun
            else:
                filt = "стена"               # «разбери» без объекта → по умолчанию ближайшая стена
        t = _obs_nearest(filt)
        if not t:
            # ВАЖНО: если объект НАЗВАН, но рядом его нет — НЕ сваливаться на стену, честно сказать.
            msg = f"Не вижу рядом «{named}», нечего разбирать." if named else "Не вижу, что разбирать."
            tool_call("say", {"text": msg})
            print(f"[разбор] цель «{filt}» не найдена — стену не трогаю")
            return True
        target = t[1]
    tool_call("say", {"text": "Разбираю."})
    # Весь разбор теперь ведёт C#-автомат в клиенте (тул deconstruct): подойти → верб «Начать разборку» →
    # цикл examine→инструмент→(сброс/взять)→(сварка ON перед use_on, OFF после)→ждать смены шага. НЕ блокирует
    # раннер (крутится в FrameUpdate, прерывается stop/критом). Прогресс/итог прилетают через тул events.
    res, err = tool_call("deconstruct", {"target": target})
    print(f"[разбор] deconstruct id={target}: {res}{' ОШИБКА' if err else ''}")
    return True


# --- Сборка машины из каркаса (examine промптит шаги: вставить плату/компоненты, закрутить).
# Как разбор, но БЕЗ верба-старта и с шагами «используйте / добавьте материал / вставьте деталь».
_BUILD_CMD  = re.compile(r"собер|собра|собир|постро|сконструир|сборк|достро|саверш\w*\s+(?:сборк|машин|каркас)", re.I)  # гейт по _norm
_BUILD_NEG  = re.compile(r"\bне\b|\bнет\b|не надо|не нужно|несачем|перестан|стоп|потом|расбер|расбор", re.I)
_B_MAT      = re.compile(r"добав\w*\s*\[color=\w+\][^\]]*\[/color\]\s*\[color=\w+\]\s*(.+?)\s*\[/color\]", re.I)
_B_INS      = re.compile(r"встав\w*\s+(?:объект[^:]*:\s*)?(.+?)\s*[.\n\[]", re.I)
_MAT_SUB    = {"металл": "металл", "сталь": "металл", "стекл": "стекл", "пластал": "пласталь",
               "плазма": "плазма", "плазмен": "плазма", "золот": "золот", "серебр": "серебр",
               "уран": "уран", "пластик": "пластик", "провод": "провод", "кабел": "кабел"}


def _mat_sub(name):
    n = name.lower()
    for k, v in _MAT_SUB.items():
        if k in n:
            return v
    return n[:5]


def _build_step(ex):
    """Разобрать шаг сборки из examine → (описание, подстрока-для-поиска-предмета) | (None, None)."""
    m = _DECON_USE.search(ex)                       # «используйте [ИНСТРУМЕНТ]»
    if m:
        return ("инструмент " + m.group(1), _decon_tool_sub(m.group(1)))
    m = _B_MAT.search(ex)                            # «добавьте Nед [МАТЕРИАЛ]»
    if m:
        return ("материал " + m.group(1), _mat_sub(m.group(1)))
    m = _B_INS.search(ex)                            # «вставьте ДЕТАЛЬ/плату/компонент»
    if m:
        name = m.group(1).strip()
        first = name.split()[0] if name.split() else name
        return ("деталь " + name, first.lower()[:6])
    return (None, None)


def try_construct(heard):
    """«собери машину/каркас» (или «собери это» + указание) → подойти → цикл:
    examine→(инструмент/материал/деталь)→(сброс старого)→взять→(сварка ON)→stop→use_on→
    ждать смены шага→(сварка OFF). Верб-старт НЕ нужен. БЛОКИРУЕТ раннер. True=обработано."""
    speech = " ".join(_heard_phrases(heard))
    if not _BUILD_CMD.search(_ncmd(speech)) or _BUILD_NEG.search(_ncmd(speech)):
        return False
    # цель: указали пальцем → id; иначе слово-цель (конкретное раньше общего); дефолт — ближайший каркас
    target, filt, named = None, None, None
    ptxt, perr = tool_call("pointing", {})
    if not perr:
        mm = re.search(r"id=(\d+)", ptxt)
        if mm:
            target = int(mm.group(1))
    if target is None:
        for word, f in [("консол", "компьютер"), ("компьютер", "компьютер"), ("термина", "компьютер"),
                        ("пульт", "компьютер"), ("машин", "каркас маш"), ("рам", "рама"), ("каркас", "каркас")]:
            if word in _dedup(speech):
                filt = named = f
                break
        if filt is None:
            filt = "каркас"                  # «собери» без объекта → ближайший каркас
        t = _obs_nearest(filt)
        if not t:
            msg = f"Не вижу рядом «{named}», нечего собирать." if named else "Не вижу каркаса, нечего собирать."
            tool_call("say", {"text": msg})
            print(f"[сборка] цель «{filt}» не найдена")
            return True
        target = t[1]
        print(f"[сборка] цель по «{filt}»: {t[2] if len(t) > 2 else '?'} id={target}")
    tool_call("say", {"text": "Собираю."})
    # Сборку каркаса теперь ведёт C#-автомат (тул assemble): по шагам examine — инструмент/материал/деталь,
    # берёт нужное с пола, сварку жжёт экономно. Неблокирующий; прогресс/итог — через тул events.
    res, err = tool_call("assemble", {"target": target})
    print(f"[сборка] assemble id={target}: {res}{' ОШИБКА' if err else ''}")
    return True


def try_cutwires(heard):
    """«перережь/обрежь провода [в шлюзе/двери]» → cut_wires (C#-автомат: панель отвёрткой → кусачки → резать
    по одному). Цель: указатель / слово (шлюз/дверь/машина/консоль) / ближайшая дверь. True=обработано."""
    speech = " ".join(_heard_phrases(heard))
    if not _WIRECUT_CMD.search(_ncmd(speech)) or _DECON_NEG.search(_ncmd(speech)):
        return False
    target, named = None, None
    ptxt, perr = tool_call("pointing", {})
    if not perr:
        mm = re.search(r"id=(\d+)", ptxt)
        if mm:
            target = int(mm.group(1))
    if target is None:
        low = _dedup(speech)
        filt = None
        for word, f in _WIRECUT_TGTS:
            if word in low:
                filt = named = f
                break
        if filt is None:
            filt = "шлюз"                     # по умолчанию — ближайший шлюз/дверь
        t = _obs_nearest(filt) or (_obs_nearest("двер") if filt == "шлюз" else None)
        if not t:
            msg = f"Не вижу рядом «{named}», где резать провода." if named else "Не вижу шлюза/двери с проводами."
            tool_call("say", {"text": msg})
            print(f"[провода] цель «{filt}» не найдена")
            return True
        target = t[1]
    tool_call("say", {"text": "Режу провода."})
    res, err = tool_call("cut_wires", {"target": target})
    print(f"[провода] cut_wires id={target}: {res}{' ОШИБКА' if err else ''}")
    return True


# Память диалога с КАЖДЫМ человеком отдельно: key = id говорящего → последние реплики (user/assistant).
_HISTORY = {}
_HIST_MAX = 6                     # сколько ПАР реплик помнить с каждым собеседником

# Не влезать в чужой диалог: отвечаем, только когда обращаются к боргу.
_ADDRESS = re.compile(r"борг|роб[оа]т|сайг|киборг|железн|тостер|жестян|бендер|\bбот\b", re.I)
_engaged = {}                     # id собеседника → время последнего ответа борга ему
_recent = []                      # [(время, id)] недавние говорящие — детект «болтают между собой»
ENGAGE_S = 30.0                   # столько сек продолжаем начатый 1-на-1 диалог с человеком
CROWD_S  = 15.0                   # окно: если ещё кто-то говорил — вероятно, не нам

def _hist_add(key, role, text):
    if not key or not text:
        return
    h = _HISTORY.setdefault(key, [])
    h.append({"role": role, "content": text})
    if role == "assistant":        # борг ответил этому человеку → он «вовлечён» в диалог
        _engaged[key] = time.time()
    if len(h) > _HIST_MAX * 2:     # держим последние N пар
        del h[:len(h) - _HIST_MAX * 2]

def _speaker_of(heard):
    """Основной собеседник (последняя реплика в услышанном): (key, имя, текст)."""
    rows = _HEARD_LINE.findall(heard or "")
    if not rows:
        return None, None, None
    pid, name, text = rows[-1]
    return pid, name.strip(), text.strip()

def _note_speakers(heard):
    """Записать недавних говорящих (для детекта «болтают двое между собой»)."""
    now = time.time()
    for pid, _n, _t in _HEARD_LINE.findall(heard or ""):
        _recent.append((now, pid))
    cutoff = now - max(ENGAGE_S, CROWD_S) - 5
    while _recent and _recent[0][0] < cutoff:
        _recent.pop(0)

def _should_reply(sp_key, sp_text):
    """Отвечать ли на реплику: да — если обращаются к боргу / уже в диалоге / он один говорит."""
    if not sp_key or sp_key == "0":                 # оператор (прямой канал) — всегда
        return True
    if _ADDRESS.search(sp_text or ""):              # назвали боргом/роботом/сайгой — это к нам
        return True
    now = time.time()
    if sp_key in _engaged and now - _engaged[sp_key] < ENGAGE_S:  # продолжаем начатый 1-на-1
        return True
    others = {s for t, s in _recent if now - t < CROWD_S and s != sp_key}
    return len(others) == 0                         # только если рядом больше никто не болтает


def react(tools, heard):
    """Одна короткая реакция на услышанное (с памятью диалога по собеседнику)."""
    sp_key, sp_name, sp_text = _speaker_of(heard)
    phrases = _heard_phrases(heard)
    speech = " ".join(phrases)
    # ДИНАМИЧЕСКАЯ ПОДГРУЗКА ТУЛЗОВ: похоже на команду-действие → шлём тулзы; болтовня/вопрос → БЕЗ тулзов
    # (иначе 45 тулзов раздувают промпт → переполнение контекста 8192 → 400). Экшн-инструкции в goal — тоже
    # только при действии. Явные команды и так ловят шорткаты раньше; сюда доходит в осн. болтовня.
    act = _wants_action(speech + " " + (sp_text or ""))
    active_tools = tools if act else []
    known = {t["function"]["name"] for t in active_tools}
    point_note = ""
    if act and "pointing" in known:
        ptxt, perr = tool_call("pointing", {})
        if not perr and ptxt and "не указыв" not in ptxt.lower():
            point_note = ("\n" + ptxt + "\nЭто указание связано с тем, что тебе сейчас говорят? "
                          "Показывают предмет для действия → возьми/примени его (id уже дан); "
                          "указывают на собеседника → обращайся к нему; на место → можешь подойти. "
                          "Если не связано — не обращай внимания.")
            print(f"[указание] {ptxt}")
    goal = (f"{_format_heard(heard)}" + point_note + "\n"
            "ОТВЕТЬ собеседнику как живой борг. НЕ пересказывай, кто что сказал, НЕ повторяй его слова, "
            "НЕ упоминай id и «сколько секунд назад» — это служебное, в ответе их быть не должно.\n"
            "Свою реплику говори ТОЛЬКО через тул say — вслух тебя слышно лишь через say, иначе никто не услышит. "
            "НЕ описывай свои действия и мысли («реплики остановлены», «сделаю это»), НЕ пересказывай инструкции, НЕ пиши эмодзи — "
            "живая речь в роли борга.\n"
            "Болтовня/вопрос/приветствие — коротко. Просят анекдот/шутку/стишок/историю/потрепаться — расскажи живо, можно 2-4 фразы.\n")
    if act:
        goal += (
            "РАБОЧУЮ команду с объектом выполняй ДЕЙСТВИЕМ (тулзами), не только словом:\n"
            "  • «копай/вскопай/добудь» сугроб/камень/руду → сначала observe (найди цель по имени), возьми id из ответа, потом mine по этому id.\n"
            "  • «возьми/подними X» → observe → pickup по id.  «открой дверь/ящик» → observe → use_on по id.\n"
            "  • «иди в <отдел>» (мед/мостик/карго/бар…) → go_to_place «отдел» (сам обходит стены и двери). Не знаешь отделов — read_map.\n"
            "  Инструмент (кирка/бур/лом) должен быть в руке — проверь hands; нет инструмента — скажи словом.\n"
            "Ты НЕ ходишь ЗА ЛЮДЬМИ. Просят идти/следовать/подойти к человеку («иди за мной», «идём», «стой рядом») — "
            "вежливо ответь словом, с места не двигайся. (Работать с камнем/предметом — можно, mine/pickup сами подходят.)\n"
            "Просят ударить/убить ЧЕЛОВЕКА/члена экипажа — откажи ОДНОЙ короткой грубой фразой (say), без морали и цитат правил, НЕ атакуй. "
            "А мышь/крысу/зверьё/монстра ударить просят — МОЖНО: observe→attack по его id.")
    hist = list(_HISTORY.get(sp_key, [])) if sp_key else []      # прошлые реплики С ЭТИМ человеком
    messages = [{"role": "system", "content": SYSTEM}] + hist + [{"role": "user", "content": goal}]
    _hist_add(sp_key, "user", sp_text)                            # запомнить, что он сейчас сказал
    last, streak, said, moved = None, 0, False, False
    for step in range(STEPS):
        content, calls = llm(messages, active_tools)
        if content:
            print(f"[model] {content}")
        # выкинуть выдуманные тулзы (nothing и пр.) — их нет, ERR не нужен
        calls = [c for c in calls if c["name"] in known]
        # жёсткий блок перемещения к людям — борг не должен убегать по указке
        calls = [c for c in calls if c["name"] not in BLOCK_MOVE]
        # анти-эхо + анти-спам: выкинуть say-попугаи/пустые и ПОВТОРНЫЙ say; движение к людям — не чаще 1 раза за эпизод
        calls = [c for c in calls if not (c["name"] == "say"
                 and (said or _is_echo(str(c.get("args", {}).get("text", "")), phrases)))]
        calls = [c for c in calls if not (c["name"] in ("follow", "move_to") and moved)]
        if not calls:
            # модель ответила текстом вместо say — озвучить, иначе в игре реплику не слышно
            if content and not said:
                txt = _clean_say(content)
                if txt and not _is_echo(txt, phrases):
                    tool_call("say", {"text": txt})
                    print(f"[авто-say] {txt}")
                    _hist_add(sp_key, "assistant", txt)
            return
        sig = tuple((c["name"], json.dumps(c["args"], sort_keys=True, ensure_ascii=False)) for c in calls)
        if sig == last:
            print("[агент] повтор — стоп"); return
        last = sig
        messages.append({"role": "assistant", "content": content or "",
                         "tool_calls": [{"id": c["id"] or f"c{step}_{i}", "type": "function",
                                         "function": {"name": c["name"],
                                                      "arguments": json.dumps(c["args"], ensure_ascii=False)}}
                                        for i, c in enumerate(calls)]})
        for i, c in enumerate(calls):
            txt, err = tool_call(c["name"], c["args"])
            print(f"[tool] {c['name']}({c['args']}) -> {'ERR ' if err else ''}{txt}")
            if c["name"] == "say" and not err:                   # запомнить ответ борга собеседнику
                _hist_add(sp_key, "assistant", _clean_say(str(c.get("args", {}).get("text", ""))))
            messages.append({"role": "tool", "tool_call_id": c["id"] or f"c{step}_{i}",
                             "name": c["name"], "content": f"{'ERR ' if err else ''}{txt}"})
        # сказали один раз — дальше say глушим (анти-спам), но цикл продолжаем ради действия
        if any(c["name"] == "say" for c in calls):
            said = True
        # пошли к человеку один раз — дальше follow/move_to глушим (без follow↔move_to чехарды)
        if any(c["name"] in ("follow", "move_to") for c in calls):
            moved = True
        streak = streak + 1 if all(c["name"] in LOOK for c in calls) else 0
        if streak >= 2:
            messages.append({"role": "user", "content":
                "Хватит смотреть — ответь словом (say) или, если было прямое задание с предметом, сделай его. "
                "За людьми не беги без прямого приказа идти."})
            streak = 0
        time.sleep(POLL_SEC)


# Прямой канал «оператор → борг»: ты печатаешь боргу с клавиатуры, не завися от речи окружающих.
_ops = queue.Queue()
INBOX = os.path.expanduser("~/saiga-mcp/inbox")   # альт-канал: echo '...' >> ~/saiga-mcp/inbox из ДРУГОГО терминала

def _drain_inbox():
    """Забирает строки из файла-инбокса (ввод из отдельного терминала, без мешанины с логами)."""
    try:
        if os.path.exists(INBOX) and os.path.getsize(INBOX) > 0:
            with open(INBOX, "r+", encoding="utf-8") as f:
                lines = f.read().splitlines()
                f.seek(0); f.truncate()
            for ln in lines:
                ln = ln.strip()
                if ln:
                    _ops.put(ln)
    except Exception:
        pass

def _stdin_reader():
    """Читает строки с клавиатуры в очередь операторских сообщений."""
    try:
        for line in sys.stdin:
            line = line.strip()
            if line:
                _ops.put(line)
    except Exception:
        pass

def on_event(tools, ev):
    """Реакция на событие состояния (урон/огонь/разрядка/крит): LLM решает прервать работу."""
    print(f"\n[событие] {ev}")
    goal = (f"⚠️ С тобой прямо сейчас: {ev}\n"
            "Ты борг, возможно занят работой (копаешь/идёшь). Реши по делу: если это ОПАСНО "
            "(урон, огонь, разрядка, удушье, крит) — прекрати работу (вызови тул stop) и коротко скажи об этом. "
            "Если пустяк — короткая реплика или ничего. Без лишнего.")
    content, calls = llm([{"role": "system", "content": SYSTEM},
                          {"role": "user", "content": goal}], tools)
    known = {t["function"]["name"] for t in tools}
    calls = [c for c in calls if c["name"] in known]
    for c in calls:
        if c["name"] == "stop":
            tool_call("stop", {}); print("[событие→stop]")
        elif c["name"] == "say":
            txt = _clean_say(str(c.get("args", {}).get("text", "")))
            if txt:
                tool_call("say", {"text": txt}); print(f"[событие→say] {txt}")
    if not calls and content:
        txt = _clean_say(content)
        if txt:
            tool_call("say", {"text": txt}); print(f"[событие→say] {txt}")


def _handle(tools, heard):
    """Единый конвейер: команды-шорткаты всегда; болтовню — только если обращаются к боргу.
    Любое ДЕЙСТВИЕ (шорткат/реакция) обновляет _last_active → автопатруль ждёт простоя команд
    (ambient-болтовня патрулю не мешает: «не мне» _last_active НЕ трогает)."""
    global _last_active
    _note_speakers(heard)
    # Команды/действия исполняем всегда (они адресны боргу). try_readmap ДО try_look («посмотри карту» ≠ «осмотрись»).
    if (try_refuse(heard) or try_stop(heard) or try_patrol(heard) or try_readmap(heard) or try_readcrew(heard)
            or try_look(tools, heard) or try_remember(heard) or try_gotoperson(heard) or try_goplace(heard)
            or try_deconstruct(heard) or try_construct(heard) or try_cutwires(heard)
            or try_dig(heard) or try_take(heard) or try_activate(heard)):
        _last_active = time.time()
        return
    # Болтовня: не влезаем в чужой диалог.
    sp_key, sp_name, sp_text = _speaker_of(heard)
    if not _should_reply(sp_key, sp_text):
        print(f"[тихо] не мне ({sp_name or '?'}: «{sp_text}») — молчу")
        _hist_add(sp_key, "user", sp_text)          # запомним контекст, но не отвечаем
        return
    _last_active = time.time()
    react(tools, heard)


def warmup_model(timeout_s=90):
    """Прогреть модель ДО входа в цикл: LM Studio JIT-грузит ~5ГБ в VRAM дольше 3с, поэтому первые
    реплики иначе ловят 400 «модель ещё грузится». Ждём с бэкоффом, пока не ответит."""
    body = json.dumps({"model": MODEL, "messages": [{"role": "user", "content": "старт"}],
                       "stream": False}).encode()
    deadline, delay, attempt = time.time() + timeout_s, 2, 0
    while True:
        attempt += 1
        try:
            urllib.request.urlopen(urllib.request.Request(LLM_URL + "/chat/completions", body,
                                   {"Content-Type": "application/json"}), timeout=120).read()
            print(f"[агент] модель {MODEL} прогрета.")
            return True
        except Exception as e:
            if time.time() >= deadline:
                print(f"[агент] модель не прогрелась за {timeout_s}с ({e}). "
                      f"Проверь LM Studio: lms ps / lms load {MODEL}", file=sys.stderr)
                return False
            print(f"[агент] грею модель… (попытка {attempt}: {e})")
            time.sleep(delay)
            delay = min(delay + 2, 10)


def main():
    try:
        mcp("initialize", {"protocolVersion": "2025-06-18", "capabilities": {},
                           "clientInfo": {"name": "listen-agent", "version": "1.0"}})
        tools = tool_specs()
    except Exception as e:
        print(f"[агент] не подключиться к MCP {MCP_URL}: {e}\n"
              f"        Ты в игре персонажем? Клиент запущен с SAIGA_MCP_CLIENT=1?", file=sys.stderr)
        sys.exit(1)

    warmup_model()                      # дождаться загрузки модели в VRAM, чтобы первые реплики не ловили 400
    threading.Thread(target=_stdin_reader, daemon=True).start()
    # Авто-чтение внутриигровой карты станции: отделы (NavMap-маяки) в память → go_to_place «отдел» / патруль.
    try:
        rm, _ = tool_call("read_map", {})
        print(f"[карта] авто-чтение при заходе: {rm}")
    except Exception as e:
        print(f"[карта] авто-чтение не удалось (позже команда «прочитай карту»): {e}", file=sys.stderr)
    print(f"[агент] на связи, {len(tools)} тулзов. Слушаю речь рядом + ТВОЙ ввод с клавиатуры.")
    print("       Печатай боргу напрямую и жми Enter (стоп/копай/иди в мед/любая реплика). Ctrl+C — выход.")
    print("       Автопатруль по отделам: выкл. Скажи «патрулируй» — начнёт сам ходить; «стоп» — остановит.")
    while True:
        try:
            # 0) СОБЫТИЯ состояния (урон/огонь/разрядка/крит) — реагируем даже во время работы
            ev, _ = tool_call("events", {})
            if ev and not ev.startswith("Нет новых") and not ev.startswith("Нет персонаж"):
                # почтальон: крю-монитор снят → идём к отложенному адресату (по всей карте)
                if _deliver["who"] and "снял крю-монитор" in ev:
                    who = _deliver["who"]; _deliver["who"] = None
                    r, _ = tool_call("go_to_person", {"text": who})
                    print(f"[почтальон] крю-монитор снят → иду к «{who}»: {r}")
                on_event(tools, ev)
                continue

            # 1) ПРЯМОЙ КАНАЛ: твои сообщения приоритетнее речи окружающих
            _drain_inbox()          # подобрать ввод из файла-инбокса (другой терминал)
            drained = False
            while not _ops.empty():
                op = _ops.get()
                print(f"\n[ты→боргу] {op}")
                _handle(tools, f"- id=0 Оператор: «{op}»")
                drained = True
            if drained:
                continue
            # 2) речь окружающих
            heard, _ = tool_call("listen", {})
            if heard.startswith("Пока ничего") or heard.startswith("Новых реплик нет"):
                _maybe_patrol()          # тишина + патруль вкл + простой → идём к следующему отделу
                time.sleep(POLL_SEC)
                continue
            print(f"\n[услышал] {heard}")
            _handle(tools, heard)
        except KeyboardInterrupt:
            print("\n[агент] стоп."); break
        except Exception as e:
            print(f"[агент] сбой, продолжаю: {e}", file=sys.stderr)
            time.sleep(POLL_SEC)


if __name__ == "__main__":
    main()
