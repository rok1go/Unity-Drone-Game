using System.Collections.Generic;
using UnityEngine;

// Brief marks for the drone pilot where people just did something audible: a door opened or shut, a pane
// broken by hand, a loud knock or crash. The authority (offline, or the host) decides who perceives it: a
// drone within medium range with at most one wall in between. Only the place is sent, never who did it,
// and a place marks again only after a pause, so it reads as a hint rather than a radar.
public static class ActivityMarks
{
    public const float Range = 35f;
    public const float Lifetime = 2.5f;
    private const float Cooldown = 4f;
    private const int MaxWalls = 2;              // colliders crossed: one wall is fine, a building is not

    private static readonly Dictionary<Vector3Int, float> recent = new Dictionary<Vector3Int, float>();
    private static readonly List<(Vector3 point, float time)> shown = new List<(Vector3, float)>();
    private static readonly RaycastHit[] hits = new RaycastHit[16];
    public static IReadOnlyList<(Vector3 point, float time)> Shown => shown;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() { recent.Clear(); shown.Clear(); }

    // After the event sources cleared their own subscribers (SubsystemRegistration).
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Init()
    {
        InteractiveDoor.Changed += (door, state) =>
        {
            if (door != null && state != InteractiveDoor.DoorState.Broken) Report(door.transform.position + Vector3.up * 1.1f);
        };
        BreakableWindow.Shattered += (window, _, _, fromBlast) => { if (window != null && !fromBlast) Report(window.transform.position); };
        GameAudio.PhysicsPlayed += (position, _, volume, _, slide, _) => { if (!slide && volume >= 0.28f) Report(position); };
    }

    public static void Report(Vector3 point)
    {
        if (!DestructionSystem.Authority || !Application.isPlaying) return;
        var key = Vector3Int.FloorToInt(point / 3f);
        if (recent.TryGetValue(key, out float last) && Time.time - last < Cooldown) return;
        recent[key] = Time.time;
        if (recent.Count > 256) recent.Clear();
        foreach (var drone in Object.FindObjectsByType<DroneController>())
        {
            // Remote pilots' drones are network replicas here: count every drone that is in the world.
            if (drone.IsDestroyed || !drone.gameObject.activeInHierarchy) continue;
            Vector3 eye = drone.transform.position;
            Vector3 delta = point - eye;
            float distance = delta.magnitude;
            // The drone's own doing (a pane it flew through) is no news to its pilot.
            if (distance < 2.5f || distance > Range) continue;
            int walls = 0;
            int count = Physics.RaycastNonAlloc(eye, delta / distance, hits, distance - 0.5f, Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                var body = hits[i].rigidbody;
                if (body == null || body.isKinematic) walls++;
            }
            if (walls > MaxWalls) continue;
            var actor = drone.GetComponent<PrototypeNetworkActor>();
            if (MultiplayerPrototype.OnlineMatch && actor != null)
            {
                if (actor.OwnerId == MultiplayerPrototype.Instance.LocalId) Show(point);
                else MultiplayerPrototype.Instance.SendMark(actor.OwnerId, point);
            }
            else if (drone.IsControlled) Show(point);
        }
    }

    public static void Show(Vector3 point)
    {
        shown.Add((point, Time.unscaledTime));
        shown.RemoveAll(m => Time.unscaledTime - m.time > Lifetime);
        if (shown.Count > 8) shown.RemoveAt(0);
    }
}
