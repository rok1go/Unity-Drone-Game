using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

// Three pocket slots per person for small items (stones, short planks, flashlights). E puts the item under
// the crosshair into a free slot, 1/2/3 or the wheel select a slot, G throws the selected item (a stone
// far and hard, to make noise elsewhere), T switches a carried flashlight. Everything is decided where
// physics is authoritative (offline, or the host); a client asks and is told its slots. Pocketed items
// are ordinary map props switched off, so dropping one is the same prop coming back.
public static class Inventory
{
    public const int Slots = 3;
    private const float PickupReach = 3.2f;

    private sealed class Pockets
    {
        public readonly PhysicsProp[] items = new PhysicsProp[Slots];
        public HumanController human;
        public bool beam;
    }

    // Authority: every owner's pockets.
    private static readonly Dictionary<ulong, Pockets> owners = new Dictionary<ulong, Pockets>();
    // This player's view of their own pockets.
    private static readonly PhysicsProp[] local = new PhysicsProp[Slots];
    public static int Selected { get; private set; }
    public static IReadOnlyList<PhysicsProp> LocalSlots => local;
    public static bool LocalBeam { get; private set; }

    private static bool Authority => !MultiplayerPrototype.OnlineMatch || MultiplayerPrototype.Instance.IsHost;
    private static ulong LocalOwner => MultiplayerPrototype.OnlineMatch ? MultiplayerPrototype.Instance.LocalId : CarrySystem.OfflineCarrier;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() => Reset();

    // A new map: nothing is in anyone's pockets.
    public static void Reset()
    {
        owners.Clear();
        for (int i = 0; i < Slots; i++) local[i] = null;
        Selected = 0; LocalBeam = false;
    }

    public static bool HasFreeSlot
    {
        get { for (int i = 0; i < Slots; i++) if (local[i] == null) return true; return false; }
    }

    public static bool HasFlashlight
    {
        get { foreach (var item in local) if (item != null && item.Pocket == PocketKind.Flashlight) return true; return false; }
    }

    // ---------------- local input ----------------

    public static void Tick(HumanController human, Ray view)
    {
        if (!MatchRules.Current.Items) return;
        var keyboard = Keyboard.current;
        if (keyboard != null)
        {
            if (keyboard.digit1Key.wasPressedThisFrame) Selected = 0;
            if (keyboard.digit2Key.wasPressedThisFrame) Selected = 1;
            if (keyboard.digit3Key.wasPressedThisFrame) Selected = 2;
        }
        float wheel = Mouse.current != null && !PlayerCarry.IsCarrying ? Mouse.current.scroll.ReadValue().y : 0f;
        if (wheel > 0.1f) Selected = (Selected + Slots - 1) % Slots;
        else if (wheel < -0.1f) Selected = (Selected + 1) % Slots;
        if (GameSettings.Pressed(GameAction.DropItem) && local[Selected] != null)
        {
            if (Authority) HostThrow(LocalOwner, Selected, view, human.GetComponent<Rigidbody>().linearVelocity);
            else MultiplayerPrototype.Instance.RequestThrow(Selected, view);
        }
        if (GameSettings.Pressed(GameAction.Flashlight) && (HasFlashlight || LocalBeam))
        {
            bool on = !LocalBeam && HasFlashlight;
            SetBeam(human, on);           // shown at once; the host confirms it for everyone
            LocalBeam = on;
            if (Authority) HostBeam(LocalOwner, human, on);
            else MultiplayerPrototype.Instance.RequestBeam(on);
        }
    }

    public static void RequestPickup(PhysicsProp prop)
    {
        if (prop == null) return;
        if (Authority)
        {
            HumanController human = null;
            foreach (var h in Object.FindObjectsByType<HumanController>()) if (h.IsControlled) { human = h; break; }
            if (human != null) HostPickup(LocalOwner, human, prop, human.viewCamera.transform.position);
        }
        else MultiplayerPrototype.Instance.RequestPocket(prop);
    }

    // ---------------- authority ----------------

    private static Pockets For(ulong owner, HumanController human)
    {
        if (!owners.TryGetValue(owner, out var pockets)) owners[owner] = pockets = new Pockets();
        _ = CarrySystem.Instance;   // its physics step runs HostUpdate (death drops)
        if (human != null) pockets.human = human;
        return pockets;
    }

    public static bool HostPickup(ulong owner, HumanController human, PhysicsProp prop, Vector3 eye)
    {
        if (!MatchRules.Current.Items || prop == null || prop.Pocket == PocketKind.None || prop.Stowed || prop.IsBroken ||
            !prop.gameObject.activeInHierarchy || human == null || human.IsEliminated) return false;
        Vector3 closest = prop.GetComponent<Collider>() != null ? prop.GetComponent<Collider>().ClosestPoint(eye) : prop.transform.position;
        if ((closest - eye).sqrMagnitude > PickupReach * PickupReach) return false;
        var pockets = For(owner, human);
        int slot = System.Array.IndexOf(pockets.items, null);
        if (slot < 0) return false;
        if (CursedStone.Trigger(prop.Body)) return false;
        pockets.items[slot] = prop;
        prop.SetStowed(true, default, default, default, true);
        GameAudio.Play(SoundBank.Rustle, prop.transform.position, 0.18f, 1f, SoundCategory.Effects, 1f, 8f);
        Publish(owner, pockets);
        return true;
    }

    // Out of the hand along the view: stones are thrown hard (they carry and land loudly), others tossed.
    public static void HostThrow(ulong owner, int slot, Ray view, Vector3 inherited)
    {
        if (slot < 0 || slot >= Slots || !owners.TryGetValue(owner, out var pockets)) return;
        var prop = pockets.items[slot];
        pockets.items[slot] = null;
        if (prop != null)
        {
            Vector3 direction = view.direction.normalized;
            Vector3 start = view.origin + direction * 0.45f;
            if (Physics.Raycast(view.origin, direction, out var wall, 0.7f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore) &&
                wall.collider.GetComponentInParent<HumanController>() == null)
                start = view.origin + direction * Mathf.Max(0.05f, wall.distance - 0.2f);
            float speed = prop.Pocket == PocketKind.Stone ? 15f : prop.Pocket == PocketKind.Flashlight ? 5f : 3.5f;
            prop.SetStowed(false, start, Quaternion.LookRotation(direction), inherited + direction * speed + Vector3.up * 1.2f, true);
        }
        if (prop != null && prop.Pocket == PocketKind.Flashlight && !Has(pockets, PocketKind.Flashlight))
            HostBeam(owner, pockets.human, false);
        Publish(owner, pockets);
    }

    public static void HostBeam(ulong owner, HumanController human, bool on)
    {
        var pockets = For(owner, human);
        pockets.beam = on && Has(pockets, PocketKind.Flashlight);
        if (pockets.human != null) SetBeam(pockets.human, pockets.beam);
        if (owner == LocalOwner) LocalBeam = pockets.beam;
    }

    public static bool HostBeamOn(ulong owner) => owners.TryGetValue(owner, out var pockets) && pockets.beam;

    private static bool Has(Pockets pockets, PocketKind kind)
    {
        foreach (var item in pockets.items) if (item != null && item.Pocket == kind) return true;
        return false;
    }

    // Authority, every physics step: the dead empty their pockets where they fell.
    public static void HostUpdate()
    {
        foreach (var pair in owners)
        {
            var pockets = pair.Value;
            if (pockets.human == null || !pockets.human.IsEliminated) continue;
            Vector3 at = pockets.human.transform.position + Vector3.up * 0.4f;
            bool any = false;
            for (int i = 0; i < Slots; i++)
            {
                var prop = pockets.items[i];
                pockets.items[i] = null;
                if (prop == null) continue;
                any = true;
                Vector3 scatter = Quaternion.Euler(0f, i * 120f, 0f) * Vector3.forward * 0.3f;
                prop.SetStowed(false, at + scatter + Vector3.up * i * 0.15f, Random.rotation, scatter * 2f, true);
            }
            if (pockets.beam) { pockets.beam = false; SetBeam(pockets.human, false); }
            pockets.human = null;
            if (any) Publish(pair.Key, pockets);
            break;   // the dictionary may be published into; one owner per step is plenty
        }
    }

    private static void Publish(ulong owner, Pockets pockets)
    {
        if (owner == LocalOwner) { ApplyLocal(pockets.items); return; }
        MultiplayerPrototype.Instance?.SendInventory(owner, pockets.items);
    }

    public static void ApplyLocal(IReadOnlyList<PhysicsProp> items)
    {
        for (int i = 0; i < Slots; i++) local[i] = i < items.Count ? items[i] : null;
        if (!HasFlashlight) LocalBeam = false;
    }

    // ---------------- flashlight beam ----------------

    // A real spot light from the head: lights what it points at and is seen by everyone else.
    public static void SetBeam(HumanController human, bool on)
    {
        if (human == null || human.viewCamera == null) return;
        var existing = human.viewCamera.transform.Find("Flashlight beam");
        if (!on) { if (existing != null) existing.gameObject.SetActive(false); return; }
        GameObject beam = existing != null ? existing.gameObject : null;
        if (beam == null)
        {
            beam = new GameObject("Flashlight beam");
            beam.transform.SetParent(human.viewCamera.transform, false);
            beam.transform.localPosition = new Vector3(0.18f, -0.15f, 0.1f);
            var created = beam.AddComponent<Light>();
            created.type = LightType.Spot;
            created.spotAngle = 46f; created.innerSpotAngle = 18f;
            created.intensity = 2.4f; created.color = new Color(1f, 0.95f, 0.85f);
        }
        // A shadowed spot re-renders every caster in its cone each frame, which in a dense town is the cost.
        // Only your own torch casts (low-resolution) shadows, so it never lights the room behind a wall in
        // your face; other players' beams are plain, shorter lights that are still seen from far away.
        var light = beam.GetComponent<Light>();
        bool own = human.IsControlled;
        light.range = own ? 18f : 14f;
        light.shadows = own ? LightShadows.Hard : LightShadows.None;
        light.shadowResolution = UnityEngine.Rendering.LightShadowResolution.Low;
        light.shadowNearPlane = 0.3f;
        light.renderMode = own ? LightRenderMode.ForcePixel : LightRenderMode.Auto;
        beam.SetActive(true);
    }
}
