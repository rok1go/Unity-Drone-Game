using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

// Rebindable player actions. Only keys the game actually reads are listed.
public enum GameAction
{
    MoveForward, MoveBack, MoveLeft, MoveRight, Sprint, Jump, Crouch, Prone, Interact, LeanLeft, LeanRight,
    Push, DropItem, Flashlight,
    DroneUp, DroneDown, DroneMotors, DroneMode, DroneDetonate, DroneCenterCamera, DroneLight, Help
}

public enum SoundCategory { Effects, Ambient, Interface }

// Player preferences: graphics, controls, key bindings and audio. Stored as JSON in PlayerPrefs and
// applied immediately; the display mode and resolution go through PrototypeMenu's timed confirmation.
[Serializable]
public sealed class GameSettings
{
    private const string PrefsKey = "game.settings.v1";
    public static readonly string[] QualityNames = { "Низкое", "Среднее", "Высокое", "Ультра" };
    private static readonly int[] QualityLevels = { 1, 2, 3, 5 };   // indices into the project's quality levels

    // Graphics
    public int Width, Height;
    public FullScreenMode Mode = FullScreenMode.FullScreenWindow;
    public int Quality = 2;
    public bool VSync = true;
    public int FpsLimit;                 // 0 = unlimited
    public float Fov = 75f;              // human view
    public float DroneFov = 100f;
    public int Shadows = 2;              // 0 off, 1 low, 2 medium, 3 high
    public int Effects = 1;              // 0 low, 1 normal, 2 high: dust, shards, rain
    public bool ReducedConcussion;       // accessibility: no blur or sway after nearby blasts (sound only)
    public float DrawDistance = 400f;
    // Controls
    public float HumanSensitivity = 0.12f, DroneSensitivity = 0.12f, LeanSensitivity = 0.75f;
    public bool InvertHuman, InvertDrone;
    public float DroneExpo = 0.35f;
    public bool DroneAcro;
    public List<string> Bindings = new List<string>();   // "Action=Key"
    // Audio
    public float Master = 1f, EffectsVolume = 1f, AmbientVolume = 1f, InterfaceVolume = 1f;
    public bool Muted;

    private static GameSettings current;
    private static readonly Dictionary<GameAction, Key> keys = new Dictionary<GameAction, Key>();
    public static event Action Changed;

    public static GameSettings Current
    {
        get
        {
            if (current == null) Load();
            return current;
        }
    }

    public static readonly Dictionary<GameAction, Key> Defaults = new Dictionary<GameAction, Key>
    {
        { GameAction.MoveForward, Key.W }, { GameAction.MoveBack, Key.S }, { GameAction.MoveLeft, Key.A },
        { GameAction.MoveRight, Key.D }, { GameAction.Sprint, Key.LeftShift }, { GameAction.Jump, Key.Space },
        { GameAction.Crouch, Key.LeftCtrl }, { GameAction.Prone, Key.Z }, { GameAction.Interact, Key.F },
        { GameAction.LeanLeft, Key.Q }, { GameAction.LeanRight, Key.E }, { GameAction.Push, Key.R },
        { GameAction.DropItem, Key.G }, { GameAction.Flashlight, Key.T },
        { GameAction.DroneUp, Key.Space },
        { GameAction.DroneDown, Key.LeftCtrl }, { GameAction.DroneMotors, Key.B }, { GameAction.DroneMode, Key.V },
        { GameAction.DroneDetonate, Key.F }, { GameAction.DroneCenterCamera, Key.C }, { GameAction.DroneLight, Key.L }, { GameAction.Help, Key.H },
    };

    public static readonly Dictionary<GameAction, string> ActionNames = new Dictionary<GameAction, string>
    {
        { GameAction.MoveForward, "Вперёд" }, { GameAction.MoveBack, "Назад" }, { GameAction.MoveLeft, "Влево" },
        { GameAction.MoveRight, "Вправо" }, { GameAction.Sprint, "Бег" }, { GameAction.Jump, "Прыжок / перелезть" },
        { GameAction.Crouch, "Присесть" }, { GameAction.Prone, "Лечь" }, { GameAction.Interact, "Действие" },
        { GameAction.LeanLeft, "Выглянуть влево" }, { GameAction.LeanRight, "Выглянуть вправо" },
        { GameAction.Push, "Толкать мебель" }, { GameAction.DropItem, "Бросить предмет из слота" },
        { GameAction.Flashlight, "Фонарик" },
        { GameAction.DroneUp, "Дрон: вверх" }, { GameAction.DroneDown, "Дрон: вниз" },
        { GameAction.DroneMotors, "Дрон: моторы" }, { GameAction.DroneMode, "Дрон: режим полёта" },
        { GameAction.DroneDetonate, "Дрон: подрыв" }, { GameAction.DroneCenterCamera, "Дрон: камера вперёд" }, { GameAction.DroneLight, "Дрон: фонарь" },
        { GameAction.Help, "Подсказки" },
    };

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        current = null;
        Changed = null;
        keys.Clear();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void ApplyAtStartup()
    {
        Current.Apply(false);
        if (!Application.isEditor) Current.ApplyDisplay();
    }

    private static void Load()
    {
        string json = PlayerPrefs.GetString(PrefsKey, "");
        try { current = string.IsNullOrEmpty(json) ? new GameSettings() : JsonUtility.FromJson<GameSettings>(json); }
        catch (Exception) { current = null; }
        current ??= new GameSettings();
        if (current.Width <= 0 || current.Height <= 0)
        {
            current.Width = Screen.currentResolution.width;
            current.Height = Screen.currentResolution.height;
            current.Mode = Screen.fullScreenMode;
        }
        current.RebuildKeys();
    }

    public GameSettings Clone() => JsonUtility.FromJson<GameSettings>(JsonUtility.ToJson(this));

    // ---------------- keys ----------------

    public static Key KeyFor(GameAction action) => Current != null && keys.TryGetValue(action, out var key) ? key : Defaults[action];

    public static bool Held(GameAction action)
    {
        var keyboard = Keyboard.current;
        if (keyboard == null) return false;
        var key = KeyFor(action);
        if (key == Key.None) return false;
        // Both Shift / Ctrl keys count for the left one, as before rebinding existed.
        if (key == Key.LeftShift) return keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed;
        if (key == Key.LeftCtrl) return keyboard.leftCtrlKey.isPressed || keyboard.rightCtrlKey.isPressed;
        return keyboard[key].isPressed;
    }

    public static bool Pressed(GameAction action)
    {
        var keyboard = Keyboard.current;
        var key = KeyFor(action);
        if (keyboard == null || key == Key.None) return false;
        if (key == Key.LeftShift) return keyboard.leftShiftKey.wasPressedThisFrame || keyboard.rightShiftKey.wasPressedThisFrame;
        if (key == Key.LeftCtrl) return keyboard.leftCtrlKey.wasPressedThisFrame || keyboard.rightCtrlKey.wasPressedThisFrame;
        return keyboard[key].wasPressedThisFrame;
    }

    public static bool Released(GameAction action)
    {
        var keyboard = Keyboard.current;
        var key = KeyFor(action);
        if (keyboard == null || key == Key.None) return false;
        return keyboard[key].wasReleasedThisFrame;
    }

    public static string KeyName(GameAction action) => KeyLabel(KeyFor(action));

    public static string KeyLabel(Key key) => key switch
    {
        Key.None => "—", Key.Space => "Пробел", Key.LeftShift => "Shift", Key.RightShift => "Shift",
        Key.LeftCtrl => "Ctrl", Key.RightCtrl => "Ctrl", Key.LeftAlt => "Alt", Key.RightAlt => "Alt",
        Key.Tab => "Tab", Key.Enter => "Enter", Key.Backspace => "Backspace",
        _ => key.ToString().Replace("Digit", "").ToUpperInvariant()
    };

    public void SetKey(GameAction action, Key key)
    {
        Bindings.RemoveAll(b => b.StartsWith(action + "="));
        if (key != Defaults[action]) Bindings.Add(action + "=" + key);
        RebuildKeys();
    }

    public void ResetKeys()
    {
        Bindings.Clear();
        RebuildKeys();
    }

    // Two human actions (or two drone actions) on one key would fight. Interact and lean right
    // share E on purpose: E acts on what is under the crosshair, and leans when there is nothing.
    public static GameAction? Conflict(GameAction action, Key key)
    {
        bool drone = action.ToString().StartsWith("Drone");
        foreach (var pair in Defaults)
        {
            var other = pair.Key;
            if (other == action || KeyFor(other) != key) continue;
            if (other.ToString().StartsWith("Drone") != drone && other != GameAction.Help && action != GameAction.Help) continue;
            if ((action == GameAction.Interact && other == GameAction.LeanRight) ||
                (action == GameAction.LeanRight && other == GameAction.Interact)) continue;
            return other;
        }
        return null;
    }

    private void RebuildKeys()
    {
        keys.Clear();
        foreach (var pair in Defaults) keys[pair.Key] = pair.Value;
        foreach (string binding in Bindings)
        {
            int split = binding.IndexOf('=');
            if (split <= 0) continue;
            if (Enum.TryParse(binding.Substring(0, split), out GameAction action) &&
                Enum.TryParse(binding.Substring(split + 1), out Key key)) keys[action] = key;
        }
    }

    // ---------------- applying ----------------

    public static float Volume(SoundCategory category)
    {
        var s = Current;
        return category switch
        {
            SoundCategory.Ambient => s.AmbientVolume,
            SoundCategory.Interface => s.InterfaceVolume,
            _ => s.EffectsVolume
        };
    }

    // Everything except the display mode and resolution (see ApplyDisplay).
    public void Apply(bool save = true)
    {
        current = this;
        Width = Mathf.Max(640, Width); Height = Mathf.Max(480, Height);
        HumanSensitivity = Mathf.Clamp(HumanSensitivity, 0.02f, 0.6f);
        DroneSensitivity = Mathf.Clamp(DroneSensitivity, 0.02f, 0.6f);
        Master = Mathf.Clamp01(Master); EffectsVolume = Mathf.Clamp01(EffectsVolume);
        AmbientVolume = Mathf.Clamp01(AmbientVolume); InterfaceVolume = Mathf.Clamp01(InterfaceVolume);
        RebuildKeys();
        int quality = Mathf.Clamp(QualityLevels[Mathf.Clamp(Quality, 0, 3)], 0, QualitySettings.names.Length - 1);
        if (QualitySettings.GetQualityLevel() != quality) QualitySettings.SetQualityLevel(quality, true);
        QualitySettings.vSyncCount = VSync ? 1 : 0;
        QualitySettings.pixelLightCount = Quality == 0 ? 4 : 8;
        Application.targetFrameRate = VSync || FpsLimit <= 0 ? -1 : Mathf.Clamp(FpsLimit, 30, 360);
        QualitySettings.shadows = Shadows == 0 ? ShadowQuality.Disable : Shadows == 1 ? ShadowQuality.HardOnly : ShadowQuality.All;
        QualitySettings.shadowDistance = Shadows switch { 0 => 0f, 1 => 25f, 2 => 50f, _ => 110f };
        QualitySettings.shadowResolution = Shadows >= 3 ? ShadowResolution.VeryHigh : Shadows == 2 ? ShadowResolution.High : ShadowResolution.Medium;
        AudioListener.volume = Muted ? 0f : Mathf.Clamp01(Master);
        if (save)
        {
            PlayerPrefs.SetString(PrefsKey, JsonUtility.ToJson(this));
            PlayerPrefs.Save();
        }
        Changed?.Invoke();
    }

    public void ApplyDisplay()
    {
        if (Width > 0 && Height > 0) Screen.SetResolution(Width, Height, Mode);
        else Screen.fullScreenMode = Mode;
    }

    // Per-camera parts of the settings: field of view and draw distance.
    public static void ApplyCamera(Camera camera, bool drone)
    {
        if (camera == null) return;
        var s = Current;
        camera.fieldOfView = Mathf.Clamp(drone ? s.DroneFov : s.Fov, 60f, 120f);
        camera.farClipPlane = Mathf.Clamp(s.DrawDistance, 80f, 1000f);
    }
}
