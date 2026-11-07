using UnityEngine;

// The one strange stone in town: violet and faintly glowing. The first time anyone picks it up (pocket or
// grab) there is an even chance it goes off, and if it does, an even chance between a small blast and one
// that wrecks nearly half the map. Decided where destruction is authoritative (offline, or the host); the
// wreckage reaches clients through the destruction stream and the flash and boom through a broadcast.
[DisallowMultipleComponent]
public sealed class CursedStone : MonoBehaviour
{
    private bool rolled;
    private static readonly System.Random dice = new System.Random();

    // Returns true when the stone exploded (and is gone), so the pickup must not go ahead.
    public static bool Trigger(Rigidbody body)
    {
        if (body == null || !DestructionSystem.Authority || !body.TryGetComponent<CursedStone>(out var stone) || stone.rolled)
            return false;
        stone.rolled = true;
        if (dice.NextDouble() >= 0.5) return false;
        bool huge = dice.NextDouble() < 0.5;
        Vector3 at = body.worldCenterOfMass;
        if (body.TryGetComponent<PhysicsProp>(out var prop)) prop.Break();
        Detonate(at, huge);
        return true;
    }

    private static void Detonate(Vector3 at, bool huge)
    {
        var area = SuburbanTrainingMap.PlayArea;
        float span = area.size.x > 0f ? Mathf.Max(area.size.x, area.size.z) : 80f;
        float radius = huge ? span * 0.4f : 3.5f;
        float impulse = huge ? 60f : 12f;
        int destroyed = DestructionSystem.ApplyExplosion(new ExplosionDamage
        {
            position = at, radius = radius, impulse = impulse, humanDamage = huge ? 400f : 70f,
            maxFragments = huge ? 700 : 30, maxActiveDebris = huge ? 256 : 64, impactFractureSpeed = 6f
        });
        RoundStats.Explosion(destroyed);
        PlayEffect(at, radius, impulse);
        MultiplayerPrototype.Instance?.BroadcastBlast(at, radius, impulse);
    }

    // The flash, shockwave, sound and concussion; every peer plays it.
    public static void PlayEffect(Vector3 at, float radius, float impulse)
    {
        Material look = null;
        foreach (var drone in FindObjectsByType<DroneController>(FindObjectsInactive.Include))
            if (drone.explosionMaterial != null) { look = drone.explosionMaterial; break; }
        DroneExplosionEffect.Play(at, look, radius, radius > 10f ? 2.6f : 1.2f, radius > 10f ? 320 : 120, impulse / 21f);
    }
}
