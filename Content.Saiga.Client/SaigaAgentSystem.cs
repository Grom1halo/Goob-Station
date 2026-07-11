using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using Content.Client.Examine;
using Content.Client.Pointing.Components;
using Content.Client.Verbs;
using Content.Client.Wires.UI;
using Content.Shared.Access.Components;
using Content.Shared.Access.Systems;
using Content.Shared.Actions;
using Content.Shared.CCVar;
using Content.Shared.Climbing.Components;
using Content.Shared.CombatMode;
using Content.Shared.Construction;
using Content.Shared.Doors.Components;
using Content.Shared.Examine;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Input;
using Content.Shared.Inventory;
using Content.Shared.Maps;
using Content.Shared.Medical.CrewMonitoring;
using Content.Shared.Medical.SuitSensor;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Movement.Components;
using Content.Shared.PDA;
using Content.Shared.Physics;
using Content.Shared.Pinpointer;
using Content.Shared.Storage;
using Content.Shared.Verbs;
using Content.Shared.Wires;
using Content.Shared.Weapons.Melee.Events;
using Content.Shared.Weapons.Ranged.Events;
using Content.Shared.Weapons.Ranged.Systems;
using Robust.Client.GameObjects;
using Robust.Client.Input;
using Robust.Shared.Configuration;
using Robust.Shared.Console;
using Robust.Shared.Containers;
using Robust.Shared.Input;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Physics.Components;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Client._Mono.SaigaAgent;

/// <summary>
///     CLIENT-ONLY steerer that pilots the local player's character via simulated input — pressing
///     movement keys ("incmd"), firing interactions, speaking with the "say" command. It is driven
///     entirely from the client (by <c>SaigaMcpClientSystem</c> calling <see cref="ApplyDecision"/>),
///     with NO networked events or shared/server Saiga types — so the client's network type registry
///     stays identical to a vanilla server and it can join any server on a compatible build.
///
///     IMPORTANT: input commands (incmd) are only issued from <see cref="FrameUpdate"/> — never from
///     network handlers — because those run inside the sim tick / prediction, and injecting input
///     there mutates the input queue mid-enumeration (crash).
/// </summary>
public sealed class SaigaAgentSystem : EntitySystem
{
    [Dependency] private readonly IConsoleHost _con = default!;
    [Dependency] private readonly ISharedPlayerManager _player = default!;
    [Dependency] private readonly SharedTransformSystem _xform = default!;
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IInputManager _input = default!;
    [Dependency] private readonly InputSystem _inputSys = default!;
    [Dependency] private readonly SharedCombatModeSystem _combat = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly InventorySystem _inventory = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;
    [Dependency] private readonly SharedUserInterfaceSystem _ui = default!;
    [Dependency] private readonly SharedWiresSystem _wires = default!;
    [Dependency] private readonly SharedGunSystem _gun = default!;
    [Dependency] private readonly TurfSystem _turf = default!;
    [Dependency] private readonly AccessReaderSystem _access = default!;
    [Dependency] private readonly ExamineSystem _examine = default!;
    [Dependency] private readonly VerbSystem _verbs = default!;

    /// <summary>Steerer active? The client MCP enables it via <see cref="EnableLocalControl"/>.</summary>
    public bool Enabled { get; private set; }

    // Steering (state only; actual key presses happen in FrameUpdate).
    private NetEntity? _followTarget;          // follow a moving entity
    private MapCoordinates? _gotoTarget;       // walk to a fixed point (e.g. a pointed location)
    private NetEntity? _pickupTarget;          // walk to an item, then grab it
    private NetEntity? _pullTarget;            // walk to an item, then start pulling it
    private NetEntity? _moveToTarget;          // walk right up to an entity (no interaction)
    private NetEntity? _placeTarget;           // walk right up to an entity, then drop the held item
    private NetEntity? _attackTarget;          // walk into melee range, then attack/mine (combat mode)
    private NetEntity? _shootTarget;           // fire the held gun at this entity (once combat mode is on)
    private NetEntity? _useOnTarget;           // walk up, then Use the held item ON it (pry a door w/ crowbar, insert part…)
    private NetEntity? _altUseTarget;          // walk up, then alt-use the held item on it
    private readonly Queue<(BoundKeyFunction Func, NetEntity? Target, bool AtCoords)> _pending = new();

    // Навигация (Фича 3): память мест на сессию + очередь waypoint'ов проложенного A*-маршрута.
    private readonly Dictionary<string, EntityCoordinates> _places = new();
    // Запомненные места (remember_place): имя→индекс тайла на гриде. ТОЛЬКО в памяти сессии (на void каждый раунд
    // другая станция → координаты прошлого раунда стухают; сбрасываем при смене грида, см. TickSession).
    private readonly Dictionary<string, Vector2i> _savedTiles = new();
    // Отделы с внутриигровой карты станции (тул read_map, NavMapComponent.Beacons) — имя→тайл. ТОЛЬКО в памяти сессии
    // (на void каждый раунд другая станция → тайлы прошлого раунда стухают; сбрасываем при смене грида).
    private readonly Dictionary<string, Vector2i> _mapTiles = new();
    private EntityUid? _lastGrid;             // грид, под который набраны места/карта — сменился = новый раунд → сброс
    private TimeSpan _mapAutoNext;            // троттл авто-мёржа маяков карты
    // Короткие ХЭНДЛЫ вместо гигантских net-id: observe/listen/contents/pointing выдают 1,2,3…, тут карта хэндл→NetEntity.
    // Экономит токены (id=46831876 ≈ 7 токенов → «3» = 1) и 8B перестаёт выдумывать id (выбор из коротких чисел).
    private readonly Dictionary<int, NetEntity> _handles = new();
    private int _handleSeq;
    // Разведка (explore): двери, через которые уже прошли, и тайлы, где были — чтобы лезть в новое.
    private readonly HashSet<Vector2i> _usedDoors = new();
    private readonly HashSet<Vector2i> _visitedTiles = new();
    private readonly Queue<MapCoordinates> _pathQueue = new();
    private const int PathMaxExpand = 60000;  // предел раскрытых тайлов A* (масштаб станции; + wall-clock-гард в FindPath)
    private const int PathMaxRange  = 250;    // не искать дальше N тайлов от старта
    private static readonly TimeSpan PathTimeBudget = TimeSpan.FromMilliseconds(60); // жёсткий потолок времени A* (не вешать тик)
    // Реальная маска коллизии ПИЛОТИРУЕМОЙ сущности (обновляется в TickSession): борг=MobMask(+MidImpassable=столы блок),
    // дрон=SmallMobMask(без MidImpassable → под столами проходит). A* строит путь по НЕЙ, не по хардкоду. Фолбэк — NavBlockMask.
    private CollisionGroup _selfMask = NavBlockMask;
    private string _pathFail = "";             // ДИАГНОСТИКА: почему FindPath вернул null (лимит/таймаут/нет пути)
    private readonly HashSet<string> _patrolDone = new(); // отделы, посещённые/пропущенные в текущем круге патруля (wander)
    // Почтальон: A*-путь до движущегося человека (тул go_to_person) — догоняем с перепланом.
    private NetEntity? _navEntity;             // за кем идём (человек)
    private Vector2i? _navGoalTile;            // куда был проложен последний путь (для детекта «ушёл»)
    private float _navArrive = 1.4f;           // на сколько подойти к человеку
    private TimeSpan _navNext;                 // троттл реплана
    private bool _navReadCrew;                 // дойдя до цели-консоли — снять крю-монитор (а не «дошёл до»)
    private bool _navFollow;                   // follow: не сбрасывать на прибытии, держаться рядом (переплан при отдалении)
    // Крю-монитор: снимок позиций всего экипажа (имя/должность/координаты) с консоли — для go_to_person по всей карте.
    private readonly List<(string Name, string Job, NetCoordinates Coords)> _crew = new();
    private TimeSpan? _crewReadAt;             // когда прочитать state открытой консоли (даём серверу прислать)
    private NetEntity? _crewConsole;           // консоль, чей UI открыли для чтения
    private static readonly Vector2i[] PathDirs = { new(1, 0), new(-1, 0), new(0, 1), new(0, -1) };
    // Что блокирует ход при навигации. MidImpassable = столы/машины/крупные структуры (их слой), но НЕ обычные
    // люди (их слой Opaque|BulletImpassable без MidImpassable) — потому люди путь не рушат, а столы обходятся.
    private const CollisionGroup NavBlockMask = CollisionGroup.Impassable | CollisionGroup.HighImpassable | CollisionGroup.MidImpassable;

    // Build a wall: the next point sets the build tile; she walks there and raises the girder.
    private bool _awaitBuildPoint;
    private EntityCoordinates? _buildCoords;
    private int _buildAck;
    private const float BuildRange = 1.4f;
    private const string GirderPrototype = "Girder";

    private const float StopRange = 1.8f;
    private const float PickupApproach = 1.0f; // get this close before trying to grab
    private const float PickupRange = 1.6f;    // try the grab once within this distance
    private const float MoveToRange = 0.4f;    // arrive this close for move_to / place (right on the spot)
    private const float WaypointArrive = 0.7f; // промежуточный waypoint маршрута — прибытие пошире (не разворачиваться при перелёте)
    // Per-axis threshold to press a movement key. MUST stay below the tightest arrival (MoveToRange),
    // else the agent stops correcting an axis while still short of the target and walks past it.
    private const float Deadzone = 0.15f;
    private const float PointDetectRange = 12f; // how close a fresh pointing-arrow must be to react
    private readonly HashSet<string> _heldKeys = new();
    private readonly HashSet<EntityUid> _seenArrows = new();

    // Последнее указание (стрелка) — ТОЛЬКО как перцепция: модель сама решит, что делать (тул pointing).
    // Раньше сюда шёл авто-goto (борг шёл к любой стрелке даже без модели) — убрано: указатель в SS14
    // перегружен (просто показать / указать на собеседника / указать предмет для взаимодействия).
    private MapCoordinates? _lastPointPos;
    private TimeSpan _lastPointTime;

    // Local obstacle avoidance (slide along one axis when stuck).
    private TimeSpan _stuckCheck;
    private float _prevDist;
    private int _slideMode; // 0 = straight to target, 1 = X axis only, 2 = Y axis only

    // Give-up: if a walk target isn't getting closer for this long, abandon it (don't push a wall
    // forever). Lets the agent fail gracefully instead of bricking on an unreachable target.
    private float _walkBest = float.MaxValue;
    private TimeSpan _walkImprove;
    private static readonly TimeSpan StuckGiveUp = TimeSpan.FromSeconds(6);

    // Shuttle piloting: hold a thrust/rotate key for a short pulse, then release. Requires being the
    // pilot (interact with the shuttle console first). Reuses the same input path as walking.
    private BoundKeyFunction? _pilotKey;
    private bool _pilotDown;
    private TimeSpan _pilotUntil;
    private static readonly TimeSpan PilotPulse = TimeSpan.FromSeconds(0.7);

    // Combat toggle is an async networked ACTION (server round-trip); debounce re-sends until it lands.
    private bool _combatWant;
    private TimeSpan _combatReqUntil;
    // Melee/mining: fire a real LightAttack at this cadence while in range (mining = repeated hits).
    private TimeSpan _nextAttack;
    private static readonly TimeSpan AttackCooldown = TimeSpan.FromSeconds(0.5);
    private const float MeleeRange = 1.4f;

    // Auto-mining loop: keep hitting the target; when it's destroyed, walk to the nearest entity with
    // the SAME name and keep going, until `stop`. _mineName = what to keep mining (e.g. "камень").
    private bool _mineLoop;
    private string? _mineName;
    private const float MineSearchRange = 12f;

    // --- Составной скилл РАЗБОРА/СБОРКИ (C#-автомат, как mine-loop: неблокирующий, прерываемый stop/крит) ---
    // Ведёт цель по её examine-шагам: подойти → (разбор: верб «Начать разборку») → цикл {читаем шаг из
    // examine → нужный инструмент/материал → разгрузить руку (drop) → взять → (сварку жечь ТОЛЬКО перед
    // use_on и тушить сразу после — экономия топлива) → use_on → ждать смены шага поллингом examine}.
    private enum ConMode { None, Deconstruct, Construct }
    private enum ConPhase
    {
        // Prep для шлюза (U8): панель отвёрткой → провода кусачками → сварка двери → потом обычный граф-разбор.
        PrepWires, PrepWeldGet, PrepWeldOn, PrepWeldUse, PrepWeldOff,
        Approach, KickVerb, StartVerb, Exam, Acquire, AcquirePick, AcquireWait, WeldOn, UseStep,
    }
    private ConMode _conMode;
    private ConPhase _conPhase;
    private NetEntity? _conTarget;
    private TimeSpan _conNext;          // не действовать раньше этого момента (тайминги async examine/verb/pickup)
    private TimeSpan _conStartTime;     // общий дедлайн скилла (защита от залипания)
    private int _conRetry;              // ретраи чтения examine-кеша
    private int _conPoll;              // поллы «шаг ещё идёт» (ожидание смены/исчезновения)
    private int _conStep;              // всего пройдено шагов (защита от бесконечного цикла)
    private bool _conExamIssued;        // запрос examine уже отправлен, ждём ответ
    private bool _conExpectChange;      // фаза ожидания смены шага после use_on (иначе — берём шаг как есть)
    private string? _conHint;           // текст текущего шага (инструмент/материал) — для детекта смены
    private string? _conToolSub;        // подстрока имени нужного инструмента/предмета (для pickup/hands)
    private bool _conIsTool;            // шаг = инструмент (иначе материал/деталь)
    private bool _conReqMode;           // текущий шаг — пункт списка «Требования:» машинного каркаса (вставлять по одному, порядок неважен)
    private int _conReqStuck;           // сколько use_on подряд по ТОМУ ЖЕ элементу без сдвига (нет предмета/не лезет)
    private bool _conWireStarted;       // U8: prep-резка проводов шлюза запущена (ждём завершения TickWireCut)

    // --- Хакинг проводов (cut_wires): открыть техпанель отвёрткой → взять кусачки → перерезать провода по одному
    // (у каждого свой doAfter). Читаем состояние из BUI-стейта, режем сетевым WiresActionMessage — без визуального UI.
    private enum WirePhase { Panel, PanelWait, Cutters, OpenUi, Cut }
    private NetEntity? _wireTarget;
    private WirePhase _wirePhase;
    private TimeSpan _wireNext, _wireStart;
    private int _wireRetry, _wireStuck, _wirePrevUncut;
    private bool _conWeldOn;            // сварка сейчас зажжена нами (тушить перед сбросом/сменой шага)
    // Собственный кеш ответов сервера (независимо от MCP-системы — оба могут подписаться на один NetMessage).
    private string? _conExamText;
    private NetEntity? _conExaminedNet;
    private NetEntity? _conVerbTarget;
    private readonly List<Verb> _conVerbs = new();
    // Очередь статус-сообщений скилла — MCP-тул events сливает её, чтобы раннер/LLM видели прогресс.
    private readonly Queue<string> _conEvents = new();
    private static readonly Regex ConUseToolRx =
        new(@"использ\w*\s*\[color=\w+\]\s*(.+?)\s*\[/color\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ConAddMatRx =
        new(@"добав\w*\s+\d+\s*ед[^\[]*\[color=\w+\]\s*(.+?)\s*\[/color\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ConInsertRx =
        new(@"встав\w*\s*\[color=\w+\]\s*(.+?)\s*\[/color\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Пункт списка «Требования:» машинного каркаса: «[color=yellow]Nед[/color] [color=green]ИМЯ[/color]».
    // (construction-condition-machine-frame-required-element-entry). Group1=остаток, Group2=имя элемента.
    private static readonly Regex ConReqEntryRx =
        new(@"\[color=yellow\]\s*(\d+)\s*ед\s*\[/color\]\s*\[color=\w+\]\s*(.+?)\s*\[/color\]",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Шаг «закрепите/прикрутите на месте» (anchor к полу) идёт БЕЗ формата «используйте [color]…[/color]» —
    // распознаём отдельно, инструмент = гаечный ключ.
    private static readonly Regex ConAnchorRx =    // закрепить/открепить гаечным ключом (повелит. форма, не пассив)
        new(@"закрепит[еь]|закрепи\b|открепит[еь]|привинтит[еь]|отвинтит[еь]|прикрутит[еь]",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly (string key, string sub)[] ConToolSubs =    // «ключ»→«гаечн», иначе схватит «ключ шифрования»/«ключ доступа»
        { ("сварк", "сварочн"), ("монтиров", "монтиров"), ("отвёрт", "отвёрт"),
          ("отверт", "отверт"), ("кусач", "кусач"), ("гаечн", "гаечн"), ("ключ", "гаечн") };

    /// <summary>Enables the steerer. Purely local — no network event, safe on a vanilla server.</summary>
    public void EnableLocalControl()
    {
        Enabled = true;
    }

    public override void Initialize()
    {
        base.Initialize();
        // Ловим серверные ответы для авто-скилла разбора/сборки (свой кеш; MCP-система подписана отдельно — ок).
        SubscribeNetworkEvent<ExamineSystemMessages.ExamineInfoResponseMessage>(OnConExamineResponse);
        SubscribeNetworkEvent<VerbsResponseEvent>(OnConVerbsResponse);
    }

    private void OnConExamineResponse(ExamineSystemMessages.ExamineInfoResponseMessage ev)
    {
        if (_conMode == ConMode.None)
            return;
        _conExaminedNet = ev.EntityUid;
        _conExamText = ev.Message.ToMarkup();
    }

    private void OnConVerbsResponse(VerbsResponseEvent ev)
    {
        if (_conMode == ConMode.None || ev.Entity != _conVerbTarget || ev.Verbs == null)
            return;
        foreach (var v in ev.Verbs)
            if (_conVerbs.All(x => x.Text != v.Text))
                _conVerbs.Add(v);
    }

    /// <summary>Runs between ticks — the only safe place to issue input (incmd / interactions).</summary>
    public override void FrameUpdate(float frameTime)
    {
        // Рефлекс-пол (Фича 2): при крите/смерти сам прерываю добычу и движение — выживание важнее задачи.
        if (Enabled && _player.LocalEntity is { } me && !Deleted(me)
            && TryComp<MobStateComponent>(me, out var mst) && mst.CurrentState != MobState.Alive
            && (_mineLoop || _gotoTarget != null || _pathQueue.Count > 0 || _attackTarget != null))
        {
            ClearMovement();
        }

        ExecutePending();
        TryReachInteract(ref _pickupTarget, EngineKeyFunctions.Use);          // pick up
        TryReachInteract(ref _pullTarget, ContentKeyFunctions.TryPullObject); // start pulling
        TryReachInteract(ref _useOnTarget, EngineKeyFunctions.Use);           // walk up, then use held item on it (pry/insert)
        TryReachInteract(ref _altUseTarget, ContentKeyFunctions.AltActivateItemInWorld); // walk up, then alt-use on it
        TryAttack();                                                          // walk into melee range, then LightAttack (repeat = mine)
        TryShoot();                                                           // fire held gun at target once combat mode is confirmed
        TryReachPlace();                                                      // walk up, then drop
        TryBuild();
        TickConstruction();                                                   // авто-скилл разбора/сборки по examine-шагам
        try { TickWireCut(); } catch (Exception e) { FinishWire($"сбой: {e.Message}"); } // хакинг проводов (не ронять клиент)
        try { TickNavEntity(); } catch (Exception e) { _navEntity = null; Log.Warning($"saiga: nav сбой: {e.Message}"); } // почтальон: догон человека A*
        try { TickCrewRead(); } catch (Exception e) { _crewReadAt = null; Log.Warning($"saiga: crew сбой: {e.Message}"); } // чтение крю-монитора
        if (Enabled && _player.LocalEntity is { } sess && !Deleted(sess))
            try { TickSession(sess); } catch (Exception e) { Log.Warning($"saiga: session сбой: {e.Message}"); } // смена раунда/грида + авто-мёрж карты
        TryPilot();                                                           // shuttle thrust/rotate pulse
        DetectPointingArrow();
        ApplyKeys(DesiredKeys());
    }

    /// <summary>When at the build tile, raise the girder construction (server consumes held steel).</summary>
    private void TryBuild()
    {
        if (_buildCoords is not { } coords)
            return;

        if (_player.LocalEntity is not { } self || Deleted(self))
        {
            _buildCoords = null;
            return;
        }

        var selfPos = _xform.GetMapCoordinates(self);
        var buildPos = _xform.ToMapCoordinates(coords);
        if (selfPos.MapId != buildPos.MapId)
            return;

        if ((buildPos.Position - selfPos.Position).Length() > BuildRange)
            return; // keep walking toward the tile

        RaiseNetworkEvent(new TryStartStructureConstructionMessage(
            GetNetCoordinates(coords), GirderPrototype, Angle.Zero, unchecked(_buildAck++)));
        _buildCoords = null;
    }

    /// <summary>Holds the current shuttle thrust/rotate key until its pulse ends, then releases.</summary>
    private void TryPilot()
    {
        if (_pilotKey is not { } key)
            return;

        if (_player.LocalEntity is not { } self || Deleted(self) || _player.LocalSession is not { } session)
        {
            _pilotKey = null; _pilotDown = false;
            return;
        }

        var coords = Transform(self).Coordinates;
        if (_timing.CurTime < _pilotUntil)
        {
            if (!_pilotDown)
            {
                SendKey(session, key, BoundKeyState.Down, coords);
                _pilotDown = true;
            }
        }
        else
        {
            if (_pilotDown)
                SendKey(session, key, BoundKeyState.Up, coords);
            _pilotKey = null; _pilotDown = false;
        }
    }

    /// <summary>
    ///     Ensures combat mode matches <paramref name="want"/> by performing the combat-toggle ACTION
    ///     over the network (RequestPerformActionEvent) — the same path the action button uses, so the
    ///     server actually flips it (a client-only SetInCombatMode never reaches the server, hence the
    ///     old Hotbar1 tap was unreliable). Combat ON makes a LightAttack a real hit (melee / mining);
    ///     OFF keeps clicks as interactions. Debounced: the toggle is async, don't re-send while pending.
    /// </summary>
    private void RequestCombatMode(bool want)
    {
        if (_player.LocalEntity is not { } self || Deleted(self)
            || !TryComp<CombatModeComponent>(self, out var cm))
            return;
        if (cm.IsInCombatMode == want)
            return;
        if (_timing.CurTime < _combatReqUntil && _combatWant == want)
            return; // a toggle request for this state is already in flight
        if (cm.CombatToggleActionEntity is not { } act || Deleted(act))
            return;

        RaiseNetworkEvent(new RequestPerformActionEvent(GetNetEntity(act)));
        _combatWant = want;
        _combatReqUntil = _timing.CurTime + TimeSpan.FromSeconds(0.6);
    }

    /// <summary>
    ///     Walk-to-then-attack: once within melee range of <see cref="_attackTarget"/>, raise a real
    ///     <see cref="LightAttackEvent"/> (exactly what a click makes) at ~2/s. Repeats until the target
    ///     is gone — mining a rock takes several hits. Requires combat mode (requested when the target
    ///     is set and re-confirmed here before each swing).
    /// </summary>
    private void TryAttack()
    {
        if (_attackTarget is not { } net)
            return;

        if (_player.LocalEntity is not { } self || Deleted(self))
        {
            _attackTarget = null;
            return;
        }

        if (!TryGetEntity(net, out var ent) || Deleted(ent.Value))
        {
            // Target gone (rock mined out). In a mine-loop, hop to the nearest same-named entity and
            // keep going; otherwise stop swinging. Reset the give-up tracker so walking to the next
            // (farther) rock isn't instantly judged "stuck" and doesn't kill the loop.
            _attackTarget = _mineLoop ? FindNearestNamed(self, _mineName) : null;
            _walkBest = float.MaxValue;
            _walkImprove = _timing.CurTime;
            if (_attackTarget == null)
                _mineLoop = false;
            return;
        }

        var selfPos = _xform.GetMapCoordinates(self);
        var entPos = _xform.GetMapCoordinates(ent.Value);
        if (selfPos.MapId != entPos.MapId)
            return;
        if ((entPos.Position - selfPos.Position).Length() > MeleeRange)
            return; // keep walking toward it (TryResolveTargetPos drives the movement)

        RequestCombatMode(true);            // server-side combat must be ON or the hit is rejected
        if (!_combat.IsInCombatMode(self))
            return;                         // not confirmed yet — wait for the toggle round-trip
        if (_timing.CurTime < _nextAttack)
            return;

        var weapon = _hands.GetActiveItem(self) ?? self; // held tool (pickaxe) or unarmed fists
        RaisePredictiveEvent(new LightAttackEvent(
            GetNetEntity(ent.Value), GetNetEntity(weapon), GetNetCoordinates(Transform(ent.Value).Coordinates)));
        _nextAttack = _timing.CurTime + AttackCooldown;
    }

    /// <summary>
    ///     Fires the held gun at <see cref="_shootTarget"/> via a real <see cref="RequestShootEvent"/>
    ///     (exactly what a click sends) — NOT a simulated Use, which the gun system's per-frame Use-state
    ///     poll never sees. One shot per request; requires combat mode (requested on set) and a gun in
    ///     hand (e.g. the proto-kinetic accelerator). Aims at the target's coordinates.
    /// </summary>
    private void TryShoot()
    {
        if (_shootTarget is not { } net)
            return;

        if (_player.LocalEntity is not { } self || Deleted(self)
            || !TryGetEntity(net, out var ent) || Deleted(ent.Value))
        {
            _shootTarget = null;
            return;
        }

        RequestCombatMode(true);        // gun fire is gated on combat mode server-side too
        if (!_combat.IsInCombatMode(self))
            return;                     // wait for the toggle round-trip, then fire next frame
        if (!_gun.TryGetGun(self, out var gunUid, out _))
        {
            _shootTarget = null;        // nothing gun-like in hand — give up
            return;
        }

        var coords = Transform(ent.Value).Coordinates;
        RaisePredictiveEvent(new RequestShootEvent
        {
            Gun = GetNetEntity(gunUid),
            Coordinates = GetNetCoordinates(coords),
            Target = net, // Goob's RequestShootEvent has no Shot field; server fires authoritative projectiles
        });
        _shootTarget = null;
    }

    /// <summary>Nearest entity sharing <paramref name="name"/> within <see cref="MineSearchRange"/> (for the mine-loop), optionally skipping one.</summary>
    private NetEntity? FindNearestNamed(EntityUid self, string? name, EntityUid? exclude = null)
    {
        if (string.IsNullOrEmpty(name))
            return null;

        var selfPos = _xform.GetMapCoordinates(self);
        EntityUid? best = null;
        var bestDist = MineSearchRange;

        var query = EntityQueryEnumerator<MetaDataComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var meta, out _))
        {
            if (uid == self || uid == exclude || meta.EntityName != name)
                continue;
            var pos = _xform.GetMapCoordinates(uid);
            if (pos.MapId != selfPos.MapId)
                continue;
            var dist = (pos.Position - selfPos.Position).Length();
            if (dist < bestDist)
            {
                bestDist = dist;
                best = uid;
            }
        }

        return best is { } b ? GetNetEntity(b) : null;
    }

    // Составные скиллы: LLM зовёт один тул (dig/take), а поиск цели по имени делаем детерминированно
    // здесь, рядом с mine-циклом, и переиспользуем проверенные действия mine/pickup.
    private static readonly string[] DiggableKeywords =
        { "сугроб", "снег", "камен", "руд", "астероид", "обломок", "порода", "минерал", "rock", "ore" };

    /// <summary>Ближайший «копаемый» объект (сугроб/камень/руда) для составного тула dig.</summary>
    public (NetEntity net, string name)? FindNearestDiggable(EntityUid self)
        => FindNearestWhere(self, n =>
        {
            var low = n.ToLowerInvariant();
            if (low.Contains("стен"))                       // «копай» не должно долбить стены
                return false;
            foreach (var k in DiggableKeywords)
                if (low.Contains(k))
                    return true;
            return false;
        });

    /// <summary>Ближайший предмет, чьё имя содержит подстроку (или ВСЕ подстроки, если заданы через '+').</summary>
    public (NetEntity net, string name)? FindNearestByName(EntityUid self, string sub)
    {
        if (string.IsNullOrWhiteSpace(sub))
            return null;
        var parts = sub.Trim().ToLowerInvariant().Split('+', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return null;
        return FindNearestWhere(self, n =>
        {
            var low = n.ToLowerInvariant();
            foreach (var p in parts)
                if (!low.Contains(p.Trim()))
                    return false;
            return true;
        });
    }

    /// <summary>Ближайшая сущность, чьё имя проходит предикат (общий движок для dig/take).</summary>
    private (NetEntity net, string name)? FindNearestWhere(EntityUid self, Func<string, bool> match, EntityUid? exclude = null)
    {
        var selfPos = _xform.GetMapCoordinates(self);
        EntityUid? best = null;
        var bestName = "";
        var bestDist = MineSearchRange;

        var query = EntityQueryEnumerator<MetaDataComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var meta, out _))
        {
            if (uid == self || uid == exclude || string.IsNullOrEmpty(meta.EntityName) || !match(meta.EntityName))
                continue;
            var pos = _xform.GetMapCoordinates(uid);
            if (pos.MapId != selfPos.MapId)
                continue;
            var dist = (pos.Position - selfPos.Position).Length();
            if (dist < bestDist)
            {
                bestDist = dist;
                best = uid;
                bestName = meta.EntityName;
            }
        }

        return best is { } b ? (GetNetEntity(b), bestName) : null;
    }

    /// <summary>Должность сущности из ID-карты (слот «id»: PDA→карта или карта напрямую). null — нет карты/должности.</summary>
    public string? JobOf(EntityUid uid)
    {
        if (!_inventory.TryGetSlotEntity(uid, "id", out var idUid))
            return null;
        string? job = null;
        if (TryComp<PdaComponent>(idUid, out var pda) && TryComp<IdCardComponent>(pda.ContainedId, out var pid))
            job = pid.LocalizedJobTitle;
        else if (TryComp<IdCardComponent>(idUid, out var id))
            job = id.LocalizedJobTitle;
        return string.IsNullOrWhiteSpace(job) ? null : job;
    }

    /// <summary>Ближайший видимый ЧЕЛОВЕК (моб), чьё имя ИЛИ должность содержит query. Для тула go_to_person.</summary>
    private (NetEntity net, string name)? FindNearestPerson(EntityUid self, string query)
    {
        var q = query.Trim().ToLowerInvariant();
        if (q.Length == 0)
            return null;
        var selfPos = _xform.GetMapCoordinates(self);
        EntityUid? best = null;
        var bestName = "";
        var bestDist = float.MaxValue;
        var it = EntityQueryEnumerator<MobStateComponent, MetaDataComponent, TransformComponent>();
        while (it.MoveNext(out var uid, out _, out var meta, out _))
        {
            if (uid == self || string.IsNullOrEmpty(meta.EntityName))
                continue;
            var name = meta.EntityName.ToLowerInvariant();
            var job = JobOf(uid)?.ToLowerInvariant();
            if (!name.Contains(q) && (job == null || !job.Contains(q)))
                continue;
            var pos = _xform.GetMapCoordinates(uid);
            if (pos.MapId != selfPos.MapId)
                continue;
            var dist = (pos.Position - selfPos.Position).Length();
            if (dist < bestDist) { bestDist = dist; best = uid; bestName = meta.EntityName; }
        }
        return best is { } b ? (GetNetEntity(b), bestName) : null;
    }

    /// <summary>Почтальон: пойти к видимому человеку по имени/должности, обходя стены (A* с перепланом-догоном).</summary>
    /// <summary>Пойти к сущности A*-путём с догоном (переплан пока движется). follow=true — держаться рядом, не сбрасывать на прибытии.</summary>
    private void GoToEntity(NetEntity net, float arrive, bool follow)
    {
        EnableLocalControl();
        ClearMovement();                 // сбрасывает прочие интенты (и прошлый _navEntity/_navFollow)
        _navEntity = net;
        _navFollow = follow;
        _navArrive = arrive;
        _navGoalTile = null;
        _navNext = _timing.CurTime;      // немедленный первый переплан в TickNavEntity
    }

    public (string msg, bool err) GoToPerson(string query)
    {
        if (_player.LocalEntity is not { } self || Deleted(self))
            return ("Нет персонажа (не в игре).", true);
        if (string.IsNullOrWhiteSpace(query))
            return ("Кого искать?", true);

        // 1) человек ВИДИМ (в PVS) — идём и догоняем.
        if (FindNearestPerson(self, query) is { } hit)
        {
            GoToEntity(hit.net, 1.4f, follow: false);
            return ($"Иду к «{hit.name}».", false);
        }

        // 2) не виден — берём последнюю позицию из снятого крю-монитора (по всей карте).
        if (CrewTileOf(query, out var gridUid, out var tile, out var who))
        {
            var xform = Transform(self);
            if (xform.GridUid != gridUid || !TryComp<MapGridComponent>(gridUid, out var grid))
                return ($"«{who}» где-то не на твоём гриде — так не дойти.", true);
            EnableLocalControl();
            var start = grid.TileIndicesFor(xform.Coordinates);
            var n = StartPathTo(gridUid, grid, start, tile);
            if (n < 0)
                return ($"Не проложить путь к «{who}» (перекрыто/далеко?).", true);
            return ($"Иду к «{who}» (по крю-монитору, последняя позиция).", false);
        }

        if (_crew.Count == 0)
            return ($"Не вижу «{query}» и не знаю где он — сперва сними крю-монитор (read_crew) у консоли.", true);
        return ($"«{query}» нет в крю-мониторе (сенсор выключен?) и рядом не вижу.", true);
    }

    /// <summary>Найти тайл человека по имени/должности из снятого крю-монитора (_crew). who — реальное имя.</summary>
    private bool CrewTileOf(string query, out EntityUid grid, out Vector2i tile, out string who)
    {
        grid = default; tile = default; who = query;
        var q = query.Trim().ToLowerInvariant();
        if (q.Length == 0)
            return false;
        foreach (var (name, job, coords) in _crew)
        {
            if (!name.Contains(q) && (job.Length == 0 || !job.Contains(q)))
                continue;
            var ec = GetCoordinates(coords);
            if (!Exists(ec.EntityId))
                continue;
            EntityUid g;
            if (HasComp<MapGridComponent>(ec.EntityId)) g = ec.EntityId;
            else if (Transform(ec.EntityId).GridUid is { } gg) g = gg;
            else continue;
            if (!TryComp<MapGridComponent>(g, out var gc))
                continue;
            grid = g; tile = gc.TileIndicesFor(ec); who = name;
            return true;
        }
        return false;
    }

    /// <summary>read_crew: снять снимок позиций экипажа с крю-консоли. Рядом — сразу; далеко — сам подойдёт (nav).</summary>
    public string ReadCrew()
    {
        if (_player.LocalEntity is not { } self || Deleted(self))
            return "Нет персонажа (не в игре).";
        if (FindNearestConsole(self) is not { } hit)
            return "Не вижу крю-монитор-консоль рядом — подойди к ней (медбей/мостик/СБ) и повтори.";
        EnableLocalControl();
        ClearMovement();
        if (hit.dist <= 2.5f)
        {
            OpenCrewConsole(hit.net);
            return "Снимаю крю-монитор…";
        }
        _navEntity = hit.net;
        _navReadCrew = true;
        _navArrive = 1.6f;
        _navGoalTile = null;
        _navNext = _timing.CurTime;
        return "Иду к крю-монитору снять данные экипажа.";
    }

    /// <summary>Ближайшая сущность с UI крю-монитора (консоль) в PVS.</summary>
    private (NetEntity net, float dist)? FindNearestConsole(EntityUid self)
    {
        var selfPos = _xform.GetMapCoordinates(self);
        EntityUid? best = null;
        var bestDist = float.MaxValue;
        var it = EntityQueryEnumerator<UserInterfaceComponent, TransformComponent>();
        while (it.MoveNext(out var uid, out _, out _))
        {
            if (!_ui.HasUi(uid, CrewMonitoringUIKey.Key))
                continue;
            var pos = _xform.GetMapCoordinates(uid);
            if (pos.MapId != selfPos.MapId)
                continue;
            var d = (pos.Position - selfPos.Position).Length();
            if (d < bestDist) { bestDist = d; best = uid; }
        }
        return best is { } b ? (GetNetEntity(b), bestDist) : null;
    }

    /// <summary>Открыть UI крю-консоли и запланировать чтение state (серверу нужен тик прислать данные).</summary>
    private void OpenCrewConsole(NetEntity console)
    {
        if (_player.LocalEntity is not { } self || !TryGetEntity(console, out var ent) || Deleted(ent.Value))
        {
            _conEvents.Enqueue("📋 консоль пропала");
            return;
        }
        _ui.TryOpenUi((ent.Value, null), CrewMonitoringUIKey.Key, self);
        _crewConsole = console;
        _crewReadAt = _timing.CurTime + TimeSpan.FromSeconds(1.2);
        _conEvents.Enqueue("📋 открыл крю-монитор, читаю…");
    }

    /// <summary>Когда UI открыт и state пришёл — считать сенсоры (имя/должность/координаты) в _crew.</summary>
    private void TickCrewRead()
    {
        if (_crewReadAt is not { } due || _timing.CurTime < due)
            return;
        _crewReadAt = null;
        if (_crewConsole is not { } cn || !TryGetEntity(cn, out var ent) || Deleted(ent.Value))
        {
            _conEvents.Enqueue("📋 консоль недоступна");
            return;
        }
        if (!_ui.TryGetUiState<CrewMonitoringState>((ent.Value, null), CrewMonitoringUIKey.Key, out var state))
        {
            _conEvents.Enqueue("📋 крю-монитор не отдал данные (пусто/сенсоры off)");
            return;
        }
        _crew.Clear();
        foreach (var s in state.Sensors)
        {
            if (string.IsNullOrWhiteSpace(s.Name) || s.Coordinates == null)
                continue;
            _crew.Add((s.Name.ToLowerInvariant(), (s.Job ?? "").ToLowerInvariant(), s.Coordinates.Value));
        }
        var names = string.Join(", ", state.Sensors.Where(x => !string.IsNullOrWhiteSpace(x.Name)).Select(x => x.Name).Take(10));
        _conEvents.Enqueue($"📋 снял крю-монитор: {_crew.Count} чел. с координатами ({names})");
    }

    /// <summary>Догон человека A*: троттл-переплан пути к его текущему тайлу; на подходе — «дошёл». Прерывается stop/ClearMovement.</summary>
    private void TickNavEntity()
    {
        if (_navEntity is not { } net)
            return;
        if (!Enabled || _player.LocalEntity is not { } self || Deleted(self)) { _navEntity = null; return; }
        if (!TryGetEntity(net, out var tEnt) || Deleted(tEnt.Value)) { _conEvents.Enqueue("📦 цель пропала"); _navEntity = null; return; }
        if (_timing.CurTime < _navNext)
            return;
        _navNext = _timing.CurTime + TimeSpan.FromSeconds(0.8);

        var selfPos = _xform.GetMapCoordinates(self);
        var tgtPos = _xform.GetMapCoordinates(tEnt.Value);
        if (selfPos.MapId != tgtPos.MapId) { _conEvents.Enqueue("📦 цель не на этой карте"); _navEntity = null; return; }
        if ((tgtPos.Position - selfPos.Position).Length() <= _navArrive)
        {
            _gotoTarget = null; _pathQueue.Clear();
            if (_navReadCrew)
            {
                _navReadCrew = false;
                OpenCrewConsole(net);            // дошли до консоли — снимаем крю-монитор
                _navEntity = null;
            }
            else if (_navFollow)
            {
                // follow: дошли, стоим рядом; _navEntity держим — уйдёт цель, переплан ниже сам догонит.
            }
            else
            {
                _conEvents.Enqueue($"📦 дошёл до {MetaData(tEnt.Value).EntityName}");
                _navEntity = null;
            }
            return;
        }
        var xform = Transform(self);
        if (xform.GridUid is not { } gridUid || !TryComp<MapGridComponent>(gridUid, out var grid))
            return;
        var tgtXform = Transform(tEnt.Value);
        if (tgtXform.GridUid != gridUid) { _gotoTarget = tgtPos; return; }   // на другом гриде — идём в его сторону напрямую

        var goal = grid.TileIndicesFor(tgtXform.Coordinates);
        var moved = _navGoalTile is { } prev && Heur(prev, goal) > 3;
        var needPlan = (_pathQueue.Count == 0 && _gotoTarget == null) || _navGoalTile == null || moved;
        if (!needPlan)
            return;
        var start = grid.TileIndicesFor(xform.Coordinates);
        if (!Walkable(gridUid, grid, goal) && NearestWalkable(gridUid, grid, goal, start) is { } g2)
            goal = g2;
        var path = FindPath(gridUid, grid, start, goal);
        if (path == null)
            return;                       // не проложить сейчас — на следующем троттле
        _pathQueue.Clear();
        foreach (var t in path)
            _pathQueue.Enqueue(_xform.ToMapCoordinates(grid.GridTileToLocal(t)));
        _gotoTarget = _pathQueue.Count > 0 ? _pathQueue.Dequeue() : null;
        _navGoalTile = goal;
    }

    // --- Составной скилл разбора/сборки (C#-автомат, честно для статьи: LLM зовёт один тул, механика детерминирована) ---

    /// <summary>Запустить авто-разбор цели (само-старт вербом «Начать разборку», затем по шагам examine).</summary>
    public string StartDeconstruct(NetEntity target) => StartConstruction(target, ConMode.Deconstruct, "разбор");

    /// <summary>Запустить авто-сборку цели (каркас машины: вести по шагам examine — инструмент/материал/деталь).</summary>
    public string StartConstruct(NetEntity target) => StartConstruction(target, ConMode.Construct, "сборку");

    private string StartConstruction(NetEntity target, ConMode mode, string word)
    {
        if (_player.LocalEntity is not { } self || Deleted(self))
            return "Нет персонажа (не в игре).";
        if (!TryGetEntity(target, out var ent) || Deleted(ent.Value))
            return $"Цель id={target.Id} не найдена.";

        EnableLocalControl();
        ClearMovement();                 // сбрасываем прочие интенты (внутри обнулит и прошлый con-скилл)
        _conMode = mode;
        _conTarget = target;
        // U8: разбор ШЛЮЗА/двери (есть панель проводов) — сперва prep (панель→провода→сварка), затем граф-разбор.
        var airlockPrep = mode == ConMode.Deconstruct && HasComp<WiresPanelComponent>(ent.Value);
        _conPhase = airlockPrep ? ConPhase.PrepWires : ConPhase.Approach;
        _conWireStarted = false;
        _conNext = _timing.CurTime;
        _conStartTime = _timing.CurTime;
        _conRetry = 0; _conPoll = 0; _conStep = 0;
        _conExamIssued = false; _conExpectChange = false;
        _conHint = null; _conToolSub = null; _conIsTool = false; _conWeldOn = false;
        _conReqMode = false; _conReqStuck = 0;
        _conExamText = null; _conExaminedNet = null;
        _conVerbTarget = null; _conVerbs.Clear();
        var name = MetaData(ent.Value).EntityName;
        var pre = airlockPrep ? " (сначала вскрою панель, перережу провода, заварю)" : "";
        return $"ok: начал {word} «{name}» (id={target.Id}){pre} — веду автоматически, stop чтобы прервать.";
    }

    /// <summary>Слить накопленные статус-сообщения скилла (для MCP-тула events).</summary>
    public List<string> DrainConEvents()
    {
        var list = new List<string>();
        while (_conEvents.Count > 0)
            list.Add(_conEvents.Dequeue());
        return list;
    }

    /// <summary>
    ///     Обёртка автомата: НИКАКОЕ исключение внутри скилла не должно ронять клиент. FrameUpdate дёргает
    ///     сетевые вызовы (examine/verbs), и повторяющийся throw каждый кадр съедал бы EXCEPTION_TOLERANCE
    ///     до вылета. При сбое — гасим скилл (сварка off, интенты сброшены) и сообщаем в события.
    /// </summary>
    private void TickConstruction()
    {
        try
        {
            TickConstructionCore();
        }
        catch (Exception e)
        {
            FinishCon($"сбой скилла: {e.Message} — прерываю");
        }
    }

    /// <summary>Пошаговый автомат разбора/сборки — тикается из FrameUpdate, неблокирующий.</summary>
    private void TickConstructionCore()
    {
        if (_conMode == ConMode.None)
            return;
        if (!Enabled || _player.LocalEntity is not { } self || Deleted(self))
        {
            _conMode = ConMode.None;
            return;
        }
        if (_timing.CurTime < _conNext)
            return;
        if (_timing.CurTime - _conStartTime > TimeSpan.FromMinutes(5))
        {
            FinishCon("таймаут скилла (5 мин) — прерываю");
            return;
        }

        var target = _conTarget!.Value;
        var alive = TryGetEntity(target, out var tEnt) && !Deleted(tEnt.Value);

        switch (_conPhase)
        {
            // --- U8: prep шлюза перед граф-разбором: панель отвёрткой → все провода кусачками → заварить дверь ---
            case ConPhase.PrepWires:
            {
                if (!alive) { FinishCon("цель исчезла"); return; }
                if (!_conWireStarted)
                {
                    // Запускаем резку проводов НАПРЯМУЮ (не через StartCutWires — он сбросил бы con-скилл).
                    _wireTarget = target;
                    _wirePhase = WirePhase.Panel;
                    _wireNext = _timing.CurTime; _wireStart = _timing.CurTime;
                    _wireRetry = 0; _wireStuck = 0; _wirePrevUncut = -1;
                    _conWireStarted = true;
                    _conEvents.Enqueue("🚪 подготовка шлюза: вскрываю панель и режу провода");
                    _conNext = _timing.CurTime + TimeSpan.FromSeconds(1.0);
                    return;
                }
                if (_wireTarget != null)                       // ждём, пока TickWireCut закончит (обнулит _wireTarget)
                {
                    _conNext = _timing.CurTime + TimeSpan.FromSeconds(0.5);
                    return;
                }
                _conRetry = 0;
                _conPhase = ConPhase.PrepWeldGet;
                _conNext = _timing.CurTime;
                break;
            }
            case ConPhase.PrepWeldGet:
            {
                if (!alive) { FinishCon("цель исчезла — ГОТОВО ✅"); return; }
                if (!EnsureHeldTool(self, "сварочн"))
                {
                    if (_conRetry++ > 8)                       // нет сварочника — пропускаем сварку, идём разбирать
                    {
                        _conEvents.Enqueue("🚪 без сварки (нет сварочника) — перехожу к разбору");
                        _conPhase = ConPhase.Approach; _conNext = _timing.CurTime;
                        break;
                    }
                    _conNext = _timing.CurTime + TimeSpan.FromSeconds(1.0);
                    return;
                }
                _conRetry = 0;
                _conPhase = ConPhase.PrepWeldOn;
                _conNext = _timing.CurTime;
                break;
            }
            case ConPhase.PrepWeldOn:
            {
                if (!_conWeldOn)
                {
                    _pending.Enqueue((ContentKeyFunctions.UseItemInHand, null, false)); // зажечь сварку
                    _conWeldOn = true;
                    _conNext = _timing.CurTime + TimeSpan.FromSeconds(0.6);
                    return;
                }
                _conPhase = ConPhase.PrepWeldUse;
                _conNext = _timing.CurTime;
                break;
            }
            case ConPhase.PrepWeldUse:
            {
                if (!alive) { WeldOff(); FinishCon("цель исчезла — ГОТОВО ✅"); return; }
                _useOnTarget = target;                         // заварить дверь (сварочник по двери)
                _conEvents.Enqueue("🚪 завариваю дверь");
                _conPhase = ConPhase.PrepWeldOff;
                _conNext = _timing.CurTime + TimeSpan.FromSeconds(2.6);
                break;
            }
            case ConPhase.PrepWeldOff:
            {
                WeldOff();                                     // потушить сварку (экономия топлива)
                _conEvents.Enqueue("🚪 подготовка готова — начинаю разбор");
                _conPhase = ConPhase.Approach;
                _conNext = _timing.CurTime + TimeSpan.FromSeconds(0.4);
                break;
            }
            case ConPhase.Approach:
            {
                if (!alive) { FinishCon("цель исчезла до подхода"); return; }
                var d = (_xform.GetMapCoordinates(tEnt!.Value).Position - _xform.GetMapCoordinates(self).Position).Length();
                if (d <= 1.7f)
                {
                    _moveToTarget = null;
                    _conPhase = _conMode == ConMode.Deconstruct ? ConPhase.KickVerb : ConPhase.Exam;
                    _conNext = _timing.CurTime + TimeSpan.FromSeconds(0.3);
                }
                else
                {
                    _moveToTarget = target;
                    _conNext = _timing.CurTime + TimeSpan.FromSeconds(1.0);
                }
                break;
            }
            case ConPhase.KickVerb:
            {
                if (!alive) { FinishCon("цель исчезла"); return; }
                // 1-й вызов: локальные пункты + запрос серверу (серверные добьются в OnConVerbsResponse).
                _conVerbTarget = target;
                _conVerbs.Clear();
                foreach (var v in _verbs.GetVerbs(target, self, Verb.VerbTypes, out _, force: false))
                    if (_conVerbs.All(x => x.Text != v.Text))
                        _conVerbs.Add(v);
                _conPhase = ConPhase.StartVerb;
                _conNext = _timing.CurTime + TimeSpan.FromSeconds(0.9);
                break;
            }
            case ConPhase.StartVerb:
            {
                // серверный пункт «Начать разборку» приходит только ко 2-му моменту — теперь он в _conVerbs.
                var vb = _conVerbs.FirstOrDefault(v => !v.Disabled && v.Text.ToLowerInvariant().Contains("разбор"));
                if (vb != null)
                    _verbs.ExecuteVerb(target, vb);
                _conPhase = ConPhase.Exam;
                _conExpectChange = false; _conExamIssued = false; _conRetry = 0;
                _conNext = _timing.CurTime + TimeSpan.FromSeconds(1.2);
                break;
            }
            case ConPhase.Exam:
            {
                if (!alive) { WeldOff(); FinishCon("цель разобрана — ГОТОВО ✅"); return; }
                if (!_conExamIssued)
                {
                    _conExamText = null; _conExaminedNet = null;
                    _examine.DoExamine(tEnt!.Value);
                    _conExamIssued = true; _conRetry = 0;
                    _conNext = _timing.CurTime + TimeSpan.FromSeconds(1.4);
                    return;
                }
                var ex = _conExaminedNet == target ? _conExamText : null;
                if (ex == null)
                {
                    if (_conRetry++ < 4)                    // кеш чередует full/short — ретраим
                    {
                        _examine.DoExamine(tEnt!.Value);
                        _conNext = _timing.CurTime + TimeSpan.FromSeconds(1.3);
                        return;
                    }
                    if (_conExpectChange && _conPoll++ < 12) // во время ожидания шага не сдаёмся
                    {
                        _conExamIssued = false;
                        _conNext = _timing.CurTime + TimeSpan.FromSeconds(1.2);
                        return;
                    }
                    WeldOff(); FinishCon("не удалось прочитать осмотр цели"); return;
                }

                var step = ParseConStep(ex);              // сайд-эффект: ставит _conReqMode для пунктов «Требования:»
                if (_conExpectChange)
                {
                    if (_conReqMode)
                    {
                        // Машинный каркас: вставляем компоненты по одному, порядок неважен, examine показывает
                        // ОСТАТОК. Тот же элемент первый = ещё не довставили (у компонентов счётчик не убывает) →
                        // вставляем снова. Прогресс-гард: если тот же элемент не ушёл за N use_on — предмета нет/не лезет.
                        if (step != null && step.Value.hint == _conHint)
                        {
                            if (++_conReqStuck > 8)
                            {
                                WeldOff();
                                FinishCon($"не могу вставить «{_conHint}» — нет предмета или не лезет");
                                return;
                            }
                        }
                        else
                        {
                            _conReqStuck = 0;             // элемент сменился/ушёл — прогресс
                        }
                        WeldOff();                        // (сварки в req-режиме нет, но на всякий)
                        // НЕ ждём «смены» — сразу действуем по текущему первому требованию ниже.
                    }
                    else if (step != null && step.Value.hint == _conHint) // обычный (инструмент): тот же шаг ещё идёт (doAfter)
                    {
                        if (_conPoll++ < 12)
                        {
                            _conExamIssued = false;
                            _conNext = _timing.CurTime + TimeSpan.FromSeconds(1.6);
                            return;
                        }
                        WeldOff(); FinishCon($"шаг «{_conHint}» не завершился за ~20с — прерываю"); return;
                    }
                    else
                    {
                        WeldOff();                        // шаг сменился/закончился — гасим сварку
                    }
                }

                if (step == null)
                {
                    // Первый шаг может не сразу отразиться в examine (верб «Начать разборку» применяется
                    // на сервере с задержкой) — даём несколько перечитываний, прежде чем сдаться.
                    if (_conStep == 0 && !_conExpectChange && _conPoll++ < 4)
                    {
                        _conExamIssued = false;
                        _conNext = _timing.CurTime + TimeSpan.FromSeconds(1.3);
                        return;
                    }
                    FinishCon(_conStep == 0
                        ? "нет активного шага (цель не в состоянии разбора/сборки или нужен рецепт)"
                        : "шагов больше нет — ГОТОВО ✅");
                    return;
                }

                var newElement = step.Value.hint != _conHint;      // сменился ли элемент/шаг (для события, чтобы не спамить)
                _conHint = step.Value.hint;
                _conToolSub = step.Value.sub;
                _conIsTool = step.Value.isTool;
                if (++_conStep > 40) { FinishCon("слишком много шагов (>40) — прерываю"); return; }
                if (newElement)
                    _conEvents.Enqueue($"🔧 шаг {_conStep}: {_conHint}");
                _conPhase = ConPhase.Acquire;
                _conExamIssued = false; _conExpectChange = false; _conRetry = 0; _conPoll = 0;
                _conNext = _timing.CurTime;
                break;
            }
            case ConPhase.Acquire:
            {
                // Приоритет «сделать нужный инструмент активным» для КПБ/борга (инструменты в руках/поясе, не на полу):
                // 1) уже в активной руке.
                if (HeldNameContains(self, _conToolSub!))
                {
                    _conPhase = ConPhase.WeldOn; _conNext = _timing.CurTime; break;
                }
                // 2) в ДРУГОЙ руке (борг: модуль) → просто сделать ту руку активной, без drop/pickup.
                if (TrySwitchToHandWith(self, _conToolSub!))
                {
                    _conWeldOn = false;                       // сменили руку — «наша» сварка (если была) уже не в активной
                    _conPhase = ConPhase.WeldOn;
                    _conNext = _timing.CurTime + TimeSpan.FromSeconds(0.3);
                    break;
                }
                // 3) в поясе/рюкзаке → освободить руку (не роняя модули) и достать в руку (pickup из контейнера работает).
                if (FindInInventory(self, _conToolSub!) is { } invNet)
                {
                    FreeActiveHand(self);
                    _pickupTarget = invNet;
                    _conRetry = 0;
                    _conPhase = ConPhase.AcquireWait;
                    _conNext = _timing.CurTime + TimeSpan.FromSeconds(0.9);
                    break;
                }
                // 4) ничего в руках/поясе — ищем на ПОЛУ (последний путь).
                FreeActiveHand(self);
                _conPhase = ConPhase.AcquirePick;
                _conNext = _timing.CurTime + TimeSpan.FromSeconds(0.7);
                break;
            }
            case ConPhase.AcquirePick:
            {
                if (FindNearestByName(self, _conToolSub!) is not { } hit)
                {
                    FinishCon($"для шага нужен «{_conHint}» ({_conToolSub}) — нет ни в руках, ни в поясе, ни рядом");
                    return;
                }
                _pickupTarget = hit.net;
                _conRetry = 0;
                _conPhase = ConPhase.AcquireWait;
                _conNext = _timing.CurTime + TimeSpan.FromSeconds(1.2);
                break;
            }
            case ConPhase.AcquireWait:
            {
                if (HeldNameContains(self, _conToolSub!))
                {
                    _conPhase = ConPhase.WeldOn; _conNext = _timing.CurTime; break;
                }
                if (_conRetry++ < 6)
                {
                    if (_pickupTarget == null)               // интеракт был, но не в руке — повторим захват (пояс/пол)
                        _pickupTarget = FindInInventory(self, _conToolSub!)
                                        ?? (FindNearestByName(self, _conToolSub!)?.net);
                    _conNext = _timing.CurTime + TimeSpan.FromSeconds(1.2);
                    return;
                }
                FinishCon($"не смог взять «{_conHint}» ({_conToolSub})");
                return;
            }
            case ConPhase.WeldOn:
            {
                if (_conIsTool && _conToolSub == "сварочн" && !_conWeldOn)
                {
                    _pending.Enqueue((ContentKeyFunctions.UseItemInHand, null, false)); // зажечь сварку
                    _conWeldOn = true;
                    _conNext = _timing.CurTime + TimeSpan.FromSeconds(0.6);
                    return;                                   // дать сварке зажечься перед применением
                }
                _conPhase = ConPhase.UseStep;
                _conNext = _timing.CurTime + TimeSpan.FromSeconds(0.1);
                break;
            }
            case ConPhase.UseStep:
            {
                if (!alive) { WeldOff(); FinishCon("цель разобрана — ГОТОВО ✅"); return; }
                _useOnTarget = target;                        // подойти и применить предмет из руки на цели
                _conExpectChange = true;                      // дальше ждём смену/исчезновение шага
                _conExamIssued = false; _conRetry = 0; _conPoll = 0;
                _conPhase = ConPhase.Exam;
                _conNext = _timing.CurTime + TimeSpan.FromSeconds(2.5); // дать use_on стартовать doAfter
                break;
            }
        }
    }

    /// <summary>Разобрать шаг из текста examine: инструмент (использ.) / материал (добав.) / деталь (встав.).</summary>
    private (string hint, string sub, bool isTool)? ParseConStep(string text)
    {
        _conReqMode = false;                          // сбрасываем; ставим true только если это пункт «Требования:»
        var m = ConUseToolRx.Match(text);
        if (m.Success)
        {
            var h = m.Groups[1].Value.Trim();
            // «Используйте <настоящий инструмент>» — это инструмент. А «Используйте любую плату для машины»,
            // «...любой стек стали» и т.п. по факту ВСТАВКА детали/материала (глагол «используйте», но не инструмент).
            if (ConToolSubKnown(h) is { } ts)
                return (h, ts, true);
            return (h, ConItemSub(h), false);
        }
        // «Сначала закрепите/открепите это на месте» — anchor/unanchor гаечным ключом (без формата «используйте …»).
        if (ConAnchorRx.IsMatch(text))
            return ("закрепить/открепить (гаечный ключ)", "гаечн", true);
        if (_conMode == ConMode.Construct)
        {
            // Список «Требования:» машинного каркаса — берём ПЕРВЫЙ остаток (порядок вставки неважен).
            var mr = ConReqEntryRx.Match(text);
            if (mr.Success)
            {
                var elem = mr.Groups[2].Value.Trim();
                _conReqMode = true;
                return (elem, ConItemSub(elem), false);
            }
            var mm = ConAddMatRx.Match(text);
            if (mm.Success)
            {
                var h = mm.Groups[1].Value.Trim();
                return (h, ConItemSub(h), false);
            }
            var mi = ConInsertRx.Match(text);
            if (mi.Success)
            {
                var h = mi.Groups[1].Value.Trim();
                return (h, ConItemSub(h), false);
            }
        }
        return null;
    }

    /// <summary>Подстрока инструмента, если hint — НАСТОЯЩИЙ инструмент (иначе null → это деталь/материал).</summary>
    private static string? ConToolSubKnown(string ru)
    {
        var low = ru.ToLowerInvariant();
        foreach (var (k, s) in ConToolSubs)
            if (low.Contains(k))
                return s;
        return null;
    }

    private static string ConToolSub(string ru)
    {
        var low = ru.ToLowerInvariant();
        return ConToolSubKnown(ru) ?? (low.Length >= 5 ? low[..5] : low);
    }

    private static string ConItemSub(string ru)
    {
        var low = ru.Trim().ToLowerInvariant();
        // Кабель бывает НВ/СВ/ВВ, в мире это «Моток … кабеля» — брать нужно строго по СОКРАЩЕНИЮ
        // напряжения, иначе схватит не тот кабель. Возвращаем AND-подстроки через '+': «кабел»+метка.
        if (low.Contains("кабел") || low.Contains("провод") || low.Contains("cable"))
        {
            foreach (var t in new[] { "нв", "св", "вв" })
                if (Regex.IsMatch(low, $@"(?<![а-яё]){t}(?![а-яё])"))   // метка как отдельный токен, не внутри слова
                    return "кабел+" + t;
            if (Regex.IsMatch(low, @"(?<![a-z])lv(?![a-z])")) return "кабел+нв";
            if (Regex.IsMatch(low, @"(?<![a-z])mv(?![a-z])")) return "кабел+св";
            if (Regex.IsMatch(low, @"(?<![a-z])hv(?![a-z])")) return "кабел+вв";
            return "кабел";   // тип не указан — любой моток кабеля
        }
        // Частые детали машин (в т.ч. англ. имя в рецепте vs рус. имя предмета). «любую плату» → любая плата в руке/рядом.
        if (low.Contains("плат")) return "плат";                                       // любая машинная плата / плата для машины
        if (low.Contains("манипул") || low.Contains("manipulator")) return "манипул";  // микроманипулятор
        if (low.Contains("конденсат") || low.Contains("capacitor")) return "конденсат";
        // Общий случай: пропустить служебные слова/числа («любую», «для», «5», «ед») и взять значимое слово (стем 4).
        foreach (var w in low.Split(new[] { ' ', ',', ';', '.', '\t', '(', ')' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (w.Length < 3 || ConFillerWords.Contains(w) || w.All(char.IsDigit))
                continue;
            return w.Length >= 4 ? w[..4] : w;
        }
        return low.Length >= 4 ? low[..4] : low;
    }

    private static readonly string[] ConFillerWords =
        { "любую", "любой", "любое", "любые", "люба", "для", "ед", "единиц", "штук", "шт", "штуки", "как", "минимум" };

    /// <summary>Имя сущности содержит подстроку (или ВСЕ подстроки, если заданы через '+').</summary>
    private bool NameMatches(EntityUid ent, string sub)
    {
        if (string.IsNullOrEmpty(sub) || Deleted(ent))
            return false;
        var low = MetaData(ent).EntityName.ToLowerInvariant();
        foreach (var p in sub.ToLowerInvariant().Split('+', StringSplitOptions.RemoveEmptyEntries))
            if (!low.Contains(p.Trim()))
                return false;
        return true;
    }

    /// <summary>Нужный предмет в АКТИВНОЙ руке?</summary>
    private bool HeldNameContains(EntityUid self, string sub)
        => _hands.GetActiveItem(self) is { } item && NameMatches(item, sub);

    /// <summary>Нужный предмет в ДРУГОЙ руке (борг: модуль) → сделать ту руку активной. true = переключились.</summary>
    private bool TrySwitchToHandWith(EntityUid self, string sub)
    {
        if (!TryComp<HandsComponent>(self, out var hands))
            return false;
        var active = _hands.GetActiveHand((self, hands));
        foreach (var hand in _hands.EnumerateHands((self, hands)))
        {
            if (hand == active)
                continue;
            if (_hands.TryGetHeldItem((self, hands), hand, out var held) && NameMatches(held.Value, sub))
                return _hands.TrySetActiveHand((self, hands), hand);
        }
        return false;
    }

    /// <summary>Нужный предмет в поясе/рюкзаке/костюме → его NetEntity (pickup по id достаёт из контейнера).</summary>
    private NetEntity? FindInInventory(EntityUid self, string sub)
    {
        foreach (var slot in new[] { "belt", "back", "suitstorage" })
        {
            if (!_inventory.TryGetSlotEntity(self, slot, out var cont) || cont is not { } c || Deleted(c))
                continue;
            if (!_container.TryGetContainer(c, StorageComponent.ContainerId, out var storage))
                continue;
            foreach (var item in storage.ContainedEntities)
                if (NameMatches(item, sub))
                    return GetNetEntity(item);
        }
        return null;
    }

    /// <summary>Освободить активную руку под новый предмет, НЕ роняя модули: пустая рука → пояс → (крайне) drop.</summary>
    private void FreeActiveHand(EntityUid self)
    {
        WeldOff();                                        // сперва погасить сварку, если жгли
        if (_hands.GetActiveItem(self) is not { } cur || Deleted(cur))
            return;                                       // рука уже пуста
        if (!TryComp<HandsComponent>(self, out var hands))
            return;
        // 1) есть пустая рука → просто переключиться на неё (текущий предмет остаётся в своей руке).
        var active = _hands.GetActiveHand((self, hands));
        foreach (var hand in _hands.EnumerateHands((self, hands)))
        {
            if (hand == active)
                continue;
            if (!_hands.TryGetHeldItem((self, hands), hand, out _))
            {
                _hands.TrySetActiveHand((self, hands), hand);
                return;
            }
        }
        // 2) есть пояс → стожить активный предмет в пояс (не ронять).
        if (_inventory.TryGetSlotEntity(self, "belt", out var belt) && belt is { } b && !Deleted(b))
        {
            _pending.Enqueue((ContentKeyFunctions.SmartEquipBelt, null, false));
            return;
        }
        // 3) крайний случай — уронить на пол.
        _pending.Enqueue((ContentKeyFunctions.Drop, null, false));
    }

    /// <summary>Потушить сварку, если мы её зажигали (экономия топлива; без рекурсии — прямо в очередь).</summary>
    private void WeldOff()
    {
        if (!_conWeldOn)
            return;
        _pending.Enqueue((ContentKeyFunctions.UseItemInHand, null, false));
        _conWeldOn = false;
    }

    /// <summary>Завершить/прервать скилл: погасить сварку, сбросить интенты, отдать статус раннеру.</summary>
    private void FinishCon(string reason)
    {
        WeldOff();
        _conMode = ConMode.None;
        _conTarget = null;
        _moveToTarget = null;
        _useOnTarget = null;
        _pickupTarget = null;
        _conEvents.Enqueue($"🏗 {reason}");
    }

    /// <summary>Тихо отменить скилл при перехвате другой командой (без статус-строки «готово»).</summary>
    private void CancelConstruction()
    {
        if (_conMode == ConMode.None)
            return;
        WeldOff();
        _conMode = ConMode.None;
        _conTarget = null;
    }

    // --- Хакинг проводов: перерезать все провода в панели цели (сетевым WiresActionMessage, без визуального UI) ---

    /// <summary>Запустить авто-резку проводов цели (открыть панель отвёрткой → кусачки → резать по одному).</summary>
    public string StartCutWires(NetEntity target)
    {
        if (_player.LocalEntity is not { } self || Deleted(self))
            return "Нет персонажа (не в игре).";
        if (!TryGetEntity(target, out var ent) || Deleted(ent.Value))
            return $"Цель id={target.Id} не найдена.";
        if (!HasComp<WiresPanelComponent>(ent.Value))
            return $"У «{MetaData(ent.Value).EntityName}» нет панели проводов.";

        EnableLocalControl();
        ClearMovement();                     // сбрасываем прочие интенты (внутри обнулит и прошлую резку)
        _wireTarget = target;
        _wirePhase = WirePhase.Panel;
        _wireNext = _timing.CurTime;
        _wireStart = _timing.CurTime;
        _wireRetry = 0; _wireStuck = 0; _wirePrevUncut = -1;
        return $"ok: режу провода «{MetaData(ent.Value).EntityName}» (id={target.Id}) — открою панель, возьму кусачки, перережу по одному. stop прервёт.";
    }

    /// <summary>Инструмент по подстроке в АКТИВНОЙ руке: true = уже там; иначе инициирует добычу (рука/пояс/пол) и false.</summary>
    private bool EnsureHeldTool(EntityUid self, string sub)
    {
        if (HeldNameContains(self, sub))
            return true;
        if (TrySwitchToHandWith(self, sub))         // в другой руке — переключаемся (мгновенно)
            return true;
        if (_pickupTarget == null)                  // достать из пояса/рюкзака/с пола
        {
            FreeActiveHand(self);
            _pickupTarget = FindInInventory(self, sub) ?? (FindNearestByName(self, sub)?.net);
        }
        return false;
    }

    /// <summary>Автомат резки проводов — тикается из FrameUpdate, неблокирующий, прерывается stop.</summary>
    private void TickWireCut()
    {
        if (_wireTarget is not { } target)
            return;
        if (!Enabled || _player.LocalEntity is not { } self || Deleted(self))
        {
            _wireTarget = null;
            return;
        }
        if (_timing.CurTime < _wireNext)
            return;
        if (_timing.CurTime - _wireStart > TimeSpan.FromMinutes(3))
        {
            FinishWire("таймаут резки (3 мин) — прерываю");
            return;
        }
        if (!TryGetEntity(target, out var ent) || Deleted(ent.Value))
        {
            FinishWire("цель исчезла");
            return;
        }

        switch (_wirePhase)
        {
            case WirePhase.Panel:
            {
                if (!HasComp<WiresPanelComponent>(ent.Value)) { FinishWire("у цели нет панели проводов"); return; }
                if (_wires.IsPanelOpen(ent.Value)) { _wirePhase = WirePhase.Cutters; _wireNext = _timing.CurTime; break; }
                // панель закрыта → нужна отвёртка, вскрыть
                if (!EnsureHeldTool(self, "отвёрт"))
                {
                    if (_wireRetry++ > 8) { FinishWire("нет отвёртки — нечем открыть панель"); return; }
                    _wireNext = _timing.CurTime + TimeSpan.FromSeconds(1.0);
                    return;
                }
                _wireRetry = 0;
                _useOnTarget = target;                       // отвёртка тогглит панель (откроет)
                _wirePhase = WirePhase.PanelWait;
                _wireNext = _timing.CurTime + TimeSpan.FromSeconds(1.6);
                break;
            }
            case WirePhase.PanelWait:
            {
                if (_wires.IsPanelOpen(ent.Value)) { _wireRetry = 0; _wirePhase = WirePhase.Cutters; _wireNext = _timing.CurTime; break; }
                if (_wireRetry++ < 6) { _useOnTarget = target; _wireNext = _timing.CurTime + TimeSpan.FromSeconds(1.6); return; }
                FinishWire("не смог открыть панель отвёрткой");
                return;
            }
            case WirePhase.Cutters:
            {
                if (!EnsureHeldTool(self, "кусач"))
                {
                    if (_wireRetry++ > 8) { FinishWire("нет кусачек — нечем резать провода"); return; }
                    _wireNext = _timing.CurTime + TimeSpan.FromSeconds(1.0);
                    return;
                }
                _wireRetry = 0;
                _wirePhase = WirePhase.OpenUi;
                _wireNext = _timing.CurTime;
                break;
            }
            case WirePhase.OpenUi:
            {
                _ui.TryOpenUi(ent.Value, WiresUiKey.Key, self);   // открыть панель проводов (сервер пришлёт state)
                _wirePhase = WirePhase.Cut;
                _wireRetry = 0;
                _wireNext = _timing.CurTime + TimeSpan.FromSeconds(1.3);
                break;
            }
            case WirePhase.Cut:
            {
                if (!_ui.TryGetUiState<WiresBoundUserInterfaceState>(ent.Value, WiresUiKey.Key, out var state))
                {
                    if (_wireRetry++ < 5)
                    {
                        _ui.TryOpenUi(ent.Value, WiresUiKey.Key, self);
                        _wireNext = _timing.CurTime + TimeSpan.FromSeconds(1.3);
                        return;
                    }
                    FinishWire("не прочитал провода (панель закрыта или UI не открылся)");
                    return;
                }
                var uncut = state.WiresList.Count(w => !w.IsCut);
                if (uncut == 0) { FinishWire("перерезал все провода ✅"); return; }
                // прогресс-гард: если число целых проводов не падает — режется не тем/нет доступа
                if (_wirePrevUncut >= 0 && uncut >= _wirePrevUncut)
                {
                    if (++_wireStuck > 6) { FinishWire("провод не режется (кусачки в руке? панель открыта?)"); return; }
                }
                else
                {
                    _wireStuck = 0;
                }
                _wirePrevUncut = uncut;
                if (!HeldNameContains(self, "кусач")) { _wirePhase = WirePhase.Cutters; _wireNext = _timing.CurTime; break; }

                var wire = state.WiresList.FirstOrDefault(w => !w.IsCut);
                if (wire != null && _ui.TryGetOpenUi<WiresBoundUserInterface>(ent.Value, WiresUiKey.Key, out var bui))
                {
                    bui.PerformAction(wire.Id, WiresAction.Cut);
                    _conEvents.Enqueue($"✂ режу провод (осталось {uncut})");
                }
                else
                {
                    _ui.TryOpenUi(ent.Value, WiresUiKey.Key, self);   // BUI закрылся — переоткрыть
                }
                _wireNext = _timing.CurTime + TimeSpan.FromSeconds(2.2);  // ждём doAfter резки
                break;
            }
        }
    }

    /// <summary>Завершить/прервать резку проводов: сбросить интенты, отдать статус раннеру.</summary>
    private void FinishWire(string reason)
    {
        if (_wireTarget == null)
            return;
        _wireTarget = null;
        _useOnTarget = null;
        _pickupTarget = null;
        _conEvents.Enqueue($"🔌 {reason}");
    }

    // --- Навигация: память мест + A*-путь по сетке (Фича 3) ---

    /// <summary>Запомнить текущую позицию борга под именем (ТОЛЬКО на сессию — на void каждый раунд другая станция).</summary>
    public string RememberPlace(string name)
    {
        if (_player.LocalEntity is not { } self || Deleted(self))
            return "Нет персонажа (не в игре).";
        var key = name.Trim().ToLowerInvariant();
        if (key.Length == 0)
            return "Пустое имя места.";
        var xform = Transform(self);
        if (xform.GridUid is { } gridUid && TryComp<MapGridComponent>(gridUid, out var grid))
        {
            var tile = grid.TileIndicesFor(xform.Coordinates);
            foreach (var (existing, t) in _savedTiles)
                if (existing != key && Heur(t, tile) <= 4)
                    return $"Тут в двух шагах уже запомнено место «{existing}» — не дублируй. Иди дальше (explore).";
            _places[key] = xform.Coordinates;
            _savedTiles[key] = tile;
            return $"Запомнил место «{name}».";
        }
        _places[key] = xform.Coordinates;
        return $"Запомнил место «{name}» (ты не на станции).";
    }

    /// <summary>Список всех известных мест (запомненные + отделы карты). Всё сессионное.</summary>
    public string ListPlaces()
    {
        var names = new SortedSet<string>(_places.Keys);
        foreach (var k in _savedTiles.Keys) names.Add(k);
        foreach (var k in _mapTiles.Keys) names.Add(k);
        return names.Count == 0
            ? "Мест пока не запомнено."
            : $"Известные места ({names.Count}): " + string.Join(", ", names);
    }

    // --- Карта станции: отделы из NavMap-маяков (для go_to_place/wander). ТОЛЬКО в памяти сессии, накапливается. ---

    /// <summary>Прочитать/добрать отделы с внутриигровой карты (маяки NavMapComponent). Накапливает — стрим дотекает.</summary>
    public string ReadMap()
    {
        if (_player.LocalEntity is not { } self || Deleted(self))
            return "Нет персонажа (не в игре).";
        var xform = Transform(self);
        if (xform.GridUid is not { } gridUid || !TryComp<MapGridComponent>(gridUid, out var grid))
            return "Ты не на станции (в космосе/на шаттле?) — карты нет.";
        MergeBeacons(gridUid, grid);
        if (_mapTiles.Count == 0)
            return "Карта станции пуста/ещё не дотекла — подожди пару секунд и повтори (или подойди к настенной карте).";
        var names = string.Join(", ", _mapTiles.Keys.OrderBy(k => k));
        return $"Карта станции: {_mapTiles.Count} отделов — {names}. Могу идти в любой: go_to_place «имя».";
    }

    /// <summary>Домешать текущие маяки NavMap в _mapTiles (не затирая — стрим приходит дельтами).</summary>
    private void MergeBeacons(EntityUid gridUid, MapGridComponent grid)
    {
        if (!TryComp<NavMapComponent>(gridUid, out var navmap))
            return;
        foreach (var beacon in navmap.Beacons.Values)
        {
            var text = beacon.Text?.Trim();
            if (string.IsNullOrEmpty(text))
                continue;
            _mapTiles[text.ToLowerInvariant()] = grid.TileIndicesFor(new EntityCoordinates(gridUid, beacon.Position));
        }
    }

    // --- Короткие ХЭНДЛЫ (1,2,3…) вместо гигантских net-id: экономия токенов + 8B не выдумывает id ---

    /// <summary>Сбросить карту хэндлов — зовётся в начале observe, чтобы номера были уникальны 1..N за вызов.</summary>
    public void ClearHandles()
    {
        _handles.Clear();
        _handleSeq = 0;
    }

    /// <summary>Присвоить сущности короткий хэндл и запомнить (уникален в пределах одного observe).</summary>
    public int Handle(NetEntity net)
    {
        var h = ++_handleSeq;
        _handles[h] = net;
        return h;
    }

    /// <summary>Число из тула → реальный NetEntity: короткий хэндл из последнего observe/listen, иначе сырой net-id.</summary>
    public NetEntity ResolveHandle(int n) => _handles.TryGetValue(n, out var net) ? net : new NetEntity(n);

    /// <summary>Сессия раунда: смена грида (новый раунд на void) → сброс мест/карты/крю/хэндлов; авто-мёрж маяков карты.</summary>
    private void TickSession(EntityUid self)
    {
        // Маска коллизии пилотируемой сущности → A* по ней (дрон под столами, борг обходит). 0 = ещё не пришли фикстуры.
        _selfMask = TryComp<PhysicsComponent>(self, out var body) && body.CollisionMask != 0
            ? (CollisionGroup) body.CollisionMask
            : NavBlockMask;

        var grid = Transform(self).GridUid;
        if (grid != _lastGrid)
        {
            _lastGrid = grid;
            _places.Clear(); _savedTiles.Clear(); _mapTiles.Clear();
            _crew.Clear(); _handles.Clear(); _handleSeq = 0;
            _mapAutoNext = _timing.CurTime;
        }
        if (grid is { } g && _timing.CurTime >= _mapAutoNext)
        {
            _mapAutoNext = _timing.CurTime + TimeSpan.FromSeconds(10);
            if (TryComp<MapGridComponent>(g, out var mg))
                MergeBeacons(g, mg);         // тихо добираем отделы, пока стрим NavMap дотекает
        }
    }

    /// <summary>Патруль: идти к следующему отделу карты по кругу. Уже в пути — не перебивать (раннер зовёт на интервале).</summary>
    public (string msg, bool err) Wander()
    {
        if (_player.LocalEntity is not { } self || Deleted(self))
            return ("Нет персонажа (не в игре).", true);
        // Занят задачей или уже в пути — не перебиваем (раннер зовёт wander на интервале, это безопасно).
        if (_pathQueue.Count > 0 || _gotoTarget != null)
            return ("Иду по маршруту, продолжаю.", false);
        if (_mineLoop || _conMode != ConMode.None || _wireTarget != null
            || _attackTarget != null || _pickupTarget != null || _useOnTarget != null
            || _moveToTarget != null || _followTarget != null)
            return ("Занят делом — не патрулирую.", false);

        if (_mapTiles.Count == 0)
        {
            var rm = ReadMap();                 // попробовать прочитать карту прямо сейчас
            if (_mapTiles.Count == 0)
                return ($"Патрулировать негде: {rm}", true);
        }

        var xform = Transform(self);
        if (xform.GridUid is not { } gridUid || !TryComp<MapGridComponent>(gridUid, out var grid))
            return ("Ты не на станции — патруль невозможен.", true);
        var start = grid.TileIndicesFor(xform.Coordinates);

        // Жадный обход: идём к БЛИЖАЙШЕМУ ещё не посещённому отделу (а не по алфавиту через всю станцию).
        // Круг закончился (все посещены/пропущены) → сбрасываем и начинаем заново.
        string? bestName = null;
        Vector2i bestTile = default;
        var bestD = int.MaxValue;
        foreach (var (name, tile) in _mapTiles)
        {
            if (_patrolDone.Contains(name))
                continue;
            if (Heur(start, tile) <= 3)         // фактически уже здесь — считаем посещённым
            {
                _patrolDone.Add(name);
                continue;
            }
            var d = Heur(start, tile);
            if (d < bestD) { bestD = d; bestName = name; bestTile = tile; }
        }
        if (bestName == null)
        {
            _patrolDone.Clear();
            return ("Обошёл все отделы — начинаю круг заново.", false);
        }
        var n = StartPathTo(gridUid, grid, start, bestTile);
        _patrolDone.Add(bestName);              // помечаем даже если не проложилось — чтобы не залипать на нём
        if (n < 0)
            return ($"«{bestName}» недостижим [{_pathFail}], пропускаю (иду дальше).", false);
        return ($"Патрулирую: иду в «{bestName}» — {n} шагов.", false);
    }

    /// <summary>Проложить A*-путь к запомненному месту / отделу карты и пойти туда, обходя стены.</summary>
    public (string msg, bool err) GoToPlace(string name)
    {
        if (_player.LocalEntity is not { } self || Deleted(self))
            return ("Нет персонажа (не в игре).", true);

        var xform = Transform(self);
        if (xform.GridUid is not { } gridUid || !TryComp<MapGridComponent>(gridUid, out var grid))
            return ("Ты не на станции (в космосе/на шаттле?) — путь не проложить.", true);

        if (!ResolvePlaceTile(name, gridUid, grid, out var goal, out var matched))
            return ($"Не помню место «{name}». Известно: {PlacesBrief()}. Сначала «прочитай карту» (read_map) или «запомни место».", true);

        var start = grid.TileIndicesFor(xform.Coordinates);
        var n = StartPathTo(gridUid, grid, start, goal);
        if (n < 0)
            return ($"Не могу проложить путь к «{matched}» [{_pathFail}].", true);
        if (n == 0)
            return ($"Уже на месте «{matched}».", false);
        return ($"Иду к «{matched}» — {n} шагов.", false);
    }

    /// <summary>Найти тайл места по имени: точное → подстрочное (обе стороны, напр. «мед» ↔ «медицинский отдел»).
    /// Ищет в местах памяти (_places/_savedTiles) и отделах карты (_mapTiles). matched — реально найденный ключ.</summary>
    private bool ResolvePlaceTile(string name, EntityUid gridUid, MapGridComponent grid, out Vector2i goal, out string matched)
    {
        // (PlaceNameHit ниже: подстрока или общая основа ≥4 — ловит склонения «кухню»↔«кухня».)
        goal = default;
        matched = name;
        var key = name.Trim().ToLowerInvariant();
        if (key.Length == 0)
            return false;
        // 1) точное совпадение
        if (_places.TryGetValue(key, out var dest) && dest.EntityId == gridUid) { goal = grid.TileIndicesFor(dest); matched = key; return true; }
        if (_savedTiles.TryGetValue(key, out var ts)) { goal = ts; matched = key; return true; }
        if (_mapTiles.TryGetValue(key, out var tm)) { goal = tm; matched = key; return true; }
        // 2) нечёткое: ключ содержит запрос или запрос содержит ключ — берём самый короткий (самый специфичный) ключ
        string? best = null;
        Vector2i bestTile = default;
        void Consider(string k, Vector2i t)
        {
            if (PlaceNameHit(key, k) && (best == null || k.Length < best.Length)) { best = k; bestTile = t; }
        }
        foreach (var (k, t) in _savedTiles) Consider(k, t);
        foreach (var (k, t) in _mapTiles) Consider(k, t);
        foreach (var (k, c) in _places)
            if (c.EntityId == gridUid) Consider(k, grid.TileIndicesFor(c));
        if (best == null)
            return false;
        goal = bestTile;
        matched = best;
        return true;
    }

    /// <summary>Проложить A*-путь start→goal и встать на маршрут. Возврат: число шагов (0 — уже на месте), −1 — пути нет.</summary>
    private int StartPathTo(EntityUid gridUid, MapGridComponent grid, Vector2i start, Vector2i goal)
    {
        // ДИАГНОСТИКА: есть ли у клиента NavMap-чанк цели (если нет — карта дальше не пришла/не разведана).
        var goalKnown = true;
        if (TryComp<NavMapComponent>(gridUid, out var nmc))
            NavTile(nmc, goal, out _, out _, out _, out goalKnown);
        // Цель-тайл может быть НЕпроходим (маяки NavMap часто висят на стене → тайл-стена, борг упёрся бы в неё).
        // Снапим на ближайший проходимый тайл, чтобы прийти В отдел, а не в стену.
        if (!Walkable(gridUid, grid, goal) && NearestWalkable(gridUid, grid, goal, start) is { } g2)
            goal = g2;
        var path = FindPath(gridUid, grid, start, goal);
        if (path == null)
        {
            if (!goalKnown)
                _pathFail += " | цель вне присланной NavMap (чанк не пришёл)";
            return -1;
        }
        ClearMovement();                 // сбрасывает и _pathQueue
        foreach (var tile in path)
            _pathQueue.Enqueue(_xform.ToMapCoordinates(grid.GridTileToLocal(tile)));
        if (_pathQueue.Count > 0)
            _gotoTarget = _pathQueue.Dequeue();
        return path.Count;
    }

    /// <summary>Короткий список известных мест/отделов (для подсказок в ошибках).</summary>
    private string PlacesBrief()
    {
        var names = new SortedSet<string>(_savedTiles.Keys);
        foreach (var k in _mapTiles.Keys) names.Add(k);
        foreach (var k in _places.Keys) names.Add(k);
        if (names.Count == 0)
            return "(пусто)";
        var s = string.Join(", ", names.Take(12));
        return names.Count > 12 ? s + "…" : s;
    }

    /// <summary>Совпадение имени места: подстрока (обе стороны) ИЛИ общая основа ≥4 симв (склонение меняет
    /// окончание: «кухню»↔«кухня», «мостике»↔«мостик»). Короткие запросы (&lt;4) — только подстрокой.</summary>
    private static bool PlaceNameHit(string query, string k)
    {
        if (k.Contains(query) || query.Contains(k))
            return true;
        var max = System.Math.Min(query.Length, k.Length);
        var n = 0;
        while (n < max && query[n] == k[n]) n++;
        return n >= 4 && n >= max - 2;      // общий префикс покрывает основу, различие только в хвосте-окончании
    }

    /// <summary>A* (манхэттен) по проходимым тайлам грида. Список тайлов БЕЗ стартового; null — пути нет.</summary>
    private List<Vector2i>? FindPath(EntityUid gridUid, MapGridComponent grid, Vector2i start, Vector2i goal)
    {
        _pathFail = "";
        if (start == goal)
            return new List<Vector2i>();
        var open = new PriorityQueue<Vector2i, int>();
        var came = new Dictionary<Vector2i, Vector2i>();
        var gScore = new Dictionary<Vector2i, int> { [start] = 0 };
        var closed = new HashSet<Vector2i>();
        open.Enqueue(start, Heur(start, goal));
        var expanded = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (open.TryDequeue(out var cur, out _))
        {
            if (cur == goal)
                return Reconstruct(came, cur);
            if (!closed.Add(cur))
                continue;
            // Потолок: по числу раскрытых тайлов И по времени (раз в ~1024 итерации, чтобы не вешать тик на огромной станции).
            if (++expanded > PathMaxExpand || (expanded % 1024 == 0 && sw.Elapsed > PathTimeBudget))
            {
                _pathFail = expanded > PathMaxExpand ? $"предел A* ({PathMaxExpand} тайлов)" : "таймаут A* (60мс)";
                break;
            }
            foreach (var d in PathDirs)
            {
                var nb = cur + d;
                if (System.Math.Abs(nb.X - start.X) > PathMaxRange || System.Math.Abs(nb.Y - start.Y) > PathMaxRange)
                    continue;
                if (nb != goal && !Walkable(gridUid, grid, nb))
                    continue;
                var tentative = gScore[cur] + 1;
                if (tentative < gScore.GetValueOrDefault(nb, int.MaxValue))
                {
                    came[nb] = cur;
                    gScore[nb] = tentative;
                    open.Enqueue(nb, tentative + Heur(nb, goal));
                }
            }
        }
        if (_pathFail == "")                       // цикл исчерпал open — связного пути нет (разрыв карты/двери/доступ)
            _pathFail = $"нет пути ({expanded} тайлов пройдено — разрыв NavMap/двери/доступ)";
        return null;
    }

    private static int Heur(Vector2i a, Vector2i b)
        => System.Math.Abs(a.X - b.X) + System.Math.Abs(a.Y - b.Y);

    /// <summary>Декод тайла из NavMap-чанка (данные ВСЕЙ станции, синхронятся мимо PVS). known=false — чанк не пришёл.</summary>
    private static void NavTile(NavMapComponent navmap, Vector2i tile, out bool floor, out bool wall, out bool door, out bool known)
    {
        floor = wall = door = false;
        known = false;
        var origin = new Vector2i(tile.X >> 3, tile.Y >> 3);           // чанк 8×8: origin = tile >> 3 (floor-деление)
        if (!navmap.Chunks.TryGetValue(origin, out var chunk))
            return;
        known = true;
        var data = chunk.TileData[(tile.X & 7) * SharedNavMapSystem.ChunkSize + (tile.Y & 7)];
        floor = (data & SharedNavMapSystem.FloorMask) != 0;
        wall  = (data & SharedNavMapSystem.WallMask) != 0;
        door  = (data & SharedNavMapSystem.AirlockMask) != 0;
    }

    /// <summary>На тайле дверь (ЛЮБАЯ: аирлок/файрлок/штора/виндор/матдверь)? Для планирования считаем проходимой:
    /// дрон проходит без доступа, борг таранит/откроет; заболченную/чужую заклинит — стирер сдастся отдельно.
    /// Доступ/болты НЕ проверяем — иначе секции станции (за файрлоками/access-дверьми) «запаиваются» и путь не строится.</summary>
    private bool DoorPassable(MapGridComponent grid, Vector2i tile, EntityUid? self)
    {
        foreach (var uid in grid.GetAnchoredEntities(tile))
            if (HasComp<DoorComponent>(uid))
                return true;
        return false;
    }

    /// <summary>Тайл проходим (ГИБРИД): структура (пол/стена/дверь) из NavMap = вся станция мимо PVS; где тайл
    /// ЗАГРУЖЕН в PVS — доп. проверка живого грида на столы/машины (их NavMap не знает).</summary>
    private bool Walkable(EntityUid gridUid, MapGridComponent grid, Vector2i tile)
    {
        var self = _player.LocalEntity;
        var loaded = !grid.GetTileRef(tile).Tile.IsEmpty;             // тайл в PVS (живой грид его знает)

        if (TryComp<NavMapComponent>(gridUid, out var navmap))
        {
            NavTile(navmap, tile, out var nFloor, out var nWall, out var nDoor, out var known);
            if (known)
            {
                if (nDoor)                                            // дверь (любой тип) — для планирования всегда проходима
                    return true;
                if (nWall || !nFloor)
                    return false;                                    // стена / нет пола → не идём
                // Пол в PVS: отсечь то, что реально блокирует ЭТУ сущность (столы/машины по её маске; дрон под столами — ок).
                if (loaded && _turf.IsTileBlocked(gridUid, tile, _selfMask))
                    return DoorPassable(grid, tile, self);           // перекрыто — проходимо лишь если это дверь
                return true;                                         // пол, стены нет (вдали — доверяем NavMap)
            }
        }

        // NavMap не знает тайл (чанк не пришёл) — по живому гриду (в пределах PVS), маска реальной сущности.
        if (!loaded)
            return false;
        if (!_turf.IsTileBlocked(gridUid, tile, _selfMask))
            return true;
        return DoorPassable(grid, tile, self);
    }

    private static List<Vector2i> Reconstruct(Dictionary<Vector2i, Vector2i> came, Vector2i cur)
    {
        var path = new List<Vector2i> { cur };
        while (came.TryGetValue(cur, out var prev))
        {
            cur = prev;
            path.Add(cur);
        }
        path.Reverse();                 // path[0] = старт … последний = цель
        // Упрощение: оставить только точки ПОВОРОТА и цель — длинные прямые = один waypoint (плавный ход).
        var simp = new List<Vector2i>();
        for (var i = 0; i < path.Count; i++)
        {
            if (i == 0 || i == path.Count - 1 || (path[i] - path[i - 1]) != (path[i + 1] - path[i]))
                simp.Add(path[i]);
        }
        if (simp.Count > 0)
            simp.RemoveAt(0);           // убрать стартовый тайл — мы уже на нём
        return simp;
    }

    /// <summary>grid: локальная ASCII-карта NxN вокруг борга (@ ты, . пол, # стена, + дверь, ~ космос).</summary>
    public string Grid(int radius)
    {
        if (_player.LocalEntity is not { } self || Deleted(self))
            return "Нет персонажа (не в игре).";
        var xform = Transform(self);
        if (xform.GridUid is not { } gridUid || !TryComp<MapGridComponent>(gridUid, out var grid))
            return "Ты не на станции (в космосе/на шаттле?) — карты нет.";
        radius = System.Math.Clamp(radius <= 0 ? 7 : radius, 3, 12);
        var c = grid.TileIndicesFor(xform.Coordinates);
        var sb = new System.Text.StringBuilder();
        sb.Append("Карта вокруг тебя (@ — ты, . — пол, # — стена, + — дверь, ~ — космос). Верх = север, право = восток:\n");
        for (var dy = radius; dy >= -radius; dy--)          // север сверху
        {
            for (var dx = -radius; dx <= radius; dx++)      // восток справа
            {
                if (dx == 0 && dy == 0) { sb.Append('@'); continue; }
                var t = new Vector2i(c.X + dx, c.Y + dy);
                if (grid.GetTileRef(t).Tile.IsEmpty) { sb.Append('~'); continue; }
                var ch = '.';
                if (_turf.IsTileBlocked(gridUid, t, _selfMask))
                {
                    ch = '#';
                    foreach (var uid in grid.GetAnchoredEntities(t))
                        if (HasComp<DoorComponent>(uid)) { ch = '+'; break; }
                }
                sb.Append(ch);
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>go_to: пойти на смещение (dx восток+/запад−, dy север+/юг−) тайлов, обходя стены (A*).</summary>
    public (string msg, bool err) GoTo(int dx, int dy)
    {
        if (_player.LocalEntity is not { } self || Deleted(self))
            return ("Нет персонажа (не в игре).", true);
        if (dx == 0 && dy == 0)
            return ("Смещение нулевое — некуда идти.", true);
        var xform = Transform(self);
        if (xform.GridUid is not { } gridUid || !TryComp<MapGridComponent>(gridUid, out var grid))
            return ("Ты не на станции — путь не проложить.", true);
        var start = grid.TileIndicesFor(xform.Coordinates);
        var goal = new Vector2i(start.X + dx, start.Y + dy);
        if (!Walkable(gridUid, grid, goal))                 // целевой тайл — стена/космос: ближайший проходимый рядом
        {
            if (NearestWalkable(gridUid, grid, goal, start) is not { } g2)
                return ($"Точка ({dx},{dy}) перекрыта и рядом нет прохода. Посмотри grid и выбери другую сторону.", true);
            goal = g2;
        }
        var n = StartPathTo(gridUid, grid, start, goal);
        if (n <= 0)
            return ($"Не могу проложить путь к ({dx},{dy}) — перекрыто. Посмотри grid.", true);
        return ($"Иду на ({dx},{dy}) — {n} шагов.", false);
    }

    /// <summary>Ближайший проходимый тайл к <paramref name="near"/> (кольцами до 4), ближе к <paramref name="from"/>.</summary>
    private Vector2i? NearestWalkable(EntityUid gridUid, MapGridComponent grid, Vector2i near, Vector2i from)
    {
        for (var r = 1; r <= 4; r++)
        {
            Vector2i? best = null;
            var bestD = int.MaxValue;
            for (var ox = -r; ox <= r; ox++)
            for (var oy = -r; oy <= r; oy++)
            {
                if (System.Math.Abs(ox) != r && System.Math.Abs(oy) != r)
                    continue;                               // только внешнее кольцо
                var t = new Vector2i(near.X + ox, near.Y + oy);
                if (!Walkable(gridUid, grid, t))
                    continue;
                var d = Heur(t, from);
                if (d < bestD) { bestD = d; best = t; }
            }
            if (best is { } b)
                return b;
        }
        return null;
    }

    // Вездесущая инфраструктура (есть в КАЖДОЙ комнате) — не считать ни за какое помещение.
    private static readonly string[] IgnoreMarkers =
    {
        "вент", "скруббер", "труб", "кабель", "провод", "лампа", "освещ", "воздушн",
        "сигнализ", "решётк", "решетк", "розетк", "apc", "распределительный щит",
        // структурный мусор — есть везде, комнату не характеризует
        "стена", "ставни", "шлюз", "окно", "светильник", "сенсор", "камер", "интерком",
        "информационная доск", "дверь", "стекло", "пол ",
    };

    // Определение помещения по ХАРАКТЕРНЫМ, уникальным для него объектам (без вездесущей инфраструктуры).
    private static readonly (string room, string[] kw)[] RoomMarkers =
    {
        ("мед",    new[]{ "операционн", "крио", "аптечк", "дефибрил", "скальпел", "медкроват", "саркофаг", "химфабрик", "медицинский шкаф", "спальный медицинск" }),
        ("карго",  new[]{ "конвейер", "телепад", "автодок", "процессор руды", "ящик снаб", "заказ снаб", "погрузчик" }),
        ("инж",    new[]{ "генератор", "солнеч панел", "тесла", "сингуляр", "накопитель энерг", "матрица pacman", "плазмен генератор", "am-двигател" }),
        ("атмос",  new[]{ "канистр", "газовый смеситель", "смеситель газ", "газовый фильтр", "фильтр газ", "газоанализ", "камера сгоран", "термомашин", "теплообмен" }),
        ("мостик", new[]{ "коммуникац", "консоль связи", "пульт станц", "сейф капитан", "камер наблюд", "консоль шаттл" }),
        ("сб",     new[]{ "оружейн", "бриг", "наручник", "дубин", "камера закл", "имплант-стрелк" }),
        ("бар",    new[]{ "барн", "пивн", "коктейл", "бочка эль", "музыкальн автомат" }),
        ("кухня",  new[]{ "плита", "гриль", "микроволнов", "мясоруб", "холодильн", "кухонн" }),
    };

    /// <summary>where_am_i: вернуть ХАРАКТЕРНЫЕ объекты вокруг (радиус ~7, без вездесущей инфраструктуры
    /// и без людей) — «описание» помещения. Имя комнаты придумывает сама модель по этому списку.
    /// Плюс мягкая подсказка по маркерам (не вердикт).</summary>
    public string WhereAmI()
    {
        if (_player.LocalEntity is not { } self || Deleted(self))
            return "Нет персонажа (не в игре).";
        if (Transform(self).GridUid is not { } gridUid)
            return "Ты не на станции (в космосе/на шаттле) — помещение не определить.";
        var selfPos = _xform.GetMapCoordinates(self);
        var groups = new Dictionary<string, (int n, float d)>();  // имя объекта → (сколько, ближайшая дист)
        var hits = new Dictionary<string, HashSet<string>>();     // помещение → набор РАЗНЫХ маркеров (для подсказки)
        var query = EntityQueryEnumerator<MetaDataComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var meta, out var xf))
        {
            if (uid == self || string.IsNullOrEmpty(meta.EntityName))
                continue;
            if (HasComp<MobStateComponent>(uid))                  // люди/мобы — не про помещение
                continue;
            // Только объекты, лежащие/закреплённые ПРЯМО на гриде (мебель, машины, лут на полу).
            // Всё, что в контейнере/инвентаре/теле (внутренности борга, вещи пассажиров, картриджи в КПК) —
            // parent != грид → выкидываем.
            if (xf.ParentUid != gridUid)
                continue;
            var pos = _xform.GetMapCoordinates(uid);
            if (pos.MapId != selfPos.MapId)
                continue;
            var dist = (pos.Position - selfPos.Position).Length();
            if (dist > 7f)
                continue;
            var low = meta.EntityName.ToLowerInvariant();
            var ignored = false;
            foreach (var ig in IgnoreMarkers)
                if (low.Contains(ig)) { ignored = true; break; }
            if (ignored)
                continue;
            if (groups.TryGetValue(meta.EntityName, out var g))
                groups[meta.EntityName] = (g.n + 1, System.Math.Min(g.d, dist));
            else
                groups[meta.EntityName] = (1, dist);
            foreach (var (room, kws) in RoomMarkers)
                foreach (var k in kws)
                    if (low.Contains(k))
                    {
                        if (!hits.TryGetValue(room, out var set)) { set = new HashSet<string>(); hits[room] = set; }
                        set.Add(k);
                        break;
                    }
        }
        if (groups.Count == 0)
            return "Рядом только стены/инфраструктура — характерных объектов нет. Иди дальше (explore).";

        // Топ до 12 ближайших объектов — «описание» помещения для модели.
        var items = new List<(string name, int n, float d)>();
        foreach (var (name, g) in groups)
            items.Add((name, g.n, g.d));
        items.Sort((a, b) => a.d.CompareTo(b.d));
        var sb = new System.Text.StringBuilder();
        var shown = 0;
        foreach (var it in items)
        {
            if (shown >= 12) break;
            if (shown > 0) sb.Append(", ");
            sb.Append(it.n > 1 ? $"{it.name} ×{it.n}" : it.name);
            shown++;
        }

        // Мягкая подсказка: только если ≥2 разных маркера одной комнаты.
        var hint = "";
        var top = "";
        var topN = 0;
        foreach (var (r, set) in hits)
            if (set.Count > topN) { topN = set.Count; top = r; }
        if (topN >= 2)
            hint = $" (по объектам похоже на «{top}»)";

        return $"Рядом объекты: {sb}.{hint}\n"
             + "Сам придумай КОРОТКОЕ уникальное имя этого помещения по объектам и запомни: remember_place «имя». "
             + "Если это просто коридор / ничего характерного — НЕ запоминай, иди дальше (explore).";
    }

    /// <summary>explore: сам найти ближайшую новую дверь и пройти сквозь неё в следующую комнату
    /// (или, если новых дверей рядом нет, уйти вглубь по самому длинному свободному коридору).
    /// Один вызов = уверенный переход на несколько тайлов, а не шаг.</summary>
    public (string msg, bool err) Explore()
    {
        if (_player.LocalEntity is not { } self || Deleted(self))
            return ("Нет персонажа (не в игре).", true);
        var xform = Transform(self);
        if (xform.GridUid is not { } gridUid || !TryComp<MapGridComponent>(gridUid, out var grid))
            return ("Ты не на станции — разведывать нечего.", true);

        var here = grid.TileIndicesFor(xform.Coordinates);
        _visitedTiles.Add(here);

        Vector2i target;
        string what;
        if (FindNearestNewDoor(gridUid, grid, here, 22) is { } door)
        {
            _usedDoors.Add(door);
            var beyond = door + StepDir(here, door);        // тайл за дверью — в новую комнату
            target = Walkable(gridUid, grid, beyond) ? beyond : door;
            what = $"к двери ({DirName(here, door)})";
        }
        else if (FarthestCorridor(gridUid, grid, here) is { } frontier && frontier != here)
        {
            target = frontier;
            what = $"вглубь ({DirName(here, frontier)})";
        }
        else
        {
            return ("Рядом новых дверей и коридоров нет — всё исследовано. Иди в дальнее место (go_to) или stop.", false);
        }

        var path = FindPath(gridUid, grid, here, target);
        if (path == null || path.Count == 0)
            return ("Не смог проложить путь для разведки — попробуй go_to в свободную сторону по grid.", true);
        ClearMovement();
        foreach (var tile in path)
            _pathQueue.Enqueue(_xform.ToMapCoordinates(grid.GridTileToLocal(tile)));
        if (_pathQueue.Count > 0)
            _gotoTarget = _pathQueue.Dequeue();
        return ($"Иду {what} — {path.Count} шагов.", false);
    }

    /// <summary>Ближайшая дверь-тайл в радиусе, через которую ещё не ходили.</summary>
    private Vector2i? FindNearestNewDoor(EntityUid gridUid, MapGridComponent grid, Vector2i here, int maxR)
    {
        Vector2i? best = null;
        var bestD = int.MaxValue;
        for (var dx = -maxR; dx <= maxR; dx++)
        for (var dy = -maxR; dy <= maxR; dy++)
        {
            var t = new Vector2i(here.X + dx, here.Y + dy);
            if (t == here || _usedDoors.Contains(t))
                continue;
            var isDoor = false;
            foreach (var uid in grid.GetAnchoredEntities(t))
                if (HasComp<DoorComponent>(uid)) { isDoor = true; break; }
            if (!isDoor)
                continue;
            var d = Heur(t, here);
            if (d < bestD) { bestD = d; best = t; }
        }
        return best;
    }

    /// <summary>Из 4 сторон выбрать самый длинный свободный прогон по полу и вернуть его дальний тайл
    /// (с уклоном в наименее посещённую сторону). here — если идти некуда.</summary>
    private Vector2i FarthestCorridor(EntityUid gridUid, MapGridComponent grid, Vector2i here)
    {
        var best = here;
        var bestScore = 0;
        foreach (var d in PathDirs)
        {
            var t = here;
            var steps = 0;
            while (steps < 15)
            {
                var nx = t + d;
                if (!Walkable(gridUid, grid, nx))
                    break;
                t = nx;
                steps++;
            }
            if (steps == 0)
                continue;
            var score = steps + (_visitedTiles.Contains(t) ? 0 : 4);   // бонус за непосещённое
            if (score > bestScore) { bestScore = score; best = t; }
        }
        return best;
    }

    /// <summary>Единичный шаг по доминирующей оси от a к b (для «тайла за дверью»).</summary>
    private static Vector2i StepDir(Vector2i a, Vector2i b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        return System.Math.Abs(dx) >= System.Math.Abs(dy)
            ? new Vector2i(System.Math.Sign(dx), 0)
            : new Vector2i(0, System.Math.Sign(dy));
    }

    private static string DirName(Vector2i from, Vector2i to)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var ns = dy > 0 ? "север" : dy < 0 ? "юг" : "";
        var ew = dx > 0 ? "восток" : dx < 0 ? "запад" : "";
        return (ns + (ns != "" && ew != "" ? "-" : "") + ew) is { Length: > 0 } s ? s : "рядом";
    }

    /// <summary>Maps a pilot direction to a shuttle key and starts a thrust/rotate pulse.</summary>
    private void StartPilot(string? dir)
    {
        var key = dir?.Trim().ToLowerInvariant() switch
        {
            "forward" or "up" or "вперёд" or "вперед" => ContentKeyFunctions.ShuttleStrafeUp,
            "back" or "down" or "назад" => ContentKeyFunctions.ShuttleStrafeDown,
            "left" or "влево" => ContentKeyFunctions.ShuttleStrafeLeft,
            "right" or "вправо" => ContentKeyFunctions.ShuttleStrafeRight,
            "rotate_left" or "rotleft" or "ccw" => ContentKeyFunctions.ShuttleRotateLeft,
            "rotate_right" or "rotright" or "cw" => ContentKeyFunctions.ShuttleRotateRight,
            _ => (BoundKeyFunction?) null,
        };
        if (key is not { } k)
            return;

        // release any in-progress pulse first
        if (_pilotDown && _pilotKey is { } prev && _player.LocalEntity is { } self && _player.LocalSession is { } s)
            SendKey(s, prev, BoundKeyState.Up, Transform(self).Coordinates);
        _pilotDown = false;
        _pilotKey = k;
        _pilotUntil = _timing.CurTime + PilotPulse;
    }

    /// <summary>Walk-to-then-interact: once within reach of the target entity, fire the interaction.</summary>
    private void TryReachInteract(ref NetEntity? target, BoundKeyFunction func)
    {
        if (target is not { } net)
            return;

        if (_player.LocalEntity is not { } self || Deleted(self)
            || !TryGetEntity(net, out var ent) || Deleted(ent.Value))
        {
            target = null;
            return;
        }

        var selfPos = _xform.GetMapCoordinates(self);
        var entPos = _xform.GetMapCoordinates(ent.Value);
        if (selfPos.MapId != entPos.MapId)
            return;

        if ((entPos.Position - selfPos.Position).Length() > PickupRange)
            return; // keep walking toward it

        SendInput(func, Transform(ent.Value).Coordinates, ent.Value);
        target = null;
    }

    /// <summary>Walk-to-then-drop: once right next to the place target, drop the held item there.</summary>
    private void TryReachPlace()
    {
        if (_placeTarget is not { } net)
            return;

        if (_player.LocalEntity is not { } self || Deleted(self)
            || !TryGetEntity(net, out var ent) || Deleted(ent.Value))
        {
            _placeTarget = null;
            return;
        }

        var selfPos = _xform.GetMapCoordinates(self);
        var entPos = _xform.GetMapCoordinates(ent.Value);
        if (selfPos.MapId != entPos.MapId)
            return;

        if ((entPos.Position - selfPos.Position).Length() > MoveToRange + 0.3f)
            return; // keep walking toward it

        _pending.Enqueue((ContentKeyFunctions.Drop, null, false));
        _placeTarget = null;
    }

    /// <summary>Runs instant actions (drop, swap, throw, store, use) queued from ApplyDecision.</summary>
    private void ExecutePending()
    {
        while (_pending.Count > 0)
        {
            var (func, target, atCoords) = _pending.Dequeue();
            if (_player.LocalEntity is not { } self || Deleted(self))
                continue;

            if (target is { } net)
            {
                if (!TryGetEntity(net, out var ent) || Deleted(ent.Value))
                    continue;
                // atCoords = aim at the target's position (throw); else interact ON the entity (store).
                SendInput(func, Transform(ent.Value).Coordinates, atCoords ? EntityUid.Invalid : ent.Value);
            }
            else
            {
                SendInput(func, Transform(self).Coordinates, EntityUid.Invalid);
            }
        }
    }

    /// <summary>Sends a bound-key input command (like a click/keypress) — optionally at a target entity.</summary>
    private void SendInput(BoundKeyFunction func, EntityCoordinates coords, EntityUid uid)
    {
        if (_player.LocalSession is not { } session)
            return;

        var funcId = _input.NetworkBindMap.KeyFunctionID(func);

        var down = new ClientFullInputCmdMessage(_timing.CurTick, _timing.TickFraction, funcId,
            coords, new ScreenCoordinates(0, 0, default), BoundKeyState.Down, uid);
        _inputSys.HandleInputCommand(session, func, down);

        var up = new ClientFullInputCmdMessage(_timing.CurTick, _timing.TickFraction, funcId,
            coords, new ScreenCoordinates(0, 0, default), BoundKeyState.Up, uid);
        _inputSys.HandleInputCommand(session, func, up);
    }

    /// <summary>
    ///     Watches for a fresh "point" arrow nearby and locks its location as a one-shot go-to target
    ///     (used by "build"). Arrows are networked, so we just read the nearest new one from PVS.
    /// </summary>
    private void DetectPointingArrow()
    {
        if (!Enabled || _player.LocalEntity is not { } self || Deleted(self))
            return;

        var selfPos = _xform.GetMapCoordinates(self);
        var current = new HashSet<EntityUid>();
        EntityUid? nearest = null;
        var nearestDist = float.MaxValue;
        var nearestPos = MapCoordinates.Nullspace;

        var query = EntityQueryEnumerator<PointingArrowComponent>();
        while (query.MoveNext(out var uid, out _))
        {
            current.Add(uid);
            if (_seenArrows.Contains(uid))
                continue; // already handled this arrow

            var pos = _xform.GetMapCoordinates(uid);
            if (pos.MapId != selfPos.MapId)
                continue;

            var dist = (pos.Position - selfPos.Position).Length();
            if (dist <= PointDetectRange && dist < nearestDist)
            {
                nearestDist = dist;
                nearest = uid;
                nearestPos = pos;
            }
        }

        _seenArrows.UnionWith(current);
        _seenArrows.IntersectWith(current);

        if (nearest == null)
            return;

        if (_awaitBuildPoint)
        {
            _buildCoords = Transform(nearest.Value).Coordinates; // build tile
            _awaitBuildPoint = false;
        }
        else
        {
            // НЕ авто-goto. Просто запоминаем точку — модель через тул pointing решит, что делать.
            _lastPointPos = nearestPos;
            _lastPointTime = _timing.CurTime;
        }
    }

    /// <summary>
    ///     Последнее указание (стрелка) не старше 6с и ещё не считанное; чтение «съедает» его.
    ///     Пустой результат — никто недавно не указывал. Используется тулом pointing (перцепция).
    /// </summary>
    public MapCoordinates? ConsumePoint()
    {
        if (_lastPointPos is not { } p)
            return null;
        _lastPointPos = null;                              // consume в любом случае
        if (_timing.CurTime - _lastPointTime > TimeSpan.FromSeconds(6))
            return null;
        return p;
    }

    /// <summary>
    ///     Applies one decision (speech + action) to the steerer. Called by the client MCP endpoint.
    ///     Movement only runs while <see cref="Enabled"/> is true.
    /// </summary>
    public void ApplyDecision(string? say, string? action, NetEntity? target, string? arg = null)
    {
        if (!string.IsNullOrWhiteSpace(say))
        {
            var text = Sanitize(say!);
            if (text.Length > 0)
                _con.ExecuteCommand($"say {text}"); // "say" is not an input command — safe here
        }

        var act = action?.Trim().ToLowerInvariant();

        // Combat mode gating: attack/shoot need it ON (a Use click becomes an attack); interactions
        // need it OFF (else they'd hit instead of interact).
        if (act is "attack" or "shoot" or "mine")
            RequestCombatMode(true);
        else if (act is "pickup" or "pull" or "use_on" or "store" or "place" or "drop")
            RequestCombatMode(false);

        switch (act)
        {
            case "follow":   // держаться рядом, обходя стены/столы (A*, по маске сущности)
                if (target is { } ft)
                    GoToEntity(ft, 1.4f, follow: true);
                break;
            case "pickup":
                ClearMovement();
                _pickupTarget = target;
                break;
            case "move_to":  // подойти вплотную, без интеракции — A* обходит препятствия
                if (target is { } mt)
                    GoToEntity(mt, MoveToRange, follow: false);
                break;
            case "place":  // walk right up to the target, then drop the held item
                ClearMovement();
                _placeTarget = target;
                break;
            case "pull":
                ClearMovement();
                _pullTarget = target;
                break;
            case "store":  // use held item on the backpack
                _pending.Enqueue((EngineKeyFunctions.Use, target, false));
                break;
            case "throw":  // throw held item toward the target's position
                _pending.Enqueue((ContentKeyFunctions.ThrowItemInHand, target, true));
                break;
            case "drop":
                _pending.Enqueue((ContentKeyFunctions.Drop, null, false));
                break;
            case "swap":
                _pending.Enqueue((ContentKeyFunctions.SwapHands, null, false));
                break;
            case "build":  // next point sets the build tile
                ClearMovement();
                _awaitBuildPoint = true;
                break;
            case "craft":  // craft an item recipe from materials in hand / nearby
                if (!string.IsNullOrWhiteSpace(arg))
                    RaiseNetworkEvent(new TryStartItemConstructionMessage(arg!));
                break;
            case "construct":  // place a structure / machine frame recipe at the agent's tile
                if (!string.IsNullOrWhiteSpace(arg)
                    && _player.LocalEntity is { } cself && !Deleted(cself))
                    RaiseNetworkEvent(new TryStartStructureConstructionMessage(
                        GetNetCoordinates(Transform(cself).Coordinates), arg!, Angle.Zero, unchecked(_buildAck++)));
                break;
            case "use_on":  // walk up, then use the held item ON the target (pry a door/shutter with a crowbar, insert a part…)
                ClearMovement();
                _useOnTarget = target;
                break;
            case "activate":  // toggle the held item in hand (light a welder, flashlight, ...)
                _pending.Enqueue((ContentKeyFunctions.UseItemInHand, null, false));
                break;
            case "pilot":  // thrust/rotate the shuttle (must be piloting a console). arg = direction
                StartPilot(arg);
                break;
            case "attack":  // walk into melee range, then attack/mine (combat mode is on)
                ClearMovement();
                _attackTarget = target;
                break;
            case "mine":  // auto-mining loop: hit target, then hop to the next same-named entity, until stop
                ClearMovement();
                _attackTarget = target;
                if (target is { } mnet && TryGetEntity(mnet, out var mEnt) && !Deleted(mEnt.Value))
                    _mineName = MetaData(mEnt.Value).EntityName;
                _mineLoop = true;
                break;
            case "shoot":  // fire the held gun at the target (deferred until combat mode is confirmed)
                _shootTarget = target;
                break;
            case "smart_equip":  // stash the held item into a slot (Goob only exposes back/backpack/belt keybinds)
                var eqFunc = arg switch
                {
                    "belt" => ContentKeyFunctions.SmartEquipBelt,
                    "back" => ContentKeyFunctions.SmartEquipBack,
                    _      => ContentKeyFunctions.SmartEquipBackpack,
                };
                _pending.Enqueue((eqFunc, null, false));
                break;
            case "alt_activate":  // alt-activate the item in hand
                _pending.Enqueue((ContentKeyFunctions.AltUseItemInHand, null, false));
                break;
            case "alt_use_on":  // walk up, then alt-click the target with the held item
                ClearMovement();
                _altUseTarget = target;
                break;
            case "release_pull":  // release the pulled object
                _pending.Enqueue((ContentKeyFunctions.ReleasePulledObject, null, false));
                break;
            case "stop":
                _followTarget = null;
                _pickupTarget = null;
                _pullTarget = null;
                _gotoTarget = null;
                _moveToTarget = null;
                _placeTarget = null;
                _attackTarget = null;
                _useOnTarget = null;
                _altUseTarget = null;
                _mineLoop = false;
                _shootTarget = null;
                _buildCoords = null;
                _awaitBuildPoint = false;
                _pathQueue.Clear();
                CancelConstruction();
                _wireTarget = null;
                _navEntity = null;
                _navReadCrew = false;
                _navFollow = false;
                break;
        }
    }

    /// <summary>Clears every walk intent — called before setting a fresh one so they don't pin each other.</summary>
    private void ClearMovement()
    {
        _followTarget = null;
        _pickupTarget = null;
        _pullTarget = null;
        _moveToTarget = null;
        _placeTarget = null;
        _attackTarget = null;
        _useOnTarget = null;
        _altUseTarget = null;
        _mineLoop = false;               // switching tasks stops the mine-loop (mine re-enables it after)
        CancelConstruction();            // и авто-разбор/сборку (новая команда перехватывает скилл)
        _wireTarget = null;              // и резку проводов
        _gotoTarget = null;
        _pathQueue.Clear();              // бросаем недошедший маршрут
        _navEntity = null;               // и погоню за человеком (почтальон/move_to/follow)
        _navReadCrew = false;
        _navFollow = false;
        _walkBest = float.MaxValue;      // reset give-up tracking for the next target
        _walkImprove = _timing.CurTime;
    }

    private HashSet<string> DesiredKeys()
    {
        var want = new HashSet<string>();

        if (!Enabled || _player.LocalEntity is not { } self || Deleted(self))
            return want;

        // Маршрут: дошли до текущего waypoint (_gotoTarget очищается на прибытии) → берём следующий,
        // сбрасывая трекер give-up (иначе более дальний waypoint сочтётся «нет прогресса» и путь бросится).
        if (_gotoTarget == null && _pathQueue.Count > 0)
        {
            _gotoTarget = _pathQueue.Dequeue();
            _walkBest = float.MaxValue;
            _walkImprove = _timing.CurTime;
        }

        if (!TryResolveTargetPos(self, out var targetPos, out var arrival, out var isGoto, out var isFollow))
            return want;

        var selfPos = _xform.GetMapCoordinates(self);
        if (selfPos.MapId != targetPos.MapId)
            return want;

        var delta = targetPos.Position - selfPos.Position;
        var dist = delta.Length();
        if (dist <= arrival)
        {
            if (isGoto)
            {
                _gotoTarget = null;   // arrived at the fixed point
                _moveToTarget = null; // move_to is one-shot — release on arrival
            }
            return want;
        }

        // Give-up: track closest approach; if no progress for StuckGiveUp, abandon the target so the
        // agent doesn't shove an unreachable wall forever (keys released next frame, target dropped).
        if (dist < _walkBest - 0.1f)
        {
            _walkBest = dist;
            _walkImprove = _timing.CurTime;
        }
        else if (!isFollow && _timing.CurTime - _walkImprove > StuckGiveUp)
        {
            // Can't reach the current target. In a mine-loop, don't abandon mining — skip THIS rock
            // and try the next reachable same-named one; only stop if none are left.
            if (_mineLoop && _attackTarget is { } cur)
            {
                var excl = TryGetEntity(cur, out var ce) ? ce.Value : (EntityUid?) null;
                _attackTarget = FindNearestNamed(self, _mineName, excl);
                _walkBest = float.MaxValue;
                _walkImprove = _timing.CurTime;
                if (_attackTarget == null)
                    _mineLoop = false;
                return want;
            }

            // Path-following: застряли на ЭТОМ waypoint — пропускаем его и идём к следующему,
            // а не бросаем весь маршрут (тайлы соседние, обход стен уже учтён A*).
            if (_pathQueue.Count > 0)
            {
                _gotoTarget = _pathQueue.Dequeue();
                _walkBest = float.MaxValue;
                _walkImprove = _timing.CurTime;
                return want;
            }

            ClearMovement();
            return want; // empty → ApplyKeys releases held movement keys
        }

        // Movement keys are interpreted relative to the grid/eye rotation (engine rotates input by
        // GetParentGridAngle). Convert the world direction into that frame, else she walks wrong.
        if (_cfg.GetCVar(CCVars.RelativeMovement) && TryComp<InputMoverComponent>(self, out var mover))
        {
            var parentRotation = mover.RelativeRotation;
            if (mover.RelativeEntity is { } rel && !Deleted(rel))
                parentRotation = _xform.GetWorldRotation(rel) + mover.RelativeRotation;

            delta = (-parentRotation).RotateVec(delta);
        }

        delta = Avoid(delta, dist);
        return KeysFromDelta(delta);
    }

    /// <summary>
    ///     Local obstacle avoidance via axis-sliding: if we stop making progress, walk along just one
    ///     axis toward the target instead of pushing into it diagonally. Cycles straight -> X -> Y.
    /// </summary>
    private Vector2 Avoid(Vector2 delta, float dist)
    {
        if (_timing.CurTime >= _stuckCheck)
        {
            _stuckCheck = _timing.CurTime + TimeSpan.FromSeconds(0.35);

            var progressed = _prevDist > 0f && _prevDist - dist > 0.08f;
            if (progressed)
                _slideMode = 0;                 // moving toward target — go straight
            else
                _slideMode = (_slideMode + 1) % 3; // stuck — try the next axis

            _prevDist = dist;
        }

        return _slideMode switch
        {
            1 => new Vector2(delta.X, 0f), // slide along X
            2 => new Vector2(0f, delta.Y), // slide along Y
            _ => delta,                    // straight to target
        };
    }

    private HashSet<string> KeysFromDelta(Vector2 delta)
    {
        var want = new HashSet<string>();
        if (delta.X > Deadzone) want.Add("MoveRight");
        else if (delta.X < -Deadzone) want.Add("MoveLeft");
        if (delta.Y > Deadzone) want.Add("MoveUp");
        else if (delta.Y < -Deadzone) want.Add("MoveDown");
        return want;
    }

    /// <summary>Resolves where to walk: pickup &gt; pull &gt; move_to &gt; place &gt; build &gt; point &gt; follow.</summary>
    private bool TryResolveTargetPos(EntityUid self, out MapCoordinates targetPos, out float arrival, out bool isGoto, out bool isFollow)
    {
        isGoto = false;
        isFollow = false;
        arrival = StopRange;

        if (_pickupTarget is { } pnet)
        {
            if (TryGetEntity(pnet, out var pent) && !Deleted(pent.Value))
            {
                targetPos = _xform.GetMapCoordinates(pent.Value);
                arrival = PickupApproach;
                return true;
            }
            _pickupTarget = null;
        }

        if (_pullTarget is { } qnet)
        {
            if (TryGetEntity(qnet, out var qent) && !Deleted(qent.Value))
            {
                targetPos = _xform.GetMapCoordinates(qent.Value);
                arrival = PickupApproach;
                return true;
            }
            _pullTarget = null;
        }

        if (_useOnTarget is { } unet)
        {
            if (TryGetEntity(unet, out var uent) && !Deleted(uent.Value))
            {
                targetPos = _xform.GetMapCoordinates(uent.Value);
                arrival = PickupApproach;
                return true;
            }
            _useOnTarget = null;
        }

        if (_altUseTarget is { } aunet)
        {
            if (TryGetEntity(aunet, out var auent) && !Deleted(auent.Value))
            {
                targetPos = _xform.GetMapCoordinates(auent.Value);
                arrival = PickupApproach;
                return true;
            }
            _altUseTarget = null;
        }

        if (_moveToTarget is { } mnet)
        {
            if (TryGetEntity(mnet, out var ment) && !Deleted(ment.Value))
            {
                targetPos = _xform.GetMapCoordinates(ment.Value);
                arrival = MoveToRange;
                isGoto = true; // one-shot: release _moveToTarget on arrival
                return true;
            }
            _moveToTarget = null;
        }

        if (_placeTarget is { } plnet)
        {
            if (TryGetEntity(plnet, out var plent) && !Deleted(plent.Value))
            {
                targetPos = _xform.GetMapCoordinates(plent.Value);
                arrival = MoveToRange;
                return true;
            }
            _placeTarget = null;
        }

        if (_attackTarget is { } anet)
        {
            if (TryGetEntity(anet, out var aent) && !Deleted(aent.Value))
            {
                targetPos = _xform.GetMapCoordinates(aent.Value);
                arrival = MeleeRange; // within melee reach, then TryAttack swings (LightAttackEvent)
                return true;
            }
            _attackTarget = null;
        }

        if (_buildCoords is { } bcoords)
        {
            targetPos = _xform.ToMapCoordinates(bcoords);
            arrival = BuildRange;
            return true;
        }

        if (_gotoTarget is { } point)
        {
            targetPos = point;
            // Промежуточные точки маршрута — прибытие пошире (не разворачиваться при перелёте); последняя/указанная — точно.
            arrival = _pathQueue.Count > 0 ? WaypointArrive : MoveToRange;
            isGoto = true;
            return true;
        }

        if (_followTarget is { } net && TryGetEntity(net, out var ent) && !Deleted(ent.Value))
        {
            targetPos = _xform.GetMapCoordinates(ent.Value);
            isFollow = true; // persistent: never give up on a follow (person may stop / match our pace)
            return true;
        }

        _followTarget = null;
        targetPos = MapCoordinates.Nullspace;
        return false;
    }

    /// <summary>
    ///     Presses/releases movement keys via the direct input system (like a real keypress) instead
    ///     of the "incmd" console command — <b>incmd is admin-gated</b>, so on a foreign server without
    ///     perms it fails ("Insufficient perms"). HandleInputCommand has no such gate, so movement
    ///     works on any server. Held semantics: send Down once, Up when the key leaves the wanted set.
    /// </summary>
    private void ApplyKeys(HashSet<string> want)
    {
        if (_player.LocalEntity is not { } self || Deleted(self) || _player.LocalSession is not { } session)
            return;

        var coords = Transform(self).Coordinates;

        foreach (var key in _heldKeys.ToArray())
        {
            if (!want.Contains(key))
            {
                SendKey(session, new BoundKeyFunction(key), BoundKeyState.Up, coords);
                _heldKeys.Remove(key);
            }
        }
        foreach (var key in want)
        {
            if (_heldKeys.Add(key))
                SendKey(session, new BoundKeyFunction(key), BoundKeyState.Down, coords);
        }
    }

    /// <summary>Sends a single held key state (Down = press, Up = release) through the input system.</summary>
    private void SendKey(ICommonSession session, BoundKeyFunction func, BoundKeyState state, EntityCoordinates coords)
    {
        var funcId = _input.NetworkBindMap.KeyFunctionID(func);
        var msg = new ClientFullInputCmdMessage(_timing.CurTick, _timing.TickFraction, funcId,
            coords, new ScreenCoordinates(0, 0, default), state, EntityUid.Invalid);
        _inputSys.HandleInputCommand(session, func, msg);
    }

    private static string Sanitize(string s)
    {
        return s.Replace("\r", " ").Replace("\n", " ").Replace("\"", "").Trim();
    }
}
