using UnityEngine;
using UnityEngine.Rendering;

/// <summary>Simple third-person silhouette; the local first-person camera only sees its shadow.</summary>
public class HumanVisual : MonoBehaviour
{
    public HumanController controller;
    private Renderer[] parts;
    
    private void Awake()
    {
        parts = GetComponentsInChildren<Renderer>();
        if (controller == null) controller = GetComponentInParent<HumanController>();
        SetFirstPersonVisibility(false);
    }

    private void OnEnable()
    {
        Camera.onPreCull += BeforeCamera;
        Camera.onPostRender += AfterCamera;
    }

    private void OnDisable()
    {
        Camera.onPreCull -= BeforeCamera;
        Camera.onPostRender -= AfterCamera;
        SetFirstPersonVisibility(false);
    }

    private void LateUpdate()
    {
        if (controller == null) return;
        transform.localScale = new Vector3(1, controller.CurrentHeight / controller.standingHeight, 1);
    }

    private void BeforeCamera(Camera camera)
    {
        var spectator = camera.GetComponent<SpectatorController>();
        SetFirstPersonVisibility(controller != null &&
            ((controller.IsControlled && camera == controller.viewCamera) ||
             (spectator != null && spectator.ObservedHuman == controller)));
    }

    private void AfterCamera(Camera camera) => SetFirstPersonVisibility(false);

    private void SetFirstPersonVisibility(bool firstPerson)
    {
        if (parts == null) return;
        var mode = firstPerson ? ShadowCastingMode.ShadowsOnly : ShadowCastingMode.On;
        foreach (var part in parts)
            if (part != null) part.shadowCastingMode = mode;
    }
}

