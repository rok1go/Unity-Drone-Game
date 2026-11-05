using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

public class DroneHud : MonoBehaviour
{
    [SerializeField, HideInInspector] private LocalControlSwitcher controls;
    [SerializeField, HideInInspector] private UIDocument document;
    [SerializeField, HideInInspector] private PanelSettings panel;
    private VisualElement osd, humanHealth, healthFill, damageFlash, crosshair, result;
    private VisualElement hintWrap, roleIcon;
    private VisualElement warningPanel;
    private DroneFlightDisplay flightDisplay;
    private Label flightClock, flightState, droneHeading, homeDistance, verticalSpeed, horizontalSpeed,
        groundHeight, positionX, positionZ, dronePitch, droneRoll;
    [SerializeField, HideInInspector] private Font osdFont;
    private float cachedGround = float.PositiveInfinity;
    private Label helpKey;
    private Label role, phase, roundTimer, aliveCount, speed, altitude, throttle, flightMode, warning, hint, help;
    private Label healthTitle, healthValue, healthStatus, healthHit, resultTitle, resultReason;
    private Button restartButton;
    private Label stockLabel, resultKey;
    private readonly RaycastHit[] groundHits = new RaycastHit[16];
    private bool showHelp;
    private float nextRefresh;

    private void OnEnable()
    {
        if (Application.isPlaying && controls != null) Initialize(controls);
    }

    public void Initialize(LocalControlSwitcher switcher)
    {
        controls = switcher;
        var tree = Resources.Load<VisualTreeAsset>("UI/FpvHud");
        var sheet = Resources.Load<StyleSheet>("UI/FpvHud");
        if (tree == null || sheet == null)
        {
            Debug.LogError("HUD resources are missing.", this);
            enabled = false;
            return;
        }
        if (restartButton != null) restartButton.clicked -= RestartRound;
        if (panel == null) panel = ScriptableObject.CreateInstance<PanelSettings>();
        panel.name = "Game HUD";
        panel.themeStyleSheet = Resources.Load<ThemeStyleSheet>("UI/FpvTheme");
        panel.scaleMode = PanelScaleMode.ScaleWithScreenSize;
        panel.referenceResolution = new Vector2Int(1600, 900);
        panel.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
        panel.match = 0.5f;
        panel.sortingOrder = 50;
        if (document == null) document = GetComponent<UIDocument>();
        if (document == null) document = gameObject.AddComponent<UIDocument>();
        document.panelSettings = panel;
        document.enabled = true;
        document.visualTreeAsset = tree;
        var root = document.rootVisualElement;
        root.Clear();
        root.style.position = Position.Absolute;
        root.style.left = root.style.right = root.style.top = root.style.bottom = new StyleLength(0f);
        root.style.width = Length.Percent(100f);
        root.style.height = Length.Percent(100f);
        tree.CloneTree(root);
        root.styleSheets.Add(sheet);
        root.style.unityFontDefinition = FontDefinition.FromFont(Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));
        if (osdFont == null)
            osdFont = Font.CreateDynamicFontFromOSFont(new[] { "Consolas", "Menlo", "DejaVu Sans Mono", "Liberation Mono", "Courier New" }, 20);
        HudGlassPanel.Attach(root.Q(className: "role-panel"), new Color(0.07f, 0.13f, 0.22f, 0.56f),
            new Color(0.08f, 0.14f, 0.22f, 0.03f), true, 7f, 0.45f);
        HudGlassPanel.Attach(root.Q("alivePanel"), new Color(0.08f, 0.14f, 0.22f, 0.03f),
            new Color(0.07f, 0.13f, 0.22f, 0.56f), true, 7f, 0.45f);
        HudGlassPanel.Attach(root.Q("clockPanel"), new Color(0.10f, 0.17f, 0.26f, 0.34f),
            new Color(0.06f, 0.11f, 0.19f, 0.60f), false, 6f, 0.9f);
        HudGlassPanel.Attach(root.Q("hintPanel"), new Color(0.17f, 0.24f, 0.26f, 0.36f),
            new Color(0.06f, 0.13f, 0.15f, 0.65f), false, 8f, 0.65f);
        HudGlassPanel.Attach(root.Q("warningPanel"), new Color(0.17f, 0.24f, 0.26f, 0.36f),
            new Color(0.06f, 0.13f, 0.15f, 0.65f), false, 8f, 0.65f);
        flightDisplay = new DroneFlightDisplay();
        root.Q("flightGeometry").Add(flightDisplay);
        root.Query<VisualElement>().ForEach(element => element.pickingMode = PickingMode.Ignore);
        osd = root.Q("osd"); humanHealth = root.Q("humanHealth"); healthFill = root.Q("healthFill");
        if (osdFont != null) osd.style.unityFontDefinition = FontDefinition.FromFont(osdFont);
        damageFlash = root.Q("damageFlash"); crosshair = root.Q("crosshair"); result = root.Q("roundResult");
        role = root.Q<Label>("playerRole"); phase = root.Q<Label>("roundPhase");
        hintWrap = root.Q("hintWrap"); roleIcon = root.Q("roleIcon"); helpKey = root.Q<Label>("helpKey");
        roundTimer = root.Q<Label>("roundTimer"); aliveCount = root.Q<Label>("aliveCount");
        stockLabel = root.Q<Label>("droneStock"); resultKey = root.Q<Label>("resultKey");
        speed = root.Q<Label>("speed"); altitude = root.Q<Label>("altitude");
        throttle = root.Q<Label>("throttle"); flightMode = root.Q<Label>("flightMode");
        warning = root.Q<Label>("warning"); hint = root.Q<Label>("hint"); help = root.Q<Label>("help");
        warningPanel = root.Q("warningPanel");
        flightClock = root.Q<Label>("flightClock"); flightState = root.Q<Label>("flightState");
        droneHeading = root.Q<Label>("droneHeading"); homeDistance = root.Q<Label>("homeDistance");
        verticalSpeed = root.Q<Label>("verticalSpeed"); horizontalSpeed = root.Q<Label>("horizontalSpeed");
        groundHeight = root.Q<Label>("groundHeight"); positionX = root.Q<Label>("positionX");
        positionZ = root.Q<Label>("positionZ"); dronePitch = root.Q<Label>("dronePitch"); droneRoll = root.Q<Label>("droneRoll");
        healthTitle = root.Q<Label>("healthTitle"); healthValue = root.Q<Label>("healthValue");
        healthStatus = root.Q<Label>("healthStatus"); healthHit = root.Q<Label>("healthHit");
        resultTitle = root.Q<Label>("resultTitle"); resultReason = root.Q<Label>("resultReason");
        restartButton = root.Q<Button>("restartRound");
        result.pickingMode = PickingMode.Position;
        restartButton.pickingMode = PickingMode.Position;
        restartButton.clicked += RestartRound;
    }

    private void Update()
    {
        if (controls == null) return;
        // UIDocument rebuilds its tree after re-enabling or a script reload.
        if (document == null || document.rootVisualElement == null || osd == null || !document.rootVisualElement.Contains(osd)) Initialize(controls);
        if (document == null || osd == null) return;
        var round = controls.Round;
        bool finished = round != null && round.Phase == HideAndSeekRound.RoundPhase.Finished;
        var drone = controls.drone;
        bool onlineDrone = MultiplayerPrototype.OnlineMatch && MultiplayerPrototype.Instance.LocalIsDrone;
        bool flying = !finished && controls.ControllingDrone && drone != null && (drone.IsControlled || onlineDrone) && !drone.IsDestroyed;
        if (flying) flightDisplay.UpdateTelemetry(drone, cachedGround);
        osd.style.display = flying ? DisplayStyle.Flex : DisplayStyle.None;
        bool aiming = !finished && !controls.IsSpectating &&
            (flying || (controls.human != null && controls.human.IsControlled));
        crosshair.style.display = aiming && !flying ? DisplayStyle.Flex : DisplayStyle.None;
        result.style.display = finished ? DisplayStyle.Flex : DisplayStyle.None;
        hintWrap.style.display = finished ? DisplayStyle.None : DisplayStyle.Flex;
        roleIcon.style.display = !flying && !controls.IsSpectating ? DisplayStyle.Flex : DisplayStyle.None;
        helpKey.style.display = !controls.IsSpectating && round != null &&
            round.Phase == HideAndSeekRound.RoundPhase.Hiding ? DisplayStyle.Flex : DisplayStyle.None;
        UpdateHumanHealth(!finished && !flying && !onlineDrone);
        bool online = MultiplayerPrototype.OnlineMatch;
        stockLabel.style.display = online ? DisplayStyle.Flex : DisplayStyle.None;
        stockLabel.parent.EnableInClassList("match-stock", online);
        if (online) stockLabel.text = "Дроны: " + MultiplayerPrototype.Instance.State.DroneStock;
        if (Keyboard.current != null && Keyboard.current.hKey.wasPressedThisFrame) showHelp = !showHelp;
        help.style.display = showHelp && !finished ? DisplayStyle.Flex : DisplayStyle.None;
        if (Time.unscaledTime < nextRefresh) return;
        nextRefresh = Time.unscaledTime + 0.1f;
        role.text = controls.IsSpectating ? "Наблюдение" : flying ? "Дрон" : "Человек";
        if (round != null)
        {
            int seconds = Mathf.CeilToInt(round.RemainingSeconds);
            roundTimer.text = (seconds / 60) + ":" + (seconds % 60).ToString("00");
            phase.text = finished ? "Раунд завершён"
                : round.Phase == HideAndSeekRound.RoundPhase.Hiding ? "Прячьтесь" : "Поиск";
            aliveCount.text = "В живых: " + round.AliveHumans + " / " + round.TotalHumans;
        }
        if (finished)
        {
            resultTitle.text = round.DronesWon ? "Дроны победили" : "Люди выжили";
            resultReason.text = round.DronesWon ? "Все люди найдены."
                : "Время вышло. Выжило " + round.AliveHumans + " из " + round.TotalHumans + ".";
            if (MultiplayerPrototype.OnlineMatch) resultReason.text = MultiplayerPrototype.Instance.State.ResultReason;
            restartButton.text = !round.CanRestart ? "Ждём хоста" : round.IsRestarting ? "Загрузка…" : "Ещё раунд";
            restartButton.SetEnabled(round.CanRestart && !round.IsRestarting);
            resultKey.text = round.CanRestart ? "или нажми R" : "Следующий раунд запускает хост";
            warningPanel.style.display = DisplayStyle.None;
            return;
        }
        if (controls.IsSpectating)
            hint.text = controls.Spectator != null && controls.Spectator.IsFollowing
                ? "Ты выбыл.  Tab — следующий игрок"
                : "Ты выбыл.  WASD — свободная камера";
        else if (round != null && round.Phase == HideAndSeekRound.RoundPhase.Hiding)
            hint.text = "Найди укрытие.  H — управление";
        else
            hint.text = drone != null && drone.IsDestroyed ? "R — новый дрон"
                : flying ? "F — подрыв    Tab — человек    H — управление" : "Tab — дрон    H — управление";
        if (online)
        {
            if (controls.IsSpectating)
                hint.text = onlineDrone ? "Дрон потерян. Следующий запуск — автоматически."
                    : controls.Spectator.IsFollowing ? "Ты выбыл. Tab — следующий игрок" : "Ты выбыл. Ожидай конца раунда.";
            else if (onlineDrone && round.Phase == HideAndSeekRound.RoundPhase.Hiding)
                hint.text = "Люди прячутся. Ожидай начала поиска.";
            else if (onlineDrone) hint.text = "F — подрыв    H — управление    Esc — меню";
            else hint.text = round.Phase == HideAndSeekRound.RoundPhase.Hiding
                ? "Найди укрытие. H — управление" : "H — управление    Esc — меню";
        }
        help.text = controls.IsSpectating
            ? "Tab — следующий игрок\nСвободная камера: WASD, Space / Ctrl\nShift — быстрее"
            : flying
                ? "WASD — наклон\nМышь — поворот и обзор\nSpace / Ctrl — вверх и вниз\nC — камера вперёд\nV — режим полёта   B — моторы\nF — подрыв   Tab — человек"
                : "WASD — движение\nМышь — обзор\nShift — бег\nCtrl — присесть\nSpace — прыжок";
        if (online) help.text = help.text.Replace("   Tab — человек", "").Replace("Свободная камера: WASD, Space / Ctrl\nShift — быстрее", "Esc — меню");
        warning.text = "";
        if (flying)
        {
            speed.text = (drone.Velocity.magnitude * 3.6f).ToString("0.0");
            float ground = float.PositiveInfinity;
            int count = Physics.RaycastNonAlloc(drone.transform.position + Vector3.up * 0.1f,
                Vector3.down, groundHits, 500f, ~0, QueryTriggerInteraction.Ignore);
            var body = drone.GetComponent<Rigidbody>();
            for (int i = 0; i < count; i++)
                if (groundHits[i].rigidbody != body) ground = Mathf.Min(ground, groundHits[i].distance);
            cachedGround = float.IsInfinity(ground) ? ground : Mathf.Max(0f, ground - 0.1f);
            altitude.text = float.IsInfinity(cachedGround) ? "—" : cachedGround.ToString("0.0");
            throttle.text = "ГАЗ " + Mathf.RoundToInt(drone.Throttle * 100f) + "%";
            flightMode.text = drone.flightMode == DroneController.FlightMode.Angle ? "FPV / ANGLE" : "FPV / ACRO";
            int flightSeconds = (int)drone.FlightSeconds;
            flightClock.text = "В ПОЛЁТЕ " + (flightSeconds / 60).ToString("00") + ":" + (flightSeconds % 60).ToString("00");
            flightState.text = drone.MotorsArmed ? "МОТОРЫ ВКЛ." : "МОТОРЫ ВЫКЛ.";
            droneHeading.text = Mathf.Repeat(drone.transform.eulerAngles.y, 360f).ToString("000") + "°";
            homeDistance.text = "ДОМ  " + Vector3.Distance(drone.transform.position, drone.HomePosition).ToString("0") + " м";
            verticalSpeed.text = "ПОДЪЁМ " + drone.Velocity.y.ToString("+0.0;-0.0;0.0") + " м/с";
            horizontalSpeed.text = "V " + (Vector3.ProjectOnPlane(drone.Velocity, Vector3.up).magnitude * 3.6f).ToString("0.0") + " км/ч";
            groundHeight.text = "H " + altitude.text + " м";
            positionX.text = "X " + drone.transform.position.x.ToString("0.0") + " м";
            positionZ.text = "Z " + drone.transform.position.z.ToString("0.0") + " м";
            dronePitch.text = "PITCH " + Mathf.DeltaAngle(0, drone.transform.eulerAngles.x).ToString("+0;-0;0") + "°";
            droneRoll.text = "ROLL  " + Mathf.DeltaAngle(0, drone.transform.eulerAngles.z).ToString("+0;-0;0") + "°";
            if (!drone.MotorsArmed) warning.text = "Моторы выключены.  B — включить";
            else if (ground < 1.5f && drone.Velocity.y < -1.5f) warning.text = "Низко";
        }
        if (!controls.IsSpectating && UnityEngine.Cursor.lockState != CursorLockMode.Locked)
            warning.text = "Нажми на экран, чтобы продолжить";
        warningPanel.style.display = warning.text.Length > 0 ? DisplayStyle.Flex : DisplayStyle.None;
    }

    private void UpdateHumanHealth(bool visible)
    {
        var human = controls.IsSpectating && controls.Spectator != null && controls.Spectator.IsFollowing
            ? controls.Spectator.ObservedHuman : controls.human;
        visible &= human != null;
        humanHealth.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
        damageFlash.style.opacity = 0f;
        if (!visible) return;
        float fraction = Mathf.Clamp01(human.Health / Mathf.Max(1f, human.maxHealth));
        Color color = fraction <= 0.3f ? new Color(0.86f, 0.39f, 0.32f)
            : fraction <= 0.6f ? new Color(0.95f, 0.8f, 0.52f) : new Color(0.96f, 0.98f, 0.97f);
        healthFill.style.width = Length.Percent(fraction * 100f);
        healthFill.style.backgroundColor = color;
        healthValue.text = Mathf.CeilToInt(human.Health).ToString();
        healthTitle.text = controls.IsSpectating && !human.IsEliminated ? "Здоровье игрока" : "Здоровье";
        healthTitle.style.display = controls.IsSpectating && !human.IsEliminated ? DisplayStyle.Flex : DisplayStyle.None;
        healthStatus.text = human.IsEliminated ? "Выбыл" : fraction <= 0.3f ? "Тяжёлое ранение"
            : fraction < 0.999f ? "Ранен" : "";
        float sinceDamage = Time.unscaledTime - human.LastDamageTime;
        healthHit.text = sinceDamage < 1.5f ? "−" + Mathf.CeilToInt(human.LastDamage) : "";
        healthStatus.parent.style.display = healthStatus.text.Length > 0 || healthHit.text.Length > 0
            ? DisplayStyle.Flex : DisplayStyle.None;
        if (human == controls.human)
            damageFlash.style.opacity = Mathf.Clamp01(1f - sinceDamage / 0.6f);
    }

    private void RestartRound()
    {
        if (controls != null && controls.Round != null) controls.Round.RestartRound();
    }

    private void OnDestroy()
    {
        if (restartButton != null) restartButton.clicked -= RestartRound;
        if (document != null) Destroy(document);
        if (panel != null) Destroy(panel);
        if (osdFont != null) Destroy(osdFont);
    }
}
