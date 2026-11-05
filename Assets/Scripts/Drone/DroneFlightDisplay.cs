using UnityEngine;
using UnityEngine.UIElements;

// Live FPV instruments. Labels are retained; vector lines repaint with the camera attitude.
public sealed class DroneFlightDisplay : VisualElement
{
    private readonly Label[] speedLabels = new Label[9];
    private readonly Label[] altitudeLabels = new Label[9];
    private readonly Label[] compassLabels = new Label[4];
    private readonly Label[] pitchLabels = new Label[34];
    private float speed, altitude, heading, pitch, roll;
    private bool hasGround;
    private static readonly string[] Cardinals = { "N", "E", "S", "W" };

    public DroneFlightDisplay()
    {
        pickingMode = PickingMode.Ignore;
        style.position = Position.Absolute;
        style.left = style.right = style.top = style.bottom = 0f;
        for (int i = 0; i < 9; i++) { speedLabels[i] = MakeLabel(); altitudeLabels[i] = MakeLabel(); }
        for (int i = 0; i < 4; i++) compassLabels[i] = MakeLabel();
        for (int i = 0; i < pitchLabels.Length; i++) pitchLabels[i] = MakeLabel();
        generateVisualContent += Draw;
        RegisterCallback<GeometryChangedEvent>(_ => LayoutLabels());
    }

    private Label MakeLabel()
    {
        var label = new Label { pickingMode = PickingMode.Ignore };
        label.style.position = Position.Absolute;
        label.style.width = 64; label.style.height = 24;
        label.style.fontSize = 20;
        label.style.unityTextAlign = TextAnchor.MiddleCenter;
        Add(label);
        return label;
    }

    public void UpdateTelemetry(DroneController drone, float groundHeight)
    {
        speed = drone.Velocity.magnitude * 3.6f;
        hasGround = !float.IsInfinity(groundHeight);
        altitude = hasGround ? groundHeight : 0f;
        heading = Mathf.Repeat(drone.transform.eulerAngles.y, 360f);
        Transform camera = drone.viewCamera != null ? drone.viewCamera.transform : drone.transform;
        pitch = Mathf.Asin(Mathf.Clamp(camera.forward.y, -1f, 1f)) * Mathf.Rad2Deg;
        roll = Mathf.Atan2(camera.right.y, camera.up.y) * Mathf.Rad2Deg;
        LayoutLabels();
        MarkDirtyRepaint();
    }

    private static void Place(Label label, string text, float x, float y, bool visible)
    {
        label.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
        if (!visible) return;
        label.text = text;
        label.style.left = x - 32f; label.style.top = y - 12f;
    }

    private void LayoutLabels()
    {
        float w = contentRect.width, h = contentRect.height;
        if (w < 1f || h < 1f) return;
        for (int i = 0; i < 9; i++)
        {
            float value = Mathf.Floor(speed / 5f) * 5f - 20f + i * 5f;
            float y = h * 0.46f - (value - speed) * h * 0.3f / 40f;
            Place(speedLabels[i], value.ToString("0"), w * 0.125f - 42f, y,
                value >= 0f && Mathf.Abs(value - speed) > 2.3f && y > h * 0.30f && y < h * 0.62f);
            value = Mathf.Floor(altitude / 5f) * 5f - 20f + i * 5f;
            y = h * 0.46f - (value - altitude) * h * 0.3f / 25f;
            Place(altitudeLabels[i], value.ToString("0"), w * 0.88f + 40f, y,
                hasGround && Mathf.Abs(value - altitude) > 1.7f && y > h * 0.30f && y < h * 0.62f);
        }
        for (int i = 0; i < 4; i++)
        {
            float delta = Mathf.DeltaAngle(heading, i * 90f);
            Place(compassLabels[i], Cardinals[i], w * 0.5f + delta / 120f * w * 0.4f,
                h * 0.16f, Mathf.Abs(delta) < 60f);
        }
        for (int i = 0; i < 17; i++)
        {
            float angle = (i - 8) * 5f;
            float y = (pitch - angle) * h * 0.012f;
            bool visible = angle != 0f && Mathf.Abs(y) < h * 0.24f;
            Vector2 left = Rotate(new Vector2(-92f, y), w, h);
            Vector2 right = Rotate(new Vector2(92f, y), w, h);
            Place(pitchLabels[i * 2], angle.ToString("0"), left.x, left.y, visible);
            Place(pitchLabels[i * 2 + 1], angle.ToString("0"), right.x, right.y, visible);
            pitchLabels[i * 2].style.rotate = pitchLabels[i * 2 + 1].style.rotate = new Rotate(Angle.Degrees(roll));
        }
    }

    private Vector2 Rotate(Vector2 point, float w, float h)
    {
        float radians = roll * Mathf.Deg2Rad;
        return new Vector2(w * 0.5f + point.x * Mathf.Cos(radians) - point.y * Mathf.Sin(radians),
            h * 0.54f + point.x * Mathf.Sin(radians) + point.y * Mathf.Cos(radians));
    }

    private static void Line(Painter2D p, Vector2 a, Vector2 b)
    {
        p.BeginPath(); p.MoveTo(a); p.LineTo(b); p.Stroke();
    }

    private void Draw(MeshGenerationContext context)
    {
        float w = contentRect.width, h = contentRect.height;
        if (w < 1f || h < 1f) return;
        var p = context.painter2D;
        p.strokeColor = new Color(0.96f, 0.99f, 0.98f, 0.92f);
        p.lineWidth = 1.8f;
        // Compass ticks scroll through the camera's actual yaw.
        for (int degree = 0; degree < 360; degree += 5)
        {
            float delta = Mathf.DeltaAngle(heading, degree);
            if (Mathf.Abs(delta) > 60f) continue;
            float x = w * 0.5f + delta / 120f * w * 0.4f;
            Line(p, new Vector2(x, h * 0.13f), new Vector2(x, h * 0.13f + (degree % 15 == 0 ? 12 : 6)));
        }
        Line(p, new Vector2(w * 0.5f - 4, h * 0.18f), new Vector2(w * 0.5f, h * 0.18f + 7));
        Line(p, new Vector2(w * 0.5f, h * 0.18f + 7), new Vector2(w * 0.5f + 4, h * 0.18f));
        DrawTape(p, w * 0.125f, w, h, speed, 40f, true);
        if (hasGround) DrawTape(p, w * 0.88f, w, h, altitude, 25f, false);
        // Pitch ladder follows the complete FPV camera pose, including mouse look.
        for (int i = -8; i <= 8; i++)
        {
            float y = (pitch - i * 5f) * h * 0.012f;
            if (Mathf.Abs(y) > h * 0.24f) continue;
            float length = i == 0 ? 120f : i % 2 == 0 ? 42f : 30f;
            Line(p, Rotate(new Vector2(-38f - length, y), w, h), Rotate(new Vector2(-38f, y), w, h));
            Line(p, Rotate(new Vector2(38f, y), w, h), Rotate(new Vector2(38f + length, y), w, h));
        }
        float cy = h * 0.54f, cx = w * 0.5f;
        Line(p, new Vector2(w * 0.24f, cy), new Vector2(w * 0.33f, cy));
        Line(p, new Vector2(w * 0.67f, cy), new Vector2(w * 0.76f, cy));
        p.BeginPath(); p.Arc(new Vector2(cx, cy), 8f, Angle.Degrees(0), Angle.Degrees(360)); p.Stroke();
        Line(p, new Vector2(cx - 28, cy), new Vector2(cx - 17, cy));
        Line(p, new Vector2(cx + 17, cy), new Vector2(cx + 28, cy));
        Line(p, new Vector2(cx, cy - 28), new Vector2(cx, cy - 17));
        Line(p, new Vector2(cx, cy + 17), new Vector2(cx, cy + 28));
    }

    private static void DrawTape(Painter2D p, float x, float w, float h, float value, float range, bool left)
    {
        float top = h * 0.30f, bottom = h * 0.62f, centre = h * 0.46f;
        Line(p, new Vector2(x, top), new Vector2(x, bottom));
        int start = Mathf.FloorToInt(value - range * 0.6f);
        int end = Mathf.CeilToInt(value + range * 0.6f);
        for (int tick = start; tick <= end; tick++)
        {
            if (left && tick < 0) continue;
            float y = centre - (tick - value) * h * 0.3f / range;
            if (y < top || y > bottom) continue;
            float length = tick % 5 == 0 ? 14f : 6f;
            Line(p, new Vector2(x, y), new Vector2(x + (left ? -length : length), y));
        }
        float direction = left ? -1f : 1f;
        float outer = left ? w * 0.05f : w * 0.96f;
        p.BeginPath(); p.MoveTo(new Vector2(x, centre));
        p.LineTo(new Vector2(x + direction * 16f, centre - 18f));
        p.LineTo(new Vector2(outer, centre - 18f)); p.LineTo(new Vector2(outer, centre + 18f));
        p.LineTo(new Vector2(x + direction * 16f, centre + 18f)); p.ClosePath(); p.Stroke();
    }
}
