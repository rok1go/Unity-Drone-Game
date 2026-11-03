using System;
using UnityEngine;

/// <summary>Keep nearby interiors lit without exceeding the map's realtime light budget.</summary>
public class InteriorLightBudget : MonoBehaviour
{
    [Range(0, 64)] public int maximumActive = 24;
    private Light[] lamps;
    private float nextRefresh;

    private void Start() => lamps = GetComponentsInChildren<Light>(true);

    private void LateUpdate()
    {
        if (Time.unscaledTime < nextRefresh || lamps == null) return;
        nextRefresh = Time.unscaledTime + 0.25f;
        var camera = Camera.main;
        if (camera == null) return;
        Vector3 position = camera.transform.position;
        Array.Sort(lamps, (a, b) =>
        {
            if (a == null) return b == null ? 0 : 1;
            if (b == null) return -1;
            return (a.transform.position - position).sqrMagnitude.CompareTo((b.transform.position - position).sqrMagnitude);
        });
        int active = 0;
        foreach (var lamp in lamps)
            if (lamp != null) lamp.enabled = active++ < maximumActive;
    }
}
