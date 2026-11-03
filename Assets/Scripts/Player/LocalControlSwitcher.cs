using UnityEngine;
using UnityEngine.InputSystem;

public class LocalControlSwitcher : MonoBehaviour
{
    public HumanController human;
    public DroneController drone;
    public bool startAsDrone;
    public ExplosionTestArea testArea;
    public bool ControllingDrone { get; private set; }

    private void Start()
    {
        if (drone != null) drone.Exploded += HandleDroneExploded;
        SetDroneControl(startAsDrone);
        SetCursor(true);
    }

    private void Update()
    {
        var keyboard = Keyboard.current;
        if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame) SetCursor(false);
        if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame) SetCursor(true);
        if (keyboard != null && keyboard.rKey.wasPressedThisFrame)
        {
            drone.ResetDrone();
            if (testArea != null) testArea.ResetCrates();
            SetDroneControl(true);
        }
        if (keyboard != null && keyboard.tabKey.wasPressedThisFrame)
            SetDroneControl(!ControllingDrone);
    }

    public void SetDroneControl(bool useDrone)
    {
        if (human == null || drone == null) return;
        useDrone = useDrone && !drone.IsDestroyed && drone.gameObject.activeInHierarchy;
        ControllingDrone = useDrone;
        human.SetControlled(!useDrone);
        drone.SetControlled(useDrone);
    }

    private void HandleDroneExploded() => SetDroneControl(false);

    private void OnDestroy()
    {
        if (drone != null) drone.Exploded -= HandleDroneExploded;
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

