using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(Camera), typeof(AudioListener))]
public class SpectatorController : MonoBehaviour
{
    // Off: the fallen only watch living teammates and cannot scout the map for them.
    public bool AllowFreeCamera { get; set; }
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
            if (MultiplayerPrototype.OnlineMatch && !MultiplayerPrototype.Instance.CanObserve(candidate)) continue;
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
        if (IsFollowing || !AllowFreeCamera || Cursor.lockState != CursorLockMode.Locked) return;
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

    // Over the shoulder of the watched player, smoothed so network jitter does not shake the view, and pulled
    // in front of walls so it never looks through them.
    private void LateUpdate()
    {
        if (!IsFollowing) return;
        var source = ObservedHuman.viewCamera.transform;
        Vector3 head = source.position;
        Vector3 back = -source.forward * 1.9f + Vector3.up * 0.35f + source.right * 0.35f;
        float distance = back.magnitude;
        if (Physics.SphereCast(head, 0.18f, back / distance, out var hit, distance, Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Ignore) && hit.collider.GetComponentInParent<HumanController>() != ObservedHuman)
            distance = Mathf.Max(0.2f, hit.distance - 0.05f);
        Vector3 target = head + back.normalized * distance;
        float blend = 1f - Mathf.Exp(-Time.unscaledDeltaTime * 12f);
        transform.position = Vector3.Lerp(transform.position, target, blend);
        transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(head + source.forward * 4f - transform.position), blend);
        view.fieldOfView = source.GetComponent<Camera>().fieldOfView;
    }
}
