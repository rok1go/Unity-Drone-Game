using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

// A hinged door in a voxel doorway. E opens it away from the player and closes it again; holding E on
// the inner side of a lockable door locks or unlocks it. A nearby blast tears the leaf off and it becomes
// an ordinary physics prop (carry, throw, barricade). State changes are raised for multiplayer replication.
[DisallowMultipleComponent]
public sealed class InteractiveDoor : MonoBehaviour, IInteractable, IHoldInteractable
{
    public enum DoorState : sbyte { Closed = 0, OpenPositive = 1, OpenNegative = -1, Broken = 2, Locked = 3 }

    private const float InteractionDistance = 2.6f;
    private const float SwingDegreesPerSecond = 300f;
    private static readonly List<InteractiveDoor> all = new List<InteractiveDoor>();
    private static Material woodMaterial, handleMaterial;
    private static readonly Dictionary<Color, Material> tinted = new Dictionary<Color, Material>();
    private static readonly Collider[] overlaps = new Collider[8];

    public static IReadOnlyList<InteractiveDoor> All => all;
    // Door, new state, position that caused it (for the swing side).
    public static event Action<InteractiveDoor, DoorState> Changed;

    public DoorState State { get; private set; } = DoorState.Closed;
    public bool IsOpen => State == DoorState.OpenPositive || State == DoorState.OpenNegative;
    public bool IsLocked => State == DoorState.Locked;
    // Narrow leaves are the doors of hiding wardrobes; the HUD names them so.
    public string Noun => width < 0.65f ? "дверцу шкафа" : "дверь";
    public string Prompt => State == DoorState.Broken ? null :
        IsLocked && !Inside(LocalUser) ? "Заперто" : (IsOpen ? "Закрыть " : "Открыть ") + Noun;
    // Holding the same key: lock or unlock, offered only on the inner side of a closed lockable door.
    public string HoldPrompt => !Lockable || IsOpen || State == DoorState.Broken || !Inside(LocalUser) ? null :
        IsLocked ? "Отпереть" : "Запереть";
    public void Interact(Vector3 user)
    {
        if (IsLocked && !Inside(user)) { Rattle(); return; }
        Toggle(user);
    }
    public void HoldInteract(Vector3 user)
    {
        if (!Lockable || IsOpen || State == DoorState.Broken || !Inside(user)) return;
        SetState(IsLocked ? DoorState.Closed : DoorState.Locked);
    }
    // House entrances and most room doors; never wardrobes or vehicles.
    public bool Lockable { get; private set; }
    // The leaf after a blast tore it off: an ordinary physics prop.
    public Rigidbody BrokenBody { get; private set; }
    private Vector3 insideDirection;
    private static Vector3 LocalUser => MultiplayerPrototype.Instance != null && MultiplayerPrototype.Instance.TryLocalView(out var view)
        ? view.origin : Vector3.positiveInfinity;
    private Transform panel;
    private Vector3 span;           // world direction from the hinge to the free edge
    private float width;
    private Quaternion closedRotation;
    private float angle, targetAngle;
    private int fixedSwing;         // 0: away from the user; +1/-1: always that way (vehicle doors)
    private BoxCollider panelCollider;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        all.Clear();
        tinted.Clear();
        Changed = null;
    }

    public static InteractiveDoor Create(Transform parent, string id, Vector3 hinge, Vector3 freeEdge, float height,
        float thickness, int swing = 0, Color? tint = null)
    {
        var root = new GameObject(id);
        root.transform.SetParent(parent, false);
        var door = root.AddComponent<InteractiveDoor>();
        door.fixedSwing = swing;
        door.Build(hinge, freeEdge, height, thickness, tint);
        return door;
    }

    private void Build(Vector3 hinge, Vector3 freeEdge, float height, float thickness, Color? tint)
    {
        Vector3 along = freeEdge - hinge;
        width = along.magnitude;
        span = along / Mathf.Max(0.001f, width);
        transform.position = hinge;
        closedRotation = Quaternion.LookRotation(Vector3.Cross(span, Vector3.up), Vector3.up);
        transform.rotation = closedRotation;
        if (woodMaterial == null)
        {
            woodMaterial = new Material(Shader.Find("Standard")) { name = "Door wood", color = new Color(0.47f, 0.31f, 0.19f) };
            woodMaterial.SetFloat("_Glossiness", 0.1f);
            handleMaterial = new Material(Shader.Find("Standard")) { name = "Door handle", color = new Color(0.8f, 0.78f, 0.7f) };
        }
        // Local +X runs from the hinge to the free edge in the closed pose.
        float gap = 0.02f;
        panel = GameObject.CreatePrimitive(PrimitiveType.Cube).transform;
        panel.name = "Panel";
        panel.SetParent(transform, false);
        panel.localPosition = new Vector3(SideSign() * width * 0.5f, height * 0.5f, 0f);
        panel.localScale = new Vector3(width - gap * 2f, height - gap, thickness);
        panel.GetComponent<MeshRenderer>().sharedMaterial = tint.HasValue ? Tinted(tint.Value) : woodMaterial;
        panelCollider = panel.GetComponent<BoxCollider>();
        var handle = GameObject.CreatePrimitive(PrimitiveType.Cube).transform;
        handle.name = "Handle";
        Destroy(handle.GetComponent<Collider>());
        handle.SetParent(panel, false);
        handle.localPosition = new Vector3(0.38f, -0.02f, 0f);
        handle.localScale = new Vector3(0.12f / Mathf.Max(0.1f, width), 0.03f / height, 2.2f);
        handle.GetComponent<MeshRenderer>().sharedMaterial = handleMaterial;
        all.Add(this);
    }

    private static Material Tinted(Color colour)
    {
        if (tinted.TryGetValue(colour, out var material)) return material;
        material = new Material(Shader.Find("Standard")) { name = "Door paint", color = colour };
        material.SetFloat("_Glossiness", 0.35f);
        tinted[colour] = material;
        return material;
    }

    // LookRotation(cross(span, up)) puts span on local -X or +X depending on handedness; keep the panel on the span side.
    private float SideSign() => Vector3.Dot(closedRotation * Vector3.right, span) >= 0f ? 1f : -1f;

    private void OnDestroy() => all.Remove(this);

    public static bool TryGetTarget(Ray view, out InteractiveDoor door)
    {
        door = null;
        if (!Physics.Raycast(view, out var hit, InteractionDistance, Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Ignore)) return false;
        door = hit.collider.GetComponentInParent<InteractiveDoor>();
        return door != null && door.State != DoorState.Broken;
    }

    public static bool TryInteract(Ray view, Vector3 user)
    {
        if (!TryGetTarget(view, out var door)) return false;
        door.Toggle(user);
        return true;
    }

    public void Toggle(Vector3 user) => SetState(IsOpen ? DoorState.Closed : OpenAwayFrom(user));

    // The side a lock is worked from: the room (or the building) the door closes off.
    public bool Inside(Vector3 user) =>
        Lockable && !float.IsInfinity(user.x) && Vector3.Dot(user - transform.position, insideDirection) > 0f;

    // Whether a player at `user` may put the door into `next`; the host validates remote requests with it.
    public bool Allows(DoorState next, Vector3 user)
    {
        if (State == DoorState.Broken || next == DoorState.Broken) return false;
        if (next == DoorState.Locked) return Lockable && State == DoorState.Closed && Inside(user);
        if (State == DoorState.Locked) return Inside(user);
        return true;
    }

    // Chooses which doors lock and from which side, from map geometry only, so every peer agrees.
    // Inside is the side under a ceiling; between two rooms, the smaller one (nearer opposite wall).
    public void ConfigureLock()
    {
        Lockable = false;
        if (fixedSwing != 0 || width < 0.65f || panel == null) return;
        Vector3 normal = Vector3.Cross(Vector3.up, span).normalized;
        Vector3 middle = transform.position + span * width * 0.5f + Vector3.up * 1.2f;
        float Room(Vector3 side)
        {
            Vector3 probe = middle + side * 0.8f;
            if (!Physics.Raycast(probe, Vector3.up, 3.5f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                return -1f;    // open sky: outdoors
            return Physics.Raycast(probe, side, out var wall, 12f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)
                ? wall.distance : 12f;
        }
        float a = Room(normal), b = Room(-normal);
        if (a < 0f && b < 0f) return;
        insideDirection = a < 0f ? -normal : b < 0f ? normal : (a <= b ? normal : -normal);
        // Every outer door; about two room doors in three.
        int hash = 17;
        foreach (char c in name) hash = unchecked(hash * 31 + c);
        Lockable = a < 0f || b < 0f || (hash & 0x7fffffff) % 3 != 0;
    }

    private void Rattle()
    {
        if (panel == null) return;
        GameAudio.Play(SoundBank.DoorClose, panel.position, 0.25f, UnityEngine.Random.Range(1.05f, 1.1f),
            SoundCategory.Effects, 1.5f, 20f);
    }

    public DoorState OpenAwayFrom(Vector3 user)
    {
        if (fixedSwing != 0) return fixedSwing > 0 ? DoorState.OpenPositive : DoorState.OpenNegative;
        // Swing so the free edge moves away from whoever opens it.
        Vector3 normal = Vector3.Cross(Vector3.up, span);
        return Vector3.Dot(user - transform.position, normal) > 0f ? DoorState.OpenNegative : DoorState.OpenPositive;
    }

    public void SetState(DoorState state, bool raise = true)
    {
        if (state == State || State == DoorState.Broken) return;
        bool lockChange = state == DoorState.Locked || (State == DoorState.Locked && state == DoorState.Closed);
        // An open door is closed by the same request that locks it (a remote lock that crossed a close).
        State = state;
        if (state == DoorState.Broken) TearOff(transform.position + Vector3.up, 4f);
        else if (lockChange)
        {
            targetAngle = 0f;
            GameAudio.Play(SoundBank.Switch, panel != null ? panel.position : transform.position, 0.35f,
                state == DoorState.Locked ? 0.92f : 1.05f, SoundCategory.Effects, 1f, 14f);
        }
        else
        {
            targetAngle = state == DoorState.OpenPositive ? 90f : state == DoorState.OpenNegative ? -90f : 0f;
            // Wardrobe leaves are smaller: a lighter, higher sound.
            float pitch = (width < 0.65f ? 1.3f : 1f) * UnityEngine.Random.Range(0.94f, 1.06f);
            if (fixedSwing != 0)
                GameAudio.Play(state == DoorState.Closed ? SoundBank.CarDoorClose : SoundBank.CarDoorOpen,
                    panel != null ? panel.position : transform.position, state == DoorState.Closed ? 0.55f : 0.4f,
                    UnityEngine.Random.Range(0.96f, 1.04f), SoundCategory.Effects, 1.5f, 35f);
            else
            GameAudio.Play(state == DoorState.Closed ? SoundBank.DoorClose : SoundBank.DoorOpen, panel != null
                ? panel.position : transform.position, width < 0.65f ? 0.45f : 0.7f, pitch, SoundCategory.Effects, 1.5f, 30f);
        }
        if (raise) Changed?.Invoke(this, state);
    }

    // Blast damage: the panel leaves the frame as a short-lived physics prop.
    public void Break(Vector3 blast, float impulse)
    {
        if (State == DoorState.Broken) return;
        State = DoorState.Broken;
        TearOff(blast, impulse);
        Changed?.Invoke(this, DoorState.Broken);
    }

    private void TearOff(Vector3 from, float impulse)
    {
        if (panel == null) return;
        GameAudio.Play(SoundBank.DoorBreak, panel.position, 1f, UnityEngine.Random.Range(0.9f, 1.1f), SoundCategory.Effects, 3f, 60f);
        panel.SetParent(null, true);
        SceneManager.MoveGameObjectToScene(panel.gameObject, gameObject.scene);
        panel.name = name + " (leaf)";
        var body = panel.gameObject.AddComponent<Rigidbody>();
        // Solid wood at ~200 kg/m³: a house door ~25 kg (one player, with effort), a wardrobe leaf ~8 kg.
        Vector3 size = panel.lossyScale;
        float mass = Mathf.Clamp(size.x * size.y * size.z * 200f, 4f, 40f);
        panel.gameObject.AddComponent<PhysicsProp>().Initialize(-1, width < 0.65f ? "Дверца" : "Дверь", mass, 80f);
        BrokenBody = body;
        // The host throws it; clients turn it into a kinematic replica right after (DestructionReplication).
        if (DestructionSystem.Authority)
            body.AddExplosionForce(Mathf.Max(2f, impulse) * mass * 0.25f, from, 8f, 0.4f, ForceMode.Impulse);
        panel = null;
    }

    private void Update()
    {
        if (panel == null || Mathf.Approximately(angle, targetAngle)) return;
        float next = Mathf.MoveTowards(angle, targetAngle, SwingDegreesPerSecond * Time.deltaTime);
        Quaternion rotation = Quaternion.AngleAxis(next, Vector3.up) * closedRotation;
        // A swinging door never sweeps through a player: it waits until they step out of the way.
        // Shoving the capsule instead could wedge it into a wall or furniture.
        if (BlocksPlayer(rotation)) return;
        angle = next;
        transform.rotation = rotation;
    }

    private bool BlocksPlayer(Quaternion rotation)
    {
        if (panelCollider == null) return false;
        Vector3 local = Vector3.Scale(panel.localPosition, Vector3.one) + panelCollider.center;
        Vector3 centre = transform.position + rotation * local;
        Vector3 half = Vector3.Scale(panel.localScale, panelCollider.size) * 0.5f + new Vector3(0.02f, 0f, 0.05f);
        int count = Physics.OverlapBoxNonAlloc(centre, half, overlaps, rotation, Physics.DefaultRaycastLayers,
            QueryTriggerInteraction.Ignore);
        for (int i = 0; i < count; i++)
        {
            // Players, and anything pushed against the door (a barricade), stop it swinging.
            var body = overlaps[i].attachedRigidbody;
            if (body != null && (body.GetComponent<HumanController>() != null || CarrySystem.IsCarryable(body)))
                return true;
        }
        return false;
    }
}
