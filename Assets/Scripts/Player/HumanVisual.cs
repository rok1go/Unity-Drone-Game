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

    }
    private void LateUpdate()
    {
        transform.localScale = new Vector3(1, controller.CurrentHeight / controller.standingHeight, 1);
        var mode = controller.IsControlled ? ShadowCastingMode.ShadowsOnly : ShadowCastingMode.On;
        foreach (var part in parts) part.shadowCastingMode = mode;
    }
}

