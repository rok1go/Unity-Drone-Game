using UnityEngine;

// Ownership survives lost generator references and makes cleanup independent of names/active state.
public class GeneratedMapRoot : MonoBehaviour
{
    public ProceduralMapGenerator owner;

    private void Awake()
    {
        if (GetComponent<MapRenderBatcher>() == null) gameObject.AddComponent<MapRenderBatcher>();
        QualitySettings.shadowDistance = 50f;
        QualitySettings.shadowCascades = 2;
        QualitySettings.shadowResolution = ShadowResolution.Medium;
        QualitySettings.pixelLightCount = 2;
        foreach (var light in GetComponentsInChildren<Light>(true)) light.shadows = LightShadows.None;
    }
}
