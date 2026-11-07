using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(BoxCollider))]
public class DestructibleBlock : MonoBehaviour
{
    [SerializeField] private string stableId;
    [SerializeField] private Transform debrisParent;
    [SerializeField] private BuildingStructure structure;
    [SerializeField] private Vector3Int gridCell;
    public string StableId => stableId;
    public Transform DebrisParent => debrisParent;
    public BuildingStructure Structure => structure;
    public Vector3Int GridCell => gridCell;
    public bool IsDetached { get; private set; }
    public MapRenderChunk RenderChunk { get; set; }
    public void Initialize(string id, Transform debrisRoot) { stableId = id; debrisParent = debrisRoot; }
    public void SetStructure(BuildingStructure owner, Vector3Int cell) { structure = owner; gridCell = cell; }

    internal void DetachInto(Transform chunk, PhysicsMaterial contactMaterial)
    {
        if (RenderChunk != null)
        {
            RenderChunk.Remove(GetComponent<MeshFilter>());
            RenderChunk = null;
            GetComponent<MeshRenderer>().enabled = true;
        }
        IsDetached = true;
        if (contactMaterial != null)
            foreach (var collider in GetComponents<Collider>()) collider.sharedMaterial = contactMaterial;
        transform.SetParent(chunk, true); // Original geometry and collider become part of the compound body.
    }

    public void HideReplicated()
    {
        if (RenderChunk != null) { RenderChunk.Remove(GetComponent<MeshFilter>()); RenderChunk = null; }
        IsDetached = true;
        gameObject.SetActive(false);
    }
    private void OnDestroy()
    {
        if (RenderChunk != null) RenderChunk.Remove(GetComponent<MeshFilter>());
        if (Application.isPlaying && !IsDetached && structure != null && structure.gameObject.activeInHierarchy)
            structure.MarkDirty();
    }
}
