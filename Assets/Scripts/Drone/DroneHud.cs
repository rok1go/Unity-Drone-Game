using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

// One retained overlay; text refreshes at 10 Hz rather than rebuilding UI every frame.
public class DroneHud : MonoBehaviour
{
    private LocalControlSwitcher controls;
    private UIDocument document;
    private PanelSettings panel;
    private VisualElement osd, pilotHint, horizon, throttleFill, help;
    private Label speed, altitude, climb, heading, throttle, mode, timer, home, warning, hint, attitude;
    private readonly RaycastHit[] groundHits = new RaycastHit[16];
    private float nextRefresh;
    private bool showHelp = true;
    private static readonly string[] Cardinal = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };

    public void Initialize(LocalControlSwitcher switcher)
    {
        controls = switcher;
        var tree = Resources.Load<VisualTreeAsset>("UI/FpvHud");
        var sheet = Resources.Load<StyleSheet>("UI/FpvHud");
        if (tree == null || sheet == null)
        {
            Debug.LogError("FPV HUD resources are missing.", this);
            enabled = false;
            return;
        }
        panel = ScriptableObject.CreateInstance<PanelSettings>();
        panel.name = "FPV OSD (runtime)";
        panel.themeStyleSheet = Resources.Load<ThemeStyleSheet>("UI/FpvTheme");
        panel.scaleMode = PanelScaleMode.ScaleWithScreenSize;
        panel.referenceResolution = new Vector2Int(1440, 900);
        panel.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
        panel.match = 0.5f;
        panel.sortingOrder = 50;
        document = gameObject.AddComponent<UIDocument>();
        document.panelSettings = panel;
        var root = document.rootVisualElement;
        root.style.position = Position.Absolute;
        root.style.left = root.style.right = root.style.top = root.style.bottom = new StyleLength(0f);
        tree.CloneTree(root);
        root.styleSheets.Add(sheet);
        root.style.unityFontDefinition = FontDefinition.FromFont(Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));
        root.Query<VisualElement>().ForEach(element => element.pickingMode = PickingMode.Ignore);
        osd = root.Q("osd"); pilotHint = root.Q("pilotHint"); horizon = root.Q("horizon");
        throttleFill = root.Q("throttleFill"); help = root.Q("help");
        speed = root.Q<Label>("speed"); altitude = root.Q<Label>("altitude"); climb = root.Q<Label>("climb");
        heading = root.Q<Label>("heading"); throttle = root.Q<Label>("throttle"); mode = root.Q<Label>("mode");
        timer = root.Q<Label>("timer"); home = root.Q<Label>("home"); warning = root.Q<Label>("warning");
        hint = root.Q<Label>("hint"); attitude = root.Q<Label>("attitude");
    }

    private void Update()
    {
        if (controls == null || document == null) return;
        var drone = controls.drone;
        if (drone == null) return;
        bool active = controls.ControllingDrone && drone.IsControlled && !drone.IsDestroyed;
        osd.style.display = active ? DisplayStyle.Flex : DisplayStyle.None;
        pilotHint.style.display = active ? DisplayStyle.None : DisplayStyle.Flex;
        hint.text = drone.IsDestroyed ? "ДРОН ПОТЕРЯН   /   R — новый вылет" : "TAB — FPV дрон   /   R — вернуть дрон на старт";
        if (!active) return;
        if (Keyboard.current != null && Keyboard.current.hKey.wasPressedThisFrame) showHelp = !showHelp;
        help.style.display = showHelp ? DisplayStyle.Flex : DisplayStyle.None;
        float pitch = Mathf.DeltaAngle(0, drone.transform.eulerAngles.x);
        float roll = Mathf.DeltaAngle(0, drone.transform.eulerAngles.z);
        var view = drone.viewCamera != null ? drone.viewCamera.transform : drone.transform;
        float viewPitch = -Mathf.Asin(Mathf.Clamp(view.forward.y, -1f, 1f)) * Mathf.Rad2Deg;
        float viewRoll = Mathf.Atan2(view.right.y, view.up.y) * Mathf.Rad2Deg;
        horizon.style.rotate = new Rotate(Angle.Degrees(viewRoll));
        horizon.style.translate = new Translate(0, Mathf.Clamp(-viewPitch * 3f, -115, 115), 0);
        throttleFill.style.height = Length.Percent(drone.Throttle * 100f);
        if (Time.unscaledTime < nextRefresh) return;
        nextRefresh = Time.unscaledTime + 0.1f;
        Vector3 velocity = drone.Velocity;
        speed.text = (velocity.magnitude * 3.6f).ToString("000");
        float ground = float.PositiveInfinity;
        int count = Physics.RaycastNonAlloc(drone.transform.position + Vector3.up * 0.1f, Vector3.down,
            groundHits, 500f, ~0, QueryTriggerInteraction.Ignore);
        var body = drone.GetComponent<Rigidbody>();
        for (int i = 0; i < count; i++)
            if (groundHits[i].rigidbody != body) ground = Mathf.Min(ground, groundHits[i].distance);
        altitude.text = float.IsInfinity(ground) ? "---" : Mathf.Max(0, ground - 0.1f).ToString("0.0");
        climb.text = velocity.y.ToString("+0.0;-0.0;0.0") + " м/с";
        float bearing = Mathf.Repeat(drone.transform.eulerAngles.y, 360);
        heading.text = Cardinal[Mathf.RoundToInt(bearing / 45f) % 8] + "   " + Mathf.FloorToInt(bearing).ToString("000") + "°";
        throttle.text = Mathf.RoundToInt(drone.Throttle * 100) + "%";
        mode.text = drone.flightMode == DroneController.FlightMode.Angle ? "ANGLE / СТАБИЛИЗАЦИЯ" : "ACRO / RATE";
        timer.text = ((int)drone.FlightSeconds / 60).ToString("00") + ":" + ((int)drone.FlightSeconds % 60).ToString("00");
        Vector3 offset = drone.transform.position - drone.HomePosition; offset.y = 0;
        home.text = "HOME  " + offset.magnitude.ToString("0") + " м";
        attitude.text = "P " + pitch.ToString("+0;-0;0") + "°     R " + (-roll).ToString("+0;-0;0") + "°";
        warning.text = drone.IsArmed ? "ПОДРЫВ  " + drone.DetonationRemaining.ToString("0.0") + " с"
            : !drone.MotorsArmed ? "МОТОРЫ ВЫКЛЮЧЕНЫ  /  B — включить"
            : UnityEngine.Cursor.lockState != CursorLockMode.Locked ? "ЩЁЛКНИ ДЛЯ УПРАВЛЕНИЯ"
            : ground < 1.5f && velocity.y < -1.5f ? "НИЗКО!  ДОБАВЬ ГАЗ" : "";
        warning.style.display = warning.text.Length == 0 ? DisplayStyle.None : DisplayStyle.Flex;
    }

    private void OnDestroy()
    {
        if (document != null) Destroy(document);
        if (panel != null) Destroy(panel);
    }
}
