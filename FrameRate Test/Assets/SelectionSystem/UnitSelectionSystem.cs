using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

// ─────────────────────────────────────────────────────────────────────────────
//  UnitSelectionSystem.cs
//
//  Click-to-select logic. Runs after PlayerInputGatherSystem, before
//  PlayerActions.MoveCommandSystem.
//
//  Behaviour:
//    • Click a unit (not selected)   → clear old selection, select it
//    • Click a unit (already selected) → deselect it  (toggle off)
//    • Shift-click a unit            → additive toggle
//    • Click a Group BannerHolder    → select entire group
//    • Click a BigGroup BannerHolder → select all sub-groups
//    • Click empty ground            → do NOTHING to selection
//                                      (MoveCommandSystem handles ground clicks)
//
//  ClickConsumedBySelection is set TRUE only when a unit was hit, so
//  MoveCommandSystem never fires on the same frame as a selection change.
//
//  Structural changes: NONE — only IEnableableComponent toggles.
// ─────────────────────────────────────────────────────────────────────────────

[WorldSystemFilter(WorldSystemFilterFlags.Default)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(PlayerInputGatherSystem))]
[UpdateBefore(typeof(PlayerActions.MoveCommandSystem))]
public partial struct UnitSelectionSystem : ISystem
{
    public void OnCreate(ref SystemState state)
        => state.RequireForUpdate<NavGridConfig>();

    public void OnUpdate(ref SystemState state)
    {
        var input = SystemAPI.ManagedAPI.GetSingleton<PlayerInputSingleton>();
        if (!input.LeftClickThisFrame) return;

        var sel = SystemAPI.ManagedAPI.GetSingleton<SelectionSingleton>();

        // ── Try to find a unit under the cursor ───────────────────────────────
        Entity picked = Entity.Null;
        SelectableKind pickedKind = SelectableKind.Unit;
        float bestDist = float.MaxValue;

        foreach (var (transform, unit, selTag, entity) in
            SystemAPI.Query<RefRO<LocalTransform>, RefRO<Unit>, RefRO<SelectableTag>>()
                     .WithEntityAccess())
        {
            float radius = math.max(0.5f, unit.ValueRO.Size.x * unit.ValueRO.Size.y * 0.5f);
            float dist = RayVsCylinder(
                input.RayOrigin, input.RayDirection,
                transform.ValueRO.Position, radius, radius * 4f);

            if (dist >= 0f && dist < bestDist)
            {
                bestDist = dist;
                picked = entity;
                pickedKind = selTag.ValueRO.Kind;
            }
        }

        // ── No unit hit → deselect all ────────────────────────────────────────
        if (picked == Entity.Null)
        {
            if (sel.HasSelection)
            {
                ClearSelection(ref state, sel);
                input.ClickConsumedBySelection = true;
            }
            return;
        }

        // A unit was hit — consume the click so MoveCommandSystem skips this frame
        input.ClickConsumedBySelection = true;

        switch (pickedKind)
        {
            case SelectableKind.Unit:
                HandleUnitClick(ref state, sel, picked, input.ShiftHeld);
                break;
            case SelectableKind.Group:
                HandleGroupClick(ref state, sel, picked, input.ShiftHeld);
                break;
            case SelectableKind.BigGroup:
                HandleBigGroupClick(ref state, sel, picked, input.ShiftHeld);
                break;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Click handlers
    // ─────────────────────────────────────────────────────────────────────────

    private void HandleUnitClick(ref SystemState state, SelectionSingleton sel,
                                  Entity unit, bool shift)
    {
        bool isSelected = SystemAPI.IsComponentEnabled<Selected>(unit);

        if (shift)
        {
            // Shift: toggle this unit only
            if (isSelected) DeselectEntity(ref state, sel, unit);
            else SelectEntity(ref state, sel, unit);
        }
        else
        {
            if (isSelected && sel.SelectedEntities.Length == 1)
            {
                // Clicking the only selected unit → deselect (toggle off)
                DeselectEntity(ref state, sel, unit);
            }
            else
            {
                // Clear all, select this one
                ClearSelection(ref state, sel);
                SelectEntity(ref state, sel, unit);
            }
        }
    }

    private void HandleGroupClick(ref SystemState state, SelectionSingleton sel,
                                   Entity bannerHolder, bool shift)
    {
        if (!shift) ClearSelection(ref state, sel);

        foreach (var (group, members) in
            SystemAPI.Query<RefRO<Group>, DynamicBuffer<GroupMember>>())
        {
            if (group.ValueRO.BannerHolder != bannerHolder) continue;

            if (SystemAPI.HasComponent<Selected>(bannerHolder))
                SelectEntity(ref state, sel, bannerHolder);

            for (int i = 0; i < members.Length; i++)
            {
                Entity m = members[i].Member;
                if (m != Entity.Null && m != bannerHolder && SystemAPI.HasComponent<Selected>(m))
                    SelectEntity(ref state, sel, m);
            }
            break;
        }
    }

    private void HandleBigGroupClick(ref SystemState state, SelectionSingleton sel,
                                      Entity bigBannerHolder, bool shift)
    {
        if (!shift) ClearSelection(ref state, sel);

        foreach (var (bigGroup, bigMembers) in
            SystemAPI.Query<RefRO<BigGroup>, DynamicBuffer<BigGroupMember>>())
        {
            if (bigGroup.ValueRO.BannerHolder != bigBannerHolder) continue;

            for (int gi = 0; gi < bigMembers.Length; gi++)
            {
                int subId = bigMembers[gi].Member.id;

                foreach (var (group, members) in
                    SystemAPI.Query<RefRO<Group>, DynamicBuffer<GroupMember>>())
                {
                    if (group.ValueRO.id != subId) continue;

                    Entity bh = group.ValueRO.BannerHolder;
                    if (bh != Entity.Null && SystemAPI.HasComponent<Selected>(bh))
                        SelectEntity(ref state, sel, bh);

                    for (int mi = 0; mi < members.Length; mi++)
                    {
                        Entity m = members[mi].Member;
                        if (m != Entity.Null && m != bh && SystemAPI.HasComponent<Selected>(m))
                            SelectEntity(ref state, sel, m);
                    }
                    break;
                }
            }
            break;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Select / Deselect / Clear  (non-structural — toggle only)
    // ─────────────────────────────────────────────────────────────────────────

    private void SelectEntity(ref SystemState state, SelectionSingleton sel, Entity e)
    {
        if (sel.Contains(e)) return;
        SystemAPI.SetComponentEnabled<Selected>(e, true);
        sel.Add(e);
        SetFeedback(ref state, e, true);
    }

    private void DeselectEntity(ref SystemState state, SelectionSingleton sel, Entity e)
    {
        SystemAPI.SetComponentEnabled<Selected>(e, false);
        sel.Remove(e);
        SetFeedback(ref state, e, false);
    }

    private void ClearSelection(ref SystemState state, SelectionSingleton sel)
    {
        for (int i = 0; i < sel.SelectedEntities.Length; i++)
        {
            Entity e = sel.SelectedEntities[i];
            if (!SystemAPI.Exists(e)) continue;
            SystemAPI.SetComponentEnabled<Selected>(e, false);
            SetFeedback(ref state, e, false);
        }
        sel.Clear();
    }

    private void SetFeedback(ref SystemState state, Entity e, bool active)
    {
        if (!SystemAPI.HasComponent<Selected>(e)) return;
        var comp = SystemAPI.GetComponent<Selected>(e);
        Entity fe = comp.FeedbackEntity;
        if (fe != Entity.Null && SystemAPI.HasComponent<SelectionFeedbackActive>(fe))
            SystemAPI.SetComponentEnabled<SelectionFeedbackActive>(fe, active);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Ray vs vertical cylinder  (no physics)
    //  Returns distance t >= 0 on hit, -1 on miss.
    // ─────────────────────────────────────────────────────────────────────────

    private static float RayVsCylinder(
        float3 rayOrigin, float3 rayDir,
        float3 centre, float radius, float height)
    {
        float2 ro = new float2(rayOrigin.x - centre.x, rayOrigin.z - centre.z);
        float2 rd = new float2(rayDir.x, rayDir.z);

        float a = math.dot(rd, rd);
        if (a < 1e-8f) return -1f;

        float b = 2f * math.dot(ro, rd);
        float c = math.dot(ro, ro) - radius * radius;
        float disc = b * b - 4f * a * c;
        if (disc < 0f) return -1f;

        float sq = math.sqrt(disc);
        float t = (-b - sq) / (2f * a);
        if (t < 0f) t = (-b + sq) / (2f * a);
        if (t < 0f) return -1f;

        float hitY = rayOrigin.y + rayDir.y * t;
        if (hitY < centre.y || hitY > centre.y + height) return -1f;

        return t;
    }
}