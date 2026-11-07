using UnityEngine;
using UnityEngine.Rendering;

// Simple third-person body; the local first-person camera only sees its shadow. Animated
// procedurally from how the body actually moves, so it looks the same for the local player and for
// network replicas: legs and arms swing with walking speed, crouch lowers it, prone lays it flat,
// leaning tilts it. Footsteps, jumps and landings are played from the same motion.
public class HumanVisual : MonoBehaviour
{
    public HumanController controller;
    private Renderer[] parts;
    private Transform legLeft, legRight, armLeft, armRight, torso, head;
    private Vector3 legLeftRest, legRightRest, armLeftRest, armRightRest;
    private Vector3 lastPosition;
    private float phase, swing, verticalSpeed, airborne;
    private Quaternion pose = Quaternion.identity;
    private float nextStepPhase = 0.5f;
    private int stepVariant;

    private void Awake()
    {
        parts = GetComponentsInChildren<Renderer>();
        if (controller == null) controller = GetComponentInParent<HumanController>();
        legLeft = transform.Find("Leg_Left"); legRight = transform.Find("Leg_Right");
        armLeft = transform.Find("Arm_Left"); armRight = transform.Find("Arm_Right");
        torso = transform.Find("Torso"); head = transform.Find("Head");
        if (legLeft != null) legLeftRest = legLeft.localPosition;
        if (legRight != null) legRightRest = legRight.localPosition;
        if (armLeft != null) armLeftRest = armLeft.localPosition;
        if (armRight != null) armRightRest = armRight.localPosition;
        SetFirstPersonVisibility(false);
    }

    private void OnEnable()
    {
        Camera.onPreCull += BeforeCamera;
        Camera.onPostRender += AfterCamera;
        if (controller != null) lastPosition = controller.transform.position;
    }

    private void OnDisable()
    {
        Camera.onPreCull -= BeforeCamera;
        Camera.onPostRender -= AfterCamera;
        SetFirstPersonVisibility(false);
    }

    private void LateUpdate()
    {
        if (controller == null) return;
        float dt = Mathf.Max(Time.deltaTime, 0.0001f);
        Vector3 position = controller.transform.position;
        Vector3 velocity = (position - lastPosition) / dt;
        lastPosition = position;
        if (velocity.sqrMagnitude > 2500f) velocity = Vector3.zero;   // teleported (respawn, correction)
        float speed = new Vector2(velocity.x, velocity.z).magnitude;
        verticalSpeed = Mathf.Lerp(verticalSpeed, velocity.y, 1f - Mathf.Exp(-dt * 12f));
        bool prone = controller.IsProne, crouch = controller.IsCrouching;

        // Stride: one cycle per ~1.4 m standing, shorter steps when low.
        float stride = prone ? 0.7f : crouch ? 1f : 1.4f;
        bool moving = speed > 0.25f && Mathf.Abs(verticalSpeed) < 2.5f;
        if (moving) phase += speed / stride * dt;
        swing = Mathf.Lerp(swing, moving ? Mathf.Clamp01(speed / 3.5f) : 0f, 1f - Mathf.Exp(-dt * 8f));
        float s = Mathf.Sin(phase * Mathf.PI * 2f) * swing;
        PlaySteps(position, speed, moving, prone);
        TrackJumps(position, velocity);

        // Whole body: lying flat when prone, a tilt when leaning, scaled down when crouching.
        Quaternion target = prone ? Quaternion.Euler(90f, 0f, 0f) : Quaternion.Euler(0f, 0f, -controller.Lean * 14f);
        pose = Quaternion.Slerp(pose, target, 1f - Mathf.Exp(-dt * 10f));
        transform.localRotation = pose;
        transform.localPosition = prone ? new Vector3(0f, 0.22f, -0.75f) : Vector3.zero;
        float height = prone ? 1f : controller.CurrentHeight / controller.standingHeight;
        transform.localScale = new Vector3(1f, height, 1f);

        // Limbs swing about the hip and shoulder (approximated by shifting the boxes and tilting them).
        Swing(legLeft, legLeftRest, s * 32f, 0.35f);
        Swing(legRight, legRightRest, -s * 32f, 0.35f);
        Swing(armLeft, armLeftRest, -s * (prone ? 50f : 26f), 0.3f);
        Swing(armRight, armRightRest, s * (prone ? 50f : 26f), 0.3f);
        if (torso != null) torso.localRotation = Quaternion.Euler(crouch ? 18f : speed > 4.5f ? 8f : 0f, 0f, 0f);
        if (head != null) head.localRotation = Quaternion.Euler(prone ? -70f : 0f, 0f, 0f);
    }

    private static void Swing(Transform limb, Vector3 rest, float degrees, float pivot)
    {
        if (limb == null) return;
        limb.localRotation = Quaternion.Euler(degrees, 0f, 0f);
        // Keep the top of the limb in place: move the centre along the arc.
        float a = degrees * Mathf.Deg2Rad;
        limb.localPosition = rest + new Vector3(0f, pivot * (1f - Mathf.Cos(a)), pivot * Mathf.Sin(a));
    }

    // A footstep at each half stride on the ground: hard surfaces outdoors, floorboards indoors.
    private void PlaySteps(Vector3 position, float speed, bool moving, bool prone)
    {
        if (!moving || airborne > 0.15f || !TryFooting(position, out var ground)) return;
        if (phase < nextStepPhase) return;
        nextStepPhase = Mathf.Floor(phase * 2f) / 2f + 0.5f;
        var surface = SurfaceUnder(ground);
        bool glass = OnBrokenGlass(position);
        if (prone)
        {
            // Crawling: cloth on the floor, plus a crunch when dragging yourself over broken glass.
            GameAudio.Play(SoundBank.Rustle, position, 0.2f, Random.Range(0.9f, 1.1f), SoundCategory.Effects, 1f, 10f);
            if (glass) GlassCrunch(position, 0.1f, 14f);
            return;
        }
        // Gait sets the base level and how far it carries: sneaking ~10 m, walking ~22 m, running ~32 m.
        // A car roof rings louder than a floor; grass, rugs and mattresses swallow the step.
        float gait = controller.IsCrouching ? 0.07f : Mathf.Lerp(0.16f, 0.34f, Mathf.InverseLerp(2f, 6f, speed));
        float range = controller.IsCrouching ? 10f : Mathf.Lerp(22f, 32f, Mathf.InverseLerp(3.5f, 6f, speed));
        float material = surface switch
        {
            StepSurface.Metal => 1.15f, StepSurface.Grass => 0.6f, StepSurface.Soft => 0.5f, _ => 1f
        };
        if (surface == StepSurface.Metal) range *= 1.15f;
        float pitch = (surface == StepSurface.Metal ? 0.8f : surface == StepSurface.Grass ? 0.93f : 1f) * Random.Range(0.97f, 1.03f);
        stepVariant = (stepVariant + 1) % 5;
        GameAudio.Play(SoundBank.Step(surface, stepVariant), position, gait * material, pitch, SoundCategory.Effects, 1f, range);
        // Broken glass is the step plus a dry crunch under the sole: clearly louder and carrying further.
        if (glass) GlassCrunch(position, gait * 0.7f, range * 1.3f);
    }

    // A low, short crunch: the recorded glass knock pitched down and kept quiet, a different take each step.
    private void GlassCrunch(Vector3 position, float volume, float range)
    {
        GameAudio.Play(SoundBank.Step(StepSurface.Glass, Random.Range(0, 5)), position, volume, Random.Range(0.72f, 0.82f),
            SoundCategory.Effects, 1f, range);
    }

    private void TrackJumps(Vector3 position, Vector3 velocity)
    {
        // Air time from the motion: rising fast, or falling for a while.
        bool inAir = Mathf.Abs(verticalSpeed) > 1.2f;
        if (inAir) airborne += Time.deltaTime;
        else
        {
            if (airborne > 0.3f && TryFooting(position, out _))
                GameAudio.Play(SoundBank.Land, position, Mathf.Clamp(0.1f + airborne * 0.08f, 0.1f, 0.3f), Random.Range(0.98f, 1.02f),
                    SoundCategory.Effects, 1.5f, 25f);
            airborne = 0f;
        }
    }

    private void BeforeCamera(Camera camera)
    {
        var spectator = camera.GetComponent<SpectatorController>();
        SetFirstPersonVisibility(controller != null &&
            ((controller.IsControlled && camera == controller.viewCamera) ||
             (spectator != null && spectator.ObservedHuman == controller)));
    }

    // Shards lie on the floor right under a broken pane, within about a metre of the wall on either side and
    // along the window's width; walking past or brushing the wall beside it is not stepping on glass.
    private static bool OnBrokenGlass(Vector3 feet)
    {
        foreach (var window in BreakableWindow.BrokenWindows)
        {
            if (window == null) continue;
            Transform pane = window.transform;
            Vector3 offset = feet - pane.position;
            float across = Mathf.Abs(Vector3.Dot(offset, pane.forward));
            float along = Mathf.Abs(Vector3.Dot(offset, pane.right));
            float sill = pane.position.y - pane.lossyScale.y * 0.5f;
            if (across < 1.0f && along < pane.lossyScale.x * 0.5f + 0.2f && feet.y > sill - 1.6f && feet.y < sill + 0.2f)
                return true;
        }
        return false;
    }

    private static StepSurface SurfaceUnder(RaycastHit ground)
    {
        var prop = ground.collider.GetComponentInParent<PhysicsProp>();
        if (prop != null)
            return prop.SoundMaterial switch
            {
                SoundMaterial.Soft => StepSurface.Soft, SoundMaterial.Metal => StepSurface.Metal,
                SoundMaterial.Stone => StepSurface.Concrete, _ => StepSurface.Wood
            };
        if (ground.collider.GetComponentInParent<DestructionDebris>() != null) return StepSurface.Concrete;
        return SuburbanTrainingMap.Surface(ground);
    }

    private bool TryFooting(Vector3 position, out RaycastHit footing)
    {
        footing = default;
        float nearest = float.PositiveInfinity;
        foreach (var hit in Physics.RaycastAll(position + Vector3.up * 0.15f, Vector3.down, 0.45f, ~0, QueryTriggerInteraction.Ignore))
            if (!hit.collider.transform.IsChildOf(controller.transform) && hit.normal.y > 0.6f && hit.distance < nearest)
            { nearest = hit.distance; footing = hit; }
        return nearest < float.PositiveInfinity;
    }

    private void AfterCamera(Camera camera) => SetFirstPersonVisibility(false);

    private void SetFirstPersonVisibility(bool firstPerson)
    {
        if (parts == null) return;
        var mode = firstPerson ? ShadowCastingMode.ShadowsOnly : ShadowCastingMode.On;
        foreach (var part in parts)
            if (part != null) part.shadowCastingMode = mode;
    }
}
