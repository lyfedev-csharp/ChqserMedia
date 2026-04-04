using UnityEngine;
using UnityEngine.UI;

[ExecuteAlways]
[RequireComponent(typeof(Image))]
public class UIGradient : BaseMeshEffect
{
    [SerializeField] private Color topColor = Color.white;
    [SerializeField] private Color bottomColor = Color.black;
    [SerializeField] private bool horizontal = false;

    private Image targetImage;

    protected override void Awake()
    {
        base.Awake();
        Cache();
    }

    protected override void OnEnable()
    {
        base.OnEnable();
        Cache();
        SetDirty();
    }

#if UNITY_EDITOR
    protected override void OnValidate()
    {
        base.OnValidate();
        Cache();
        SetDirty();
    }
#endif

    private void Cache()
    {
        if (targetImage == null)
            targetImage = GetComponent<Image>();
    }

    private void Reset()
    {
        Cache();

        if (targetImage != null)
        {
            Color baseColor = targetImage.color;

            topColor = baseColor * 1.2f;
            bottomColor = baseColor * 0.6f;

            topColor.a = baseColor.a;
            bottomColor.a = baseColor.a;
        }
    }

    public override void ModifyMesh(VertexHelper vh)
    {
        if (!IsActive() || vh.currentVertCount == 0)
            return;

        UIVertex vertex = new UIVertex();

        float min = float.MaxValue;
        float max = float.MinValue;

        for (int i = 0; i < vh.currentVertCount; i++)
        {
            vh.PopulateUIVertex(ref vertex, i);
            float value = horizontal ? vertex.position.x : vertex.position.y;

            if (value < min) min = value;
            if (value > max) max = value;
        }

        float size = max - min;
        if (Mathf.Approximately(size, 0f))
            return;

        for (int i = 0; i < vh.currentVertCount; i++)
        {
            vh.PopulateUIVertex(ref vertex, i);

            float value = horizontal ? vertex.position.x : vertex.position.y;
            float t = (value - min) / size;

            Color gradientColor = Color.Lerp(bottomColor, topColor, t);

            vertex.color *= gradientColor;
            vh.SetUIVertex(vertex, i);
        }
    }

    public void SetDirty()
    {
        if (targetImage != null)
            targetImage.SetVerticesDirty();
    }

    public void SetColors(Color top, Color bottom)
    {
        topColor = top;
        bottomColor = bottom;
        SetDirty();
    }
}