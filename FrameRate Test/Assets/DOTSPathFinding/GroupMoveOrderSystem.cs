using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

[WorldSystemFilter(WorldSystemFilterFlags.Default)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateBefore(typeof(PathRequestSystem))]
public partial struct GroupMoveOrderSystem : ISystem
{
    private const float FormationSpacing = 1.5f;
    private const float BigGroupSpacing = 9f;

    public void OnCreate(ref SystemState state) { }
    public void OnDestroy(ref SystemState state) { }

    public void OnUpdate(ref SystemState state)
    {
        // ── Group ─────────────────────────────────────────────────────────────

        foreach (var (order, group, members, groupEntity) in
            SystemAPI.Query<RefRO<GroupMoveOrder>, RefRW<Group>, DynamicBuffer<GroupMember>>()
                     .WithAll<GroupMoveOrder>()       // only enabled GroupMoveOrders
                     .WithEntityAccess())
        {
            float3 dest = order.ValueRO.Destination;
            Entity leader = group.ValueRO.BannerHolder;

            if (leader != Entity.Null && SystemAPI.HasComponent<NavAgent>(leader))
            {
                var la = SystemAPI.GetComponent<NavAgent>(leader);
                la.FormationOffset = float3.zero; la.Destination = dest;
                la.Status = NavAgentStatus.Requesting; la.CurrentPathIndex = 0;
                SystemAPI.SetComponent(leader, la);

                // Enable PathRequest on the leader (non-structural)
                var pr = SystemAPI.GetComponent<PathRequest>(leader);
                pr.Start = GetPos(ref state, leader);
                pr.End = dest;
                pr.RequestId = UnityEngine.Time.frameCount;
                SystemAPI.SetComponent(leader, pr);
                SystemAPI.SetComponentEnabled<PathRequest>(leader, true);
            }

            int cols = (int)math.ceil(math.sqrt(math.max(1, members.Length)));
            for (int i = 0; i < members.Length; i++)
            {
                Entity m = members[i].Member;
                if (m == Entity.Null || m == leader || !SystemAPI.HasComponent<NavAgent>(m)) continue;

                float3 offset = FormationOffset(i, cols, FormationSpacing);
                var ma = SystemAPI.GetComponent<NavAgent>(m);
                ma.FormationOffset = offset; ma.Destination = dest;
                ma.Status = NavAgentStatus.Requesting; ma.CurrentPathIndex = 0;
                SystemAPI.SetComponent(m, ma);

                // Enable PathRequest on each member (non-structural)
                var pr = SystemAPI.GetComponent<PathRequest>(m);
                pr.Start = GetPos(ref state, m);
                pr.End = dest + offset;
                pr.RequestId = UnityEngine.Time.frameCount;
                SystemAPI.SetComponent(m, pr);
                SystemAPI.SetComponentEnabled<PathRequest>(m, true);
            }

            group.ValueRW.GroupState = UnitState.Moving;

            // Disable GroupMoveOrder instead of removing it (non-structural)
            SystemAPI.SetComponentEnabled<GroupMoveOrder>(groupEntity, false);
        }

        // ── BigGroup ──────────────────────────────────────────────────────────

        foreach (var (bigOrder, _, bigMembers, bigEntity) in
            SystemAPI.Query<RefRO<BigGroupMoveOrder>, RefRO<BigGroup>, DynamicBuffer<BigGroupMember>>()
                     .WithAll<BigGroupMoveOrder>()    // only enabled BigGroupMoveOrders
                     .WithEntityAccess())
        {
            float3 dest = bigOrder.ValueRO.Destination;
            int cols = (int)math.ceil(math.sqrt(math.max(1, bigMembers.Length)));

            for (int i = 0; i < bigMembers.Length; i++)
            {
                int id = bigMembers[i].Member.id;
                float3 subDest = dest + FormationOffset(i, cols, BigGroupSpacing);

                foreach (var (grp, grpEntity) in SystemAPI.Query<RefRO<Group>>().WithEntityAccess())
                {
                    if (grp.ValueRO.id != id) continue;

                    // Enable GroupMoveOrder on sub-group (non-structural)
                    var gmo = SystemAPI.GetComponent<GroupMoveOrder>(grpEntity);
                    gmo.Destination = subDest;
                    SystemAPI.SetComponent(grpEntity, gmo);
                    SystemAPI.SetComponentEnabled<GroupMoveOrder>(grpEntity, true);
                    break;
                }
            }

            // Disable BigGroupMoveOrder instead of removing it (non-structural)
            SystemAPI.SetComponentEnabled<BigGroupMoveOrder>(bigEntity, false);
        }
    }

    private static float3 FormationOffset(int i, int cols, float spacing)
        => new float3((i % cols - cols * 0.5f + 0.5f) * spacing, 0f, -(i / cols + 1) * spacing);

    private float3 GetPos(ref SystemState s, Entity e)
        => SystemAPI.HasComponent<LocalTransform>(e)
           ? SystemAPI.GetComponent<LocalTransform>(e).Position
           : float3.zero;
}