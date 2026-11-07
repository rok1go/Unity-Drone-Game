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
    private bool suburbanTraining;
    private int trainingSeed, trainingBuildingCount;
    private Vector2 trainingMapSize;
    private bool trainingRoofs;
    private PrototypeLobbySettings settings = new PrototypeLobbySettings();
    private readonly Dictionary<ulong, PrototypeMember> members = new Dictionary<ulong, PrototypeMember>();
    private readonly Dictionary<ulong, PrototypeNetworkActor> actors = new Dictionary<ulong, PrototypeNetworkActor>();
    private readonly Dictionary<string, DestructibleBlock> mapBlocks = new Dictionary<string, DestructibleBlock>();
    private readonly Dictionary<ulong, float> lastPoseAt = new Dictionary<ulong, float>();
    private readonly Dictionary<ulong, float> respawns = new Dictionary<ulong, float>();
    private readonly HashSet<ulong> sceneReady = new HashSet<ulong>();
    private readonly HashSet<string> pendingBlocks = new HashSet<string>();
    private PrototypePhase phase = PrototypePhase.Lobby;
    private double phaseEnds;
    private int stock, generation;
    private bool handlersRegistered, leaving, sceneLoading, loadForMatch, waitingForScenes, localControlInitialized, localActive;
    private bool dronesWon;
    private string resultReason;
    private float nextSend;
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
        // The menu and scene gate must exist even if the optional network startup fails.
        menu = gameObject.AddComponent<PrototypeMenu>();
        menu.Initialize(this);
        SceneManager.sceneLoaded += SceneLoaded;
        DestructionSystem.BlockDetached += BlockDetached;
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
        scenePath = scene.path;
        TrainingGenerator = FindObjectsByType<ProceduralMapGenerator>().FirstOrDefault(g => g.gameObject.scene == scene);
        sceneRig.enabled = false;
        humanTemplate = sceneRig.human.gameObject; droneTemplate = sceneRig.drone.gameObject;
        sceneRig.human.SetControlled(false); sceneRig.drone.SetControlled(false);
        humanTemplate.SetActive(false); droneTemplate.SetActive(false);
        localControls = null; localControlInitialized = false;
        actors.Clear(); mapBlocks.Clear(); respawns.Clear(); lastPoseAt.Clear(); pendingBlocks.Clear();
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
            if (IsHost) { sceneReady.Add(LocalId); TryFinishLoading(); }
            else SendServer(new PrototypeMessage { Kind = "scene-ready", Generation = generation });
        }
        else
        {
            phase = PrototypePhase.Lobby; menu.ShowHome(); LockCursor(false);
            if (!string.IsNullOrEmpty(networkStartupError)) menu.ShowError(networkStartupError);
        }
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
            options.Clamp(); settings = options; phase = PrototypePhase.Lobby;
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
            members.Remove(id); sceneReady.Remove(id); respawns.Remove(id);
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
        { sceneReady.Add(sender); TryFinishLoading(); return; }
        if (message.Kind == "pose" && InMatch && message.Generation == generation && message.Pose != null)
        { ReceivePose(sender, message.Pose); return; }
        if (message.Kind == "detonate" && InMatch && message.Generation == generation && phase == PrototypePhase.Hunting)
        {
            if (actors.TryGetValue(sender, out var actor) && actor.Team == PrototypeTeam.Drones && !actor.Dead)
            {
                if (message.Pose != null) ReceivePose(sender, message.Pose);
                actor.Drone.DetonateAuthoritatively();
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
        if (message.Kind == "state" && message.State != null)
        {
            State = message.State; phase = State.Phase;
            members.Clear(); foreach (var member in State.Members) members[member.Id] = member;
            if ((phase == PrototypePhase.Hiding || phase == PrototypePhase.Hunting || phase == PrototypePhase.Finished) && !InMatch)
                SpawnMatchActors(State);
            if (InMatch)
            {
                var present = new HashSet<ulong>(State.Members.Select(m => m.Id));
                foreach (ulong id in actors.Keys.Where(id => !present.Contains(id)).ToArray())
                { if (actors[id] != null) Destroy(actors[id].gameObject); actors.Remove(id); }
                foreach (var pose in State.Actors)
                    if (actors.TryGetValue(pose.Id, out var actor)) actor.ReceiveServerPose(pose);
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
            if (message.Pose != null)
            {
                actor.transform.SetPositionAndRotation(message.Pose.Position, message.Pose.Rotation);
                var body = actor.GetComponent<Rigidbody>();
                body.position = message.Pose.Position; body.rotation = message.Pose.Rotation;
            }
            actor.Drone.ApplyNetworkDestroyed(true);
        }
        else if (message.Kind == "blocks" && message.Blocks != null)
            foreach (string id in message.Blocks)
                if (mapBlocks.TryGetValue(id, out var block) && block != null) block.HideReplicated();
        else if (message.Kind == "close") LeaveWithReason(message.Text);
    }

    public void ChooseTeam(PrototypeTeam team) => SendServer(new PrototypeMessage { Kind = "team", Team = (int)team });
    public void SetReady(bool ready) => SendServer(new PrototypeMessage { Kind = "ready", Ready = ready });

    public void ChangeSettings(PrototypeLobbySettings options)
    {
        if (!IsHost || phase != PrototypePhase.Lobby || Busy) return;
        options.Clamp(); options.MaxPlayers = settings.MaxPlayers;
        settings = options;
        foreach (var member in members.Values) member.Ready = false;
        PublishState();
    }

    public async void StartMatch()
    {
        if (!IsHost || Busy || phase != PrototypePhase.Lobby) return;
        if (members.Count < 2 || members.Values.Any(m => !m.Ready))
        { menu.ShowError("Нужны хотя бы два игрока. Все должны нажать «Готов»."); return; }
        int drones = members.Values.Count(m => m.Team == PrototypeTeam.Drones);
        if (drones == 0 || drones == members.Count) { menu.ShowError("Выберите обе команды: люди и дроны."); return; }
        if (settings.DroneStock < drones) { menu.ShowError("Запас дронов должен быть не меньше числа пилотов."); return; }
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
        int humanIndex = 0, droneIndex = 0;
        foreach (var member in state.Members)
        {
            bool drone = member.Team == PrototypeTeam.Drones;
            GameObject template = drone ? droneTemplate : humanTemplate;
            Vector3 position = template.transform.position + Vector3.right * (drone ? droneIndex++ : humanIndex++) * 2.2f;
            var obj = Instantiate(template, position, template.transform.rotation);
            obj.name = (drone ? "Drone_" : "Human_") + member.Id;
            obj.SetActive(true);
            var actor = obj.AddComponent<PrototypeNetworkActor>(); actor.Configure(member.Id, member.Team);
            actors.Add(member.Id, actor);
            if (actor.Drone != null) actor.Drone.SetFlightLocked(true);
            if (member.Id == LocalId)
            {
                sceneRig.human = drone ? humanTemplate.GetComponent<HumanController>() : actor.Human;
                sceneRig.drone = drone ? actor.Drone : droneTemplate.GetComponent<DroneController>();
            }
        }
        foreach (var block in FindObjectsByType<DestructibleBlock>())
            if (!string.IsNullOrEmpty(block.StableId)) mapBlocks[block.StableId] = block;
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
        localControls.Round.ApplyNetworkState(roundPhase, State.DronesWon, State.Remaining, total, alive);
        RefreshLocalControl();
    }

    private PrototypeState BuildState()
    {
        return new PrototypeState
        {
            Code = session != null ? session.Code : "", Settings = settings,
            Members = members.Values.OrderBy(m => m.Id).ToArray(), Actors = actors.Values.Select(a => a.Capture()).ToArray(),
            Phase = phase, Remaining = phase == PrototypePhase.Hiding || phase == PrototypePhase.Hunting
                ? Mathf.Max(0f, (float)(phaseEnds - network.ServerTime.Time)) : 0f,
            DroneStock = stock, DronesWon = dronesWon, ResultReason = resultReason, Generation = generation
        };
    }

    private void PublishState()
    {
        if (!IsHost || !network.IsListening) return;
        State = BuildState(); Broadcast(new PrototypeMessage { Kind = "state", State = State });
        if (phase == PrototypePhase.Lobby) menu.RefreshLobby();
        if (InMatch) SynchronizeLocalRound();
    }

    private void ReceivePose(ulong sender, PrototypePose pose)
    {
        if (!actors.TryGetValue(sender, out var actor) || actor.Dead || phase == PrototypePhase.Finished) return;
        if (actor.Team == PrototypeTeam.Drones && phase != PrototypePhase.Hunting) return;
        if (!Finite(pose.Position) || !Finite(pose.Velocity) || !Finite(pose.Rotation) || !Finite(pose.ViewRotation)) return;
        float elapsed = lastPoseAt.TryGetValue(sender, out float last) ? Mathf.Clamp(Time.unscaledTime - last, 0.02f, 1f) : 0.1f;
        lastPoseAt[sender] = Time.unscaledTime;
        float limit = (actor.Team == PrototypeTeam.Humans ? 12f : 65f) * elapsed + 1f;
        pose.Position = actor.transform.position + Vector3.ClampMagnitude(pose.Position - actor.transform.position, limit);
        pose.Velocity = Vector3.ClampMagnitude(pose.Velocity, actor.Team == PrototypeTeam.Humans ? 20f : 65f);
        pose.Id = sender;
        pose.Rotation = pose.Rotation.normalized; pose.ViewRotation = pose.ViewRotation.normalized;
        pose.Throttle = float.IsNaN(pose.Throttle) ? 0f : Mathf.Clamp01(pose.Throttle);
        pose.FlightSeconds = float.IsNaN(pose.FlightSeconds) || float.IsInfinity(pose.FlightSeconds) ? 0f : Mathf.Max(0f, pose.FlightSeconds);
        actor.ReceiveOwnerPose(pose);
    }

    private static bool Finite(Vector3 v) => !float.IsNaN(v.x + v.y + v.z) && !float.IsInfinity(v.x) && !float.IsInfinity(v.y) && !float.IsInfinity(v.z);
    private static bool Finite(Quaternion q) => !float.IsNaN(q.x + q.y + q.z + q.w) &&
        Quaternion.Dot(q, q) > 0.25f && Quaternion.Dot(q, q) < 4f;

    private void Update()
    {
        if ((InMatch || InTraining) && Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
        {
            if (MenuOpen) { menu.Hide(); LockCursor(phase != PrototypePhase.Finished); }
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
        if (Time.unscaledTime < nextSend) return;
        nextSend = Time.unscaledTime + 0.1f;
        if (IsHost)
        {
            if (pendingBlocks.Count > 0)
            {
                string[] blocks = pendingBlocks.ToArray(); pendingBlocks.Clear();
                for (int offset = 0; offset < blocks.Length; offset += 128)
                    Broadcast(new PrototypeMessage { Kind = "blocks", Blocks = blocks.Skip(offset).Take(128).ToArray() });
            }
            PublishState();
        }
        else if (InMatch && actors.TryGetValue(LocalId, out var actor) && !actor.Dead)
            SendServer(new PrototypeMessage { Kind = "pose", Pose = actor.Capture(), Generation = generation });
    }

    private void AdvanceRound()
    {
        if (phase == PrototypePhase.Finished) return;
        if (!actors.Values.Any(a => a.Team == PrototypeTeam.Humans && !a.Dead)) { Finish(true, "Все люди найдены."); return; }
        if (!members.Values.Any(m => m.Team == PrototypeTeam.Drones)) { Finish(false, "Все пилоты покинули матч."); return; }
        if (phase == PrototypePhase.Hiding && network.ServerTime.Time >= phaseEnds)
        {
            phase = PrototypePhase.Hunting; phaseEnds = network.ServerTime.Time + settings.HuntingSeconds;
            foreach (var actor in actors.Values)
                if (actor.Drone != null && actor.IsLocal) actor.Drone.SetFlightLocked(false);
            RefreshLocalControl(true); PublishState();
        }
        if (phase != PrototypePhase.Hunting) return;
        if (stock <= 0) { Finish(false, "Дроны закончились."); return; }
        if (network.ServerTime.Time >= phaseEnds) { Finish(false, "Время поиска вышло."); return; }
        foreach (ulong id in respawns.Keys.ToArray())
        {
            if (Time.time < respawns[id] || stock <= actors.Values.Count(a => a.Drone != null && !a.Dead)) continue;
            respawns.Remove(id);
            if (!actors.TryGetValue(id, out var actor)) continue;
            actor.Drone.SetFlightLocked(false); actor.Drone.ResetDrone();
            if (!actor.IsLocal)
            {
                actor.Drone.IsNetworkReplica = true;
                var body = actor.GetComponent<Rigidbody>();
                body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative; body.isKinematic = true;
            }
            RefreshLocalControl(true); PublishState();
        }
    }

    private void Finish(bool drones, string reason)
    {
        phase = PrototypePhase.Finished; dronesWon = drones; resultReason = reason;
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
        stock = Mathf.Max(0, stock - 1); respawns[actor.OwnerId] = Time.time + 3f;
        Broadcast(new PrototypeMessage { Kind = "blast", ActorId = actor.OwnerId, Pose = actor.Capture() });
        PublishState();
    }

    private void BlockDetached(string id, Vector3 position) { if (InMatch && IsHost) pendingBlocks.Add(id); }

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
            facing = Quaternion.Euler(0f, -90f, 0f);
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
        handlersRegistered = false; members.Clear(); State = new PrototypeState();
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
        DestructionSystem.BlockDetached -= BlockDetached;
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
