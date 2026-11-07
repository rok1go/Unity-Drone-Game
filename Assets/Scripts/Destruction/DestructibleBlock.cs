using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(BoxCollider))]
public class DestructibleBlock : MonoBehaviour
{
    [SerializeField] private string stableId;
    [SerializeField] private Transform debrisParent;
    [SerializeField] private BuildingStructure structure;
    [SerializeField] private Vector3Int gridCell;
    // 1 light (wood, plaster, furniture), 2 brick or sheet metal, 3 reinforced concrete; 0 = unspecified.
    [SerializeField] private byte material;
    private float mass = -1f;
    public byte Material => material;
    // How hard the block is to break: a blast's shatter and tear-off distances are divided by it.
    public float Toughness => material == 3 ? 1.2f : material == 2 ? 1.05f : 0.8f;
    // How far (in blocks) this material can reach sideways beyond its original support before it gives way.
    public int Span => material == 3 ? 3 : material == 2 ? 2 : 1;
    // Game-scale densities (kg per cubic metre of solid voxels): light rubble can be carried, concrete cannot.
    public float Density => material == 3 ? 150f : material == 2 ? 110f : 60f;
    public float Mass
    {
        get
        {
            if (mass >= 0f) return mass;
            float volume = 0f;
            Vector3 scale = transform.lossyScale;
            foreach (var box in GetComponents<BoxCollider>())
                volume += Mathf.Abs(box.size.x * scale.x * box.size.y * scale.y * box.size.z * scale.z);
            return mass = volume * Density;
        }
    }
    public void SetMaterial(byte value) { material = value; mass = -1f; }
    public string StableId => stableId;
    public Transform DebrisParent => debrisParent;
    public BuildingStructure Structure => structure;
    public Vector3Int GridCell => gridCell;
    public bool IsDetached { get; private set; }
    // Part of a car or van (a placed "vehicle" template): it sounds like sheet metal, not masonry.
    public bool IsVehicle => structure != null && structure.transform.parent != null &&
        structure.transform.parent.name.StartsWith("vehicle");
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

    // Pulverised by a blast: leaves the batched mesh this frame, not when Destroy runs.
    internal void Remove()
    {
        if (RenderChunk != null)
        {
            RenderChunk.Remove(GetComponent<MeshFilter>());
            RenderChunk = null;
        }
        gameObject.SetActive(false);
        Destroy(gameObject);
    }

    private void OnDestroy()
    {
        if (RenderChunk != null) RenderChunk.Remove(GetComponent<MeshFilter>());
        if (Application.isPlaying && !IsDetached && structure != null && structure.gameObject.activeInHierarchy)
            structure.MarkDirty();
    }
}
