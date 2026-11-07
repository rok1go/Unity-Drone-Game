using UnityEngine;

// Anything the E key acts on: doors, wardrobe leaves, windows, light switches, breaker panels, TVs,
// radios and small items that go into the inventory.
public interface IInteractable
{
    // What E does right now ("Открыть дверь"), or null when it cannot be used.
    string Prompt { get; }
    void Interact(Vector3 user);
}

// A second action on the same key, done by holding it (lock / unlock a door).
public interface IHoldInteractable
{
    string HoldPrompt { get; }
    void HoldInteract(Vector3 user);
}

// One raycast under the crosshair finds the interactable, for the E key and the HUD prompt alike.
public static class Interaction
{
    public const float Reach = 2.6f;
    public const float HoldSeconds = 0.45f;
    private static IInteractable pending;
    private static Vector3 pendingUser;
    private static float pendingSince;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() => pending = null;

    public static bool TryFind(Ray view, out IInteractable target)
    {
        target = null;
        if (!Physics.Raycast(view, out var hit, Reach, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            return false;
        // An object can carry several (a radio is a prop and a device): the first that is usable now wins.
        foreach (var candidate in hit.collider.GetComponentsInParent<IInteractable>())
            if (candidate.Prompt != null) { target = candidate; return true; }
        return false;
    }

    public static string HoldPrompt(IInteractable target) => target is IHoldInteractable hold ? hold.HoldPrompt : null;

    // E pressed: act at once, or, where holding does something else, wait to see whether it is a tap or a hold.
    // Returns true while the key is spoken for (so the shared E does not also lean).
    public static bool Press(Ray view, Vector3 user)
    {
        pending = null;
        if (!TryFind(view, out var target)) return false;
        if (HoldPrompt(target) == null) { target.Interact(user); return true; }
        pending = target; pendingUser = user; pendingSince = Time.unscaledTime;
        return true;
    }

    // Called every frame while a press is pending.
    public static void Hold(bool held, Vector3 user)
    {
        if (pending == null) return;
        if (pending is Object unityObject && unityObject == null) { pending = null; return; }
        if (held && Time.unscaledTime - pendingSince >= HoldSeconds)
        {
            if (pending is IHoldInteractable hold) hold.HoldInteract(user);
            pending = null;
        }
        else if (!held)
        {
            pending.Interact(pendingUser);
            pending = null;
        }
    }

    public static float HoldProgress => pending == null ? 0f : Mathf.Clamp01((Time.unscaledTime - pendingSince) / HoldSeconds);

    public static bool TryInteract(Ray view, Vector3 user)
    {
        if (!TryFind(view, out var target)) return false;
        target.Interact(user);
        return true;
    }
}
