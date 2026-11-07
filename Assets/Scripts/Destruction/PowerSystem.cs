using System.Collections.Generic;
using UnityEngine;

// One circuit per building, fed through the building's breaker panel (щиток); every lamp, sensor lamp
// and appliance also keeps its own switch state. Stable creation-order indices let the host replicate
// only the few devices that change. The host owns every state; clients only display it.
public sealed class PowerSystem : MonoBehaviour
{
    public static PowerSystem Instance { get; private set; }
    private const float SensorRange = 7f;          // horizontal reach of a corridor / landing sensor
    private const float SensorHold = 6f;           // seconds the light stays on after the last sighting
    private readonly List<PoweredDevice> devices = new List<PoweredDevice>();
    private readonly Dictionary<int, DestructibleBlock> feeders = new Dictionary<int, DestructibleBlock>();
    private readonly Dictionary<int, PoweredDevice> breakers = new Dictionary<int, PoweredDevice>();
    private readonly HashSet<int> failed = new HashSet<int>();
    private readonly List<HumanController> people = new List<HumanController>();
    private float nextTick, nextPeopleRefresh;

    public int[] CaptureFlags()
    {
        var flags = new int[devices.Count];
        for (int i = 0; i < flags.Length; i++) flags[i] = devices[i] != null ? devices[i].Flags : 0;
        return flags;
    }

    public void ApplySnapshot(int[] flags)
    {
        if (flags == null) return;
        for (int i = 0; i < Mathf.Min(flags.Length, devices.Count); i++) Apply(i, flags[i], false);
    }

    // Host / offline only; clients start dark and take the host's snapshot. A new map rolls new states per
    // building: about four in ten are empty (every room dark), the rest have a third of their rooms lit;
    // about one radio or TV in five plays. Breakers start on. Overall roughly 75-80% of room lamps are off.
    public void InitializeStates()
    {
        bool replica = MultiplayerPrototype.Instance != null && MultiplayerPrototype.Instance.HasSession && !MultiplayerPrototype.Instance.IsHost;
        var random = new System.Random();
        var occupied = new Dictionary<int, bool>();
        foreach (var device in devices)
        {
            if (!occupied.ContainsKey(device.Building)) occupied[device.Building] = random.NextDouble() >= 0.4;
            bool lived = occupied[device.Building];
            device.On = !replica && (device.Breaker ||
                (!device.MotionSensor && lived && random.NextDouble() < (device.Appliance ? 0.3 : 0.36)));
            device.Powered = !replica && MatchRules.Current.Electricity;
            device.Refresh();
        }
    }

    public int DeviceCount => devices.Count;

    public void SetMotionSensor(int index)
    {
        if (index >= 0 && index < devices.Count) { devices[index].MotionSensor = true; devices[index].On = false; }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() => Instance = null;
    private void Awake() => Instance = this;
    private void OnDestroy() { if (Instance == this) Instance = null; }

    public void Register(PoweredDevice device, int building, DestructibleBlock support)
    {
        device.Index = devices.Count; device.Building = building; device.Support = support;
        devices.Add(device);
        if (building != 0 && support != null && !feeders.ContainsKey(building)) feeders.Add(building, support);
        if (device.Breaker && building != 0) breakers[building] = device;
    }

    // The host accepts a request when the device is within reach and roughly in view of the requester.
    // A remote player's view arrives interpolated, a little late, so an exact ray onto a 10 cm switch
    // would reject honest presses.
    private static bool CanReach(Ray view, PoweredDevice device)
    {
        var collider = device.GetComponent<Collider>();
        if (collider == null || !collider.enabled) return false;
        Vector3 point = collider.ClosestPoint(view.origin);
        Vector3 delta = point - view.origin;
        if (delta.magnitude > Interaction.Reach + 0.8f) return false;
        if (delta.sqrMagnitude > 0.04f && Vector3.Angle(view.direction, delta) > 35f) return false;
        // Nothing solid between the eye and the device (the device itself or its own parts may be hit).
        return !Physics.Linecast(view.origin, point - delta.normalized * 0.03f, out var hit, Physics.DefaultRaycastLayers,
            QueryTriggerInteraction.Ignore) || hit.collider.GetComponentInParent<PoweredDevice>() == device ||
            hit.collider.GetComponentInParent<HumanController>() != null;
    }

    public void Toggle(int index, Ray view)
    {
        if (!MatchRules.Current.Electricity || index < 0 || index >= devices.Count) return;
        var device = devices[index];
        if (device == null || device.IsWrecked || (!device.Powered && !device.Breaker) || !CanReach(view, device)) return;
        if (device.MotionSensor) { device.SensorEnabled = !device.SensorEnabled; if (!device.SensorEnabled) device.On = false; }
        else device.On = !device.On;
        GameAudio.Play(SoundBank.Switch, device.transform.position, device.Breaker ? 0.4f : 0.25f, device.Breaker ? 0.85f : 1f);
        device.Refresh();
        MultiplayerPrototype.Instance?.PublishPower(index, device.Flags);
        nextTick = 0f;   // apply a breaker to its building right away
    }

    public void Apply(int index, int flags, bool audible = true)
    {
        if (index < 0 || index >= devices.Count || devices[index] == null) return;
        var device = devices[index];
        bool changed = device.On != ((flags & 1) != 0);
        device.On = (flags & 1) != 0; device.Powered = (flags & 2) != 0;
        device.SensorEnabled = (flags & 4) != 0;
        device.Damaged = (flags & 8) != 0;
        if (changed && audible && !device.MotionSensor) GameAudio.Play(SoundBank.Switch, device.transform.position, 0.25f);
        device.Refresh();
    }

    // A blast next to a breaker panel wrecks it: the building stays dark and the panel can no longer be used.
    public void BlastDamage(Vector3 blast, float radius)
    {
        float reach = Mathf.Max(1.5f, radius * 0.3f);
        foreach (var device in devices)
        {
            if (device == null || !device.Breaker || device.Damaged) continue;
            var collider = device.GetComponent<Collider>();
            Vector3 nearest = collider != null ? collider.ClosestPoint(blast) : device.transform.position;
            if ((nearest - blast).sqrMagnitude > reach * reach) continue;
            device.Damaged = true;
            device.On = false;
            device.Refresh();
            MultiplayerPrototype.Instance?.PublishPower(device.Index, device.Flags);
        }
        nextTick = 0f;
    }

    private bool BuildingLive(int building)
    {
        if (building == 0) return true;
        if (failed.Contains(building)) return false;
        return !breakers.TryGetValue(building, out var breaker) || (breaker != null && breaker.On && !breaker.IsWrecked);
    }

    // Someone is in sight of the sensor: same storey, within range, not behind a wall.
    private bool Detects(PoweredDevice device)
    {
        Vector3 at = device.Lamp.transform.position;
        foreach (var human in people)
        {
            if (human == null || !human.isActiveAndEnabled || human.IsEliminated) continue;
            Vector3 feet = human.transform.position;
            if (feet.y > at.y - 0.2f || feet.y < at.y - 3.4f) continue;
            Vector3 flat = feet - at; flat.y = 0f;
            if (flat.sqrMagnitude > SensorRange * SensorRange) continue;
            Vector3 target = feet + Vector3.up * Mathf.Min(human.CurrentHeight * 0.65f, 1.4f);
            Vector3 delta = target - at;
            if (Physics.Raycast(at - Vector3.up * 0.1f, delta.normalized, out var hit, delta.magnitude - 0.1f,
                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore) &&
                hit.collider.GetComponentInParent<HumanController>() != human) continue;
            return true;
        }
        return false;
    }

    private void Update()
    {
        if (Time.unscaledTime < nextTick) return;
        nextTick = Time.unscaledTime + 0.2f;
        bool authority = !MultiplayerPrototype.OnlineMatch || MultiplayerPrototype.Instance.IsHost;
        if (authority)
        {
            // Few people, many colliders: test the people directly (an overlap query filled up with walls).
            if (Time.unscaledTime >= nextPeopleRefresh)
            {
                nextPeopleRefresh = Time.unscaledTime + 1f;
                people.Clear();
                people.AddRange(FindObjectsByType<HumanController>());
            }
            foreach (var pair in feeders)
                if (pair.Value == null || pair.Value.IsDetached || !pair.Value.gameObject.activeInHierarchy) failed.Add(pair.Key);
            foreach (var device in devices)
            {
                if (device == null) continue;
                bool previousOn = device.On;
                bool powered = MatchRules.Current.Electricity && !device.IsWrecked && (device.Breaker || BuildingLive(device.Building));
                if (device.MotionSensor && device.Lamp != null)
                {
                    // A dead circuit runs no sensor either.
                    if (powered && device.SensorEnabled && Detects(device)) device.OccupiedUntil = Time.time + SensorHold;
                    device.On = powered && device.SensorEnabled && Time.time < device.OccupiedUntil;
                }
                if (device.Powered == powered && previousOn == device.On) continue;
                device.Powered = powered; device.Refresh();
                MultiplayerPrototype.Instance?.PublishPower(device.Index, device.Flags);
            }
        }
        foreach (var device in devices)
            if (device != null && device.Lamp != null)
                device.Lamp.enabled = device.LampLit;
    }
}

public sealed class PoweredDevice : MonoBehaviour, IInteractable
{
    public int Index, Building;
    public bool On = true, Powered = true, HasSupport;
    public bool Appliance, MotionSensor, Breaker, Damaged, SensorEnabled = true;
    public float OccupiedUntil;
    public DestructibleBlock Support;
    public Light Lamp;
    private DestructibleBlock lampMount;
    private bool hasLampMount;
    // A lamp torn down with its ceiling goes dark whatever its switch says.
    public bool LampLit => On && Powered && (!hasLampMount || (lampMount != null && !lampMount.IsDetached));
    // A wall-mounted switch or panel whose wall came loose is wrecked: it no longer works.
    public bool IsWrecked => Damaged || (!Appliance && HasSupport && (Support == null || Support.IsDetached));
    private AudioSource sound;
    private AudioLowPassFilter filter;
    private float muffle;
    private string noun;
    private Renderer indicator;
    private Material indicatorMaterial;
    public int Flags => (On ? 1 : 0) | (Powered ? 2 : 0) | (SensorEnabled ? 4 : 0) | (Damaged ? 8 : 0);
    public string Prompt => !MatchRules.Current.Electricity || IsWrecked ? null :
        Breaker ? (On ? "Отключить щиток" : "Включить щиток") :
        !Powered ? noun + ": нет питания" :
        MotionSensor ? (SensorEnabled ? "Отключить датчик" : "Включить датчик") : (On ? "Выключить " : "Включить ") + noun;

    public void Initialize(string label, Light lamp, AudioClip clip, Renderer display)
    {
        noun = label; Lamp = lamp; indicator = display;
        lampMount = lamp != null ? lamp.GetComponentInParent<DestructibleBlock>() : null;
        hasLampMount = lampMount != null;
        Appliance = clip != null;
        if (Appliance || MotionSensor) On = false;
        if (lamp != null) { lamp.renderMode = LightRenderMode.ForcePixel; lamp.shadows = LightShadows.None; }
        HasSupport = Support != null;
        if (indicator != null)
        {
            indicatorMaterial = new Material(Shader.Find("Standard"));
            indicator.sharedMaterial = indicatorMaterial;
        }
        if (clip != null)
        {
            sound = gameObject.AddComponent<AudioSource>(); sound.playOnAwake = false; sound.volume = 0f;
            sound.clip = clip; sound.loop = true; GameAudio.Configure3D(sound, 1.2f, 22f);
            filter = gameObject.AddComponent<AudioLowPassFilter>();
        }
        Powered = MatchRules.Current.Electricity;
        Refresh();
    }

    public void Interact(Vector3 user)
    {
        if (Prompt == null || (!Powered && !Breaker)) return;
        if (MultiplayerPrototype.OnlineMatch && !MultiplayerPrototype.Instance.IsHost)
            MultiplayerPrototype.Instance.RequestPower(Index);
        else
        {
            Vector3 direction = transform.position - user;
            if (MultiplayerPrototype.Instance != null && MultiplayerPrototype.Instance.TryLocalView(out var view))
                PowerSystem.Instance.Toggle(Index, view);
            else PowerSystem.Instance.Toggle(Index, new Ray(user, direction.normalized));
        }
    }

    public void Refresh()
    {
        bool active = On && Powered;
        if (Lamp != null) Lamp.enabled = LampLit;
        if (sound != null)
        {
            if (active && !sound.isPlaying) sound.Play();
            else if (!active && sound.isPlaying) sound.Stop();
        }
        if (indicatorMaterial != null)
        {
            Color lit = Breaker ? new Color(0.35f, 0.85f, 0.35f) : new Color(0.65f, 0.85f, 0.75f);
            indicatorMaterial.color = active ? lit : new Color(0.06f, 0.075f, 0.08f);
            indicatorMaterial.EnableKeyword("_EMISSION");
            indicatorMaterial.SetColor("_EmissionColor", active ? lit * 0.6f : Color.black);
        }
    }

    private void Update()
    {
        if (sound == null || !sound.isPlaying) return;
        GameAudio.Occlude(sound, filter, ref muffle, Time.deltaTime);
        sound.volume = 0.22f * GameAudio.MixHeadroom * GameAudio.OcclusionGain(muffle) * GameSettings.Volume(SoundCategory.Ambient);
    }

    private void OnDestroy()
    {
        // The switch went with its wall: the circuit it closed is open now.
        if (Lamp != null) Lamp.enabled = false;
        if (indicatorMaterial == null) return;
        // The lamp shade on the ceiling may outlive its switch: it keeps the (now dark) material and frees it
        // itself. Destroying it here left the shade with a missing material (rendered magenta).
        if (indicator != null && !indicator.transform.IsChildOf(transform))
        {
            indicatorMaterial.color = new Color(0.06f, 0.075f, 0.08f);
            indicatorMaterial.SetColor("_EmissionColor", Color.black);
            indicator.gameObject.AddComponent<OwnedMaterial>().Material = indicatorMaterial;
        }
        else Destroy(indicatorMaterial);
    }
}

// Frees a runtime material together with the renderer that still shows it.
public sealed class OwnedMaterial : MonoBehaviour
{
    public Material Material;
    private void OnDestroy() { if (Material != null) Destroy(Material); }
}
