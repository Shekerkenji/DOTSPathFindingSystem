using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;


[WorldSystemFilter(WorldSystemFilterFlags.Default)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
[BurstCompile]
public partial struct NavAgentMoveSystem : ISystem
{
    [BurstCompile] public void OnCreate(ref SystemState state) { }
    [BurstCompile] public void OnDestroy(ref SystemState state) { }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        float dt = SystemAPI.Time.DeltaTime;

        // ── Consume PathReady (non-structural: just disable the flag) ──────────
        foreach (var (agent, pathReady) in
            SystemAPI.Query<RefRW<NavAgent>, EnabledRefRW<PathReady>>()
                     .WithAll<PathReady>())
        {
            agent.ValueRW.Status = NavAgentStatus.Moving;
            agent.ValueRW.CurrentPathIndex = 0;
            pathReady.ValueRW = false;   // disable — no structural change
        }

        // ── Consume PathFailed (non-structural: just disable the flag) ─────────
        foreach (var (agent, pathFailed) in
            SystemAPI.Query<RefRW<NavAgent>, EnabledRefRW<PathFailed>>()
                     .WithAll<PathFailed>())
        {
            agent.ValueRW.Status = NavAgentStatus.PathFailed;
            pathFailed.ValueRW = false;  // disable — no structural change
        }

        // ── Move agents ───────────────────────────────────────────────────────
        foreach (var (transform, agent, waypoints) in
            SystemAPI.Query<RefRW<LocalTransform>, RefRW<NavAgent>, DynamicBuffer<PathWaypoint>>())
        {
            if (agent.ValueRO.Status != NavAgentStatus.Moving) continue;

            int idx = agent.ValueRO.CurrentPathIndex;
            if (idx >= waypoints.Length) { agent.ValueRW.Status = NavAgentStatus.Arrived; continue; }

            float3 target = waypoints[idx].Position + agent.ValueRO.FormationOffset;
            float3 pos = transform.ValueRO.Position;
            float3 delta = target - pos;
            float dist = math.length(delta);

            if (dist <= agent.ValueRO.StoppingDistance || dist < 0.001f)
            {
                agent.ValueRW.CurrentPathIndex++;
                if (agent.ValueRO.CurrentPathIndex >= waypoints.Length)
                    agent.ValueRW.Status = NavAgentStatus.Arrived;
            }
            else
            {
                float step = agent.ValueRO.MoveSpeed * dt;
                transform.ValueRW.Position = pos + (delta / dist) * math.min(step, dist);
                transform.ValueRW.Rotation = quaternion.RotateY(math.atan2(delta.x, delta.z));
            }
        }
    }
}