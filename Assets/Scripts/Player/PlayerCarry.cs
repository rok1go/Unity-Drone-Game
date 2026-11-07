using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

// The local player's side of grabbing: hold the left mouse button to hold an object, release to let
// go (it keeps its momentum, so a swing throws it), right mouse button fixes it to what it touches.
// Offline and on the host the CarrySystem runs directly; an online client asks the host, which
// simulates the grip, and the client only shows the outcome. Also owns the hover hint, the outline
// of the targeted object and the marker of the point it would be fixed at.
public static class PlayerCarry
{
    private static Rigidbody carried;
    private static HumanController owner;
    private static bool requested;                 // client: waiting for the host's answer
    private static readonly Dictionary<Rigidbody, int> remoteGrips = new Dictionary<Rigidbody, int>();
    private static CarryVisuals visuals;

    public static bool IsCarrying => carried != null;
    // RMB held and dragged while holding: the mouse turns the object, not the view.
    public static bool Rotating { get; private set; }
    private static float holdDistance;
    private static Quaternion holdRelative = Quaternion.identity;
    private static float rmbDownAt, nextHoldSend;
    private static bool rmbDown, holdDirty;
    private static Vector2 rmbTravel;
    public static string Hint { get; private set; }

    private static bool Authority => !MultiplayerPrototype.OnlineMatch || MultiplayerPrototype.Instance.IsHost;
    private static ulong LocalCarrier => MultiplayerPrototype.OnlineMatch
        ? MultiplayerPrototype.Instance.LocalId : CarrySystem.OfflineCarrier;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Init()
    {
        carried = null; owner = null; requested = false; Hint = null; visuals = null;
        remoteGrips.Clear();
        CarrySystem.CarryChanged += (carrier, body) => { if (carrier == LocalCarrier) SetLocal(body); };
    }

    public static int CarrierCount(Rigidbody body)
    {
        if (body == null) return 0;
        if (Authority) return CarrySystem.Instance.CarrierCount(body);
        return remoteGrips.TryGetValue(body, out int n) ? n : 0;
    }

    // Movement scale while holding something: your share of its weight against your strength.
    public static float LoadFactor(HumanController human)
    {
        if (human != owner || carried == null) return 0f;
        float share = carried.mass / Mathf.Max(1, CarrierCount(carried));
        return Mathf.Clamp01(share / CarrySystem.PlayerStrength);
    }

    // Called every frame by the controlled human. Handles input and refreshes the hint and visuals.
    public static void Tick(HumanController human, Ray view, bool inputEnabled)
    {
        inputEnabled &= MatchRules.Current.Carrying;
        var mouse = Mouse.current;
        if (inputEnabled && mouse != null)
        {
            if (carried == null && !requested && mouse.leftButton.wasPressedThisFrame) TryGrab(human, view);
            else if (carried != null && !mouse.leftButton.isPressed) Drop();
            else if (carried != null) Steer(human, mouse);
        }
        else if (carried != null) Drop();
        if (carried == null) { Rotating = false; rmbDown = false; }
        Refresh(view, inputEnabled);
    }

    // While holding: the wheel moves it nearer or further (within arm's reach); a short RMB click fixes it in
    // place as before, RMB held and dragged turns it (yaw with mouse X, tilt with mouse Y).
    private static void Steer(HumanController human, Mouse mouse)
    {
        float wheel = mouse.scroll.ReadValue().y;
        if (Mathf.Abs(wheel) > 0.1f)
        {
            holdDistance = Mathf.Clamp(holdDistance + Mathf.Sign(wheel) * 0.15f, CarrySystem.MinHold, CarrySystem.Reach);
            holdDirty = true;
        }
        if (mouse.rightButton.wasPressedThisFrame) { rmbDown = true; rmbDownAt = Time.unscaledTime; rmbTravel = Vector2.zero; }
        if (rmbDown && mouse.rightButton.isPressed)
        {
            Vector2 delta = mouse.delta.ReadValue();
            rmbTravel += new Vector2(Mathf.Abs(delta.x), Mathf.Abs(delta.y));
            if (!Rotating && (rmbTravel.magnitude > 12f || Time.unscaledTime - rmbDownAt > 0.3f)) Rotating = true;
            if (Rotating && delta.sqrMagnitude > 0f)
            {
                var heading = CarrySystem.Heading(human.viewCamera.transform);
                // In the carrier's frame: mouse X turns about up, mouse Y tilts about the carrier's right.
                holdRelative = Quaternion.AngleAxis(delta.x * 0.35f, Vector3.up) *
                               Quaternion.AngleAxis(-delta.y * 0.35f, Vector3.right) * holdRelative;
                holdRelative = Quaternion.Normalize(holdRelative);
                holdDirty = true;
            }
        }
        else if (rmbDown)
        {
            // Released: a click without dragging is the old "fix it in place"; after a turn, physics resumes.
            if (!Rotating) Attach();
            else holdDirty = true;
            rmbDown = false; Rotating = false;
        }
        if (Rotating && !holdDirty && Time.unscaledTime >= nextHoldSend) holdDirty = true;
        if (holdDirty && Time.unscaledTime >= nextHoldSend)
        {
            holdDirty = false;
            nextHoldSend = Time.unscaledTime + 1f / 15f;
            if (Authority) CarrySystem.Instance.SetHold(LocalCarrier, holdDistance, holdRelative, Rotating);
            else MultiplayerPrototype.Instance.RequestHold(holdDistance, holdRelative, Rotating);
        }
    }

    private static void TryGrab(HumanController human, Ray view)
    {
        if (!CarrySystem.TryFindTarget(view, out var body, out var point) || body.mass > CarrySystem.MaxMass) return;
        owner = human;
        // The same starting hold the authority computes, so wheel and drag continue from it.
        holdDistance = Mathf.Clamp(Vector3.Distance(view.origin, point), CarrySystem.MinHold, CarrySystem.Reach);
        holdRelative = Quaternion.Inverse(CarrySystem.Heading(human.viewCamera.transform)) * body.rotation;
        holdDirty = false; Rotating = false; rmbDown = false;
        Vector3 local = body.transform.InverseTransformPoint(point);
        if (Authority)
            CarrySystem.Instance.Grab(LocalCarrier, human.viewCamera.transform, human.GetComponentsInChildren<Collider>(),
                body, local);
        else
        {
            requested = true;
            MultiplayerPrototype.Instance.RequestGrab(body, local);
        }
    }

    private static float nextPush;

    // F: one shove of what is under the crosshair. Offline / host apply it; a client asks the host.
    public static void TryPush(HumanController human, Ray view)
    {
        if (carried != null || Time.unscaledTime < nextPush) return;
        if (!Physics.Raycast(view, out var hit, CarrySystem.PushReach, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore) ||
            !CarrySystem.IsCarryable(hit.rigidbody) || CarrierCount(hit.rigidbody) > 0) return;
        nextPush = Time.unscaledTime + 0.4f;
        if (Authority) CarrySystem.Push(hit.rigidbody, hit.point, view.direction);
        else MultiplayerPrototype.Instance.RequestPush(hit.rigidbody, hit.point);
    }

    // Switching to the drone, dying or a round ending lets go and clears the highlight.
    public static void ControlLost(HumanController human)
    {
        if (human == owner) Drop();
        Hint = null;
        if (visuals != null) visuals.Hide();
    }

    public static void Drop()
    {
        if (carried == null) return;
        if (Authority) CarrySystem.Instance.Release(LocalCarrier);
        else MultiplayerPrototype.Instance.RequestDrop();
    }

    private static void Attach()
    {
        if (carried == null || !CarrySystem.TryFindContact(carried, out _, out _)) return;
        if (Authority) CarrySystem.Instance.Attach(LocalCarrier);
        else MultiplayerPrototype.Instance.RequestAttach();
    }

    // E draws a nearby fixing surface into contact, then uses the same host-owned joint as RMB.
    public static bool TryQuickBarricade()
    {
        if (!MatchRules.Current.Barricades || carried == null) return false;
        if (!CarrySystem.TryFindFixing(carried, 0.4f, out _, out _, out _)) return false;
        if (Authority) CarrySystem.Instance.QuickBarricade(LocalCarrier);
        else MultiplayerPrototype.Instance.RequestAttach(true);
        return true;
    }

    // Host answers for an online client (null = not holding anything).
    public static void ApplyRemoteState(Rigidbody body)
    {
        requested = false;
        SetLocal(body);
    }

    public static void ApplyRemoteGrips(Rigidbody body, int count)
    {
        if (body == null) return;
        if (count > 0) remoteGrips[body] = count;
        else remoteGrips.Remove(body);
    }

    private static void SetLocal(Rigidbody body)
    {
        carried = body;
    }

    // ---------------- hint and visuals ----------------

    private static void Refresh(Ray view, bool active)
    {
        if (visuals == null) visuals = CarryVisuals.Create();
        Hint = null;
        if (!active)
        {
            visuals.Hide();
            return;
        }
        if (carried != null)
        {
            int need = CarrySystem.RequiredCarriers(carried.mass);
            string players = need > 1 ? "Игроков: " + CarrierCount(carried) + "/" + need + "  ·  " : "";
            bool canFix = CarrySystem.TryFindContact(carried, out _, out var point);
            Hint = players + "Отпустить ЛКМ — Бросить  ·  Колесо — ближе/дальше  ·  Удерж. ПКМ — Вращать" +
                (canFix && MatchRules.Current.Barricades ? "  ·  ПКМ — Закрепить" : "");
            visuals.Show(carried, CarryVisuals.ForMass(carried.mass), canFix, point);
            return;
        }
        // Nothing nobody can move is highlighted: it is not interactive.
        if (!CarrySystem.TryFindTarget(view, out var body, out _) || body.mass > CarrySystem.MaxMass)
        {
            visuals.Hide();
            return;
        }
        string name = CarrySystem.Describe(body) + " (" + Mathf.RoundToInt(body.mass) + " кг)";
        int required = CarrySystem.RequiredCarriers(body.mass);
        int holding = CarrierCount(body);
        // Furniture can also be shoved; small things are simply picked up.
        string push = body.mass > CarrySystem.EffortMass && holding == 0 ? "  ·  " + GameSettings.KeyName(GameAction.Push) + " — Толкать" : "";
        if (required > 1)
            Hint = name + "  ·  Нужно 2 игрока" + (holding > 0 ? " (" + holding + "/" + required + ")" : "") +
                "  ·  ЛКМ — Схватить" + push;
        else if (body.mass > CarrySystem.EffortMass) Hint = name + "  ·  Тяжёлый  ·  ЛКМ — Схватить" + push;
        else Hint = name + "  ·  ЛКМ — Схватить";
        visuals.Show(body, CarryVisuals.ForMass(body.mass), false, default);
    }
}

// Thin outline hugging the targeted object (its box in the object's own frame, so it follows rotation)
// and a dot where it would be fixed. The colour is the weight class.
public sealed class CarryVisuals : MonoBehaviour
{
    public static readonly Color Light = new Color(0.55f, 0.95f, 0.65f);    // one player, freely
    public static readonly Color Heavy = new Color(1f, 0.8f, 0.3f);         // one player, slowed
    public static readonly Color TwoPlayers = new Color(1f, 0.42f, 0.36f);  // needs a second player
    private static readonly Vector3[] boxCorners = new Vector3[8];
    private LineRenderer outline;
    private Transform marker;
    private Material markerMaterial;
    private readonly Vector3[] corners = new Vector3[16];

    public static CarryVisuals Create()
    {
        var obj = new GameObject("CarryVisuals");
        DontDestroyOnLoad(obj);
        var v = obj.AddComponent<CarryVisuals>();
        v.outline = obj.AddComponent<LineRenderer>();
        v.outline.positionCount = 16;
        v.outline.widthMultiplier = 0.012f;
        v.outline.useWorldSpace = true;
        v.outline.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        v.outline.receiveShadows = false;
        v.outline.material = new Material(Shader.Find("Sprites/Default"));
        var dot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        Destroy(dot.GetComponent<Collider>());
        dot.transform.SetParent(obj.transform, false);
        dot.transform.localScale = Vector3.one * 0.08f;
        v.markerMaterial = new Material(Shader.Find("Sprites/Default")) { color = Light };
        dot.GetComponent<MeshRenderer>().sharedMaterial = v.markerMaterial;
        v.marker = dot.transform;
        v.Hide();
        return v;
    }

    public void Hide()
    {
        if (outline != null) outline.enabled = false;
        if (marker != null) marker.gameObject.SetActive(false);
    }

    public static Color ForMass(float mass) =>
        mass <= CarrySystem.EffortMass ? Light : mass <= CarrySystem.PlayerStrength ? Heavy : TwoPlayers;

    public void Show(Rigidbody body, Color colour, bool showMarker, Vector3 point)
    {
        // Bounds of every collider in the body's local frame.
        Transform frame = body.transform;
        Bounds b = default;
        bool any = false;
        foreach (var c in body.GetComponentsInChildren<Collider>())
        {
            if (!c.enabled) continue;
            int n = Corners(c);
            for (int k = 0; k < n; k++)
            {
                Vector3 local = frame.InverseTransformPoint(boxCorners[k]);
                if (!any) { b = new Bounds(local, Vector3.zero); any = true; }
                else b.Encapsulate(local);
            }
        }
        if (!any) { Hide(); return; }
        Vector3 pad = new Vector3(1f / Mathf.Max(0.01f, frame.lossyScale.x), 1f / Mathf.Max(0.01f, frame.lossyScale.y),
            1f / Mathf.Max(0.01f, frame.lossyScale.z)) * 0.01f;
        Vector3 min = b.min - pad, max = b.max + pad;
        // One continuous path over all twelve edges of the box.
        Vector3 p(int x, int y, int z) => frame.TransformPoint(new Vector3(x == 0 ? min.x : max.x, y == 0 ? min.y : max.y,
            z == 0 ? min.z : max.z));
        int i = 0;
        foreach (var c in new[] { p(0,0,0), p(1,0,0), p(1,0,1), p(0,0,1), p(0,0,0), p(0,1,0), p(1,1,0), p(1,0,0),
                                  p(1,1,0), p(1,1,1), p(1,0,1), p(1,1,1), p(0,1,1), p(0,0,1), p(0,1,1), p(0,1,0) })
            corners[i++] = c;
        outline.SetPositions(corners);
        outline.startColor = outline.endColor = colour;
        outline.enabled = true;
        marker.gameObject.SetActive(showMarker);
        if (showMarker) marker.position = point;
    }

    // World corners of a collider: exact for boxes, its world bounds otherwise.
    private static int Corners(Collider collider)
    {
        Vector3 centre, half;
        Transform t = null;
        if (collider is BoxCollider box)
        {
            t = box.transform;
            centre = box.center;
            half = box.size * 0.5f;
        }
        else
        {
            Bounds world = collider.bounds;
            centre = world.center;
            half = world.extents;
        }
        int i = 0;
        for (int x = -1; x <= 1; x += 2)
            for (int y = -1; y <= 1; y += 2)
                for (int z = -1; z <= 1; z += 2)
                {
                    Vector3 corner = centre + Vector3.Scale(half, new Vector3(x, y, z));
                    boxCorners[i++] = t != null ? t.TransformPoint(corner) : corner;
                }
        return i;
    }
}
