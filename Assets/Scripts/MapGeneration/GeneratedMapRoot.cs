using UnityEngine;

// Ownership survives lost generator references and makes cleanup independent of names/active state.
public class GeneratedMapRoot : MonoBehaviour
{
    public ProceduralMapGenerator owner;

    private void Awake()
    {
        if (GetComponent<MapRenderBatcher>() == null) gameObject.AddComponent<MapRenderBatcher>();
        foreach (var light in GetComponentsInChildren<Light>(true)) light.shadows = LightShadows.None;
    }
}
