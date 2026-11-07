#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;

// Opt-in regression scenarios. No gameplay component/manager is created.
public static class GameplayQualityChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static bool running;
    private static readonly List<string> results = new List<string>();
    private static string report;
    private static void Check(string name, bool passed, string detail = "")
    {
        string line = (passed ? "PASS " : "FAIL ") + name + " " + detail;
        results.Add(line); Debug.Log("QA: " + line);
        if (!string.IsNullOrEmpty(report)) File.WriteAllLines(report, results);
    }
    public static void RunPhysics()
    {
        results.Clear(); report = Path.GetFullPath("Captures/quality-physics.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(report));
        MultiplayerPrototype.Instance.StartCoroutine(PhysicsScenarios());
    }
    private static GameObject Box(Transform parent, string name, Vector3 position, Vector3 size)
    {
        var obj = GameObject.CreatePrimitive(PrimitiveType.Cube); obj.name = name;
        obj.transform.SetParent(parent); obj.transform.position = position; obj.transform.localScale = size;
        return obj;
    }
    private static IEnumerator PhysicsScenarios()
    {
        var app = MultiplayerPrototype.Instance;
        var rig = (LocalControlSwitcher)typeof(MultiplayerPrototype).GetField("sceneRig", Private).GetValue(app);
        var root = new GameObject("QA physics");
        Vector3 origin = new Vector3(500, 0, 500);
        Box(root.transform, "Ground", origin - Vector3.up * 0.5f, new Vector3(40, 1, 40));
        var obj = UnityEngine.Object.Instantiate(rig.human.gameObject); obj.name = "QA Human"; obj.transform.SetParent(root.transform);
        obj.SetActive(true); var human = obj.GetComponent<HumanController>();
        foreach (var listener in obj.GetComponentsInChildren<AudioListener>()) listener.enabled = false;
        float previousDamage = 0f;
        foreach (float height in new[] { 0.6f, 2f, 4f, 8f })
        {
            human.SetTrainingSpawn(origin + Vector3.up * height, Quaternion.identity); human.SetControlled(true);
            human.viewCamera.enabled = false; human.viewCamera.GetComponent<AudioListener>().enabled = false;
            yield return new WaitForSeconds(Mathf.Sqrt(2 * height / human.gravity) + 1.2f);
            float damage = human.maxHealth - human.Health;
            Check("landing " + height + "m", height <= 2 ? damage < 0.01f : damage > previousDamage && damage < human.maxHealth,
                "damage=" + damage.ToString("F2"));
            float health = human.Health;
            yield return new WaitForSeconds(0.35f);
            Check("landing not repeated " + height, Mathf.Abs(human.Health - health) < 0.01f);
            previousDamage = damage;
        }
        human.SetTrainingSpawn(origin + new Vector3(-8, 0.1f, 0), Quaternion.identity); human.SetControlled(false);
        var crate = Box(root.transform, "Carry crate", origin + new Vector3(0, 1, 2), Vector3.one * 0.6f);
        var body = crate.AddComponent<Rigidbody>(); crate.AddComponent<PhysicsProp>().Initialize(0, "Ящик", 12, 1000);
        var wall = Box(root.transform, "Carry wall", origin + new Vector3(0, 1.5f, 3), new Vector3(5, 3, 0.3f));
        var eye = new GameObject("Carry eye"); eye.transform.SetParent(root.transform);
        eye.transform.position = origin + new Vector3(0, 1, 0); eye.transform.rotation = Quaternion.identity;
        Physics.SyncTransforms();
        bool grabbed = CarrySystem.Instance.Grab(1001, eye.transform, obj.GetComponentsInChildren<Collider>(), body, Vector3.zero);
        Check("carry keeps player collisions", grabbed && !Physics.GetIgnoreCollision(obj.GetComponent<CapsuleCollider>(), crate.GetComponent<Collider>()));
        eye.transform.position += Vector3.forward * 2;
        yield return new WaitForSeconds(1.2f);
        Check("carry blocked by wall", body.position.z < wall.transform.position.z - 0.32f,
            "z=" + (body.position.z - origin.z).ToString("F3"));
        Check("carry speed bounded", body.linearVelocity.magnitude < 8.1f);
        CarrySystem.Instance.Release(1001);
        body.position = origin + new Vector3(0, 1, 1); body.mass = 70; body.linearVelocity = Vector3.zero;
        crate.transform.position = body.position;
        eye.transform.position = origin + new Vector3(0, 1, -1);
        Physics.SyncTransforms();
        bool first = CarrySystem.Instance.Grab(1001, eye.transform, Array.Empty<Collider>(), body, Vector3.zero);
        bool second = CarrySystem.Instance.Grab(1002, eye.transform, Array.Empty<Collider>(), body, Vector3.zero);
        Check("two carriers retained", first && second && CarrySystem.Instance.CarrierCount(body) == 2,
            "first=" + first + " second=" + second + " carriers=" + CarrySystem.Instance.CarrierCount(body));
        CarrySystem.Instance.ReleaseAll();
        // Walking contacts on a soft bed must produce steps, never furniture scrape events.
        var bed = Box(root.transform, "Soft bed", origin + new Vector3(-8, 0.4f, 0), new Vector3(3, 0.8f, 5));
        var bedBody = bed.AddComponent<Rigidbody>(); bed.AddComponent<PhysicsProp>().Initialize(0, "Кровать", 70, 1000);
        human.SetTrainingSpawn(origin + new Vector3(-8, 1, -1), Quaternion.identity); human.SetControlled(false);
        var humanBody = obj.GetComponent<Rigidbody>();
        int scrapes = 0;
        Action<Vector3, SoundMaterial, float, float, bool, bool> listen = (p, m, v, pitch, slide, heavy) => { if (slide) scrapes++; };
        GameAudio.PhysicsPlayed += listen;
        for (int i = 0; i < 45; i++)
        {
            humanBody.linearVelocity = new Vector3(0, humanBody.linearVelocity.y, 1.2f);
            yield return new WaitForFixedUpdate();
        }
        GameAudio.PhysicsPlayed -= listen;
        Check("bed foot contacts never scrape", scrapes == 0, "scrapes=" + scrapes);
        human.SetTrainingSpawn(origin + new Vector3(-12, 0.02f, 5), Quaternion.identity); human.SetControlled(false);
        var jamWall = Box(root.transform, "Jam wall", origin + new Vector3(-11.35f, 1.5f, 5), new Vector3(0.3f, 3, 3));
        var jamProp = Box(root.transform, "Jam prop", origin + new Vector3(-12.3f, 0.5f, 5), Vector3.one);
        var jamBody = jamProp.AddComponent<Rigidbody>(); jamProp.AddComponent<PhysicsProp>().Initialize(0, "Ящик", 8, 1000);
        Physics.SyncTransforms();
        yield return new WaitForSeconds(1f);
        bool overlap = Physics.ComputePenetration(obj.GetComponent<CapsuleCollider>(), humanBody.position, humanBody.rotation,
            jamProp.GetComponent<Collider>(), jamBody.position, jamBody.rotation, out _, out float jamDepth);
        Check("small prop does not permanently trap player", !overlap || jamDepth < 0.035f, "depth=" + jamDepth.ToString("F3"));
        Check("escape stays behind wall", humanBody.position.x < jamWall.transform.position.x - 0.3f);
        human.SetTrainingSpawn(origin + new Vector3(-15, 0.01f, 0), Quaternion.identity);
        typeof(HumanController).GetMethod("SetStance", Private).Invoke(human, new object[] { HumanController.Stance.Prone });
        var beam = Box(root.transform, "Low bed clearance", origin + new Vector3(-15, 0.85f, 0), new Vector3(3, 0.6f, 3));
        Physics.SyncTransforms();
        typeof(HumanController).GetMethod("UpdateStance", Private).Invoke(human, null);
        Check("cannot stand through low ceiling", human.IsProne);
        human.SetControlled(false); obj.SetActive(false);
        foreach (var p in root.GetComponentsInChildren<PhysicsProp>()) p.Body.isKinematic = true;
        // Concrete facade plus a slab behind it: a direct hit must open a local hole.
        var building = new GameObject("QA structure"); building.transform.SetParent(root.transform);
        var structure = building.AddComponent<BuildingStructure>();
        var blocks = new List<DestructibleBlock>();
        for (int x = -4; x <= 4; x++) for (int y = 0; y < 9; y++)
        {
            var b = Box(building.transform, "Wall", origin + new Vector3(x + 10, y + 0.5f, 5), Vector3.one);
            var block = b.AddComponent<DestructibleBlock>(); block.Initialize("QA/" + x + "/" + y, root.transform);
            block.SetStructure(structure, new Vector3Int(x, y, 0)); block.SetMaterial(3); blocks.Add(block);
        }
        Physics.SyncTransforms();
        int removed = DestructionSystem.ApplyExplosion(new ExplosionDamage { position = origin + new Vector3(10, 3.5f, 4.4f),
            radius = 7, impulse = 21, maxFragments = 80, maxActiveDebris = 1 });
        yield return null;
        int survivors = blocks.Count(b => b != null && !b.IsDetached);
        Check("concrete hit opens local hole", removed >= 5 && survivors >= 40, "removed=" + removed + " remaining=" + survivors);
        var listenerObject = new GameObject("QA limiter"); listenerObject.transform.SetParent(root.transform);
        var limiter = listenerObject.AddComponent<AudioPeakLimiter>();
        var samples = Enumerable.Repeat(3f, 2048).ToArray();
        typeof(AudioPeakLimiter).GetMethod("OnAudioFilterRead", Private).Invoke(limiter, new object[] { samples, 2 });
        Check("limiter stereo peaks", samples.Max() <= 0.781f && samples.Min() >= -0.781f);
        CarrySystem.Instance.ReleaseAll(); UnityEngine.Object.Destroy(root);
        Check("physics scenarios finished", true);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void CommandLineCheck()
    {
        if (running) return;
        var args = Environment.GetCommandLineArgs();
        bool host = args.Contains("-qa-host"), client = args.Contains("-qa-client");
        if (!host && !client) return;
        running = true; results.Clear();
        string folder = Path.Combine(Path.GetDirectoryName(Application.dataPath), "QA"); Directory.CreateDirectory(folder);
        report = Path.Combine(folder, host ? "host.txt" : "client.txt");
        MultiplayerPrototype.Instance.StartCoroutine(NetworkScenario(host));
    }
    private static IEnumerator NetworkScenario(bool host)
    {
        var app = MultiplayerPrototype.Instance;
        yield return null;
        var net = (NetworkManager)typeof(MultiplayerPrototype).GetField("network", Private).GetValue(app);
        net.GetComponent<UnityTransport>().SetConnectionData("127.0.0.1", 17842);
        bool started = host ? net.StartHost() : net.StartClient(); Check("transport start", started);
        float deadline = Time.realtimeSinceStartup + 45f;
        while ((host ? net.ConnectedClientsIds.Count < 2 : !net.IsConnectedClient) && Time.realtimeSinceStartup < deadline) yield return null;
        Check("real peer connected", host ? net.ConnectedClientsIds.Count == 2 : net.IsConnectedClient);
        if (!net.IsConnectedClient) { Application.Quit(1); yield break; }
        if (host)
        {
            var settings = new PrototypeLobbySettings { Density = 0, HidingSeconds = 10, HumanLives = 3, FallDamage = false };
            typeof(MultiplayerPrototype).GetField("settings", Private).SetValue(app, settings);
            // A stationary bot supplies the drone role while both real peers exercise carrying humans.
            var members = (Dictionary<ulong, PrototypeMember>)typeof(MultiplayerPrototype).GetField("members", Private).GetValue(app);
            members[99] = new PrototypeMember { Id = 99, Name = "QA bot", Team = PrototypeTeam.Drones, Ready = true };
            foreach (var member in members.Values) { member.Ready = true; if (member.Id != 99) member.Team = PrototypeTeam.Humans; }
            app.StartMatch();
        }
        deadline = Time.realtimeSinceStartup + 60f;
        while (!app.InMatch && Time.realtimeSinceStartup < deadline) yield return null;
        Check("map handshake and spawn", app.InMatch);
        if (!app.InMatch) { Application.Quit(2); yield break; }
        yield return new WaitForSeconds(1f);
        var round = UnityEngine.Object.FindFirstObjectByType<HideAndSeekRound>();
        var music = (AudioSource)typeof(HideAndSeekRound).GetField("preparationMusic", Private).GetValue(round);
        Check("preparation music once", music != null && music.isPlaying);
        string initial = string.Join(",", PowerSystem.Instance.CaptureFlags());
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(report), host ? "host-power.txt" : "client-power.txt"), initial);
        var appliances = UnityEngine.Object.FindObjectsByType<PoweredDevice>().Where(d => d.Appliance).ToArray();
        Check("most appliances off", appliances.Count(d => d.On) < appliances.Length / 2);
        while (app.State.Phase == PrototypePhase.Hiding && Time.realtimeSinceStartup < deadline) yield return null;
        yield return null;
        Check("search stops music", app.State.Phase == PrototypePhase.Hunting && music != null && !music.isPlaying);
        yield return new WaitForSeconds(0.5f);
        Check("music stays stopped", !music.isPlaying);
        if (host)
        {
            var device = UnityEngine.Object.FindObjectsByType<PoweredDevice>().First(d => d.Lamp != null && !d.MotionSensor && d.Powered);
            PowerSystem.Instance.Toggle(device.Index, new Ray(device.transform.position + device.transform.forward, -device.transform.forward));
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(report), "expected-switch.txt"), device.Index + ":" + device.Flags);
            var actors = (Dictionary<ulong, PrototypeNetworkActor>)typeof(MultiplayerPrototype).GetField("actors", Private).GetValue(app);
            var block = UnityEngine.Object.FindObjectsByType<DestructibleBlock>().First(b => b.Material == 3 && b.GridCell.y == 3 && !b.IsDetached);
            DestructionSystem.ApplyExplosion(new ExplosionDamage { position = block.transform.position + Vector3.forward * 0.65f,
                radius = 7, impulse = 10, maxFragments = 40, maxActiveDebris = 24 });
            File.WriteAllLines(Path.Combine(Path.GetDirectoryName(report), "host-destroyed.txt"), DestructionSystem.LastDetachedIds);
        }
        yield return new WaitForSeconds(3f);
        string expected = File.ReadAllText(Path.Combine(Path.GetDirectoryName(report), "expected-switch.txt"));
        var parts = expected.Split(':');
        Check("power state replicated", PowerSystem.Instance.CaptureFlags()[int.Parse(parts[0])] == int.Parse(parts[1]));
        var destroyed = File.ReadAllLines(Path.Combine(Path.GetDirectoryName(report), "host-destroyed.txt"));
        var staticIds = new HashSet<string>(UnityEngine.Object.FindObjectsByType<DestructibleBlock>().Where(b => !b.IsDetached).Select(b => b.StableId));
        Check("destruction replicated", destroyed.Length > 0 && destroyed.All(id => !staticIds.Contains(id)), "blocks=" + destroyed.Length);
        Check("one motor source per drone", UnityEngine.Object.FindObjectsByType<DroneMotorAudio>().All(d =>
            d.GetComponentsInChildren<AudioSource>().Count(s => s.gameObject.name == "Motor Audio") == 1));
        Check("network scenarios finished", true);
        if (!host) Application.Quit(0);
        else { yield return new WaitForSeconds(2f); Application.Quit(0); }
    }
}
#endif
