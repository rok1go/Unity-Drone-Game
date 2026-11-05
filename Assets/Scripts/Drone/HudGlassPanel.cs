using UnityEngine;
using UnityEngine.UIElements;

// Vector glass behind existing labels, with gradients on both the fill and rim.
public sealed class HudGlassPanel : VisualElement
{
    private readonly Color start, end;
    private readonly bool horizontal;
    private readonly float radius, rim;

    public static void Attach(VisualElement owner, Color start, Color end, bool horizontal = false,
        float radius = 6f, float rim = 0.8f)
    {
        if (owner == null) return;
        owner.style.backgroundColor = Color.clear;
        owner.style.borderTopWidth = owner.style.borderBottomWidth = 0f;
        owner.style.borderLeftWidth = owner.style.borderRightWidth = 0f;
        owner.Insert(0, new HudGlassPanel(start, end, horizontal, radius, rim));
    }

    private HudGlassPanel(Color start, Color end, bool horizontal, float radius, float rim)
    {
        this.start = start; this.end = end; this.horizontal = horizontal;
        this.radius = radius; this.rim = rim;
        AddToClassList("glass-surface");
        pickingMode = PickingMode.Ignore;
        style.position = Position.Absolute;
        style.left = style.right = style.top = style.bottom = 0f;
        generateVisualContent += Draw;
    }

    private void Draw(MeshGenerationContext context)
    {
        float w = contentRect.width, h = contentRect.height;
        if (w < 1f || h < 1f) return;
        var p = context.painter2D;
        Vector2 endPoint = horizontal ? new Vector2(w, 0) : new Vector2(w * 0.2f, h);
        p.fillGradient = FillGradient.MakeLinearGradient(start, end, Vector2.zero, endPoint, AddressMode.Clamp);
        p.BeginPath();
        RoundedPath(p, 0.5f, 0.5f, w - 1f, h - 1f, radius);
        p.Fill();
        if (rim <= 0f) return;
        p.lineWidth = rim;
        p.strokeFillGradient = FillGradient.MakeLinearGradient(new Color(0.9f, 0.96f, 1f, 0.52f),
            new Color(0.9f, 0.96f, 1f, 0.035f), Vector2.zero, new Vector2(w * 0.5f, h), AddressMode.Clamp);
        p.BeginPath();
        RoundedPath(p, 0.5f, 0.5f, w - 1f, h - 1f, radius);
        p.Stroke();
    }

    private static void RoundedPath(Painter2D p, float x, float y, float w, float h, float radius)
    {
        float r = Mathf.Min(radius, Mathf.Min(w, h) * 0.5f);
        if (r <= 0.01f)
        {
            p.MoveTo(new Vector2(x, y)); p.LineTo(new Vector2(x + w, y));
            p.LineTo(new Vector2(x + w, y + h)); p.LineTo(new Vector2(x, y + h)); p.ClosePath(); return;
        }
        p.MoveTo(new Vector2(x + r, y)); p.LineTo(new Vector2(x + w - r, y));
        p.ArcTo(new Vector2(x + w, y), new Vector2(x + w, y + r), r);
        p.LineTo(new Vector2(x + w, y + h - r));
        p.ArcTo(new Vector2(x + w, y + h), new Vector2(x + w - r, y + h), r);
        p.LineTo(new Vector2(x + r, y + h));
        p.ArcTo(new Vector2(x, y + h), new Vector2(x, y + h - r), r);
        p.LineTo(new Vector2(x, y + r));
        p.ArcTo(new Vector2(x, y), new Vector2(x + r, y), r);
        p.ClosePath();
    }
}
