using UnityEngine;
using UnityEngine.Rendering;

public enum TimeOfDay { Morning, Day, Evening, Night, Random }
public enum Weather { Clear, Cloudy, Rain, Fog }

// Time of day and weather for a match: the sun, ambient light, sky, fog and rain. Every peer applies
// the same resolved values from the lobby settings, so nothing changes over the network mid-round.
public sealed class MatchEnvironment : MonoBehaviour
{
    public static bool IsNight { get; private set; }
    public static TimeOfDay CurrentTime { get; private set; } = TimeOfDay.Day;
    private static MatchEnvironment rainFollower;
    private ParticleSystem rain;
    private AudioSource rainSound;
    private AudioLowPassFilter rainFilter;
    private Material rainMaterial;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        IsNight = false;
        CurrentTime = TimeOfDay.Day;
        rainFollower = null;
    }

    // A concrete time for "random", picked once by the host so all peers agree.
    public static TimeOfDay Resolve(TimeOfDay time, System.Random random) =>
        time != TimeOfDay.Random ? time : (TimeOfDay)random.Next(0, 4);

    public static void Apply(TimeOfDay time, Weather weather)
    {
        if (time == TimeOfDay.Random) time = TimeOfDay.Day;
        CurrentTime = time;
        IsNight = time == TimeOfDay.Night;
        Light sun = null;
        foreach (var light in FindObjectsByType<Light>())
            if (light.type == LightType.Directional && (sun == null || light.intensity > sun.intensity)) sun = light;
        // Sun height and heading, colour and strength per time of day.
        (float elevation, float azimuth, Color colour, float intensity, Color ambient, float exposure) = time switch
        {
            TimeOfDay.Morning => (14f, 95f, new Color(1f, 0.82f, 0.62f), 0.95f, new Color(0.42f, 0.45f, 0.52f), 0.9f),
            TimeOfDay.Evening => (9f, 255f, new Color(1f, 0.6f, 0.38f), 0.85f, new Color(0.38f, 0.32f, 0.36f), 0.75f),
            // Night is dark: a faint moon gives silhouettes in the open, unlit rooms are black.
            TimeOfDay.Night => (38f, 210f, new Color(0.55f, 0.65f, 0.95f), 0.045f, new Color(0.012f, 0.015f, 0.026f), 0.025f),
            _ => (52f, 150f, new Color(1f, 0.96f, 0.9f), 1.15f, new Color(0.5f, 0.53f, 0.58f), 1.1f),
        };
        bool overcast = weather == Weather.Cloudy || weather == Weather.Rain;
        if (overcast)
        {
            intensity *= 0.5f;
            colour = Color.Lerp(colour, new Color(0.75f, 0.78f, 0.82f), 0.6f);
            ambient = Color.Lerp(ambient, new Color(0.38f, 0.4f, 0.44f) * (IsNight ? 0.04f : 1f), 0.5f);
            exposure *= 0.6f;
        }
        if (sun != null)
        {
            sun.transform.rotation = Quaternion.Euler(elevation, azimuth, 0f);
            sun.color = colour;
            sun.intensity = intensity;
            sun.shadowStrength = overcast ? 0.45f : IsNight ? 0.6f : 0.9f;
        }
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = ambient;
        if (RenderSettings.skybox != null)
        {
            // A copy, so the project's skybox asset is never modified.
            if (!RenderSettings.skybox.name.EndsWith("(match)"))
                RenderSettings.skybox = new Material(RenderSettings.skybox) { name = RenderSettings.skybox.name + " (match)" };
            if (RenderSettings.skybox.HasProperty("_Exposure")) RenderSettings.skybox.SetFloat("_Exposure", exposure);
            if (RenderSettings.skybox.HasProperty("_AtmosphereThickness"))
                RenderSettings.skybox.SetFloat("_AtmosphereThickness", overcast ? 2.2f : time == TimeOfDay.Evening ? 1.6f : 1f);
        }
        RenderSettings.fog = weather == Weather.Fog || weather == Weather.Rain || IsNight;
        RenderSettings.fogMode = FogMode.ExponentialSquared;
        RenderSettings.fogDensity = weather == Weather.Fog ? 0.045f : weather == Weather.Rain ? 0.016f : 0.01f;
        RenderSettings.fogColor = IsNight ? new Color(0.012f, 0.015f, 0.024f) : Color.Lerp(ambient, Color.grey, 0.5f);
        DynamicGI.UpdateEnvironment();
        if (weather == Weather.Rain)
        {
            if (rainFollower == null) rainFollower = new GameObject("Rain").AddComponent<MatchEnvironment>();
        }
        else if (rainFollower != null)
        {
            Destroy(rainFollower.gameObject);
            rainFollower = null;
        }
    }

    // Rain falls in a box around whoever is listening (the player's camera), and sounds muffled indoors.
    private void Awake()
    {
        rain = gameObject.AddComponent<ParticleSystem>();
        rain.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = rain.main;
        main.loop = true;
        main.startLifetime = 1.2f;
        main.startSpeed = 0f;
        main.startSize3D = true;
        main.startSizeX = 0.012f; main.startSizeY = 0.45f; main.startSizeZ = 0.012f;
        main.startColor = new Color(0.75f, 0.8f, 0.88f, 0.35f);
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles = 4000;
        var velocity = rain.velocityOverLifetime;
        velocity.enabled = true;
        velocity.space = ParticleSystemSimulationSpace.World;
        velocity.x = new ParticleSystem.MinMaxCurve(0.6f);
        velocity.y = new ParticleSystem.MinMaxCurve(-14f);
        velocity.z = new ParticleSystem.MinMaxCurve(0.3f);
        var emission = rain.emission;
        int effects = GameSettings.Current.Effects;
        emission.rateOverTime = effects == 0 ? 700f : effects == 1 ? 1600f : 2800f;
        var shape = rain.shape;
        shape.shapeType = ParticleSystemShapeType.Box;
        shape.scale = new Vector3(30f, 0.5f, 30f);
        var collision = rain.collision;
        collision.enabled = true;
        collision.type = ParticleSystemCollisionType.World;
        collision.quality = ParticleSystemCollisionQuality.Low;
        collision.lifetimeLoss = 1f;
        var renderer = GetComponent<ParticleSystemRenderer>();
        rainMaterial = new Material(Shader.Find("Sprites/Default")) { name = "Rain" };
        renderer.sharedMaterial = rainMaterial;
        renderer.renderMode = ParticleSystemRenderMode.Stretch;
        renderer.velocityScale = 0.02f;
        renderer.lengthScale = 1f;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        rain.Play();
        rainSound = gameObject.AddComponent<AudioSource>();
        rainSound.clip = SoundBank.RainLoop;
        rainSound.loop = true;
        rainSound.spatialBlend = 0f;
        rainFilter = gameObject.AddComponent<AudioLowPassFilter>();
        rainSound.Play();
    }

    private float indoor;
    private int lastEffects = -1;

    private void LateUpdate()
    {
        int effects = GameSettings.Current.Effects;
        if (lastEffects != effects)
        {
            lastEffects = effects;
            var emission = rain.emission;
            emission.rateOverTime = effects == 0 ? 700f : effects == 1 ? 1600f : 2800f;
        }
        var ear = GameAudio.ListenerPosition;
        if (ear == null) return;
        transform.position = ear.Value + Vector3.up * 14f;
        // Under a roof the rain is outside: quieter and duller.
        bool covered = Physics.Raycast(ear.Value + Vector3.up * 0.3f, Vector3.up, 30f, Physics.DefaultRaycastLayers,
            QueryTriggerInteraction.Ignore);
        indoor = Mathf.MoveTowards(indoor, covered ? 1f : 0f, Time.deltaTime * 2f);
        rainFilter.cutoffFrequency = Mathf.Lerp(22000f, 900f, indoor);
        rainSound.volume = Mathf.Lerp(0.35f, 0.12f, indoor) * GameAudio.MixHeadroom * GameSettings.Volume(SoundCategory.Ambient);
    }

    private void OnDestroy()
    {
        if (rainMaterial != null) Destroy(rainMaterial);
        if (rainFollower == this) rainFollower = null;
    }
}
