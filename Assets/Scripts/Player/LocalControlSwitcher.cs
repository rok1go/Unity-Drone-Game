using UnityEngine;
using UnityEngine.InputSystem;

public class LocalControlSwitcher : MonoBehaviour
{
    public HumanController human;
    public DroneController drone;
    public bool startAsDrone;
    public ExplosionTestArea testArea;
    public bool ControllingDrone { get; private set; }
    public bool IsSpectating => MultiplayerPrototype.OnlineMatch ? MultiplayerPrototype.Instance.LocalDead : human != null && human.IsEliminated;
    public SpectatorController Spectator { get; private set; }
    public HideAndSeekRound Round { get; private set; }

    private void Start()
    {
        if (MultiplayerPrototype.Instance != null && !MultiplayerPrototype.OnlineMatch && !MultiplayerPrototype.Instance.InTraining)
        {
            enabled = false;
            return;
        }
        var spectatorObject = new GameObject("SpectatorCamera");
        spectatorObject.transform.SetParent(transform, false);
        Spectator = spectatorObject.AddComponent<SpectatorController>();
        ExplosionCameraShake.Attach(human != null ? human.viewCamera : null);
        ExplosionCameraShake.Attach(drone != null ? drone.viewCamera : null);
        var hud = GetComponent<DroneHud>();
        if (hud == null) hud = gameObject.AddComponent<DroneHud>();
        hud.Initialize(this);
        if (drone != null) drone.Exploded += HandleDroneExploded;
        if (human != null) human.Eliminated += HandleHumanEliminated;
        Round = GetComponent<HideAndSeekRound>();
        if (Round == null) Round = gameObject.AddComponent<HideAndSeekRound>();
        if (MultiplayerPrototype.OnlineMatch)
        {
            Round.InitializeNetwork(this);
            MultiplayerPrototype.Instance.AttachLocalControls(this);
        }
        else Round.Initialize(this);
        SetCursor(true);
    }

    private void Update()
    {
        var keyboard = Keyboard.current;
        if (MultiplayerPrototype.Instance != null && MultiplayerPrototype.Instance.MenuOpen) return;
        if (MultiplayerPrototype.Instance == null && keyboard != null && keyboard.escapeKey.wasPressedThisFrame) SetCursor(false);
        bool finished = Round != null && Round.Phase == HideAndSeekRound.RoundPhase.Finished;
        if (!finished && Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame) SetCursor(true);
        if (MultiplayerPrototype.OnlineMatch)
        {
            if (finished && keyboard != null && keyboard.rKey.wasPressedThisFrame) MultiplayerPrototype.Instance.RestartOnlineRound();
            if (!finished && IsSpectating && keyboard != null && keyboard.tabKey.wasPressedThisFrame) Spectator.CycleTarget();
            return;
        }
        if (keyboard != null && keyboard.rKey.wasPressedThisFrame)
        {
            if (finished) Round.RestartRound();
            else if (Round != null && Round.CanControlDrone && !IsSpectating && drone != null && drone.IsDestroyed)
            {
                drone.ResetDrone();
                SetDroneControl(true);
            }
        }
        if (finished) return;
        if (keyboard != null && keyboard.tabKey.wasPressedThisFrame)
        {
            if (IsSpectating) Spectator.CycleTarget();
            else SetDroneControl(!ControllingDrone);
        }
    }

    public void SetDroneControl(bool useDrone)
    {
        if (MultiplayerPrototype.OnlineMatch) { MultiplayerPrototype.Instance.RefreshLocalControl(); return; }
        if (human == null || drone == null) return;
        if (Round != null && Round.Phase == HideAndSeekRound.RoundPhase.Finished) return;
        if (IsSpectating)
        {
            EnterSpectator();
            return;
        }
        Spectator.StopObserving();
        useDrone = useDrone && (Round == null || Round.CanControlDrone) &&
            !drone.IsDestroyed && drone.gameObject.activeInHierarchy;
        ControllingDrone = useDrone;
        human.SetControlled(!useDrone);
        drone.SetControlled(useDrone);
    }

    private void HandleDroneExploded() => SetDroneControl(false);

    private void HandleHumanEliminated(HumanController eliminated) => EnterSpectator();

    private void EnterSpectator()
    {
        if (MultiplayerPrototype.OnlineMatch) { MultiplayerPrototype.Instance.RefreshLocalControl(true); return; }
        ControllingDrone = false;
        if (human != null) human.SetControlled(false);
        if (drone != null) drone.SetControlled(false);
        if (!Spectator.enabled) Spectator.Begin(human != null ? human.viewCamera : null);
    }

    public void ApplyNetworkControl(bool droneRole, bool active)
    {
        ControllingDrone = droneRole && !MultiplayerPrototype.Instance.LocalDead;
        human.SetControlled(active && !droneRole);
        drone.SetControlled(active && droneRole);
        Spectator.AllowFreeCamera = false;
        if (active) Spectator.StopObserving();
        else if (!Spectator.enabled) Spectator.Begin(droneRole ? drone.viewCamera : human.viewCamera);
    }

    public void ShowRoundResult()
    {
        Camera source = ControllingDrone && drone != null ? drone.viewCamera : human != null ? human.viewCamera : null;
        ControllingDrone = false;
        if (human != null) human.SetControlled(false);
        if (drone != null) drone.SetFlightLocked(true);
        if (!Spectator.enabled) Spectator.Begin(source);
        SetCursor(false);
    }

    private void OnDestroy()
    {
        if (drone != null) drone.Exploded -= HandleDroneExploded;
        if (human != null) human.Eliminated -= HandleHumanEliminated;
    }

    private static void SetCursor(bool locked)
    {
        Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !locked;
    }

    private void OnDisable()
    {
        if (Application.isPlaying) SetCursor(false);
    }
}

