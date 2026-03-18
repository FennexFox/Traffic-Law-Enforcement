using System.Collections.Generic;
using Game;
using Game.Common;
using Game.Net;
using Game.Pathfind;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using Entity = Unity.Entities.Entity;
using PrefabRef = Game.Prefabs.PrefabRef;
using PrefabSystem = Game.Prefabs.PrefabSystem;

namespace Traffic_Law_Enforcement
{
    public class CenterlineAccessObsoleteSystem : GameSystemBase
    {
        private const int MaxStructureSampleLogs = 64;

        private EntityQuery m_VehicleQuery;
        private EntityQuery m_CurrentLaneChangedQuery;
        private EntityQuery m_NavigationLaneChangedQuery;
        private BufferLookup<CarNavigationLane> m_NavigationLaneData;
        private ComponentLookup<CarCurrentLane> m_CurrentLaneData;
        private ComponentLookup<PathOwner> m_PathOwnerData;
        private ComponentLookup<Owner> m_OwnerData;
        private ComponentLookup<PrefabRef> m_PrefabRefData;
        private ComponentLookup<Car> m_CarData;
        private ComponentLookup<CenterlineAccessObsoleteState> m_ObsoleteStateData;
        private ComponentLookup<CarLane> m_CarLaneData;
        private ComponentLookup<EdgeLane> m_EdgeLaneData;
        private ComponentLookup<ParkingLane> m_ParkingLaneData;
        private ComponentLookup<GarageLane> m_GarageLaneData;
        private ComponentLookup<ConnectionLane> m_ConnectionLaneData;
        private readonly HashSet<Entity> m_CandidateVehicles = new HashSet<Entity>();
        private readonly HashSet<string> m_StructureSampleSignatures = new HashSet<string>();
        private PrefabSystem m_PrefabSystem;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_VehicleQuery = GetEntityQuery(
                ComponentType.ReadOnly<Car>(),
                ComponentType.ReadOnly<CarCurrentLane>(),
                ComponentType.ReadOnly<PathOwner>(),
                ComponentType.ReadOnly<CarNavigationLane>());
            m_CurrentLaneChangedQuery = GetEntityQuery(
                ComponentType.ReadOnly<Car>(),
                ComponentType.ReadOnly<CarCurrentLane>(),
                ComponentType.ReadOnly<PathOwner>(),
                ComponentType.ReadOnly<CarNavigationLane>());
            m_CurrentLaneChangedQuery.SetChangedVersionFilter(ComponentType.ReadOnly<CarCurrentLane>());
            m_NavigationLaneChangedQuery = GetEntityQuery(
                ComponentType.ReadOnly<Car>(),
                ComponentType.ReadOnly<CarCurrentLane>(),
                ComponentType.ReadOnly<PathOwner>(),
                ComponentType.ReadOnly<CarNavigationLane>());
            m_NavigationLaneChangedQuery.SetChangedVersionFilter(ComponentType.ReadOnly<CarNavigationLane>());
            m_NavigationLaneData = GetBufferLookup<CarNavigationLane>(true);
            m_CurrentLaneData = GetComponentLookup<CarCurrentLane>(true);
            m_PathOwnerData = GetComponentLookup<PathOwner>(true);
            m_OwnerData = GetComponentLookup<Owner>(true);
            m_PrefabRefData = GetComponentLookup<PrefabRef>(true);
            m_CarData = GetComponentLookup<Car>(true);
            m_ObsoleteStateData = GetComponentLookup<CenterlineAccessObsoleteState>();
            m_CarLaneData = GetComponentLookup<CarLane>(true);
            m_EdgeLaneData = GetComponentLookup<EdgeLane>(true);
            m_ParkingLaneData = GetComponentLookup<ParkingLane>(true);
            m_GarageLaneData = GetComponentLookup<GarageLane>(true);
            m_ConnectionLaneData = GetComponentLookup<ConnectionLane>(true);
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            RequireForUpdate(m_VehicleQuery);
        }

        protected override void OnUpdate()
        {
            if (!Mod.IsEnforcementEnabled)
            {
                return;
            }

            m_NavigationLaneData.Update(this);
            m_CurrentLaneData.Update(this);
            m_PathOwnerData.Update(this);
            m_OwnerData.Update(this);
            m_PrefabRefData.Update(this);
            m_CarData.Update(this);
            m_ObsoleteStateData.Update(this);
            m_CarLaneData.Update(this);
            m_EdgeLaneData.Update(this);
            m_ParkingLaneData.Update(this);
            m_GarageLaneData.Update(this);
            m_ConnectionLaneData.Update(this);

            m_CandidateVehicles.Clear();
            CollectCandidateVehicles(m_CurrentLaneChangedQuery);
            CollectCandidateVehicles(m_NavigationLaneChangedQuery);

            try
            {
                foreach (Entity vehicle in m_CandidateVehicles)
                {
                    if (!m_CurrentLaneData.TryGetComponent(vehicle, out CarCurrentLane currentLane) ||
                        !m_PathOwnerData.TryGetComponent(vehicle, out PathOwner pathOwner) ||
                        !m_NavigationLaneData.TryGetBuffer(vehicle, out DynamicBuffer<CarNavigationLane> navigationLanes))
                    {
                        continue;
                    }

                    if (EmergencyVehiclePolicy.IsEmergencyVehicle(m_CarData[vehicle]))
                    {
                        continue;
                    }

                    ResetDuplicateSuppressionIfPathChanged(vehicle, pathOwner);

                    if ((pathOwner.m_State & (PathFlags.Pending | PathFlags.Obsolete)) != 0)
                    {
                        continue;
                    }

                    if (navigationLanes.Length == 0)
                    {
                        continue;
                    }

                    if (!TryGetIllegalPlannedAccessTransition(currentLane.m_Lane, navigationLanes, out Entity sourceLane, out Entity targetLane, out int transitionIndex, out string transitionKind, out string reason))
                    {
                        continue;
                    }

                    if (ShouldSuppressDuplicateInvalidation(vehicle, currentLane.m_Lane, sourceLane, targetLane, transitionIndex))
                    {
                        continue;
                    }

                    pathOwner.m_State |= PathFlags.Obsolete;
                    EntityManager.SetComponentData(vehicle, pathOwner);
                    RecordInvalidationSnapshot(vehicle, currentLane.m_Lane, sourceLane, targetLane, transitionIndex);

                    if (EnforcementLoggingPolicy.ShouldLogEnforcementEvents())
                    {
                        Mod.log.Info($"Planned center-line access route invalidated: vehicle={vehicle}, fromLane={sourceLane}, toLane={targetLane}, accessIndex={transitionIndex}, transition={transitionKind}, reason={reason}");
                        LogStructureSample(vehicle, currentLane.m_Lane, sourceLane, targetLane, transitionIndex, transitionKind, reason);
                    }
                }
            }
            finally
            {
            }
        }

        private void CollectCandidateVehicles(EntityQuery query)
        {
            NativeArray<Entity> vehicles = query.ToEntityArray(Allocator.Temp);
            try
            {
                for (int index = 0; index < vehicles.Length; index++)
                {
                    m_CandidateVehicles.Add(vehicles[index]);
                }
            }
            finally
            {
                vehicles.Dispose();
            }
        }

        private bool TryGetIllegalPlannedAccessTransition(Entity currentLane, DynamicBuffer<CarNavigationLane> navigationLanes, out Entity sourceLane, out Entity targetLane, out int transitionIndex, out string transitionKind, out string reason)
        {
            sourceLane = currentLane;
            targetLane = Entity.Null;
            transitionIndex = -1;
            transitionKind = null;
            reason = null;

            for (int index = 0; index < navigationLanes.Length; index++)
            {
                Entity nextLane = navigationLanes[index].m_Lane;
                if (nextLane == Entity.Null || nextLane == sourceLane)
                {
                    continue;
                }

                if (!IsAccessTransition(sourceLane, nextLane))
                {
                    sourceLane = nextLane;
                    continue;
                }

                if (IsIllegalIngress(sourceLane, nextLane, out reason) || IsIllegalEgress(sourceLane, nextLane, out reason))
                {
                    targetLane = nextLane;
                    transitionIndex = index;
                    transitionKind = DescribeTransitionKind(sourceLane, nextLane);
                    return true;
                }

                return false;
            }

            return false;
        }

        private void LogStructureSample(Entity vehicle, Entity currentLane, Entity sourceLane, Entity targetLane, int transitionIndex, string transitionKind, string reason)
        {
            if (m_StructureSampleSignatures.Count >= MaxStructureSampleLogs)
            {
                return;
            }

            string currentLaneShape = DescribeLaneShape(currentLane);
            string sourceLaneShape = DescribeLaneShape(sourceLane);
            string targetLaneShape = DescribeLaneShape(targetLane);
            string currentOwnerChain = DescribeOwnerChain(currentLane);
            string sourceOwnerChain = DescribeOwnerChain(sourceLane);
            string targetOwnerChain = DescribeOwnerChain(targetLane);
            string signature = $"{transitionKind}|{currentLaneShape}|{sourceLaneShape}|{targetLaneShape}|{currentOwnerChain}|{sourceOwnerChain}|{targetOwnerChain}";
            if (!m_StructureSampleSignatures.Add(signature))
            {
                return;
            }

            Mod.log.Info(
                $"Centerline access structure sample: vehicle={vehicle}, accessIndex={transitionIndex}, transition={transitionKind}, reason={reason}, " +
                $"currentLane={currentLane}, currentShape={currentLaneShape}, currentOwnerChain={currentOwnerChain}, " +
                $"sourceLane={sourceLane}, sourceShape={sourceLaneShape}, sourceOwnerChain={sourceOwnerChain}, " +
                $"targetLane={targetLane}, targetShape={targetLaneShape}, targetOwnerChain={targetOwnerChain}");
        }

        private string DescribeLaneShape(Entity lane)
        {
            if (lane == Entity.Null)
            {
                return "null";
            }

            List<string> parts = new List<string>(6);
            if (m_EdgeLaneData.HasComponent(lane))
            {
                parts.Add("edge");
            }

            if (m_CarLaneData.TryGetComponent(lane, out CarLane carLane))
            {
                parts.Add($"car(flags={carLane.m_Flags})");
            }

            if (m_ConnectionLaneData.TryGetComponent(lane, out ConnectionLane connectionLane))
            {
                parts.Add($"connection(flags={connectionLane.m_Flags})");
            }

            if (m_ParkingLaneData.HasComponent(lane))
            {
                parts.Add("parking");
            }

            if (m_GarageLaneData.HasComponent(lane))
            {
                parts.Add("garage");
            }

            string prefabName = TryGetPrefabName(lane);
            if (prefabName != null)
            {
                parts.Add($"prefab={prefabName}");
            }

            return parts.Count == 0 ? "other" : string.Join(", ", parts);
        }

        private string DescribeOwnerChain(Entity entity)
        {
            if (entity == Entity.Null)
            {
                return "null";
            }

            List<string> parts = new List<string>(8);
            Entity current = entity;
            for (int depth = 0; depth < 8 && current != Entity.Null; depth += 1)
            {
                string prefabName = TryGetPrefabName(current);
                bool roadBuilderPrefab = IsRoadBuilderPrefabName(prefabName);
                parts.Add(prefabName != null
                    ? $"{current}[prefab={prefabName}, roadBuilder={roadBuilderPrefab}]"
                    : current.ToString());

                if (!m_OwnerData.TryGetComponent(current, out Owner owner) || owner.m_Owner == Entity.Null || owner.m_Owner == current)
                {
                    break;
                }

                current = owner.m_Owner;
            }

            return string.Join(" -> ", parts);
        }

        private string TryGetPrefabName(Entity entity)
        {
            if (!m_PrefabRefData.TryGetComponent(entity, out PrefabRef prefabRef) || prefabRef.m_Prefab == Entity.Null)
            {
                return null;
            }

            return m_PrefabSystem != null
                ? m_PrefabSystem.GetPrefabName(prefabRef.m_Prefab)
                : prefabRef.m_Prefab.ToString();
        }

        private static bool IsRoadBuilderPrefabName(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName))
            {
                return false;
            }

            return prefabName.IndexOf("Road Builder", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                prefabName.IndexOf("RB ", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                prefabName.IndexOf("Made with Road Builder", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void ResetDuplicateSuppressionIfPathChanged(Entity vehicle, PathOwner pathOwner)
        {
            if (!m_ObsoleteStateData.TryGetComponent(vehicle, out CenterlineAccessObsoleteState state) || state.m_AwaitingPathRefresh == 0)
            {
                return;
            }

            if ((pathOwner.m_State & (PathFlags.Pending | PathFlags.Updated)) == 0)
            {
                return;
            }

            state.m_AwaitingPathRefresh = 0;
            m_ObsoleteStateData[vehicle] = state;
        }

        private bool ShouldSuppressDuplicateInvalidation(Entity vehicle, Entity currentLane, Entity sourceLane, Entity targetLane, int transitionIndex)
        {
            if (!m_ObsoleteStateData.TryGetComponent(vehicle, out CenterlineAccessObsoleteState state) || state.m_AwaitingPathRefresh == 0)
            {
                return false;
            }

            bool sameSnapshot = state.m_LastCurrentLane == currentLane &&
                state.m_LastSourceLane == sourceLane &&
                state.m_LastTargetLane == targetLane &&
                state.m_LastAccessIndex == transitionIndex;

            if (sameSnapshot)
            {
                return true;
            }

            state.m_AwaitingPathRefresh = 0;
            m_ObsoleteStateData[vehicle] = state;
            return false;
        }

        private void RecordInvalidationSnapshot(Entity vehicle, Entity currentLane, Entity sourceLane, Entity targetLane, int transitionIndex)
        {
            CenterlineAccessObsoleteState state = m_ObsoleteStateData.TryGetComponent(vehicle, out CenterlineAccessObsoleteState existingState)
                ? existingState
                : default;

            state.m_LastCurrentLane = currentLane;
            state.m_LastSourceLane = sourceLane;
            state.m_LastTargetLane = targetLane;
            state.m_LastAccessIndex = transitionIndex;
            state.m_AwaitingPathRefresh = 1;

            if (m_ObsoleteStateData.HasComponent(vehicle))
            {
                m_ObsoleteStateData[vehicle] = state;
            }
            else
            {
                EntityManager.AddComponentData(vehicle, state);
            }
        }

        private bool IsAccessTransition(Entity sourceLane, Entity targetLane)
        {
            return IsAccessOrigin(sourceLane) || IsAccessTarget(targetLane);
        }

        private string DescribeTransitionKind(Entity sourceLane, Entity targetLane)
        {
            if (IsAccessOrigin(sourceLane))
            {
                return $"egress:{DescribeAccessOrigin(sourceLane)}";
            }

            if (m_ParkingLaneData.HasComponent(targetLane))
            {
                return "ingress:parking-lane";
            }

            if (m_GarageLaneData.HasComponent(targetLane))
            {
                return "ingress:garage-lane";
            }

            if (!m_ConnectionLaneData.TryGetComponent(targetLane, out ConnectionLane connectionLane))
            {
                return "ingress:other";
            }

            if ((connectionLane.m_Flags & ConnectionLaneFlags.Parking) != 0)
            {
                return "ingress:parking-connection";
            }

            if ((connectionLane.m_Flags & ConnectionLaneFlags.Road) == 0)
            {
                return "ingress:building-service-access-connection";
            }

            return "ingress:road-connection";
        }

        private bool IsAccessTarget(Entity lane)
        {
            if (m_ParkingLaneData.HasComponent(lane) || m_GarageLaneData.HasComponent(lane))
            {
                return true;
            }

            if (!m_ConnectionLaneData.TryGetComponent(lane, out ConnectionLane connectionLane))
            {
                return false;
            }

            bool parkingAccess = (connectionLane.m_Flags & ConnectionLaneFlags.Parking) != 0;
            bool roadConnection = (connectionLane.m_Flags & ConnectionLaneFlags.Road) != 0;
            return parkingAccess || !roadConnection;
        }

        private bool IsIllegalIngress(Entity sourceLane, Entity targetLane, out string reason)
        {
            reason = null;
            if (!TryGetRoadCarLane(sourceLane, out CarLane sourceCarLane) || LaneAllowsSideAccess(sourceCarLane))
            {
                return false;
            }

            if (m_ParkingLaneData.HasComponent(targetLane))
            {
                reason = "planned parking-access ingress from a lane without side-access permission";
                return true;
            }

            if (m_GarageLaneData.HasComponent(targetLane))
            {
                reason = "planned garage-access ingress from a lane without side-access permission";
                return true;
            }

            if (!m_ConnectionLaneData.TryGetComponent(targetLane, out ConnectionLane connectionLane))
            {
                return false;
            }

            if ((connectionLane.m_Flags & ConnectionLaneFlags.Parking) != 0)
            {
                reason = "planned parking-connection ingress from a lane without side-access permission";
                return true;
            }

            if ((connectionLane.m_Flags & ConnectionLaneFlags.Road) == 0)
            {
                reason = "planned building/service access ingress from a lane without side-access permission";
                return true;
            }

            return false;
        }

        private bool IsIllegalEgress(Entity sourceLane, Entity targetLane, out string reason)
        {
            reason = null;
            if (!IsAccessOrigin(sourceLane) || !TryGetRoadCarLane(targetLane, out CarLane targetCarLane) || LaneAllowsSideAccess(targetCarLane))
            {
                return false;
            }

            reason = $"planned illegal egress from {DescribeAccessOrigin(sourceLane)} into a lane without side-access permission";
            return true;
        }

        private bool IsAccessOrigin(Entity lane)
        {
            if (m_ParkingLaneData.HasComponent(lane) || m_GarageLaneData.HasComponent(lane))
            {
                return true;
            }

            if (!m_ConnectionLaneData.TryGetComponent(lane, out ConnectionLane connectionLane))
            {
                return false;
            }

            bool parkingAccess = (connectionLane.m_Flags & ConnectionLaneFlags.Parking) != 0;
            bool roadConnection = (connectionLane.m_Flags & ConnectionLaneFlags.Road) != 0;
            return parkingAccess || !roadConnection;
        }

        private string DescribeAccessOrigin(Entity lane)
        {
            if (m_ParkingLaneData.HasComponent(lane))
            {
                return "parking access";
            }

            if (m_GarageLaneData.HasComponent(lane))
            {
                return "garage access";
            }

            if (!m_ConnectionLaneData.TryGetComponent(lane, out ConnectionLane connectionLane))
            {
                return "building access";
            }

            if ((connectionLane.m_Flags & ConnectionLaneFlags.Parking) != 0)
            {
                return "parking connection";
            }

            if ((connectionLane.m_Flags & ConnectionLaneFlags.Road) == 0)
            {
                return "building/service access connection";
            }

            return "building access";
        }

        private bool TryGetRoadCarLane(Entity lane, out CarLane carLane)
        {
            if (m_EdgeLaneData.HasComponent(lane) && m_CarLaneData.TryGetComponent(lane, out carLane))
            {
                return true;
            }

            carLane = default;
            return false;
        }

        private static bool LaneAllowsSideAccess(CarLane lane)
        {
            return (lane.m_Flags & (Game.Net.CarLaneFlags.SideConnection | Game.Net.CarLaneFlags.ParkingLeft | Game.Net.CarLaneFlags.ParkingRight)) != 0;
        }
    }
}