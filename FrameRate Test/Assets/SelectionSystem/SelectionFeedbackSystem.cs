using Unity.Burst;
using Unity.Entities;
using Unity.Rendering;

// ─────────────────────────────────────────────────────────────────────────────
//  SelectionFeedbackSystem.cs
//
//  Drives the visibility of selection-feedback entities based on whether
//  SelectionFeedbackActive is enabled or disabled.
//
//  Uses Unity DOTS RenderMeshArray / MaterialMeshInfo if you are on
//  Entities Graphics. If you use a simpler bespoke renderer, swap out
//  the component query below.
//
//  Two approaches are shown:
//
//  A) Entities Graphics — toggle MaterialMeshInfo.MeshID to hide/show
//     (set MeshID to BatchMeshID.Null to hide, restore to show).
//
//  B) Custom approach — if you store a "visible" byte on a component,
//     just query that and flip it.
//
//  Currently implemented: approach A using a "RenderingEnabled" pattern
//  via DisableRendering tag (a common DOTS Entities Graphics trick).
// ─────────────────────────────────────────────────────────────────────────────

[WorldSystemFilter(WorldSystemFilterFlags.Default)]
[UpdateInGroup(typeof(PresentationSystemGroup))]
[BurstCompile]
public partial struct SelectionFeedbackSystem : ISystem
{
    [BurstCompile] public void OnCreate(ref SystemState state) { }
    [BurstCompile] public void OnDestroy(ref SystemState state) { }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        // ── Show feedback: SelectionFeedbackActive just became enabled ─────────
        // We use WithAll<SelectionFeedbackActive> to catch entities where it
        // is currently enabled, and pair with DisableRendering presence to know
        // whether they are currently hidden.

        // Show: active + currently has DisableRendering → remove disable
        // (We must use an ECB here because adding/removing DisableRendering IS structural.
        //  This is the ONLY structural change in the whole system — it only touches
        //  the feedback mesh entity, not the unit/group entities themselves.)

        var ecb = new EntityCommandBuffer(Unity.Collections.Allocator.Temp);

        // ── Reveal feedback entities that are selected ────────────────────────
        foreach (var (_, entity) in
            SystemAPI.Query<RefRO<SelectionFeedbackActive>>()
                     .WithAll<SelectionFeedbackActive>()
                     .WithAll<DisableRendering>()
                     .WithEntityAccess())
        {
            ecb.RemoveComponent<DisableRendering>(entity);
        }

        // ── Hide feedback entities that are no longer selected ────────────────
        foreach (var (_, entity) in
            SystemAPI.Query<RefRO<SelectionFeedbackActive>>()
                     .WithDisabled<SelectionFeedbackActive>()
                     .WithNone<DisableRendering>()
                     .WithEntityAccess())
        {
            ecb.AddComponent<DisableRendering>(entity);
        }

        ecb.Playback(state.EntityManager);
        ecb.Dispose();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  NOTE on DisableRendering
//  ─────────────────────────────────────────────────────────────────────────────
//  DisableRendering is a zero-size tag from Unity.Rendering that tells the
//  Entities Graphics batch renderer to skip this entity.
//  It is the standard "hide a mesh entity" pattern in DOTS.
//
//  If you are NOT using Entities Graphics, replace DisableRendering with
//  whatever visibility mechanism your renderer uses (e.g. a custom
//  RenderVisible : IComponentData, IEnableableComponent tag).
// ─────────────────────────────────────────────────────────────────────────────
