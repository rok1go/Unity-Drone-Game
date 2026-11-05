using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(Camera), typeof(AudioListener))]
public class SpectatorController : MonoBehaviour
{
    public HumanController ObservedHuman { get; private set; }
    public bool IsFollowing => ObservedHuman != null && !ObservedHuman.IsEliminated &&
        ObservedHuman.gameObject.activeInHierarchy;
    private Camera view;
    private AudioListener listener;
    private bool followingHuman;
    private float yaw, pitch;

    private void Awake()
    {
        view = GetComponent<Camera>();
        listener = GetComponent<AudioListener>();
        StopObserving();
    }

    public void Begin(Camera source)
    {
        if (source != null)
        {
            view.CopyFrom(source);
            transform.SetPositionAndRotation(source.transform.position, source.transform.rotation);
        }
        yaw = transform.eulerAngles.y;
        pitch = Mathf.DeltaAngle(0, transform.eulerAngles.x);
        enabled = true;
        view.enabled = true;
        listener.enabled = true;
        CycleTarget();
    }

    public void StopObserving()
    {
        ObservedHuman = null;
        followingHuman = false;
        if (view != null) view.enabled = false;
        if (listener != null) listener.enabled = false;
        enabled = false;
    }

    public void CycleTarget()
    {
        var humans = Object.FindObjectsByType<HumanController>(FindObjectsSortMode.InstanceID);
        int previous = System.Array.IndexOf(humans, ObservedHuman);
        ObservedHuman = null;
        for (int i = 1; i <= humans.Length; i++)
        {
            var candidate = humans[(previous + i) % humans.Length];
            if (candidate.IsEliminated || candidate.viewCamera == null) continue;
            ObservedHuman = candidate;
            break;
        }
        followingHuman = IsFollowing;
        if (!followingHuman)
        {
            yaw = transform.eulerAngles.y;
            pitch = Mathf.DeltaAngle(0, transform.eulerAngles.x);
        }
    }

    private void Update()
    {
        if (followingHuman && !IsFollowing) CycleTarget();
        if (IsFollowing || Cursor.lockState != CursorLockMode.Locked) return;
        Vector2 look = Mouse.current != null ? Mouse.current.delta.ReadValue() : Vector2.zero;
        yaw += look.x * 0.12f;
        pitch = Mathf.Clamp(pitch - look.y * 0.12f, -85f, 85f);
        transform.rotation = Quaternion.Euler(pitch, yaw, 0);
        var keyboard = Keyboard.current;
        if (keyboard == null) return;
        Vector3 input = new Vector3((keyboard.dKey.isPressed ? 1 : 0) - (keyboard.aKey.isPressed ? 1 : 0),
            (keyboard.spaceKey.isPressed ? 1 : 0) - (keyboard.leftCtrlKey.isPressed || keyboard.rightCtrlKey.isPressed ? 1 : 0),
            (keyboard.wKey.isPressed ? 1 : 0) - (keyboard.sKey.isPressed ? 1 : 0));
        Vector3 motion = transform.right * input.x + Vector3.up * input.y + transform.forward * input.z;
        float speed = keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed ? 12f : 5f;
        transform.position += Vector3.ClampMagnitude(motion, 1f) * speed * Time.unscaledDeltaTime;
    }

    private void LateUpdate()
    {
        if (!IsFollowing) return;
        var source = ObservedHuman.viewCamera;
        transform.SetPositionAndRotation(source.transform.position, source.transform.rotation);
        view.fieldOfView = source.fieldOfView;
    }
}
