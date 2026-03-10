using PlayerActions;
using Unity.Entities;
using UnityEngine;

// ?????????????????????????????????????????????????????????????????????????????
//  BoxSelectOverlay.cs
//
//  MonoBehaviour — attach to any persistent GameObject (e.g. UIManager).
//
//  Draws the double-tap drag selection rectangle using Unity IMGUI.
//  Reads BoxSelectSingleton from the ECS world — pure visual, zero logic.
//
//  CAMERA PAN INTEGRATION
//  ??????????????????????
//  Your camera pan system should check:
//      BoxSelectSingleton.IsDragging == true  ?  suppress panning
//
//  Access it from any MonoBehaviour via:
//      var world = World.DefaultGameObjectInjectionWorld;
//      using var q = world.EntityManager.CreateEntityQuery(
//          ComponentType.ReadOnly<BoxSelectSingleton>());
//      var box = q.GetSingleton<BoxSelectSingleton>();
//      if (box.IsDragging) return;
// ?????????????????????????????????????????????????????????????????????????????

public class BoxSelectOverlay : MonoBehaviour
{
    [Header("Box Visual Style")]
    [Tooltip("Fill colour of the selection rectangle.")]
    public Color FillColor = new Color(0.2f, 0.6f, 1f, 0.15f);

    [Tooltip("Border colour of the selection rectangle.")]
    public Color BorderColor = new Color(0.2f, 0.6f, 1f, 0.85f);

    [Tooltip("Border thickness in pixels.")]
    public float BorderWidth = 2f;

    // Cached textures — created once
    private Texture2D _fillTex;
    private Texture2D _borderTex;

    private void Awake()
    {
        _fillTex = MakeTex(FillColor);
        _borderTex = MakeTex(BorderColor);
    }

    private void OnGUI()
    {
        // Get the ECS world
        var world = World.DefaultGameObjectInjectionWorld;
        if (world == null || !world.IsCreated) return;

        BoxSelectSingleton box = null;
        using (var q = world.EntityManager.CreateEntityQuery(
                   ComponentType.ReadOnly<BoxSelectSingleton>()))
        {
            if (q.IsEmpty) return;
            box = q.GetSingleton<BoxSelectSingleton>();
        }

        if (box == null || !box.IsDragging) return;

        Rect r = box.ScreenRect;
        if (r.width < 1f || r.height < 1f) return;

        // Fill
        GUI.DrawTexture(r, _fillTex);

        // Border — draw four edge rects manually for a crisp outline
        float bw = BorderWidth;
        GUI.DrawTexture(new Rect(r.x, r.y, r.width, bw), _borderTex); // top
        GUI.DrawTexture(new Rect(r.x, r.yMax - bw, r.width, bw), _borderTex); // bottom
        GUI.DrawTexture(new Rect(r.x, r.y, bw, r.height), _borderTex); // left
        GUI.DrawTexture(new Rect(r.xMax - bw, r.y, bw, r.height), _borderTex); // right
    }

    private static Texture2D MakeTex(Color col)
    {
        var t = new Texture2D(1, 1);
        t.SetPixel(0, 0, col);
        t.Apply();
        return t;
    }
}