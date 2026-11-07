using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

[DisallowMultipleComponent]
public class HideAndSeekRound : MonoBehaviour
{
    public enum RoundPhase { Hiding, Hunting, Finished }
    [Min(1f)] public float hidingSeconds = 30f;
    [Min(1f)] public float huntingSeconds = 240f;
    public RoundPhase Phase { get; private set; }
    public bool DronesWon { get; private set; }
    public bool IsRestarting { get; private set; }
    public bool CanControlDrone => initialized && Phase == RoundPhase.Hunting;
    public float RemainingSeconds => Phase == RoundPhase.Finished ? 0f : Mathf.Max(0f, phaseEndsAt - Time.time);
    public int TotalHumans => networkDriven ? networkTotal : humans.Count;
    public bool CanRestart => !networkDriven || (MultiplayerPrototype.Instance != null && MultiplayerPrototype.Instance.IsHost);
    public int AliveHumans
    {
        get
        {
            if (networkDriven) return networkAlive;
            int count = 0;
            foreach (var human in humans)
                if (human != null && !human.IsEliminated) count++;
            return count;
        }
    }

    private readonly List<HumanController> humans = new List<HumanController>();
    private LocalControlSwitcher controls;
    private float phaseEndsAt;
    private bool initialized;
    private bool networkDriven;
    private int networkTotal, networkAlive;
    private AudioSource preparationMusic;
    private bool musicStarted;
    private RoundPhase musicPhase = RoundPhase.Finished;

    public void InitializeNetwork(LocalControlSwitcher switcher)
    {
        controls = switcher; networkDriven = true; initialized = true;
    }

    public void ApplyNetworkState(RoundPhase phase, bool dronesWon, float remaining, int total, int alive)
    {
        bool finishedNow = phase == RoundPhase.Finished && Phase != RoundPhase.Finished;
        Phase = phase; DronesWon = dronesWon; phaseEndsAt = Time.time + remaining;
        networkTotal = total; networkAlive = alive;
        if (phase != RoundPhase.Hiding || remaining <= 0f) StopPreparationMusic();
        if (finishedNow) controls.ShowRoundResult();
    }

    public void Initialize(LocalControlSwitcher switcher)
    {
        if (initialized) return;
        controls = switcher;
        foreach (var human in FindObjectsByType<HumanController>(FindObjectsInactive.Include))
            if (human.gameObject.scene == gameObject.scene) humans.Add(human);
        RoundStats.Begin();
        foreach (var human in humans) RoundStats.Track(human, "Вы");
        initialized = true;
        Phase = RoundPhase.Hiding;
        phaseEndsAt = Time.time + hidingSeconds;
        if (controls.drone != null) controls.drone.SetFlightLocked(true);
        controls.SetDroneControl(false);
    }

    private void Update()
    {
        UpdatePreparationMusic();
        if (!initialized || networkDriven || Phase == RoundPhase.Finished) return;
        if (humans.Count > 0 && AliveHumans == 0)
        {
            Finish(true);
            return;
        }
        if (Time.time < phaseEndsAt) return;
        if (Phase == RoundPhase.Hiding)
        {
            Phase = RoundPhase.Hunting;
            StopPreparationMusic();
            phaseEndsAt = Time.time + huntingSeconds;
            if (controls.drone != null) controls.drone.SetFlightLocked(false);
            // Local prototype: hide the human first, then search with the drone.
            controls.SetDroneControl(true);
        }
        else Finish(false);
    }

    private void Finish(bool dronesWon)
    {
        Phase = RoundPhase.Finished;
        StopPreparationMusic();
        DronesWon = dronesWon;
        RoundStats.Finish(dronesWon);
        foreach (var human in humans)
            if (human != null) human.SetControlled(false);
        controls.ShowRoundResult();
    }

    public void RestartRound()
    {
        if (networkDriven) { MultiplayerPrototype.Instance.RestartOnlineRound(); return; }
        if (Phase != RoundPhase.Finished || IsRestarting) return;
        IsRestarting = true;
        // Reload the authored scene to restore every wall, prop, spawn and health value.
        try
        {
            if (SceneManager.LoadSceneAsync(gameObject.scene.path, LoadSceneMode.Single) == null)
                IsRestarting = false;
        }
        catch (System.Exception exception)
        {
            IsRestarting = false;
            Debug.LogException(exception, this);
        }
    }

    private void UpdatePreparationMusic()
    {
        if (!initialized) return;
        if (Phase != musicPhase)
        {
            musicPhase = Phase; musicStarted = false;
            if (Phase != RoundPhase.Hiding) StopPreparationMusic();
        }
        if (Phase != RoundPhase.Hiding || RemainingSeconds <= 0f) { StopPreparationMusic(); return; }
        if (preparationMusic != null) preparationMusic.volume = 0.22f * GameAudio.MixHeadroom * GameSettings.Volume(SoundCategory.Ambient);
        if (musicStarted) return;
        musicStarted = true;
        if (preparationMusic == null)
        {
            preparationMusic = gameObject.AddComponent<AudioSource>();
            preparationMusic.playOnAwake = false; preparationMusic.loop = true;
            preparationMusic.spatialBlend = 0f;
            preparationMusic.clip = Resources.Load<AudioClip>("Audio/Preparation");
        }
        if (preparationMusic.clip == null) return;
        preparationMusic.volume = 0.22f * GameAudio.MixHeadroom * GameSettings.Volume(SoundCategory.Ambient);
        float total = networkDriven && MultiplayerPrototype.Instance != null ? MultiplayerPrototype.Instance.State.Settings.HidingSeconds : hidingSeconds;
        preparationMusic.time = Mathf.Repeat(Mathf.Max(0f, total - RemainingSeconds), preparationMusic.clip.length);
        preparationMusic.Play();
        preparationMusic.SetScheduledEndTime(AudioSettings.dspTime + RemainingSeconds);
    }
    private void StopPreparationMusic()
    {
        if (preparationMusic != null && preparationMusic.isPlaying) preparationMusic.Stop();
    }
    private void OnDisable() => StopPreparationMusic();
}
