using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

// An independent Editor view. Only a hidden preview camera renders to the texture;
// the source camera and all player cameras retain their enabled state and targets.
public class MapCameraWindow : EditorWindow
{
    [SerializeField] private Camera source;
    private Camera previewCamera;
    private RenderTexture previewTexture;
    private UnityEngine.UIElements.Image picture;
    private Label message;
    private ObjectField cameraField;
    private bool visible;

    [MenuItem("Window/Game/Map Camera")]
    public static void Open()
    {
        var window = GetWindow<MapCameraWindow>();
        window.titleContent = new GUIContent("Камера карты");
        window.minSize = new Vector2(340, 240);
        window.Show();
    }

    public void CreateGUI()
    {
        var root = rootVisualElement;
        root.style.flexGrow = 1;
        root.style.backgroundColor = new Color(0.04f, 0.055f, 0.065f);
        var toolbar = new Toolbar();
        cameraField = new ObjectField("Камера") { objectType = typeof(Camera), allowSceneObjects = true };
        cameraField.style.flexGrow = 1;
        cameraField.RegisterValueChangedCallback(evt => source = evt.newValue as Camera);
        toolbar.Add(cameraField);
        toolbar.Add(new ToolbarButton(() =>
        {
            if (source != null) { Selection.activeGameObject = source.gameObject; EditorGUIUtility.PingObject(source); }
        }) { text = "Выделить" });
        root.Add(toolbar);
        picture = new UnityEngine.UIElements.Image { scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore };
        picture.style.flexGrow = 1;
        picture.style.minHeight = 100;
        root.Add(picture);
        message = new Label("Перемещай Extra Camera в сцене — здесь обновляется её вид.");
        message.style.whiteSpace = WhiteSpace.Normal;
        message.style.paddingLeft = message.style.paddingRight = 8;
        message.style.paddingTop = message.style.paddingBottom = 6;
        message.style.color = new Color(0.75f, 0.85f, 0.8f);
        root.Add(message);
        FindSource();
        root.schedule.Execute(RenderPreview).Every(16);
    }

    private void OnBecameVisible() => visible = true;
    private void OnBecameInvisible() => visible = false;

    private void FindSource()
    {
        if (source == null)
            foreach (var camera in Resources.FindObjectsOfTypeAll<Camera>())
                if (camera.name == "Extra Camera" && camera.gameObject.scene.IsValid() && camera.gameObject.scene.isLoaded)
                { source = camera; break; }
        cameraField.SetValueWithoutNotify(source);
    }

    private void RenderPreview()
    {
        if (!visible || picture == null || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
        if (source == null) FindSource();
        if (source == null)
        { message.text = "Выбери отдельную камеру в поле сверху."; return; }
        message.text = "Перемещай " + source.name + " в сцене — здесь обновляется её вид.";
        int width = Mathf.Clamp(Mathf.RoundToInt(picture.resolvedStyle.width), 160, 960);
        int height = Mathf.Clamp(Mathf.RoundToInt(picture.resolvedStyle.height), 100, 720);
        if (previewTexture == null || previewTexture.width != width || previewTexture.height != height)
        {
            ReleaseTexture();
            previewTexture = new RenderTexture(width, height, 24)
            { name = "Map camera preview", hideFlags = HideFlags.HideAndDontSave };
            previewTexture.Create();
            picture.image = previewTexture;
        }
        if (previewCamera == null)
        {
            var obj = new GameObject("Map camera preview (Editor only)") { hideFlags = HideFlags.HideAndDontSave };
            previewCamera = obj.AddComponent<Camera>();
            previewCamera.enabled = false;
        }
        previewCamera.CopyFrom(source);
        previewCamera.enabled = false;
        previewCamera.transform.SetPositionAndRotation(source.transform.position, source.transform.rotation);
        previewCamera.targetTexture = previewTexture;
        previewCamera.aspect = (float)width / height;
        previewCamera.rect = new Rect(0, 0, 1, 1);
        previewCamera.allowHDR = false;
        previewCamera.allowMSAA = false;
        previewCamera.Render();
        picture.MarkDirtyRepaint();
    }

    private void ReleaseTexture()
    {
        if (picture != null) picture.image = null;
        if (previewTexture == null) return;
        previewTexture.Release();
        DestroyImmediate(previewTexture);
        previewTexture = null;
    }

    private void OnDisable()
    {
        if (previewCamera != null) DestroyImmediate(previewCamera.gameObject);
        ReleaseTexture();
    }
}
