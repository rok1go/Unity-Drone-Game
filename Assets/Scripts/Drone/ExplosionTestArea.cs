using UnityEngine;

/// <summary>Reset just the test crates; never regenerate or destroy the buildings.</summary>
public class ExplosionTestArea : MonoBehaviour
{
    public Rigidbody[] crates;
    private Vector3[] positions;
    private Quaternion[] rotations;

    private void Awake()
    {
        positions = new Vector3[crates.Length]; rotations = new Quaternion[crates.Length];
        for (int i = 0; i < crates.Length; i++)
        { positions[i] = crates[i].position; rotations[i] = crates[i].rotation; }
    }

    public void ResetCrates()
    {
        if (positions == null) return;
        for (int i = 0; i < crates.Length; i++)
        {
            var body = crates[i];
            if (body == null) continue;
            body.linearVelocity = Vector3.zero; body.angularVelocity = Vector3.zero;
            body.position = positions[i]; body.rotation = rotations[i];
            body.transform.SetPositionAndRotation(positions[i], rotations[i]);
            body.Sleep();
        }
        Physics.SyncTransforms();
    }
}
