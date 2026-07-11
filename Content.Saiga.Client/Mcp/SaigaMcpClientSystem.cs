using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Content.Client.Examine;
using Content.Client.Verbs;
using Content.Client.UserInterface.Systems.Chat;
using Content.Shared.Chat;
using Content.Shared.Examine;
using Content.Shared.Guidebook;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory;
using Content.Shared.Alert;
using Content.Shared.Damage;
using Content.Shared.Doors.Components;
using Content.Shared.Fluids.Components;
using Content.Shared.Item;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Storage;
using Content.Shared.SubFloor;
using Content.Shared.Verbs;
using Robust.Client.GameObjects;
using Robust.Client.UserInterface;
using Robust.Shared.Audio.Components;
using Robust.Shared.Containers;
using Robust.Shared.ContentPack;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Client._Mono.SaigaAgent.Mcp;

/// <summary>
///     CLIENT-SIDE MCP server (Model Context Protocol, JSON-RPC 2.0) — the "join any server"
///     variant. Hosts <c>/mcp</c> in the client process so an external LLM can drive the local
///     character without server-side code, on a vanilla server. Perception is read from the
///     client's own view (PVS); actions go through the existing <see cref="SaigaAgentSystem"/>
///     steerer (simulated input), exactly like a real player.
///
///     Requires the client to run UNSANDBOXED (env <c>ROBUST_DISABLE_SANDBOX=1</c>) — sandboxed
///     content cannot use <see cref="HttpListener"/>. Enable with env <c>SAIGA_MCP_CLIENT=1</c>;
///     token from <c>SAIGA_MCP_TOKEN</c> (default "devsecret"); port from <c>SAIGA_MCP_PORT</c>
///     (default 1213, distinct from the server's 1212).
///
///     Threading: the HttpListener loop runs off-thread; each request is queued and executed on the
///     main game thread in <see cref="Update"/> (ECS access must be single-threaded), then the
///     result is handed back to the listener thread to write the HTTP response.
/// </summary>
public sealed class SaigaMcpClientSystem : EntitySystem
{
    [Dependency] private readonly ISharedPlayerManager _player = default!;
    [Dependency] private readonly SharedTransformSystem _xform = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly SaigaAgentSystem _agent = default!;
    [Dependency] private readonly IUserInterfaceManager _ui = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly IResourceManager _res = default!;
    [Dependency] private readonly ExamineSystem _examine = default!;
    [Dependency] private readonly InventorySystem _inventory = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly VerbSystem _verbs = default!;

    private ISawmill _sawmill = default!;
    private HttpListener? _listener;
    private string _token = "devsecret";

    private const string ProtocolVersion = "2025-06-18";
    private const float ObserveRange = 25f;   // весь PVS (net.pvs_range), фильтр важности не даёт раздуть контекст
    private const int HeardMax = 12;
    private const float HearRange = 3f; // only "hear" speakers within this many tiles

    // Recent overheard nearby speech (captured from incoming chat) for the `listen` tool.
    private sealed class HeardLine { public string Speaker = ""; public int SpeakerId; public string Text = ""; public TimeSpan Time; public bool Read; }
    private readonly List<HeardLine> _heard = new();

    // Cache for the async examine response coming back from the server (for the `examine` tool).
    private string? _lastExamineText;
    private NetEntity? _lastExaminedNet;

    // Cache for verbs (local + async server response) for the `verbs`/`verb` tools.
    private NetEntity? _lastVerbTarget;
    private readonly List<Verb> _lastVerbs = new();

    // Событийный канал (Фича 2): отслеживаем изменения состояния борга для тула `events`.
    private MobState _prevMob = MobState.Alive;
    private float _prevDamage;
    private readonly HashSet<string> _prevAlerts = new();
    private bool _eventsInit;

    // Requests parsed off-thread, executed on the main thread, completed back to the listener thread.
    private readonly ConcurrentQueue<(JsonElement Root, TaskCompletionSource<JsonNode?> Done)> _pending = new();

    public override void Initialize()
    {
        base.Initialize();
        _sawmill = Logger.GetSawmill("saiga.mcp.client");

        if (System.Environment.GetEnvironmentVariable("SAIGA_MCP_CLIENT") != "1")
            return; // opt-in

        _token = System.Environment.GetEnvironmentVariable("SAIGA_MCP_TOKEN") ?? "devsecret";
        var port = int.TryParse(System.Environment.GetEnvironmentVariable("SAIGA_MCP_PORT"), out var p) ? p : 1213;

        try
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/mcp/");
            _listener.Start();
            _ = AcceptLoopAsync(_listener);
            _sawmill.Info($"client MCP endpoint up on http://127.0.0.1:{port}/mcp");
        }
        catch (System.Exception e)
        {
            _sawmill.Error($"failed to start client MCP endpoint (sandbox on? need ROBUST_DISABLE_SANDBOX=1): {e.Message}");
            _listener = null;
        }

        // Capture nearby speech from incoming chat for the `listen` tool (main-thread event).
        try { _ui.GetUIController<ChatUIController>().MessageAdded += OnChat; }
        catch (System.Exception e) { _sawmill.Warning($"listen: could not hook chat: {e.Message}"); }

        // Capture examine responses from the server for the `examine` tool.
        SubscribeNetworkEvent<ExamineSystemMessages.ExamineInfoResponseMessage>(OnExamineResponse);
        // Capture server-side verb lists for the `verbs`/`verb` tools (merged into local ones).
        SubscribeNetworkEvent<VerbsResponseEvent>(OnVerbsResponse);
    }

    /// <summary>Caches the server's examine reply so the next `examine` call can return the full text.</summary>
    private void OnExamineResponse(ExamineSystemMessages.ExamineInfoResponseMessage ev)
    {
        _lastExaminedNet = ev.EntityUid;
        _lastExamineText = ev.Message.ToMarkup();
    }

    /// <summary>Merges the server's verb list into the cache so the next `verbs`/`verb` call sees server-side punkts.</summary>
    private void OnVerbsResponse(VerbsResponseEvent ev)
    {
        if (ev.Entity != _lastVerbTarget || ev.Verbs == null)
            return;
        foreach (var v in ev.Verbs)
            if (_lastVerbs.All(x => x.Text != v.Text))
                _lastVerbs.Add(v);
    }

    /// <summary>Buffers nearby IC speech (Local/Whisper) heard by the local player.</summary>
    private void OnChat(ChatMessage msg)
    {
        if ((msg.Channel & (ChatChannel.Local | ChatChannel.Whisper)) == 0)
            return; // only nearby spoken IC
        var text = msg.Message?.Trim();
        if (string.IsNullOrEmpty(text))
            return;

        // Only react to speech from actual creatures (players/mobs). Drops station announcements,
        // shuttle-console chatter and other non-mob Local sources that would just spam the context.
        if (!TryGetEntity(msg.SenderEntity, out var ent) || Deleted(ent.Value)
            || !HasComp<MobStateComponent>(ent.Value))
            return;
        if (_player.LocalEntity is not { } self || Deleted(self) || ent.Value == self)
            return; // no character, or our own speech

        // Only "hear" speakers within HearRange tiles — narrows the earshot from the server's full
        // Local range so the agent reacts only to people right next to it.
        var selfPos = _xform.GetMapCoordinates(self);
        var srcPos = _xform.GetMapCoordinates(ent.Value);
        if (selfPos.MapId != srcPos.MapId || (srcPos.Position - selfPos.Position).Length() > HearRange)
            return;

        var speaker = MetaData(ent.Value).EntityName;

        _heard.Add(new HeardLine { Speaker = speaker, SpeakerId = GetNetEntity(ent.Value).Id, Text = text, Time = _timing.CurTime });
        while (_heard.Count > HeardMax)
            _heard.RemoveAt(0);
    }

    public override void Shutdown()
    {
        base.Shutdown();
        try { _listener?.Stop(); } catch { /* ignore */ }
        _listener = null;
    }

    /// <summary>Drains queued requests and runs them on the main thread.</summary>
    public override void Update(float frameTime)
    {
        while (_pending.TryDequeue(out var item))
        {
            try
            {
                item.Done.TrySetResult(Dispatch(item.Root));
            }
            catch (System.Exception e)
            {
                _sawmill.Warning($"MCP dispatch threw: {e}");
                item.Done.TrySetException(e);
            }
        }
    }

    // --- Transport (off the main thread) ---

    private async Task AcceptLoopAsync(HttpListener listener)
    {
        while (listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); }
            catch { break; }
            _ = HandleAsync(ctx);
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            if (ctx.Request.HttpMethod != "POST")
            {
                ctx.Response.StatusCode = (int) HttpStatusCode.MethodNotAllowed;
                ctx.Response.Close();
                return;
            }

            if (!CheckAuth(ctx.Request))
            {
                ctx.Response.StatusCode = (int) HttpStatusCode.Unauthorized;
                ctx.Response.Close();
                return;
            }

            JsonElement root;
            using (var reader = new System.IO.StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding))
            {
                var body = await reader.ReadToEndAsync();
                try { root = JsonSerializer.Deserialize<JsonElement>(body); }
                catch
                {
                    await WriteJson(ctx, RpcError(null, -32700, "Parse error"));
                    return;
                }
            }

            // Hand to the main thread and await the result.
            var tcs = new TaskCompletionSource<JsonNode?>();
            _pending.Enqueue((root, tcs));
            JsonNode? result;
            try { result = await tcs.Task.WaitAsync(System.TimeSpan.FromSeconds(15)); }
            catch (System.Exception e)
            {
                await WriteJson(ctx, RpcError(GetId(root), -32603, $"internal: {e.Message}"));
                return;
            }

            if (result == null)
                ctx.Response.StatusCode = (int) HttpStatusCode.NoContent;
            await WriteJson(ctx, result);
        }
        catch
        {
            try { ctx.Response.Abort(); } catch { /* ignore */ }
        }
    }

    private bool CheckAuth(HttpListenerRequest req)
    {
        var header = req.Headers["Authorization"];
        if (string.IsNullOrEmpty(header))
            return false;
        var space = header.IndexOf(' ');
        if (space == -1)
            return false;
        var scheme = header[..space];
        var value = header[space..].Trim();
        if (!string.Equals(scheme, "Bearer", System.StringComparison.OrdinalIgnoreCase))
            return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(value), Encoding.UTF8.GetBytes(_token));
    }

    private static async Task WriteJson(HttpListenerContext ctx, JsonNode? node)
    {
        if (node == null)
        {
            ctx.Response.Close();
            return;
        }
        var bytes = Encoding.UTF8.GetBytes(node.ToJsonString());
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    // --- JSON-RPC dispatch (main thread) ---

    private JsonNode? Dispatch(JsonElement root)
    {
        var id = GetId(root);
        if (!root.TryGetProperty("method", out var methodEl) || methodEl.ValueKind != JsonValueKind.String)
            return RpcError(id, -32600, "Invalid Request: missing method");

        switch (methodEl.GetString())
        {
            case "initialize":
                return RpcResult(id, new JsonObject
                {
                    ["protocolVersion"] = ProtocolVersion,
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                    ["serverInfo"] = new JsonObject { ["name"] = "saiga-agent-mcp-client", ["version"] = "0.1.0" },
                });
            case "notifications/initialized":
            case "notifications/cancelled":
                return null; // 204
            case "ping":
                return RpcResult(id, new JsonObject());
            case "tools/list":
                return RpcResult(id, BuildToolsList());
            case "tools/call":
                return ToolCall(id, root);
            default:
                return RpcError(id, -32601, "Method not found");
        }
    }

    private sealed record ToolSpec(string Name, string Desc, bool Target, bool Text, string? TextParam = null);

    // Action names mirror SaigaAgentSystem.ApplyDecision. Everything runs client-side as simulated
    // input, so it works on any server.
    private static readonly ToolSpec[] Specs =
    {
        new("observe", "Что видит персонаж вокруг на весь обзор (важное: люди с должностью, предметы, интерактив; мусор скрыт). Слева КОРОТКОЕ число=id — им и действуй (attack/pickup/…). filter — имена через запятую. raw=true — вообще всё, включая мусор.", false, false),
        new("listen",  "Что агенту сказали рядом (новые реплики с прошлого вызова): id говорящего, кто и что.", false, false),
        new("say",     "Сказать фразу вслух.", false, true),
        new("examine", "Осмотреть сущность. target — сетевой id. Первый вызов даёт имя+описание, повторный (~1с) — полный текст.", true, false),
        new("contents","Посмотреть содержимое контейнера/рюкзака: список предметов (id + имя). target — сетевой id контейнера; без target — свой рюкзак (слот «спина»).", false, false),
        new("hands",   "Что у агента В РУКАХ: предметы (id + имя) и какая рука активная. Проверять перед действиями с предметом в руке.", false, false),
        new("move_to", "Подойти вплотную к сущности (~0.4м). target — сетевой id из observe.", true, false),
        new("follow",  "Идти за сущностью / подойти к ней. target — сетевой id.", true, false),
        new("stop",    "Остановиться.", false, false),
        new("pickup",  "Взять предмет в руку. target — сетевой id.", true, false),
        new("pull",    "Тащить предмет. target — сетевой id.", true, false),
        new("drop",    "Уронить предмет из руки на пол.", false, false),
        new("swap",    "Сменить активную руку.", false, false),
        new("throw",   "Бросить предмет из руки в сторону цели. target — сетевой id.", true, false),
        new("place",   "Подойти к цели и положить предмет из руки. target — сетевой id.", true, false),
        new("use_on",  "Подойти и применить предмет из АКТИВНОЙ РУКИ на цели: лом → отжать/открыть дверь/ставни/шлюз, отвёртка → открутить, вставить деталь и т.п. Нужный инструмент должен быть в руке (проверь hands). target — сетевой id.", true, false),
        new("activate","Включить/выключить предмет в руке (сварочник, фонарик).", false, false),
        new("pilot",   "Управление шаттлом (надо быть за консолью пилота): толчок/поворот на ~0.7с. dir: forward/back/left/right/rotate_left/rotate_right.", false, false, "dir"),
        new("attack",  "Ударить/атаковать/копать цель ОДИН заход (подойти вплотную и бить, пока цель цела; для руды/камня — кирку в руку). target — сетевой id.", true, false),
        new("mine",    "ЗАЦИКЛИТЬ добычу: бить цель, а как разрушится — сам идёт к ближайшему такому же объекту и продолжает, пока не скажут stop. target — id камня/руды (киркой в руке).", true, false),
        new("shoot",   "Выстрелить по цели из пушки/оружия в руке (в т.ч. прото-кинетический ускоритель для копания) — один выстрел, без подхода. Пушка должна быть в активной руке. target — сетевой id.", true, false),
        new("store",    "Положить предмет из руки в рюкзак/контейнер. target — сетевой id контейнера.", true, false),
        new("build",    "Построить стену (жирдер): следующая стрелка-указатель задаёт тайл.", false, false),
        new("craft",    "Скрафтить предмет по рецепту. text — id рецепта (например 'Crowbar').", false, true),
        new("construct","Поставить структуру/раму на тайл агента. text — id рецепта.", false, true),
        new("smart_equip", "Убрать предмет из руки в слот. text — back/belt/pocket1/pocket2/suitstorage/id/shoes/outer.", false, true),
        new("alt_activate", "Альт-активация предмета в руке.", false, false),
        new("alt_use_on",   "Подойти и альт-кликнуть по цели предметом в руке (альт-взаимодействие). target — сетевой id.", true, false),
        new("release_pull", "Отпустить тащимый объект.", false, false),
        // Составные скиллы: одна команда = вся цепочка (сам ищет цель по имени и действует).
        new("dig",  "Копать: сам находит ближайший сугроб/камень/руду и зацикленно добывает (цель искать НЕ надо, id не нужен). Нужен бур/кирка в руке. Прекратить — stop.", false, false),
        new("take", "Взять предмет ПО НАЗВАНИЮ: находит ближайший предмет, чьё имя содержит text, подходит и берёт в руку. text — часть имени (напр. 'бур', 'лом', 'аптечка').", false, true),
        // ПКМ-меню (контекстные verbs): посмотреть пункты и выполнить по имени.
        new("verbs", "Список контекстных действий (ПКМ-меню) цели: «Извлечь магазин», «Начать разбор», «Сменить режим огня» и т.п. target — сетевой id. Первый вызов — локальные пункты, повторный (~0.3с) — плюс серверные.", true, false),
        new("verb",  "Выполнить пункт ПКМ-меню по имени. target — сетевой id, text — имя пункта из verbs (без регистра, часть имени). Сначала посмотри verbs(target).", true, true),
        new("deconstruct", "РАЗОБРАТЬ стену/окно/каркас ЦЕЛИКОМ (авто-скилл): сам подойдёт, начнёт разборку и пройдёт все шаги (нужные инструменты — сварочник/отвёртку/монтировку/кусачки/ключ — берёт с пола рядом, сварку жжёт экономно). Зови ОДИН раз. target — сетевой id. Прервать — stop.", true, false),
        new("assemble",    "СОБРАТЬ каркас машины ЦЕЛИКОМ (авто-скилл): ведёт цель по её шагам из examine (инструмент/материал/деталь), беря нужное с пола рядом. target — сетевой id уже поставленного каркаса. Прервать — stop.", true, false),
        new("cut_wires",   "ПЕРЕРЕЗАТЬ все провода в панели цели (шлюз/машина): сам откроет техпанель отвёрткой, возьмёт кусачки и перережет провода по одному. Инструменты берёт из рук/пояса/с пола. target — сетевой id. Прервать — stop.", true, false),
        // Навигация: запомнить место и вернуться к нему, обходя стены.
        new("remember_place", "Запомнить текущее место под именем (сохраняется в память, переживает рестарт). text — имя места (напр. 'мед', 'склад', 'выход').", false, true),
        new("go_to_place",    "Пойти к запомненному месту, сам обходя стены (A*-путь). text — имя из remember_place. Остановиться — stop.", false, true),
        new("list_places",    "Список всех запомненных мест (сессионные + сохранённые в память). Без параметров.", false, false),
        new("grid",           "Локальная карта вокруг тебя (ASCII: @ ты, . пол, # стена, + дверь, ~ космос). Верх=север, право=восток. Смотри ПЕРЕД движением, чтобы понять куда свободно идти. Параметр radius (опц., 3-12, по умолч. 7).", false, false),
        new("go_to",          "Пойти на смещение в тайлах от текущего места, сам обходя стены (A*). dx: восток+/запад−, dy: север+/юг−. Напр. dx=6,dy=0 — 6 тайлов на восток. Сначала посмотри grid. Остановиться — stop.", false, false),
        new("where_am_i",     "Показать характерные объекты вокруг тебя (без вентиляции/труб/кабелей и без людей) — описание помещения. По этому списку ТЫ САМ придумываешь короткое имя комнаты и зовёшь remember_place. Без параметров.", false, false),
        new("explore",        "ГЛАВНЫЙ тул разведки: сам находит ближайшую НЕпройденную дверь и проходит сквозь неё в следующую комнату (или уходит вглубь по коридору). Один вызов = переход на несколько тайлов. Не надо grid/go_to/dx/dy — просто зови explore, потом where_am_i и remember_place. Остановиться — stop.", false, false),
        new("go_to_person",   "Дойти до ЧЕЛОВЕКА по имени ИЛИ должности, обходя стены (A*, догоняет идущего). Если человек рядом (виден) — идёт к нему; иначе — к его последней позиции из крю-монитора (сперва сними его read_crew). text — часть имени или должности (напр. 'капитан', 'Иванов'). Остановиться — stop.", false, true),
        new("read_crew",      "Снять крю-монитор: подойти к консоли мониторинга экипажа и считать, ГДЕ КТО находится по всей станции (имя/должность/координаты — сенсоры костюмов). Нужно один раз, потом go_to_person дойдёт к любому. Итог придёт через тул events. Без параметров.", false, false),
        new("guidebook",      "Справка по игре из внутриигрового гайдбука: text — тема (сварка, химия, оружие, строительство, борги, атмос, медицина…). Вернёт текст статьи. Зови, когда спрашивают КАК что-то работает / устроено.", false, true),
        new("read_map",       "Прочитать карту станции (настенную): выписать все отделы и их места в память, чтобы потом ходить к ним по имени через go_to_place. Без параметров. Зови один раз после захода.", false, false),
        new("wander",         "Патруль: сам идёт к следующему отделу с карты по кругу (нужен read_map). Зови, когда нечем заняться. Остановиться — stop.", false, false),
        // Событийный канал: что случилось с боргом (урон/крит/огонь/разрядка/удушье/голод).
        new("events", "Что случилось с ТОБОЙ с прошлого опроса: урон, крит/смерть, огонь, разрядка, удушье, голод и т.п. Пусто — «нет событий». Опрашивай регулярно, в т.ч. во время работы, чтобы вовремя прерваться.", false, false),
        // На что рядом указали пальцем (стрелка-указатель). НЕ команда — ты сам решаешь смысл.
        new("pointing", "На что недавно указали пальцем рядом с тобой: id/имя/направление/дистанция ближайшей сущности к острию стрелки (или «место», если предмета нет). Пусто — никто не указывал. Указатель перегружен: могут просто показать, указать на собеседника или на предмет для действия — реши сам (взять/применить/подойти/обратиться/игнор).", false, false),
    };

    private static JsonNode BuildToolsList()
    {
        var tools = new JsonArray();
        foreach (var s in Specs)
        {
            var props = new JsonObject();
            var required = new JsonArray();
            if (s.Name == "observe")
            {
                props["filter"] = new JsonObject { ["type"] = "string", ["description"] = "Имена через запятую (опц.)." };
                props["raw"] = new JsonObject { ["type"] = "boolean", ["description"] = "true — показать вообще всё, включая мусор (опц.)." };
            }
            if (s.Name == "contents")
                props["target"] = new JsonObject { ["type"] = "integer", ["description"] = "Сетевой id контейнера (опц.; без него — свой рюкзак)." };
            if (s.Name == "grid")
                props["radius"] = new JsonObject { ["type"] = "integer", ["description"] = "Радиус обзора в тайлах (3-12, опц., по умолч. 7)." };
            if (s.Name == "go_to")
            {
                props["dx"] = new JsonObject { ["type"] = "integer", ["description"] = "Смещение на восток (+) / запад (−), тайлов." };
                props["dy"] = new JsonObject { ["type"] = "integer", ["description"] = "Смещение на север (+) / юг (−), тайлов." };
                required.Add("dx");
                required.Add("dy");
            }
            if (s.Target)
            {
                props["target"] = new JsonObject { ["type"] = "integer", ["description"] = "Сетевой id целевой сущности (из observe)." };
                required.Add("target");
            }
            if (s.Text)
            {
                props["text"] = new JsonObject { ["type"] = "string", ["description"] = "Значение параметра (для say — фраза; для craft/construct — id рецепта; для smart_equip — слот)." };
                required.Add("text");
            }
            if (s.TextParam is { } tp)
            {
                props[tp] = new JsonObject { ["type"] = "string", ["description"] = "forward/back/left/right/rotate_left/rotate_right" };
                required.Add(tp);
            }
            tools.Add(new JsonObject
            {
                ["name"] = s.Name,
                ["description"] = s.Desc,
                ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = props, ["required"] = required },
            });
        }
        return new JsonObject { ["tools"] = tools };
    }

    private JsonNode? ToolCall(JsonNode? id, JsonElement root)
    {
        if (!root.TryGetProperty("params", out var p)
            || !p.TryGetProperty("name", out var nameEl)
            || nameEl.ValueKind != JsonValueKind.String)
            return RpcError(id, -32602, "Invalid params: missing tool name");

        var name = nameEl.GetString()!;
        var args = p.TryGetProperty("arguments", out var a) ? a : default;

        var (text, isError) = name switch
        {
            "observe" => Observe(args),
            "listen" => Listen(),
            "examine" => Examine(args),
            "contents" => Contents(args),
            "hands" => Hands(),
            "dig" => Dig(),
            "take" => Take(args),
            "verbs" => ListVerbs(args),
            "verb" => DoVerb(args),
            "deconstruct" => Deconstruct(args),
            "assemble" => Assemble(args),
            "cut_wires" => CutWires(args),
            "remember_place" => DoRemember(args),
            "go_to_place" => DoGoTo(args),
            "list_places" => (_agent.ListPlaces(), false),
            "grid" => (_agent.Grid(GetInt(args, "radius", 0)), false),
            "go_to" => DoGoToXY(args),
            "where_am_i" => (_agent.WhereAmI(), false),
            "explore" => DoExplore(),
            "read_map" => (_agent.ReadMap(), false),
            "wander" => _agent.Wander(),
            "go_to_person" => DoGoToPerson(args),
            "read_crew" => (_agent.ReadCrew(), false),
            "guidebook" => Guidebook(args),
            "events" => Events(),
            "pointing" => Pointing(),
            _ => Act(name, args),
        };

        return RpcResult(id, new JsonObject
        {
            ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } },
            ["isError"] = isError,
        });
    }

    /// <summary>Executes an action by driving the existing client steerer (simulated input).</summary>
    private (string, bool) Act(string name, JsonElement args)
    {
        if (Specs.All(s => s.Name != name))
            return ($"Unknown tool: {name}", true);
        if (_player.LocalEntity is not { } self || Deleted(self))
            return ("Нет персонажа (не в игре).", true);

        // Resolve target net id for action tools that need it.
        NetEntity? target = null;
        if (name is "move_to" or "follow" or "pickup" or "pull" or "throw" or "place" or "use_on" or "attack" or "mine" or "shoot" or "store" or "alt_use_on")
        {
            if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("target", out var t))
                return ("параметр 'target' обязателен", true);
            int idn;
            if (t.ValueKind == JsonValueKind.Number && t.TryGetInt32(out idn)) { }
            else if (t.ValueKind == JsonValueKind.String && int.TryParse(t.GetString(), out idn)) { }
            else return ("'target' должен быть числом (id из observe)", true);
            target = _agent.ResolveHandle(idn);
        }

        string? say = null;
        string? arg = null;
        string act = name;
        if (name == "say")
        {
            if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("text", out var txt)
                || txt.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(txt.GetString()))
                return ("параметр 'text' обязателен", true);
            say = txt.GetString();
            act = "none";
        }
        else if (name == "pilot")
        {
            if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("dir", out var d)
                || d.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(d.GetString()))
                return ("параметр 'dir' обязателен (forward/back/left/right/rotate_left/rotate_right)", true);
            arg = d.GetString();
        }
        else if (name is "craft" or "construct" or "smart_equip")
        {
            if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("text", out var txt)
                || txt.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(txt.GetString()))
                return ("параметр 'text' обязателен", true);
            arg = txt.GetString();
        }

        _agent.EnableLocalControl();                // steerer must be enabled; no network event
        _agent.ApplyDecision(say, act, target, arg); // reuse the proven action dispatch

        var detail = target is { } tn ? $" target={tn.Id}" : "";
        var said = say != null ? $" say=\"{say}\"" : "";
        var argInfo = arg != null ? $" arg={arg}" : "";
        return ($"ok: act={act}{detail}{argInfo}{said}", false);
    }

    // --- Составные скиллы (цель ищется по имени, дальше переиспользуем mine/pickup) ---

    /// <summary>dig: найти ближайший копаемый объект и запустить mine-цикл по нему.</summary>
    private (string, bool) Dig()
    {
        if (_player.LocalEntity is not { } self || Deleted(self))
            return ("Нет персонажа (не в игре).", true);
        if (_agent.FindNearestDiggable(self) is not { } hit)
            return ("Рядом нечего копать — нет сугробов/камней/руды.", true);
        _agent.EnableLocalControl();
        _agent.ApplyDecision(null, "mine", hit.net, null);
        return ($"ok: копаю {hit.name}", false);
    }

    /// <summary>take: найти ближайший предмет по подстроке имени и взять в руку.</summary>
    private (string, bool) Take(JsonElement args)
    {
        if (_player.LocalEntity is not { } self || Deleted(self))
            return ("Нет персонажа (не в игре).", true);
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("text", out var t)
            || t.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(t.GetString()))
            return ("параметр 'text' обязателен (часть имени предмета)", true);
        var what = t.GetString()!;
        if (_agent.FindNearestByName(self, what) is not { } hit)
            return ($"Не вижу рядом ничего с именем «{what}».", true);
        _agent.EnableLocalControl();
        _agent.ApplyDecision(null, "pickup", hit.net, null);
        return ($"ok: беру {hit.name}", false);
    }

    /// <summary>deconstruct: авто-разбор цели по шагам examine (C#-автомат в SaigaAgentSystem).</summary>
    private (string, bool) Deconstruct(JsonElement args)
    {
        if (_player.LocalEntity is not { } self || Deleted(self))
            return ("Нет персонажа (не в игре).", true);
        if (!TryTargetNet(args, out var target))
            return ("параметр 'target' обязателен (сетевой id стены/окна/каркаса)", true);
        _agent.EnableLocalControl();
        return (_agent.StartDeconstruct(target), false);
    }

    /// <summary>assemble: авто-сборка каркаса машины по шагам examine (C#-автомат).</summary>
    private (string, bool) Assemble(JsonElement args)
    {
        if (_player.LocalEntity is not { } self || Deleted(self))
            return ("Нет персонажа (не в игре).", true);
        if (!TryTargetNet(args, out var target))
            return ("параметр 'target' обязателен (сетевой id каркаса)", true);
        _agent.EnableLocalControl();
        return (_agent.StartConstruct(target), false);
    }

    /// <summary>cut_wires: авто-резка всех проводов в панели цели (C#-автомат: панель→кусачки→резать по одному).</summary>
    private (string, bool) CutWires(JsonElement args)
    {
        if (_player.LocalEntity is not { } self || Deleted(self))
            return ("Нет персонажа (не в игре).", true);
        if (!TryTargetNet(args, out var target))
            return ("параметр 'target' обязателен (сетевой id шлюза/машины)", true);
        _agent.EnableLocalControl();
        return (_agent.StartCutWires(target), false);
    }

    // --- ПКМ / контекстные verbs ---

    /// <summary>Парсит целевой id из аргументов (target) → NetEntity, резолвя короткий ХЭНДЛ из последнего observe.</summary>
    private bool TryTargetNet(JsonElement args, out NetEntity target)
    {
        target = default;
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("target", out var t))
            return false;
        int idn;
        if (t.ValueKind == JsonValueKind.Number && t.TryGetInt32(out idn)) { }
        else if (t.ValueKind == JsonValueKind.String && int.TryParse(t.GetString(), out idn)) { }
        else return false;
        target = _agent.ResolveHandle(idn);
        return true;
    }

    /// <summary>Собирает локальные verbs цели и шлёт серверный запрос (ответ добьётся в OnVerbsResponse).</summary>
    private void FetchVerbs(NetEntity target, EntityUid self)
    {
        _lastVerbTarget = target;
        _lastVerbs.Clear();
        foreach (var v in _verbs.GetVerbs(target, self, Verb.VerbTypes, out _, force: false))
            if (_lastVerbs.All(x => x.Text != v.Text))
                _lastVerbs.Add(v);
    }

    /// <summary>verbs: список пунктов ПКМ-меню цели (локальные сразу, серверные — со 2-го вызова).</summary>
    private (string, bool) ListVerbs(JsonElement args)
    {
        if (_player.LocalEntity is not { } self || Deleted(self))
            return ("Нет персонажа (не в игре).", true);
        if (!TryTargetNet(args, out var target))
            return ("параметр 'target' обязателен (сетевой id)", true);

        // Повторный вызов по той же цели — отдаём накопленное (уже с серверными пунктами).
        if (_lastVerbTarget != target || _lastVerbs.Count == 0)
            FetchVerbs(target, self);

        var names = _lastVerbs.Where(v => !v.Disabled).Select(v => $"«{v.Text}»").ToList();
        if (names.Count == 0)
            return ("Нет доступных действий для этой цели.", false);
        return ($"Пункты меню: {string.Join(", ", names)}", false);
    }

    /// <summary>verb: выполнить пункт ПКМ-меню по имени (частичное совпадение без регистра).</summary>
    private (string, bool) DoVerb(JsonElement args)
    {
        if (_player.LocalEntity is not { } self || Deleted(self))
            return ("Нет персонажа (не в игре).", true);
        if (!TryTargetNet(args, out var target))
            return ("параметр 'target' обязателен (сетевой id)", true);
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("text", out var t)
            || t.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(t.GetString()))
            return ("параметр 'text' обязателен (имя пункта из verbs)", true);
        var name = t.GetString()!.Trim();

        if (_lastVerbTarget != target || _lastVerbs.Count == 0)
            FetchVerbs(target, self);

        var match = _lastVerbs.FirstOrDefault(v => !v.Disabled
            && v.Text.Contains(name, System.StringComparison.OrdinalIgnoreCase));
        if (match == null)
        {
            var avail = string.Join(", ", _lastVerbs.Where(v => !v.Disabled).Select(v => $"«{v.Text}»"));
            return ($"Нет пункта «{name}». Доступно: {avail}", true);
        }
        _verbs.ExecuteVerb(target, match);
        return ($"ok: выполнил «{match.Text}»", false);
    }

    // --- Навигация: память мест + возврат ---

    // Слабая модель часто кладёт значение под другим ключом (place/name/value…) — принимаем синонимы.
    private static readonly string[] TextKeys = { "text", "place", "name", "value", "query", "item", "arg" };

    private static bool TryText(JsonElement args, out string text)
    {
        text = "";
        if (args.ValueKind != JsonValueKind.Object)
            return false;
        foreach (var key in TextKeys)
            if (args.TryGetProperty(key, out var t) && t.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(t.GetString()))
            {
                text = t.GetString()!.Trim();
                return true;
            }
        return false;
    }

    /// <summary>remember_place: запомнить текущее место под именем.</summary>
    private (string, bool) DoRemember(JsonElement args)
    {
        if (!TryText(args, out var name))
            return ("параметр 'text' обязателен (имя места)", true);
        _agent.EnableLocalControl();
        return (_agent.RememberPlace(name), false);
    }

    private (string, bool) DoGoToPerson(JsonElement args)
    {
        if (!TryText(args, out var q))
            return ("параметр 'text' обязателен (имя или должность человека)", true);
        _agent.EnableLocalControl();
        return _agent.GoToPerson(q);
    }

    private static readonly System.Text.RegularExpressions.Regex _guideTag =
        new(@"\[/?[^\]]*\]", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>RAG по внутриигровому гайдбуку: найти запись по теме, прочитать её markup-файл, снять разметку, отдать текст.</summary>
    private (string, bool) Guidebook(JsonElement args)
    {
        if (!TryText(args, out var query))
            return ("параметр 'text' обязателен (тема из гайдбука)", true);
        var q = query.Trim().ToLowerInvariant();
        GuideEntryPrototype? best = null;
        var titles = new List<string>();
        foreach (var proto in _proto.EnumeratePrototypes<GuideEntryPrototype>())
        {
            var name = Loc.GetString(proto.Name);
            if (!($"{proto.Id} {name}".ToLowerInvariant().Contains(q)))
                continue;
            titles.Add(name);
            if (best == null || name.Length < Loc.GetString(best.Name).Length)   // более короткое имя = более специфичная статья
                best = proto;
        }
        if (best == null)
            return ($"В гайдбуке нет статьи по «{query}». Попробуй тему: сварка, химия, оружие, строительство, борги, атмос, медицина.", false);

        string text;
        try
        {
            using var reader = _res.ContentFileReadText(best.Text);
            text = reader.ReadToEnd();
        }
        catch (Exception e)
        {
            return ($"Не смог прочитать статью «{Loc.GetString(best.Name)}»: {e.Message}", true);
        }
        text = _guideTag.Replace(text, "");                                         // [color]/[bold]/[textlink]…
        text = System.Text.RegularExpressions.Regex.Replace(text, @"<[^>]*>", " "); // xml/xaml-эмбеды
        text = System.Net.WebUtility.HtmlDecode(text);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"[ \t]+", " ");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\n\s*\n\s*\n+", "\n\n").Trim();
        if (text.Length > 1500)
            text = text[..1500] + "…";
        var others = titles.Count > 1
            ? $"\n(ещё по теме: {string.Join(", ", titles.Distinct().Take(6))})"
            : "";
        return ($"📖 {Loc.GetString(best.Name)}:\n{text}{others}", false);
    }

    /// <summary>go_to_place: проложить A*-путь к запомненному месту и пойти.</summary>
    private (string, bool) DoGoTo(JsonElement args)
    {
        if (!TryText(args, out var name))
            return ("параметр 'text' обязателен (имя места)", true);
        _agent.EnableLocalControl();
        var (msg, err) = _agent.GoToPlace(name);
        return (msg, err);
    }

    /// <summary>explore: композитный шаг разведки (сам к ближайшей новой двери / вглубь).</summary>
    private (string, bool) DoExplore()
    {
        _agent.EnableLocalControl();
        return _agent.Explore();
    }

    /// <summary>go_to: пойти на относительное смещение (dx,dy) тайлов, обходя стены.</summary>
    private (string, bool) DoGoToXY(JsonElement args)
    {
        var dx = GetInt(args, "dx", 0);
        var dy = GetInt(args, "dy", 0);
        if (dx == 0 && dy == 0)
            return ("нужны dx и dy (смещение в тайлах), напр. dx=6,dy=0 — 6 тайлов на восток", true);
        _agent.EnableLocalControl();
        return _agent.GoTo(dx, dy);
    }

    /// <summary>Прочитать целое из args по ключу (число или строка-число), иначе значение по умолчанию.</summary>
    private static int GetInt(JsonElement args, string key, int def)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(key, out var v))
            return def;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n))
            return n;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var m))
            return m;
        return def;
    }

    // --- Событийный канал (урон/крит/огонь/разрядка/…) ---

    private static readonly string[] DangerAlerts =
        { "fire", "pressure", "temp", "hot", "cold", "hunger", "thirst", "toxin", "charge", "power", "radiat", "oxygen", "breath", "suffoc", "bleed", "weightless" };

    private static bool IsDangerAlert(string id)
    {
        var l = id.ToLowerInvariant();
        foreach (var d in DangerAlerts)
            if (l.Contains(d))
                return true;
        return false;
    }

    private static string AlertName(string id)
    {
        var l = id.ToLowerInvariant();
        if (l.Contains("fire")) return "горю";
        if (l.Contains("charge") || l.Contains("power")) return "разряжаюсь";
        if (l.Contains("pressure")) return "перепад давления";
        if (l.Contains("hot") || l.Contains("cold") || l.Contains("temp")) return "проблема с температурой";
        if (l.Contains("hunger")) return "голоден";
        if (l.Contains("thirst")) return "хочу пить";
        if (l.Contains("toxin")) return "отравление";
        if (l.Contains("oxygen") || l.Contains("breath") || l.Contains("suffoc")) return "задыхаюсь";
        if (l.Contains("radiat")) return "радиация";
        if (l.Contains("bleed")) return "кровотечение";
        if (l.Contains("weightless")) return "невесомость";
        return $"тревога ({id})";
    }

    /// <summary>events: значимые изменения состояния борга с прошлого опроса (для прерывания работы).</summary>
    private (string, bool) Events()
    {
        if (_player.LocalEntity is not { } self || Deleted(self))
            return ("Нет персонажа (не в игре).", true);
        var lines = new List<string>();

        if (TryComp<MobStateComponent>(self, out var mob) && mob.CurrentState != _prevMob)
        {
            if (mob.CurrentState == MobState.Critical) lines.Add("⚠️ ты при смерти (крит)!");
            else if (mob.CurrentState == MobState.Dead) lines.Add("ты погиб.");
            else if (mob.CurrentState == MobState.Alive && _prevMob != MobState.Alive) lines.Add("пришёл в себя.");
            _prevMob = mob.CurrentState;
        }

        if (TryComp<DamageableComponent>(self, out var dmg))
        {
            var cur = dmg.TotalDamage.Float();
            if (_eventsInit && cur > _prevDamage + 5f)
                lines.Add($"⚠️ получил урон (всего {(int) cur}).");
            _prevDamage = cur;
        }

        if (TryComp<AlertsComponent>(self, out var al))
        {
            var seen = new HashSet<string>();
            foreach (var st in al.Alerts.Values)
            {
                var id = st.Type.Id;
                seen.Add(id);
                if (_eventsInit && !_prevAlerts.Contains(id) && IsDangerAlert(id))
                    lines.Add($"⚠️ {AlertName(id)}.");
            }
            _prevAlerts.Clear();
            foreach (var s in seen)
                _prevAlerts.Add(s);
        }

        // Прогресс авто-скилла разбора/сборки (шаги, завершение/прерывание) — чтобы раннер/LLM его видели.
        lines.AddRange(_agent.DrainConEvents());

        _eventsInit = true;
        return (lines.Count == 0 ? "Нет новых событий." : string.Join("\n", lines), false);
    }

    /// <summary>
    ///     Последнее указание пальцем (стрелка) рядом. Резолвит ближайшую сущность к острию стрелки
    ///     (≤2м) → id/имя/направление/дистанция от тебя. Считывание «съедает» указание (см.
    ///     <see cref="SaigaAgentSystem.ConsumePoint"/>). Не команда — модель сама решает, что делать.
    /// </summary>
    private (string, bool) Pointing()
    {
        if (_player.LocalEntity is not { } self || Deleted(self))
            return ("Нет персонажа (не в игре).", true);
        if (_agent.ConsumePoint() is not { } pt)
            return ("Никто не указывал.", false);

        var selfPos = _xform.GetMapCoordinates(self);
        EntityUid? best = null;
        var bestD = 2.0f;                                  // острие стрелки ↔ предмет
        var bestName = "";
        foreach (var ent in _lookup.GetEntitiesInRange<MetaDataComponent>(pt, 2.0f))
        {
            var uid = ent.Owner;
            if (uid == self || string.IsNullOrWhiteSpace(ent.Comp.EntityName))
                continue;
            if (_container.IsEntityInContainer(uid))
                continue;
            if (HasComp<SubFloorHideComponent>(uid) || HasComp<AudioComponent>(uid))
                continue;
            var d = (_xform.GetMapCoordinates(uid).Position - pt.Position).Length();
            if (d < bestD)
            {
                bestD = d;
                best = uid;
                bestName = ent.Comp.EntityName;
            }
        }

        var delta = pt.Position - selfPos.Position;
        var dist = delta.Length();
        if (best is { } b)
            return ($"👉 указали на: {bestName} (id={_agent.Handle(GetNetEntity(b))}, {DirText(delta)}, {dist:F1}м)", false);
        return ($"👉 указали на место ({DirText(delta)}, {dist:F1}м) — рядом ничего примечательного.", false);
    }

    // --- Tools (client-side perception) ---

    /// <summary>
    ///     Осматривает сущность. Первый вызов шлёт запрос серверу (ответ приходит ~300мс спустя в
    ///     <see cref="OnExamineResponse"/>) и сразу отдаёт имя+описание из метаданных; повторный вызов
    ///     по той же цели возвращает полный закешированный текст осмотра.
    /// </summary>
    private (string, bool) Examine(JsonElement args)
    {
        if (_player.LocalEntity is not { } self || Deleted(self))
            return ("Нет персонажа (не в игре).", true);

        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("target", out var t))
            return ("параметр 'target' обязателен", true);

        int idn;
        if (t.ValueKind == JsonValueKind.Number && t.TryGetInt32(out idn)) { }
        else if (t.ValueKind == JsonValueKind.String && int.TryParse(t.GetString(), out idn)) { }
        else return ("'target' должен быть числом (id из observe)", true);

        var net = _agent.ResolveHandle(idn);
        if (!TryGetEntity(net, out var ent) || Deleted(ent.Value))
            return ($"Сущность {idn} не найдена.", true);

        // Серверный ответ для этой цели уже пришёл — отдаём полный текст и сбрасываем кеш.
        if (_lastExaminedNet == net && _lastExamineText != null)
        {
            var cached = _lastExamineText;
            _lastExamineText = null;
            _lastExaminedNet = null;
            return (cached, false);
        }

        var meta = MetaData(ent.Value);
        _examine.DoExamine(ent.Value); // шлёт запрос серверу; ответ ловит OnExamineResponse
        var desc = string.IsNullOrWhiteSpace(meta.EntityDescription) ? "(нет)" : meta.EntityDescription;
        return ($"Осмотр запрошен: {meta.EntityName}. Описание: {desc}. Вызови examine ещё раз через ~1с для полного текста.", false);
    }

    /// <summary>
    ///     Показывает содержимое контейнера. С параметром target — заданный контейнер (сетевой id);
    ///     без него — рюкзак агента из слота «спина» (back). Читает контейнер "storagebase" и его
    ///     ContainedEntities из клиентского состояния (доступно, если содержимое в PVS клиента).
    /// </summary>
    private (string, bool) Contents(JsonElement args)
    {
        if (_player.LocalEntity is not { } self || Deleted(self))
            return ("Нет персонажа (не в игре).", true);

        EntityUid container;
        string label;

        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("target", out var t)
            && t.ValueKind != JsonValueKind.Null)
        {
            int idn;
            if (t.ValueKind == JsonValueKind.Number && t.TryGetInt32(out idn)) { }
            else if (t.ValueKind == JsonValueKind.String && int.TryParse(t.GetString(), out idn)) { }
            else return ("'target' должен быть числом (id из observe)", true);

            var net = _agent.ResolveHandle(idn);
            if (!TryGetEntity(net, out var ent) || Deleted(ent.Value))
                return ($"Сущность {idn} не найдена.", true);
            container = ent.Value;
        }
        else if (_inventory.TryGetSlotEntity(self, "back", out var back) && back is { } bp && !Deleted(bp))
        {
            container = bp;
        }
        else
        {
            return ("В слоте «спина» ничего нет (надень рюкзак или укажи target).", false);
        }

        label = MetaData(container).EntityName;

        if (!_container.TryGetContainer(container, StorageComponent.ContainerId, out var storage))
            return ($"«{label}»: это не контейнер (нет storagebase).", false);
        if (storage.ContainedEntities.Count == 0)
            return ($"«{label}»: пусто (или содержимое не видно клиенту — открой хранилище).", false);

        var sb = new StringBuilder();
        sb.Append($"Содержимое «{label}» ({storage.ContainedEntities.Count}):\n");
        foreach (var item in storage.ContainedEntities)
            sb.Append($"- id={_agent.Handle(GetNetEntity(item))} {MetaData(item).EntityName}\n");
        return (sb.ToString().TrimEnd(), false);
    }

    /// <summary>Lists items the agent is holding and which hand is active (client already knows own hands).</summary>
    private (string, bool) Hands()
    {
        if (_player.LocalEntity is not { } self || Deleted(self))
            return ("Нет персонажа (не в игре).", true);

        var active = _hands.GetActiveItem(self);
        var held = _hands.EnumerateHeld(self).ToList();
        if (held.Count == 0)
            return ("Руки пусты.", false);

        var sb = new StringBuilder();
        sb.Append("В руках:\n");
        foreach (var item in held)
        {
            var mark = active is { } a && a == item ? " (активная рука)" : "";
            sb.Append($"- id={_agent.Handle(GetNetEntity(item))} {MetaData(item).EntityName}{mark}\n");
        }
        return (sb.ToString().TrimEnd(), false);
    }

    private (string, bool) Listen()
    {
        var unread = _heard.Where(l => !l.Read).ToList();
        foreach (var l in _heard)
            l.Read = true;

        if (unread.Count == 0)
            return ("Новых реплик нет.", false);

        var now = _timing.CurTime;
        var sb = new StringBuilder();
        sb.Append("Тебе сказали рядом (id говорящего, кто: что, как давно). Для действий по говорящему (следуй/иди/ударь) бери его id:\n");
        foreach (var l in unread)
        {
            var age = (int) (now - l.Time).TotalSeconds;
            sb.Append($"- id={_agent.Handle(new NetEntity(l.SpeakerId))} {l.Speaker}: «{l.Text}» ({age}с назад)\n");
        }
        return (sb.ToString().TrimEnd(), false);
    }

    private (string, bool) Observe(JsonElement args)
    {
        if (_player.LocalEntity is not { } self || Deleted(self))
            return ("Нет персонажа (не в игре).", true);
        _agent.ClearHandles();                 // уникальные хэндлы 1..N на КАЖДЫЙ observe (иначе коллизия при >300)

        string? filter = null;
        var raw = false;
        if (args.ValueKind == JsonValueKind.Object)
        {
            if (args.TryGetProperty("filter", out var fEl) && fEl.ValueKind == JsonValueKind.String)
                filter = fEl.GetString();
            if (args.TryGetProperty("raw", out var rEl) && rEl.ValueKind == JsonValueKind.True)
                raw = true;
        }
        var terms = string.IsNullOrWhiteSpace(filter)
            ? null
            : filter.Split(',', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries)
                    .Select(t => t.ToLowerInvariant()).ToArray();

        var selfPos = _xform.GetMapCoordinates(self);
        var found = new List<(string Name, NetEntity Net, float Dist, Vector2 Delta, string? Job, int Tier)>();
        // Пространственный запрос через broadphase — только в ObserveRange (=PVS), не скан всего мира (void 4500+ сущностей).
        foreach (var ent in _lookup.GetEntitiesInRange<MetaDataComponent>(selfPos, ObserveRange))
        {
            var uid = ent.Owner;
            var meta = ent.Comp;
            if (uid == self || string.IsNullOrWhiteSpace(meta.EntityName))
                continue;
            if (_container.IsEntityInContainer(uid))
                continue;
            if (HasComp<SubFloorHideComponent>(uid) || HasComp<AudioComponent>(uid))
                continue;
            var pos = _xform.GetMapCoordinates(uid);
            if (pos.MapId != selfPos.MapId)
                continue;
            var delta = pos.Position - selfPos.Position;
            var dist = delta.Length();
            if (dist > ObserveRange)
                continue;
            var tier = Importance(uid, meta.EntityName);
            if (tier < 0 && !raw)                        // мусор (лужи/пыль/газ/кабели-скрыты) — прячем, если не raw
                continue;
            if (terms != null && !terms.Any(t => meta.EntityName.Contains(t, System.StringComparison.OrdinalIgnoreCase)))
                continue;
            found.Add((meta.EntityName, GetNetEntity(uid), dist, delta,
                       HasComp<MobStateComponent>(uid) ? _agent.JobOf(uid) : null, tier < 0 ? 4 : tier));
        }

        if (found.Count == 0)
            return (terms == null ? "Рядом ничего важного не видно." : $"По фильтру «{filter}» ничего не видно.", false);

        // Схлопнуть одинаковые имена в «имя ×N» (хэндл ближайшего); сортировка по важности, затем близости.
        var groups = found
            .GroupBy(f => f.Name)
            .Select(g =>
            {
                var near = g.OrderBy(x => x.Dist).First();
                return (near.Name, near.Net, near.Dist, near.Delta, near.Job, Tier: g.Min(x => x.Tier), Count: g.Count());
            })
            .OrderBy(g => g.Tier).ThenBy(g => g.Dist)
            .ToList();

        var sb = new StringBuilder();
        var selfJob = _agent.JobOf(self);
        sb.Append(selfJob != null ? $"Ты — {MetaData(self).EntityName} ({selfJob}).\n"
                                  : $"Ты — {MetaData(self).EntityName}.\n");
        sb.Append(terms == null
            ? "Рядом (число слева = id для действий; имя, [должность], ×N одинаковых, расстояние, сторона):\n"
            : $"По фильтру «{filter}»:\n");
        // Мягкий лимит: люди (тир0) — всегда; неважное — до ~50 строк (иначе контекст 8192 переполняется). filter/raw — без лимита.
        var cap = (raw || terms != null) ? int.MaxValue : 50;
        var shownCount = 0;
        foreach (var g in groups)
        {
            if (g.Tier > 0 && shownCount >= cap)
                break;                                   // хвост менее важного отсекаем
            var h = _agent.Handle(g.Net);                // короткий хэндл (1 токен) вместо гигантского net-id
            var job = string.IsNullOrEmpty(g.Job) ? "" : $" [{g.Job}]";
            var mult = g.Count > 1 ? $" ×{g.Count}" : "";
            sb.Append($"{h} {g.Name}{job}{mult} {g.Dist:F0}м {DirText(g.Delta)}\n");
            shownCount++;
        }
        if (shownCount < groups.Count)
            sb.Append($"(+ещё {groups.Count - shownCount} менее важного скрыто — уточни observe с filter=\"имя\")");
        return (sb.ToString().TrimEnd(), false);
    }

    // Хлам/декор/грязь — прятать из observe (raw=true покажет). Полезное (аптечк/оружие/инструмент/сварочн/лом/кусачк) НЕ трогаем.
    private static readonly string[] JunkRoots =
    {
        "обёртк", "обертк", "пакетик", "палочк", "пепел", "окурок", "сигарет", "крошк", "огрызок", "осколок", "мусор",
        "плюшев", "фигурк", "статуэтк", "сувенир", "игруш", "постер", "реклам", "плакат", "гирлянд",
        "одеял", "подушк", "полотенц", "занавес", "штор",
        "пыл", "след", "лужа", "кров", "газ ", "дым", "копот", "грязь", "пятно",
    };

    /// <summary>Важность для observe: 0 люди, 1 предметы, 2 интерактив, 3 структуры/прочее, −1 хлам/декор/грязь (спрятать).</summary>
    private int Importance(EntityUid uid, string name)
    {
        if (HasComp<PuddleComponent>(uid))
            return -1;
        var low = name.ToLowerInvariant();
        foreach (var r in JunkRoots)
            if (low.Contains(r))
                return -1;
        if (HasComp<MobStateComponent>(uid))
            return 0;
        if (HasComp<ItemComponent>(uid))
            return 1;
        if (HasComp<DoorComponent>(uid) || HasComp<UserInterfaceComponent>(uid) || HasComp<StorageComponent>(uid))
            return 2;
        return 3;
    }

    private static string DirText(Vector2 d)
    {
        if (d.LengthSquared() < 0.01f)
            return "здесь";
        var ang = (System.MathF.Atan2(d.Y, d.X) * 180f / System.MathF.PI + 360f) % 360f;
        string[] names = { "В", "СВ", "С", "СЗ", "З", "ЮЗ", "Ю", "ЮВ" };
        return names[(int) System.MathF.Round(ang / 45f) % 8];
    }

    // --- JSON-RPC helpers ---

    private static JsonNode? GetId(JsonElement root)
        => root.TryGetProperty("id", out var e) && e.ValueKind != JsonValueKind.Null && e.ValueKind != JsonValueKind.Undefined
            ? JsonNode.Parse(e.GetRawText())
            : null;

    private static JsonNode RpcResult(JsonNode? id, JsonNode result)
        => new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    private static JsonNode RpcError(JsonNode? id, int code, string message)
        => new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
}
