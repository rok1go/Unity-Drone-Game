using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Multiplayer;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

[DefaultExecutionOrder(-500)]
public class MultiplayerPrototype : MonoBehaviour
{
    public static MultiplayerPrototype Instance { get; private set; }
    public static bool OnlineMatch => Instance != null && Instance.InMatch;
    public bool InMatch { get; private set; }
    public bool InTraining { get; private set; }
    public bool MenuOpen => menu != null && menu.Visible;
    public bool Busy { get; private set; }
    public bool IsHost => network != null && network.IsHost;
    public ulong LocalId => network != null ? network.LocalClientId : 0;
    public PrototypeState State { get; private set; } = new PrototypeState();
    public string JoinCode => session != null ? session.Code : State.Code;
    public string PlayerName { get; private set; }
    public bool HasSession => session != null;
    public bool LocalIsDrone => members.TryGetValue(LocalId, out var member) && member.Team == PrototypeTeam.Drones;
    public bool LocalDead => !actors.TryGetValue(LocalId, out var actor) || actor == null || actor.Dead;

    private const string Channel = "hide-and-seek-v1";
    private NetworkManager network;
    private ISession session;
    private PrototypeMenu menu;
    private LocalControlSwitcher sceneRig, localControls;
    private GameObject humanTemplate, droneTemplate;
    private Camera menuCamera;
    private string scenePath;
    public ProceduralMapGenerator TrainingGenerator { get; private set; }
    public PrototypeLobbySettings TrainingSettings { get; set; } = new PrototypeLobbySettings();
    private bool suburbanTraining;
    private int trainingSeed, trainingBuildingCount;
    private Vector2 trainingMapSize;
    private bool trainingRoofs;
    private PrototypeLobbySettings settings = new PrototypeLobbySettings();
    private readonly Dictionary<ulong, PrototypeMember> members = new Dictionary<ulong, PrototypeMember>();
    private readonly Dictionary<ulong, PrototypeNetworkActor> actors = new Dictionary<ulong, PrototypeNetworkActor>();
    private readonly Dictionary<ulong, float> lastPoseArrival = new Dictionary<ulong, float>();
    private readonly Dictionary<ulong, float> lastCorrection = new Dictionary<ulong, float>();
    private readonly Dictionary<ulong, float> respawns = new Dictionary<ulong, float>();
    private readonly Dictionary<ulong, int> humanLives = new Dictionary<ulong, int>();
    private readonly Dictionary<ulong, float> humanRespawns = new Dictionary<ulong, float>();
    private readonly Dictionary<ulong, string> mapHashes = new Dictionary<ulong, string>();
    private readonly HashSet<ulong> sceneReady = new HashSet<ulong>();
    private readonly List<ulong> recipients = new List<ulong>();
    private readonly List<PrototypeNetworkActor> snapshotActors = new List<PrototypeNetworkActor>();
    private readonly List<byte[]> outgoing = new List<byte[]>();
    private DestructionReplication replication;
    private string localMapHash = "";
    private float nextSnapshot, nextHeartbeat;
    private int lastStateHash;
    private PrototypePhase phase = PrototypePhase.Lobby;
    private double phaseEnds;
    private int stock, generation;
    private bool handlersRegistered, leaving, sceneLoading, loadForMatch, waitingForScenes, localControlInitialized, localActive;
    private bool dronesWon;
    private string resultReason;
    private RoundSummary summary;
    private float loadingDeadline;
    private string networkStartupError;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() => Instance = null;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Bootstrap()
    {
        if (Instance == null) new GameObject("MultiplayerPrototype").AddComponent<MultiplayerPrototype>();
    }

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
        Application.runInBackground = true;
        PlayerName = PlayerPrefs.GetString("prototype.playerName", "Игрок");
        TrainingSettings = PrototypeLobbySettings.Load();
        // The menu and scene gate must exist even if the optional network startup fails.
        menu = gameObject.AddComponent<PrototypeMenu>();
        menu.Initialize(this);
        replication = new DestructionReplication();
        replication.WindowRequested += RequestWindow;
        replication.DoorRequested += RequestDoor;
        CarrySystem.CarryChanged += CarryChanged;
        CarrySystem.GripsChanged += GripsChanged;
        HumanController.FallDamaged += FallDamaged;
        SceneManager.sceneLoaded += SceneLoaded;
        try { InitializeNetworkManager(); }
        catch (Exception exception)
        {
            networkStartupError = "Не удалось подготовить сеть: " + exception.Message;
            Debug.LogException(exception, this);
        }
    }

    private void InitializeNetworkManager()
    {
        var netObject = new GameObject("PrototypeNetwork");
        netObject.SetActive(false);
        DontDestroyOnLoad(netObject);
        var transport = netObject.AddComponent<UnityTransport>();
        // A large building collapse is one reliable message; the 6 KB default is too small for it.
        transport.MaxPayloadSize = 256 * 1024;
        network = netObject.AddComponent<NetworkManager>();
        // NetworkManager's Editor validation creates this implicitly; a runtime AddComponent does not.
        network.NetworkConfig = new NetworkConfig
        {
            NetworkTransport = transport,
            EnableSceneManagement = false,
            ConnectionApproval = true,
            ForceSamePrefabs = false
        };
        // Configure before Awake initializes prefabs and OnEnable installs the singleton.
        netObject.SetActive(true);
        network.ConnectionApprovalCallback = (request, response) =>
        {
            response.Approved = phase == PrototypePhase.Lobby && members.Count < settings.MaxPlayers &&
                (!Busy || request.ClientNetworkId == NetworkManager.ServerClientId);
            response.CreatePlayerObject = false;
            if (!response.Approved) response.Reason = "Матч уже начался или лобби заполнено.";
        };
        network.OnServerStarted += RegisterMessages;
        network.OnClientStarted += RegisterMessages;
        network.OnClientConnectedCallback += Connected;
        network.OnClientDisconnectCallback += Disconnected;
        network.OnTransportFailure += TransportFailed;
    }

    private void SceneLoaded(Scene scene, LoadSceneMode mode)
    {
        sceneRig = FindObjectsByType<LocalControlSwitcher>().FirstOrDefault(r => r.gameObject.scene == scene);
        if (sceneRig == null) return;
        DestructionSystem.Authority = true;
        replication.End();
        scenePath = scene.path;
        TrainingGenerator = FindObjectsByType<ProceduralMapGenerator>().FirstOrDefault(g => g.gameObject.scene == scene);
        sceneRig.enabled = false;
        humanTemplate = sceneRig.human.gameObject; droneTemplate = sceneRig.drone.gameObject;
        sceneRig.human.SetControlled(false); sceneRig.drone.SetControlled(false);
        humanTemplate.SetActive(false); droneTemplate.SetActive(false);
        localControls = null; localControlInitialized = false;
        actors.Clear(); respawns.Clear(); lastPoseArrival.Clear(); lastCorrection.Clear();
        if (menuCamera != null) Destroy(menuCamera.gameObject);
        var cameraObject = new GameObject("MenuCamera");
        menuCamera = cameraObject.AddComponent<Camera>();
        menuCamera.CopyFrom(sceneRig.human.viewCamera);
        menuCamera.transform.SetPositionAndRotation(sceneRig.human.viewCamera.transform.position,
            sceneRig.human.viewCamera.transform.rotation);
        menuCamera.enabled = true;
        cameraObject.AddComponent<AudioListener>();
        sceneLoading = false;
        if (InTraining)
        {
            StartTraining(suburbanTraining, trainingSeed, trainingMapSize, trainingBuildingCount, trainingRoofs);
            if (InTraining) return;
        }
        if (waitingForScenes && network != null && network.IsListening)
        {
            Transform mapRoot = loadForMatch ? PrepareMatchMap() : null;
            // Index the fresh map before any destruction op can reference it.
            localMapHash = replication.BuildIndex(mapRoot);
            if (IsHost) { sceneReady.Add(LocalId); TryFinishLoading(); }
            else SendServer(new PrototypeMessage { Kind = "scene-ready", Generation = generation, Text = localMapHash });
        }
        else
        {
            phase = PrototypePhase.Lobby; menu.ShowHome(); LockCursor(false);
            if (!string.IsNullOrEmpty(networkStartupError)) menu.ShowError(networkStartupError);
        }
    }

    private PrototypeLobbySettings CurrentSettings => IsHost ? settings : State.Settings ?? settings;
    private bool suburbMatch;

    // Builds the match map on every peer before indexing. The suburb is generated from the same
    // data file everywhere, so its blocks, windows and doors get identical indices.
    private Transform PrepareMatchMap()
    {
        MatchRules.Apply(CurrentSettings);
        suburbMatch = false;
        if (sceneRig == null) return null;
        if (!CurrentSettings.Suburb)
        {
            if (TrainingGenerator != null) TrainingGenerator.TryRegenerateMap();
            return TrainingGenerator != null ? TrainingGenerator.MapRoot : null;
        }
        var map = SuburbanTrainingMap.Create(sceneRig.transform);
        if (map == null)
        {
            Debug.LogWarning("Voxel suburb data is missing; the match uses the generated map.");
            return null;
        }
        if (TrainingGenerator != null) TrainingGenerator.HideGeneratedMap();
        if (sceneRig.testArea != null) sceneRig.testArea.gameObject.SetActive(false);
        suburbMatch = true;
        return map.transform;
    }

    public void SavePlayerName(string name)
    {
        PlayerName = CleanName(name);
        PlayerPrefs.SetString("prototype.playerName", PlayerName);
        PlayerPrefs.Save();
    }

    private static string CleanName(string value)
    {
        value = string.IsNullOrWhiteSpace(value) ? "Игрок" : value.Trim().Replace("\n", " ").Replace("\r", " ");
        return value.Length > 24 ? value.Substring(0, 24) : value;
    }

    private async Task InitializeServices()
    {
        if (UnityServices.State != ServicesInitializationState.Initialized)
        {
            string profile = "prototype-" + System.Diagnostics.Process.GetCurrentProcess().Id;
            await UnityServices.InitializeAsync(new InitializationOptions().SetProfile(profile));
        }
        if (!AuthenticationService.Instance.IsSignedIn) await AuthenticationService.Instance.SignInAnonymouslyAsync();
    }

    public async void CreateLobby(PrototypeLobbySettings options)
    {
        if (Busy || session != null) return;
        if (!NetworkReady()) return;
        Busy = true; menu.SetBusy("Создаём лобби…");
        try
        {
            options.Clamp(); options.Save(); settings = options; phase = PrototypePhase.Lobby;
            await InitializeServices();
            session = await MultiplayerService.Instance.CreateSessionAsync(new SessionOptions
            {
                Name = settings.Name, MaxPlayers = settings.MaxPlayers, IsPrivate = true
            }.WithRelayNetwork());
            RegisterMessages();
            members[LocalId] = new PrototypeMember { Id = LocalId, Name = PlayerName, Team = PrototypeTeam.Humans };
            PublishState(); menu.ShowLobby();
        }
        catch (Exception exception) { await ConnectionFailed(exception); }
        finally { Busy = false; menu.SetBusy(null); }
    }

    public async void JoinLobby(string code)
    {
        if (Busy || session != null) return;
        if (!NetworkReady()) return;
        code = (code ?? "").Trim().ToUpperInvariant();
        if (code.Length < 4 || code.Length > 12 || code.Any(c => !char.IsLetterOrDigit(c)))
        { menu.ShowError("Введи код лобби без пробелов."); return; }
        Busy = true; menu.SetBusy("Подключаемся…");
        try
        {
            await InitializeServices();
            session = await MultiplayerService.Instance.JoinSessionByCodeAsync(code);
            RegisterMessages();
            SendServer(new PrototypeMessage { Kind = "hello", Name = PlayerName });
            menu.ShowLobby();
        }
        catch (Exception exception) { await ConnectionFailed(exception); }
        finally { Busy = false; menu.SetBusy(null); }
    }

    private async Task ConnectionFailed(Exception exception)
    {
        await CloseConnection();
        menu.ShowError("Не удалось подключиться: " + exception.Message);
        Debug.LogWarning("Multiplayer connection: " + exception.Message);
    }

    private bool NetworkReady()
    {
        if (network != null && network.NetworkConfig != null && string.IsNullOrEmpty(networkStartupError)) return true;
        menu.ShowError(networkStartupError ?? "Сетевой модуль недоступен.");
        return false;
    }

    private void RegisterMessages()
    {
        if (handlersRegistered || network.CustomMessagingManager == null) return;
        network.CustomMessagingManager.RegisterNamedMessageHandler(Channel, Receive);
        network.CustomMessagingManager.RegisterNamedMessageHandler(NetSync.SnapshotChannel, ReceiveSnapshot);
        network.CustomMessagingManager.RegisterNamedMessageHandler(DestructionReplication.OpsChannel, ReceiveDestruction);
        handlersRegistered = true;
    }

    private void Connected(ulong id)
    {
        RegisterMessages();
        if (!IsHost) return;
        if (!members.ContainsKey(id))
            members[id] = new PrototypeMember { Id = id, Name = "Подключение…", Team =
                members.Values.Count(m => m.Team == PrototypeTeam.Humans) <= members.Values.Count(m => m.Team == PrototypeTeam.Drones)
                    ? PrototypeTeam.Humans : PrototypeTeam.Drones };
        PublishState();
    }

    private void Disconnected(ulong id)
    {
        if (leaving) return;
        if (IsHost)
        {
            members.Remove(id); sceneReady.Remove(id); respawns.Remove(id); mapHashes.Remove(id);
            CarrySystem.Instance.Release(id);
            lastPoseArrival.Remove(id); lastCorrection.Remove(id);
            if (actors.TryGetValue(id, out var actor)) { if (actor != null) Destroy(actor.gameObject); actors.Remove(id); }
            if (waitingForScenes) TryFinishLoading();
            PublishState();
        }
        else if (id == LocalId) LeaveWithReason("Соединение с хостом потеряно.");
    }

    private void TransportFailed() { if (!leaving) LeaveWithReason("Сетевое соединение прервано."); }

    private void Send(ulong id, PrototypeMessage message)
    {
        if (!network.IsListening || network.CustomMessagingManager == null) return;
        string json = JsonUtility.ToJson(message);
        using var writer = new FastBufferWriter(json.Length * 2 + 32, Allocator.Temp);
        writer.WriteValueSafe(json);
        network.CustomMessagingManager.SendNamedMessage(Channel, id, writer, NetworkDelivery.ReliableFragmentedSequenced);
    }

    private void SendServer(PrototypeMessage message)
    {
        if (IsHost) ProcessCommand(LocalId, message);
        else Send(NetworkManager.ServerClientId, message);
    }

    private void Broadcast(PrototypeMessage message)
    {
        foreach (ulong id in network.ConnectedClientsIds)
            if (id != LocalId) Send(id, message);
    }

    private List<ulong> RemoteClients()
    {
        recipients.Clear();
        foreach (ulong id in network.ConnectedClientsIds) if (id != LocalId) recipients.Add(id);
        return recipients;
    }

    private void SendRaw(string channel, IReadOnlyList<ulong> targets, byte[] data, NetworkDelivery delivery)
    {
        if (targets.Count == 0 || network.CustomMessagingManager == null) return;
        var writer = new FastBufferWriter(data.Length, Allocator.Temp);
        try
        {
            writer.WriteBytesSafe(data);
            network.CustomMessagingManager.SendNamedMessage(channel, targets, writer, delivery);
        }
        finally { writer.Dispose(); }
    }

    private void ReceiveSnapshot(ulong sender, FastBufferReader reader)
    {
        try
        {
            reader.ReadValueSafe(out byte kind);
            reader.ReadValueSafe(out int messageGeneration);
            if (!InMatch || messageGeneration != generation) return;
            if (IsHost)
            {
                if (kind == NetSync.OwnerPose) ReceiveClientPose(sender, NetSync.ReadPose(reader));
                return;
            }
            if (sender != NetworkManager.ServerClientId) return;
            if (kind == NetSync.ActorSnapshot)
            {
                reader.ReadValueSafe(out byte count);
                for (int i = 0; i < count; i++)
                {
                    reader.ReadValueSafe(out ulong id);
                    var pose = NetSync.ReadPose(reader);
                    if (id != LocalId && actors.TryGetValue(id, out var actor) && actor != null && !actor.Dead)
                        actor.PushRemotePose(pose);
                }
            }
            else if (kind == NetSync.DebrisSnapshot) replication.ReadDebrisSnapshot(reader);
        }
        catch (Exception exception) { Debug.LogWarning("Ignored invalid snapshot: " + exception.Message); }
    }

    private void ReceiveDestruction(ulong sender, FastBufferReader reader)
    {
        if (IsHost || sender != NetworkManager.ServerClientId) return;
        try { replication.ApplyOps(reader, generation); }
        catch (Exception exception) { Debug.LogWarning("Ignored invalid destruction message: " + exception.Message); }
    }

    private void SendSnapshots()
    {
        if (IsHost)
        {
            var targets = RemoteClients();
            double now = NetSync.Now;
            if (targets.Count == 0)
            {
                // Nobody to send to: still drain the queues so they cannot grow for the rest of the match.
                outgoing.Clear();
                replication.CollectDebrisSnapshots(generation, now, outgoing);
                replication.CollectOpsMessages(generation, outgoing);
                outgoing.Clear();
                return;
            }
            snapshotActors.Clear();
            foreach (var actor in actors.Values)
                if (actor != null && !actor.Dead && (actor.IsLocal || actor.HasPose)) snapshotActors.Add(actor);
            var writer = new FastBufferWriter(NetSync.MaxUnreliableBytes, Allocator.Temp);
            try
            {
                writer.WriteValueSafe(NetSync.ActorSnapshot);
                writer.WriteValueSafe(generation);
                writer.WriteValueSafe((byte)snapshotActors.Count);
                foreach (var actor in snapshotActors)
                {
                    writer.WriteValueSafe(actor.OwnerId);
                    // Relay remote owners' poses unchanged: re-sampling would add a second delay.
                    NetSync.WritePose(writer, actor.IsLocal ? actor.CaptureNet(now) : actor.LatestPose);
                }
                network.CustomMessagingManager.SendNamedMessage(NetSync.SnapshotChannel, targets, writer, NetworkDelivery.Unreliable);
            }
            finally { writer.Dispose(); }

            outgoing.Clear();
            replication.CollectDebrisSnapshots(generation, now, outgoing);
            foreach (var packet in outgoing) SendRaw(NetSync.SnapshotChannel, targets, packet, NetworkDelivery.Unreliable);
            outgoing.Clear();
            replication.CollectOpsMessages(generation, outgoing);
            foreach (var message in outgoing)
                SendRaw(DestructionReplication.OpsChannel, targets, message, NetworkDelivery.ReliableFragmentedSequenced);
            outgoing.Clear();
            return;
        }
        if (!actors.TryGetValue(LocalId, out var local) || local == null || local.Dead) return;
        if (local.Team == PrototypeTeam.Drones && phase != PrototypePhase.Hunting) return;
        var own = new FastBufferWriter(64, Allocator.Temp);
        try
        {
            own.WriteValueSafe(NetSync.OwnerPose);
            own.WriteValueSafe(generation);
            NetSync.WritePose(own, local.CaptureNet(NetSync.Now));
            network.CustomMessagingManager.SendNamedMessage(NetSync.SnapshotChannel, NetworkManager.ServerClientId, own,
                NetworkDelivery.Unreliable);
        }
        finally { own.Dispose(); }
    }

    private void Receive(ulong sender, FastBufferReader reader)
    {
        try
        {
            if (reader.Length > 128 * 1024) return;
            reader.ReadValueSafe(out string json);
            var message = JsonUtility.FromJson<PrototypeMessage>(json);
            if (message == null) return;
            if (IsHost) ProcessCommand(sender, message);
            else if (sender == NetworkManager.ServerClientId) ProcessServerMessage(message);
        }
        catch (Exception exception) { Debug.LogWarning("Ignored invalid multiplayer message: " + exception.Message); }
    }

    private void ProcessCommand(ulong sender, PrototypeMessage message)
    {
        if (!members.TryGetValue(sender, out var member)) return;
        if (message.Kind == "scene-ready" && message.Generation == generation && waitingForScenes)
        { mapHashes[sender] = message.Text ?? ""; sceneReady.Add(sender); TryFinishLoading(); return; }
        if (message.Kind == "detonate" && InMatch && message.Generation == generation && phase == PrototypePhase.Hunting)
        {
            if (actors.TryGetValue(sender, out var actor) && actor.Team == PrototypeTeam.Drones && !actor.Dead)
            {
                if (message.Pose != null && NetSync.Finite(message.Pose.Position))
                {
                    // Detonate where the pilot saw the hit, within what the drone could have flown since.
                    Vector3 reference = actor.HasPose ? actor.LatestPose.Position : actor.transform.position;
                    Vector3 position = reference + Vector3.ClampMagnitude(message.Pose.Position - reference, 8f);
                    actor.SnapTo(position, NetSync.UnpackRotation(NetSync.PackRotation(message.Pose.Rotation)));
                }
                actor.Drone.DetonateAuthoritatively();
            }
            return;
        }
        if (message.Kind == "grab" && InMatch && message.Generation == generation && phase != PrototypePhase.Finished)
        {
            // The host carries for remote players: the object follows their replicated view.
            // The grab point arrives in the object's own space, so it lands on the same spot of the host's copy.
            bool ok = settings.Carrying && actors.TryGetValue(sender, out var actor) && actor.Team == PrototypeTeam.Humans && !actor.Dead &&
                      actor.View != null && message.Pose != null && NetSync.Finite(message.Pose.Position) &&
                      message.Pose.Position.sqrMagnitude < 25f &&
                      CarrySystem.Instance.Grab(sender, actor.View.transform, actor.GetComponentsInChildren<Collider>(),
                          replication.Resolve(unchecked((uint)message.Index)), message.Pose.Position);
            if (!ok) Send(sender, new PrototypeMessage { Kind = "carry", Ready = false });
            return;
        }
        if ((message.Kind == "pocket" || message.Kind == "throw" || message.Kind == "beam") && InMatch &&
            message.Generation == generation && phase != PrototypePhase.Finished)
        {
            if (!actors.TryGetValue(sender, out var owner) || owner.Human == null || owner.Dead || owner.View == null) return;
            Vector3 eye = owner.View.transform.position;
            if (message.Kind == "pocket")
            {
                var body = replication.Resolve(unchecked((uint)message.Index));
                if (body != null) Inventory.HostPickup(sender, owner.Human, body.GetComponent<PhysicsProp>(), eye);
            }
            else if (message.Kind == "throw" && message.Pose != null && NetSync.Finite(message.Pose.Position) &&
                     (message.Pose.Position - eye).sqrMagnitude < 2f * 2f)
                Inventory.HostThrow(sender, message.Value, new Ray(message.Pose.Position, message.Pose.Rotation * Vector3.forward),
                    owner.Human.GetComponent<Rigidbody>().linearVelocity);
            else if (message.Kind == "beam") { Inventory.HostBeam(sender, owner.Human, message.Ready); PublishState(); }
            return;
        }
        if (message.Kind == "dronelight" && InMatch && message.Generation == generation)
        {
            if (actors.TryGetValue(sender, out var pilot) && pilot.Drone != null) SetDroneLight(sender, message.Ready);
            return;
        }
        if (message.Kind == "push" && InMatch && message.Generation == generation && phase != PrototypePhase.Finished)
        {
            // The shove follows the pusher's replicated view, from no further than arm's reach.
            if (settings.Carrying && actors.TryGetValue(sender, out var pusher) && pusher.Human != null && !pusher.Dead &&
                pusher.View != null && message.Pose != null && NetSync.Finite(message.Pose.Position) &&
                (message.Pose.Position - pusher.View.transform.position).sqrMagnitude < (CarrySystem.PushReach + 1f) * (CarrySystem.PushReach + 1f))
                CarrySystem.Push(replication.Resolve(unchecked((uint)message.Index)), message.Pose.Position,
                    message.Pose.Position - pusher.View.transform.position);
            return;
        }
        if (message.Kind == "hold" && InMatch && message.Generation == generation && message.Pose != null &&
            NetSync.Finite(message.Pose.Position))
        {
            CarrySystem.Instance.SetHold(sender, message.Pose.Position.x, message.Pose.Rotation, message.Ready);
            return;
        }
        if (message.Kind == "drop" && InMatch && message.Generation == generation)
        {
            CarrySystem.Instance.Release(sender);
            return;
        }
        if (message.Kind == "attach" && InMatch && message.Generation == generation)
        {
            if (actors.TryGetValue(sender, out var actor) && !actor.Dead && actor.Human != null && phase != PrototypePhase.Finished)
            {
                if (message.Value == 1) CarrySystem.Instance.QuickBarricade(sender);
                else CarrySystem.Instance.Attach(sender);
            }
            return;
        }
        if (message.Kind == "power-request" && InMatch && message.Generation == generation && phase != PrototypePhase.Finished)
        {
            if (actors.TryGetValue(sender, out var actor) && actor.Human != null && !actor.Dead && actor.View != null)
                PowerSystem.Instance?.Toggle(message.Index, new Ray(actor.View.transform.position, actor.View.transform.forward));
            return;
        }
        if (message.Kind == "fall" && InMatch && settings.FallDamage && message.Generation == generation &&
            phase != PrototypePhase.Finished && actors.TryGetValue(sender, out var falling) && falling.Human != null && !falling.Dead)
        {
            falling.Human.ApplyDamage(Mathf.Clamp(message.Value, 0, 200)); PublishState(); return;
        }
        if (message.Kind == "door" && InMatch && message.Generation == generation && phase != PrototypePhase.Finished)
        {
            if (actors.TryGetValue(sender, out var actor) && actor.Team == PrototypeTeam.Humans && !actor.Dead &&
                message.Value >= -1 && message.Value <= 3 && message.Value != 2 &&
                replication.TryGetDoorPosition(message.Index, out Vector3 door))
            {
                Vector3 feet = actor.HasPose ? actor.LatestPose.Position : actor.transform.position;
                if ((door - feet).sqrMagnitude < 5f * 5f)
                    replication.HostSetDoor(message.Index, (InteractiveDoor.DoorState)message.Value, feet + Vector3.up);
            }
            return;
        }
        if (message.Kind == "window" && InMatch && message.Generation == generation && phase != PrototypePhase.Finished)
        {
            // People break panes by hand, a drone by flying through them; both only within a few metres.
            if (actors.TryGetValue(sender, out var actor) && !actor.Dead &&
                replication.TryGetWindowPosition(message.Index, out Vector3 window))
            {
                Vector3 eye = (actor.HasPose ? actor.LatestPose.Position : actor.transform.position) + Vector3.up * 1.5f;
                if ((window - eye).sqrMagnitude < 5f * 5f) replication.HostShatterWindow(message.Index, eye);
            }
            return;
        }
        if (phase != PrototypePhase.Lobby || Busy) return;
        if (message.Kind == "hello") member.Name = CleanName(message.Name);
        else if (message.Kind == "team" && (message.Team == 0 || message.Team == 1))
        { member.Team = (PrototypeTeam)message.Team; member.Ready = false; }
        else if (message.Kind == "ready") member.Ready = message.Ready;
        else return;
        PublishState();
    }

    private void ProcessServerMessage(PrototypeMessage message)
    {
        if (message.Kind == "power" && InMatch && message.Generation == generation)
        { PowerSystem.Instance?.Apply(message.Index, message.Value); return; }
        if (message.Kind == "stoneblast" && InMatch && message.Generation == generation && message.Pose != null &&
            NetSync.Finite(message.Pose.Position))
        {
            float radius = message.Value / 10f, impulse = message.Index / 10f;
            CursedStone.PlayEffect(message.Pose.Position, radius, impulse);
            DestructionSystem.ApplyBlastForces(message.Pose.Position, radius, impulse);
            return;
        }
        if (message.Kind == "mark" && InMatch && message.Generation == generation && message.Pose != null &&
            NetSync.Finite(message.Pose.Position))
        { ActivityMarks.Show(message.Pose.Position); return; }
        if (message.Kind == "inv" && InMatch && message.Generation == generation)
        {
            var items = new List<PhysicsProp>();
            foreach (string part in (message.Text ?? "").Split(','))
            {
                var body = int.TryParse(part, out int id) ? replication.Resolve(unchecked((uint)id)) : null;
                items.Add(body != null ? body.GetComponent<PhysicsProp>() : null);
            }
            Inventory.ApplyLocal(items);
            return;
        }
        if (message.Kind == "state" && message.State != null)
        {
            State = message.State; phase = State.Phase;
            PowerSystem.Instance?.ApplySnapshot(State.PowerStates);
            members.Clear(); foreach (var member in State.Members) members[member.Id] = member;
            if ((phase == PrototypePhase.Hiding || phase == PrototypePhase.Hunting || phase == PrototypePhase.Finished) && !InMatch)
                SpawnMatchActors(State);
            if (InMatch)
            {
                var present = new HashSet<ulong>(State.Members.Select(m => m.Id));
                foreach (ulong id in actors.Keys.Where(id => !present.Contains(id)).ToArray())
                { if (actors[id] != null) Destroy(actors[id].gameObject); actors.Remove(id); }
                foreach (var actorState in State.Actors)
                    if (actors.TryGetValue(actorState.Id, out var actor) && actor != null) actor.ReceiveServerState(actorState);
                SynchronizeLocalRound();
            }
            else if (phase == PrototypePhase.Lobby) { waitingForScenes = false; menu.EnsureLobby(); }
        }
        else if (message.Kind == "load")
        {
            generation = message.Generation; loadForMatch = message.Text == "match";
            waitingForScenes = true; LoadLocalScene();
        }
        else if (message.Kind == "blast" && actors.TryGetValue(message.ActorId, out var actor) && actor.Drone != null)
        {
            if (message.Pose != null) actor.SnapTo(message.Pose.Position, message.Pose.Rotation);
            bool alreadyDestroyed = actor.Drone.IsDestroyed;
            actor.Drone.ApplyNetworkDestroyed(true);
            // Walls and rubble arrive through the destruction stream; only locally simulated
            // bodies (this player's own avatar) still need the blast push.
            if (!alreadyDestroyed)
                DestructionSystem.ApplyBlastForces(actor.transform.position, actor.Drone.explosionRadius,
                    actor.Drone.explosionImpulse, actor.GetComponent<Rigidbody>());
        }
        else if (message.Kind == "grips")
            PlayerCarry.ApplyRemoteGrips(replication.Resolve(unchecked((uint)message.Index)), message.Value);
        else if (message.Kind == "carry")
            PlayerCarry.ApplyRemoteState(message.Ready ? replication.Resolve(unchecked((uint)message.Index)) : null);
        else if (message.Kind == "correct" && message.Pose != null &&
                 actors.TryGetValue(LocalId, out var self) && self != null)
            self.ApplyCorrection(message.Pose.Position);
        else if (message.Kind == "close") LeaveWithReason(message.Text);
    }

    public void ChooseTeam(PrototypeTeam team) => SendServer(new PrototypeMessage { Kind = "team", Team = (int)team });
    public void SetReady(bool ready) => SendServer(new PrototypeMessage { Kind = "ready", Ready = ready });

    public void ChangeSettings(PrototypeLobbySettings options)
    {
        if (!IsHost || phase != PrototypePhase.Lobby || Busy) return;
        options.Clamp(); options.MaxPlayers = settings.MaxPlayers;
        if (JsonUtility.ToJson(settings) == JsonUtility.ToJson(options)) return;
        // Players keep their "ready": the new settings reach everyone at once through the lobby state.
        settings = options; settings.Save();
        PublishState();
    }

    public async void StartMatch()
    {
        if (!IsHost || Busy || phase != PrototypePhase.Lobby) return;
        if (members.Count < 2 || members.Values.Any(m => !m.Ready))
        { menu.ShowError("Нужны хотя бы два игрока. Все должны нажать «Готов»."); return; }
        int drones = members.Values.Count(m => m.Team == PrototypeTeam.Drones);
        if (drones == 0 || drones == members.Count) { menu.ShowError("Выберите обе команды: люди и дроны."); return; }
        if (settings.DroneStock > 0 && settings.DroneStock < drones) { menu.ShowError("Запас дронов должен быть не меньше числа пилотов."); return; }
        Busy = true; menu.SetBusy("Запускаем матч…");
        try
        {
            if (session is IHostSession host) { host.IsLocked = true; await host.SavePropertiesAsync(); }
            BeginSceneReload(true);
        }
        catch (Exception exception) { menu.ShowError("Не удалось начать матч: " + exception.Message); }
        finally { Busy = false; menu.SetBusy(null); }
    }

    private void BeginSceneReload(bool match)
    {
        if (match)
        {
            settings = JsonUtility.FromJson<PrototypeLobbySettings>(JsonUtility.ToJson(settings));
            settings.Time = MatchEnvironment.Resolve(settings.Time, new System.Random());
            // A fixed seed rebuilds the same town; otherwise every round gets a new one. Clients take this value.
            settings.ResolvedSeed = settings.Seed != 0 ? settings.Seed : new System.Random().Next(1, int.MaxValue);
            PublishState();
        }
        generation++; phase = PrototypePhase.Loading; loadForMatch = match; waitingForScenes = true;
        loadingDeadline = Time.unscaledTime + 90f;
        sceneReady.Clear();
        Broadcast(new PrototypeMessage { Kind = "load", Generation = generation, Text = match ? "match" : "lobby" });
        LoadLocalScene();
    }

    private void LoadLocalScene()
    {
        if (sceneLoading) return;
        InMatch = false; localControls = null; sceneLoading = true;
        menu.ShowLoading("Загружаем карту…"); LockCursor(false);
        SceneManager.LoadSceneAsync(scenePath, LoadSceneMode.Single);
    }

    private void TryFinishLoading()
    {
        if (!IsHost || !waitingForScenes || sceneLoading || network.ConnectedClientsIds.Any(id => !sceneReady.Contains(id))) return;
        if (loadForMatch)
        {
            // Destruction is replicated by map index, so every peer must have exactly the host's map.
            bool dropped = false;
            foreach (ulong id in network.ConnectedClientsIds.ToArray())
            {
                if (id == LocalId || (mapHashes.TryGetValue(id, out string hash) && hash == localMapHash)) continue;
                network.DisconnectClient(id, "Карта отличается от карты хоста. Обнови игру.");
                members.Remove(id); sceneReady.Remove(id); mapHashes.Remove(id);
                dropped = true;
            }
            if (dropped && (!members.Values.Any(m => m.Team == PrototypeTeam.Drones) ||
                            !members.Values.Any(m => m.Team == PrototypeTeam.Humans)))
            {
                BeginSceneReload(false);
                menu.ShowError("Игрок с другой версией карты отключён — в одной из команд никого не осталось.");
                return;
            }
        }
        waitingForScenes = false;
        if (!loadForMatch)
        {
            phase = PrototypePhase.Lobby;
            foreach (var member in members.Values) member.Ready = false;
            PublishState(); menu.ShowLobby(); return;
        }
        stock = settings.DroneStock; phase = PrototypePhase.Hiding;
        phaseEnds = network.ServerTime.Time + settings.HidingSeconds;
        dronesWon = false; resultReason = null;
        State = BuildState(); SpawnMatchActors(State); PublishState();
    }

    private void SpawnMatchActors(PrototypeState state)
    {
        if (sceneRig == null || InMatch) return;
        InMatch = true; waitingForScenes = false;
        settings = state.Settings ?? settings;
        humanLives.Clear(); humanRespawns.Clear();
        RoundStats.Begin(); summary = null; droneLights.Clear();
        int humanIndex = 0, droneIndex = 0;
        foreach (var member in state.Members)
        {
            bool drone = member.Team == PrototypeTeam.Drones;
            GameObject template = drone ? droneTemplate : humanTemplate;
            int index = drone ? droneIndex++ : humanIndex++;
            Vector3 position;
            Quaternion rotation = template.transform.rotation;
            if (suburbMatch)
            {
                // Humans line up along the sidewalk, alternating either side of the spawn; drones hover above.
                float offset = ((index + 1) / 2) * (index % 2 == 0 ? 1f : -1f) * 2.2f;
                position = drone ? SuburbanTrainingMap.DroneSpawn + Vector3.right * index * 2.2f
                    : SuburbanTrainingMap.HumanSpawn + SuburbanTrainingMap.SpawnLine * offset;
                rotation = Quaternion.identity;
            }
            else position = template.transform.position + Vector3.right * index * 2.2f;
            var obj = Instantiate(template, position, rotation);
            obj.name = (drone ? "Drone_" : "Human_") + member.Id;
            obj.SetActive(true);
            var actor = obj.AddComponent<PrototypeNetworkActor>(); actor.Configure(member.Id, member.Team);
            actors.Add(member.Id, actor);
            if (actor.Human != null)
            {
                actor.Human.SetSpawn(position, rotation);
                humanLives[member.Id] = settings.HumanLives;
                if (IsHost) RoundStats.Track(actor.Human, member.Name);
            }
            if (actor.Drone != null) actor.Drone.SetFlightLocked(true);
            if (member.Id == LocalId)
            {
                sceneRig.human = drone ? humanTemplate.GetComponent<HumanController>() : actor.Human;
                sceneRig.drone = drone ? actor.Drone : droneTemplate.GetComponent<DroneController>();
            }
        }
        // The host simulates destruction; clients replay its results, so cover is identical everywhere.
        DestructionSystem.Authority = IsHost;
        replication.Begin(IsHost);
        State = state; sceneRig.enabled = true;
        SetMenuCamera(false); menu.Hide(); LockCursor(true);
    }

    public void AttachLocalControls(LocalControlSwitcher controls)
    {
        localControls = controls;
        SynchronizeLocalRound(); RefreshLocalControl(true);
    }

    public void RefreshLocalControl(bool force = false)
    {
        if (!InMatch || localControls == null) return;
        bool active = !LocalDead && (phase == PrototypePhase.Hiding ? !LocalIsDrone : phase == PrototypePhase.Hunting);
        if (force || !localControlInitialized || localActive != active)
        {
            localControlInitialized = true; localActive = active;
            if (actors.TryGetValue(LocalId, out var actor) && actor.Drone != null)
                actor.Drone.SetFlightLocked(phase != PrototypePhase.Hunting);
            localControls.ApplyNetworkControl(LocalIsDrone, active);
        }
    }

    private void SynchronizeLocalRound()
    {
        if (localControls == null || localControls.Round == null) return;
        int total = members.Values.Count(m => m.Team == PrototypeTeam.Humans);
        int alive = actors.Values.Count(a => a.Team == PrototypeTeam.Humans && !a.Dead);
        var roundPhase = phase == PrototypePhase.Hiding ? HideAndSeekRound.RoundPhase.Hiding
            : phase == PrototypePhase.Finished ? HideAndSeekRound.RoundPhase.Finished : HideAndSeekRound.RoundPhase.Hunting;
        float remaining = phase == PrototypePhase.Hiding || phase == PrototypePhase.Hunting
            ? Mathf.Max(0f, (float)(State.PhaseEnds - network.ServerTime.Time)) : 0f;
        localControls.Round.ApplyNetworkState(roundPhase, State.DronesWon, remaining, total, alive);
        RefreshLocalControl();
    }

    private PrototypeState BuildState()
    {
        return new PrototypeState
        {
            PowerStates = PowerSystem.Instance != null ? PowerSystem.Instance.CaptureFlags() : null,
            Code = session != null ? session.Code : "", Settings = settings,
            Members = members.Values.OrderBy(m => m.Id).ToArray(),
            Actors = actors.Values.Where(a => a != null).Select(a => new PrototypeActorState
            {
                Id = a.OwnerId, Health = a.Human != null ? a.Human.Health : 0f, Dead = a.Dead,
                Flashlight = a.Drone != null ? DroneLightOn(a.OwnerId) : Inventory.HostBeamOn(a.OwnerId)
            }).ToArray(),
            Summary = phase == PrototypePhase.Finished ? summary : null,
            Phase = phase, PhaseEnds = phaseEnds,
            DroneStock = stock, DronesWon = dronesWon, ResultReason = resultReason, Generation = generation
        };
    }

    // Reliable state is event-driven: sent when something players can see changes, plus a slow heartbeat.
    private void PublishState()
    {
        if (!IsHost || !network.IsListening) return;
        lastStateHash = ComputeStateHash();
        nextHeartbeat = Time.unscaledTime + 1f;
        State = BuildState(); Broadcast(new PrototypeMessage { Kind = "state", State = State });
        if (phase == PrototypePhase.Lobby) menu.RefreshLobby();
        if (InMatch) SynchronizeLocalRound();
    }

    private int ComputeStateHash()
    {
        unchecked
        {
            int hash = (int)phase * 397 ^ stock * 31 ^ (dronesWon ? 1 : 0) ^ phaseEnds.GetHashCode();
            hash = hash * 31 + settings.HidingSeconds * 7 + settings.HuntingSeconds * 13 + settings.DroneStock;
            foreach (var member in members.Values)
                hash = hash * 31 + member.Id.GetHashCode() * 17 + (int)member.Team * 5 + (member.Ready ? 3 : 0) +
                    (member.Name != null ? member.Name.GetHashCode() : 0);
            foreach (var actor in actors.Values)
                if (actor != null)
                    hash = hash * 31 + (actor.Dead ? 1 : 0) +
                        ((actor.Drone != null ? DroneLightOn(actor.OwnerId) : Inventory.HostBeamOn(actor.OwnerId)) ? 5 : 0) +
                        Mathf.RoundToInt(actor.Human != null ? actor.Human.Health * 10f : 0f) * 2;
            return hash;
        }
    }

    private void ReceiveClientPose(ulong sender, NetPose pose)
    {
        if (!actors.TryGetValue(sender, out var actor) || actor == null || actor.Dead || phase == PrototypePhase.Finished) return;
        if (actor.Team == PrototypeTeam.Drones && phase != PrototypePhase.Hunting) return;
        if (!NetSync.Finite(pose.Position) || double.IsNaN(pose.Time) || double.IsInfinity(pose.Time)) return;
        if (actor.HasPose && pose.Time <= actor.LatestPose.Time) return; // Reordered datagram.
        // Speed limit over the shorter of sender time and real arrival time, so neither a forged
        // timestamp nor a burst of delayed packets moves an avatar faster than it can travel.
        float arrival = Time.unscaledTime;
        float sinceArrival = lastPoseArrival.TryGetValue(sender, out float last) ? arrival - last + 0.1f : 0.1f;
        lastPoseArrival[sender] = arrival;
        float elapsed = actor.HasPose
            ? Mathf.Clamp(Mathf.Min((float)(pose.Time - actor.LatestPose.Time), sinceArrival), 0.02f, 0.5f) : 0.1f;
        Vector3 reference = actor.HasPose ? actor.LatestPose.Position : actor.transform.position;
        float limit = (actor.Team == PrototypeTeam.Humans ? 12f : 65f) * elapsed + 1f;
        Vector3 delta = pose.Position - reference;
        if (delta.sqrMagnitude > limit * limit)
        {
            pose.Position = reference + delta.normalized * limit;
            if (!lastCorrection.TryGetValue(sender, out float corrected) || arrival - corrected > 0.3f)
            {
                lastCorrection[sender] = arrival;
                Send(sender, new PrototypeMessage
                    { Kind = "correct", Pose = new PrototypePose { Id = sender, Position = pose.Position } });
            }
        }
        if (float.IsNaN(pose.FlightSeconds) || float.IsInfinity(pose.FlightSeconds)) pose.FlightSeconds = 0f;
        actor.PushRemotePose(pose);
    }

    private void Update()
    {
        if ((InMatch || InTraining) && Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
        {
            if (MenuOpen) { menu.BackOrResume(); }
            else { menu.ShowPause(); LockCursor(false); }
        }
        if (network == null || !network.IsListening) return;
        if (waitingForScenes)
        {
            if (IsHost) TryFinishLoading();
            if (IsHost && Time.unscaledTime > loadingDeadline)
            {
                loadingDeadline = Time.unscaledTime + 90f;
                foreach (ulong id in network.ConnectedClientsIds.ToArray())
                    if (id != LocalId && !sceneReady.Contains(id)) network.DisconnectClient(id, "Карта не загрузилась вовремя.");
            }
            return;
        }
        if (IsHost && InMatch) AdvanceRound();
        if (InMatch && Time.unscaledTime >= nextSnapshot)
        {
            // Fixed cadence that neither drifts on a late frame nor bursts after a hitch.
            nextSnapshot = Mathf.Max(nextSnapshot + NetSync.SendInterval, Time.unscaledTime - NetSync.SendInterval);
            SendSnapshots();
        }
        if (IsHost && (Time.unscaledTime >= nextHeartbeat || ComputeStateHash() != lastStateHash)) PublishState();
    }

    private void AdvanceRound()
    {
        if (phase == PrototypePhase.Finished) return;
        foreach (var actor in actors.Values)
        {
            if (actor.Human == null || !actor.Dead || humanRespawns.ContainsKey(actor.OwnerId)) continue;
            if (!humanLives.TryGetValue(actor.OwnerId, out int lives) || lives <= 1) continue;
            humanLives[actor.OwnerId] = lives - 1;
            humanRespawns[actor.OwnerId] = Time.time + settings.RespawnSeconds;
        }
        foreach (ulong id in humanRespawns.Keys.ToArray())
        {
            if (Time.time < humanRespawns[id]) continue;
            humanRespawns.Remove(id);
            if (!actors.TryGetValue(id, out var actor)) continue;
            actor.Human.ResetHuman(); actor.ResetNetPose();
            if (!actor.IsLocal) actor.MakeReplicaBody();
            RefreshLocalControl(true); PublishState();
        }
        if (!actors.Values.Any(a => a.Team == PrototypeTeam.Humans && !a.Dead) && humanRespawns.Count == 0) { Finish(true, "Все люди найдены."); return; }
        if (!members.Values.Any(m => m.Team == PrototypeTeam.Drones)) { Finish(false, "Все пилоты покинули матч."); return; }
        if (phase == PrototypePhase.Hiding && network.ServerTime.Time >= phaseEnds)
        {
            phase = PrototypePhase.Hunting; phaseEnds = network.ServerTime.Time + settings.HuntingSeconds;
            foreach (var actor in actors.Values)
                if (actor.Drone != null && actor.IsLocal) actor.Drone.SetFlightLocked(false);
            RefreshLocalControl(true); PublishState();
        }
        if (phase != PrototypePhase.Hunting) return;
        if (settings.DroneStock > 0 && stock <= 0) { Finish(false, "Дроны закончились."); return; }
        if (network.ServerTime.Time >= phaseEnds) { Finish(false, "Время поиска вышло."); return; }
        foreach (ulong id in respawns.Keys.ToArray())
        {
            if (Time.time < respawns[id] || (settings.DroneStock > 0 && stock <= actors.Values.Count(a => a.Drone != null && !a.Dead))) continue;
            respawns.Remove(id);
            if (!actors.TryGetValue(id, out var actor)) continue;
            actor.Drone.SetFlightLocked(false); actor.Drone.ResetDrone();
            if (!actor.IsLocal) { actor.Drone.IsNetworkReplica = true; actor.MakeReplicaBody(); }
            actor.ResetNetPose(); lastPoseArrival.Remove(id);
            RefreshLocalControl(true); PublishState();
        }
    }

    private void Finish(bool drones, string reason)
    {
        phase = PrototypePhase.Finished; dronesWon = drones; resultReason = reason;
        summary = RoundStats.Finish(drones);
        foreach (var actor in actors.Values)
        {
            if (actor.Human != null) actor.Human.SetControlled(false);
            if (actor.Drone != null) actor.Drone.SetFlightLocked(true);
        }
        PublishState(); LockCursor(false);
    }

    public bool InterceptDetonation(DroneController drone)
    {
        if (!InMatch) return false;
        var actor = drone.GetComponent<PrototypeNetworkActor>();
        if (actor == null) return true;
        if (IsHost) return actor.Team != PrototypeTeam.Drones || phase != PrototypePhase.Hunting;
        if (actor.IsLocal && !actor.Dead && phase == PrototypePhase.Hunting)
            SendServer(new PrototypeMessage { Kind = "detonate", Pose = actor.Capture(), Generation = generation });
        return true;
    }

    public void DroneExploded(DroneController drone)
    {
        if (!InMatch || !IsHost) return;
        var actor = drone.GetComponent<PrototypeNetworkActor>();
        if (actor == null) return;
        if (settings.DroneStock > 0) stock = Mathf.Max(0, stock - 1);
        respawns[actor.OwnerId] = Time.time + settings.RespawnSeconds;
        Broadcast(new PrototypeMessage { Kind = "blast", ActorId = actor.OwnerId, Pose = actor.Capture() });
        PublishState();
    }

    private void RequestWindow(int index)
    {
        if (InMatch && !IsHost) SendServer(new PrototypeMessage { Kind = "window", Index = index, Generation = generation });
    }

    public void RequestGrab(Rigidbody body, Vector3 localAnchor)
    {
        if (InMatch && !IsHost && replication.TryGetId(body, out uint id))
            SendServer(new PrototypeMessage { Kind = "grab", Index = unchecked((int)id), Generation = generation,
                Pose = new PrototypePose { Position = localAnchor, Rotation = Quaternion.identity } });
        else PlayerCarry.ApplyRemoteState(null);
    }

    // Host: a place where a drone pilot noticed activity (only the point; see ActivityMarks).
    public void SendMark(ulong pilot, Vector3 point)
    {
        if (InMatch && IsHost && phase == PrototypePhase.Hunting)
            Send(pilot, new PrototypeMessage { Kind = "mark", Generation = generation,
                Pose = new PrototypePose { Position = point, Rotation = Quaternion.identity } });
    }

    // Host: a drone's searchlight was switched (the pilot asks, everybody sees it through the state).
    private readonly Dictionary<ulong, bool> droneLights = new Dictionary<ulong, bool>();
    public void RequestDroneLight(bool on)
    {
        if (!InMatch) return;
        if (IsHost) SetDroneLight(LocalId, on);
        else SendServer(new PrototypeMessage { Kind = "dronelight", Ready = on, Generation = generation });
    }
    private void SetDroneLight(ulong owner, bool on)
    {
        droneLights[owner] = on;
        if (actors.TryGetValue(owner, out var actor) && actor.Drone != null) actor.Drone.SetSearchlight(on);
        PublishState();
    }
    private bool DroneLightOn(ulong owner) => droneLights.TryGetValue(owner, out bool on) ? on : MatchEnvironment.IsNight;

    // Host: a blast that is not a drone's (the strange stone); clients play its flash and push their avatar.
    public void BroadcastBlast(Vector3 at, float radius, float impulse)
    {
        if (InMatch && IsHost)
            Broadcast(new PrototypeMessage { Kind = "stoneblast", Generation = generation, Value = Mathf.RoundToInt(radius * 10f),
                Index = Mathf.RoundToInt(impulse * 10f), Pose = new PrototypePose { Position = at, Rotation = Quaternion.identity } });
    }

    public void RequestPocket(PhysicsProp prop)
    {
        if (InMatch && !IsHost && prop != null && replication.TryGetId(prop.Body, out uint id))
            SendServer(new PrototypeMessage { Kind = "pocket", Index = unchecked((int)id), Generation = generation });
    }

    public void RequestThrow(int slot, Ray view)
    {
        if (InMatch && !IsHost)
            SendServer(new PrototypeMessage { Kind = "throw", Value = slot, Generation = generation,
                Pose = new PrototypePose { Position = view.origin, Rotation = Quaternion.LookRotation(view.direction) } });
    }

    public void RequestBeam(bool on)
    {
        if (InMatch && !IsHost) SendServer(new PrototypeMessage { Kind = "beam", Ready = on, Generation = generation });
    }

    // Host: a remote player's pocket contents, as prop ids (-1 = empty).
    public void SendInventory(ulong owner, IReadOnlyList<PhysicsProp> items)
    {
        if (!InMatch || !IsHost || owner == LocalId) return;
        var ids = new string[items.Count];
        for (int i = 0; i < items.Count; i++)
            ids[i] = items[i] != null && replication.TryGetId(items[i].Body, out uint id) ? unchecked((int)id).ToString() : "-";
        Send(owner, new PrototypeMessage { Kind = "inv", Text = string.Join(",", ids), Generation = generation });
    }

    public void RequestPush(Rigidbody body, Vector3 point)
    {
        if (InMatch && !IsHost && replication.TryGetId(body, out uint id))
            SendServer(new PrototypeMessage { Kind = "push", Index = unchecked((int)id), Generation = generation,
                Pose = new PrototypePose { Position = point, Rotation = Quaternion.identity } });
    }

    public void RequestHold(float distance, Quaternion relative, bool turning)
    {
        if (InMatch && !IsHost)
            SendServer(new PrototypeMessage { Kind = "hold", Generation = generation, Ready = turning,
                Pose = new PrototypePose { Position = new Vector3(distance, 0f, 0f), Rotation = relative } });
    }

    public void RequestDrop()
    {
        if (InMatch && !IsHost) SendServer(new PrototypeMessage { Kind = "drop", Generation = generation });
    }

    public void RequestAttach(bool quick = false)
    {
        if (InMatch && !IsHost) SendServer(new PrototypeMessage { Kind = "attach", Generation = generation, Value = quick ? 1 : 0 });
    }

    public void RequestPower(int index) => SendServer(new PrototypeMessage { Kind = "power-request", Index = index, Generation = generation });
    public void PublishPower(int index, int flags)
    {
        if (InMatch && IsHost) Broadcast(new PrototypeMessage { Kind = "power", Index = index, Value = flags, Generation = generation });
    }
    public bool TryLocalView(out Ray view)
    {
        var camera = InMatch && actors.TryGetValue(LocalId, out var actor) ? actor.View : sceneRig?.human?.viewCamera;
        view = camera != null ? new Ray(camera.transform.position, camera.transform.forward) : default;
        return camera != null;
    }
    private void FallDamaged(HumanController human, float damage)
    {
        if (!InMatch || !settings.FallDamage) return;
        if (IsHost) { human.ApplyDamage(damage); PublishState(); }
        else SendServer(new PrototypeMessage { Kind = "fall", Value = Mathf.CeilToInt(damage), Generation = generation });
    }

    // Host: everyone sees how many players hold an object ("players 1/2").
    private void GripsChanged(Rigidbody body, int count)
    {
        if (!InMatch || !IsHost || !replication.TryGetId(body, out uint id)) return;
        Broadcast(new PrototypeMessage { Kind = "grips", Index = unchecked((int)id), Value = count });
    }

    // Host: tell a remote player what they are now carrying (or that they let go).
    private void CarryChanged(ulong carrier, Rigidbody body)
    {
        if (!InMatch || !IsHost || carrier == LocalId || carrier == CarrySystem.OfflineCarrier) return;
        uint id = 0;
        bool carrying = body != null && replication.TryGetId(body, out id);
        Send(carrier, new PrototypeMessage { Kind = "carry", Ready = carrying, Index = unchecked((int)id) });
    }

    private void RequestDoor(int index, InteractiveDoor.DoorState state)
    {
        if (InMatch && !IsHost)
            SendServer(new PrototypeMessage { Kind = "door", Index = index, Value = (int)state, Generation = generation });
    }

    public string NameOf(HumanController human)
    {
        var actor = human != null ? human.GetComponent<PrototypeNetworkActor>() : null;
        return actor != null && members.TryGetValue(actor.OwnerId, out var member) && !string.IsNullOrEmpty(member.Name)
            ? member.Name : "Игрок";
    }

    public bool CanObserve(HumanController human)
    {
        if (!InMatch) return true;
        var actor = human.GetComponent<PrototypeNetworkActor>();
        return !LocalIsDrone && actor != null && actor.Team == PrototypeTeam.Humans;
    }

    public void RestartOnlineRound() { if (IsHost && phase == PrototypePhase.Finished) BeginSceneReload(true); }

    public async void ReturnToLobby()
    {
        if (!IsHost || Busy) return;
        Busy = true;
        try
        {
            if (session is IHostSession host) { host.IsLocked = false; await host.SavePropertiesAsync(); }
            BeginSceneReload(false);
        }
        catch (Exception exception) { menu.ShowError(exception.Message); }
        finally { Busy = false; }
    }

    public void StartTraining(bool suburban, int seed = 0, Vector2 size = default, int buildings = 0, bool roofs = true)
    {
        if (HasSession || sceneRig == null || sceneRig.enabled) return;
        TrainingSettings.ResolvedSeed = 0;   // a fresh town every training round unless a seed is fixed
        MatchRules.Apply(TrainingSettings);
        var generator = TrainingGenerator;
        Vector3 humanSpawn, droneSpawn;
        Quaternion facing;
        if (suburban)
        {
            if (SuburbanTrainingMap.Create(sceneRig.transform) == null)
            {
                InTraining = false;
                menu.ShowError("Файл карты «Воксельный пригород» отсутствует.");
                return;
            }
            if (generator != null) generator.HideGeneratedMap();
            if (sceneRig.testArea != null) sceneRig.testArea.gameObject.SetActive(false);
            humanSpawn = SuburbanTrainingMap.HumanSpawn;
            droneSpawn = SuburbanTrainingMap.DroneSpawn;
            facing = Quaternion.identity;
        }
        else
        {
            if (generator == null || size.x < 1 || size.y < 1 || size.x > 500 || size.y > 500 ||
                buildings < 1 || buildings > 16)
            {
                InTraining = false;
                menu.ShowError("Укажи размеры от 1 до 500 м и от 1 до 16 зданий.");
                return;
            }
            var previous = (generator.seed, generator.mapSize, generator.buildingCount, generator.generateRoofs);
            generator.seed = seed;
            generator.mapSize = size;
            generator.buildingCount = buildings;
            generator.generateRoofs = roofs;
            if (!generator.TryRegenerateMap())
            {
                (generator.seed, generator.mapSize, generator.buildingCount, generator.generateRoofs) = previous;
                InTraining = false;
                menu.ShowError("Здания не помещаются. Увеличь карту или уменьши число зданий.");
                return;
            }
            var edge = new Vector3(-size.x / 2f + 0.65f, 0.05f, -size.y / 2f + 0.65f);
            humanSpawn = generator.transform.TransformPoint(edge);
            droneSpawn = generator.transform.TransformPoint(edge + new Vector3(0f, 1f, 2f));
            facing = generator.transform.rotation * Quaternion.Euler(0f, 45f, 0f);
        }

        suburbanTraining = suburban;
        trainingSeed = seed;
        trainingMapSize = size;
        trainingBuildingCount = buildings;
        trainingRoofs = roofs;
        InTraining = true;
        sceneRig.human.SetTrainingSpawn(humanSpawn, facing);
        sceneRig.drone.SetTrainingSpawn(droneSpawn, facing);
        sceneRig.enabled = true;
        SetMenuCamera(false);
        menu.Hide();
        LockCursor(true);
    }

    public void Resume() { menu.Hide(); LockCursor(phase != PrototypePhase.Finished); }

    public async void LeaveWithReason(string reason = null)
    {
        if (leaving) return;
        if (IsHost && network.IsListening) Broadcast(new PrototypeMessage { Kind = "close", Text = "Хост закрыл лобби." });
        InTraining = false; InMatch = false;
        replication.End(); DestructionSystem.Authority = true;
        menu.ShowLoading("Выходим…");
        await CloseConnection();
        waitingForScenes = false; phase = PrototypePhase.Lobby;
        sceneLoading = true;
        var operation = SceneManager.LoadSceneAsync(scenePath, LoadSceneMode.Single);
        while (!operation.isDone) await Task.Yield();
        menu.ShowHome(); if (!string.IsNullOrEmpty(reason)) menu.ShowError(reason);
    }

    private async Task CloseConnection()
    {
        leaving = true;
        try { if (session != null) await session.LeaveAsync(); }
        catch (Exception exception) { Debug.LogWarning("Leave lobby: " + exception.Message); }
        session = null;
        if (network != null && network.IsListening) network.Shutdown();
        float deadline = Time.realtimeSinceStartup + 5f;
        while (network != null && network.ShutdownInProgress && Time.realtimeSinceStartup < deadline) await Task.Yield();
        handlersRegistered = false; members.Clear(); mapHashes.Clear(); State = new PrototypeState();
        waitingForScenes = false; Busy = false; leaving = false;
    }

    private void SetMenuCamera(bool active)
    {
        if (menuCamera == null) return;
        menuCamera.enabled = active;
        var listener = menuCamera.GetComponent<AudioListener>(); if (listener != null) listener.enabled = active;
    }

    public static void LockCursor(bool locked)
    {
        Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !locked;
    }

    private void OnDestroy()
    {
        leaving = true;
        SceneManager.sceneLoaded -= SceneLoaded;
        if (replication != null)
        {
            replication.WindowRequested -= RequestWindow;
            replication.DoorRequested -= RequestDoor;
            CarrySystem.CarryChanged -= CarryChanged;
            CarrySystem.GripsChanged -= GripsChanged;
            HumanController.FallDamaged -= FallDamaged;
            replication.Dispose();
        }
        DestructionSystem.Authority = true;
        if (network != null)
        {
            network.OnServerStarted -= RegisterMessages; network.OnClientStarted -= RegisterMessages;
            network.OnClientConnectedCallback -= Connected; network.OnClientDisconnectCallback -= Disconnected;
            network.OnTransportFailure -= TransportFailed;
            if (network.IsListening) network.Shutdown();
        }
        if (Instance == this) Instance = null;
    }
}
