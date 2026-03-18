using System;
using System.Reflection;
using Game;
using Game.Net;
using Game.Pathfind;
using Game.Vehicles;
using HarmonyLib;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace Traffic_Law_Enforcement
{
    internal static class VehicleUtilsPatches
    {
        private const string HarmonyId = "Traffic_Law_Enforcement.VehicleUtilsPatches";
        private const int ValidateParkingTraceLogLimit = 96;
        private static readonly Type s_PathfindExecutorType = AccessTools.Inner(typeof(PathfindJobs), "PathfindExecutor");
        private static readonly MethodInfo s_SetupPathfindMethod = AccessTools.Method(
            typeof(VehicleUtils),
            nameof(VehicleUtils.SetupPathfind),
            new[]
            {
                typeof(CarCurrentLane).MakeByRefType(),
                typeof(PathOwner).MakeByRefType(),
                typeof(NativeQueue<SetupQueueItem>.ParallelWriter),
                typeof(SetupQueueItem)
            });
        private static readonly MethodInfo s_CalculateCostMethod = AccessTools.FirstMethod(
            s_PathfindExecutorType,
            method => method.Name == "CalculateCost" && method.ReturnType == typeof(float) && method.GetParameters().Length == 4);
        private static readonly MethodInfo s_ValidateParkingSpaceMethod = AccessTools.FirstMethod(
            typeof(VehicleUtils),
            method => method.Name == nameof(VehicleUtils.ValidateParkingSpace) && method.ReturnType == typeof(Entity));

        private static Harmony s_Harmony;
    private static int s_ValidateParkingTraceLogCount;

        public static void Apply()
        {
            if (s_Harmony != null)
            {
                return;
            }

            try
            {
                s_Harmony = new Harmony(HarmonyId);
                HarmonyMethod prefix = new HarmonyMethod(typeof(VehicleUtilsPatches), nameof(SetupPathfindPrefix));
                s_Harmony.Patch(s_SetupPathfindMethod, prefix: prefix);

                HarmonyMethod calculateCostPostfix = new HarmonyMethod(typeof(VehicleUtilsPatches), nameof(CalculateCostPostfix));
                s_Harmony.Patch(s_CalculateCostMethod, postfix: calculateCostPostfix);

                HarmonyMethod validateParkingSpacePostfix = new HarmonyMethod(typeof(VehicleUtilsPatches), nameof(ValidateParkingSpacePostfix));
                s_Harmony.Patch(s_ValidateParkingSpaceMethod, postfix: validateParkingSpacePostfix);

                Mod.log.Info("VehicleUtils pathfinding patches applied.");
            }
            catch (Exception ex)
            {
                s_Harmony = null;
                Mod.log.Error(ex, "Failed to apply VehicleUtils pathfinding patches.");
            }
        }

        public static void Remove()
        {
            if (s_Harmony == null)
            {
                return;
            }

            s_Harmony.UnpatchAll(HarmonyId);
            s_Harmony = null;
            Mod.log.Info("VehicleUtils pathfinding patches removed.");
        }

        private static void SetupPathfindPrefix(ref SetupQueueItem item)
        {
            if (!Mod.IsEnforcementEnabled)
            {
                return;
            }

            World world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated)
            {
                return;
            }

            EntityManager entityManager = world.EntityManager;
            Entity owner = item.m_Owner;
            if (!entityManager.Exists(owner) || !entityManager.HasComponent<Car>(owner))
            {
                return;
            }

            Car car = entityManager.GetComponentData<Car>(owner);
            SyncPrivateTrafficIgnoredRules(world, owner, car, ref item);

            if (!EmergencyVehiclePolicy.IsEmergencyVehicle(car))
            {
                return;
            }

            item.m_Parameters.m_Weights.m_Value.z = 0f;
        }

        private static void CalculateCostPostfix(ref float __result, RuleFlags rules, float2 delta, PathfindParameters ___m_Parameters)
        {
            if (!Mod.IsEnforcementEnabled || (rules & RuleFlags.ForbidPrivateTraffic) == 0)
            {
                return;
            }

            float moneyWeight = ___m_Parameters.m_Weights.money;
            if (moneyWeight <= 0f)
            {
                return;
            }

            int publicTransportPenalty = EnforcementPenaltyService.GetPublicTransportLaneFine();
            if (publicTransportPenalty <= 0)
            {
                return;
            }

            __result += publicTransportPenalty * moneyWeight * math.abs(delta.y - delta.x);
        }

        private static void ValidateParkingSpacePostfix(Entity entity, ref CarCurrentLane currentLane, ref PathOwner pathOwner, DynamicBuffer<CarNavigationLane> navigationLanes, Entity __result)
        {
            if (!Mod.IsEnforcementEnabled || __result == Entity.Null || (pathOwner.m_State & PathFlags.Obsolete) != 0)
            {
                return;
            }

            World world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated)
            {
                return;
            }

            EntityManager entityManager = world.EntityManager;
            bool hasPlannedParkingAccess = TryGetFirstPlannedParkingAccess(
                entityManager,
                currentLane.m_Lane,
                navigationLanes,
                out Entity sourceLane,
                out Entity targetLane,
                out string targetKind,
                out bool sourceIsRoadCarLane,
                out bool sourceAllowsSideAccess);

            if (hasPlannedParkingAccess && IsIllegalParkingAccessTransition(entityManager, sourceLane, targetLane, out string reason))
            {
                TraceValidateParkingDecision(entity, currentLane.m_Lane, navigationLanes.Length, sourceLane, targetLane, targetKind, sourceIsRoadCarLane, sourceAllowsSideAccess, "obsolete", reason);
                pathOwner.m_State |= PathFlags.Obsolete;
                EnforcementLoggingPolicy.RecordEnforcementEvent($"Parking access route invalidated: vehicle={entity}, fromLane={sourceLane}, toLane={targetLane}, reason={reason}");
                return;
            }

            if (!ShouldTraceValidateParkingDecisions())
            {
                return;
            }

            if (hasPlannedParkingAccess)
            {
                string skipReason = !sourceIsRoadCarLane
                    ? "source-not-road-car-lane"
                    : (sourceAllowsSideAccess ? "source-allows-side-access" : "target-not-illegal-parking-access");
                TraceValidateParkingDecision(entity, currentLane.m_Lane, navigationLanes.Length, sourceLane, targetLane, targetKind, sourceIsRoadCarLane, sourceAllowsSideAccess, "skip", skipReason);
                return;
            }

            TraceValidateParkingDecision(entity, currentLane.m_Lane, navigationLanes.Length, Entity.Null, Entity.Null, "none", sourceIsRoadCarLane: false, sourceAllowsSideAccess: false, "skip", "no-planned-parking-target");
        }

        private static void SyncPrivateTrafficIgnoredRules(World world, Entity owner, Car car, ref SetupQueueItem item)
        {
            PathfindingMoneyPenaltySystem system = world.GetExistingSystemManaged<PathfindingMoneyPenaltySystem>();
            if (system == null)
            {
                return;
            }

            BusLaneVehicleTypeLookups typeLookups = BusLaneVehicleTypeLookups.Create(system);
            typeLookups.Update(system);

            if (!BusLanePolicy.TryGetDesiredPermissionState(owner, car, EnforcementGameplaySettingsService.Current, ref typeLookups, out bool shouldTrack, out CarFlags desiredMask) || !shouldTrack)
            {
                return;
            }

            bool allowOnPublicTransportLane = (desiredMask & CarFlags.UsePublicTransportLanes) != 0;
            SetRuleFlag(ref item.m_Parameters.m_IgnoredRules, RuleFlags.ForbidPrivateTraffic, allowOnPublicTransportLane);
            SetRuleFlag(ref item.m_Parameters.m_TaxiIgnoredRules, RuleFlags.ForbidPrivateTraffic, allowOnPublicTransportLane);
        }

        private static bool TryGetFirstPlannedParkingAccess(EntityManager entityManager, Entity currentLane, DynamicBuffer<CarNavigationLane> navigationLanes, out Entity sourceLane, out Entity targetLane, out string targetKind, out bool sourceIsRoadCarLane, out bool sourceAllowsSideAccess)
        {
            sourceLane = currentLane;
            targetLane = Entity.Null;
            targetKind = "none";
            sourceIsRoadCarLane = entityManager.HasComponent<Game.Net.EdgeLane>(currentLane) && entityManager.HasComponent<Game.Net.CarLane>(currentLane);
            sourceAllowsSideAccess = sourceIsRoadCarLane && LaneAllowsSideAccess(entityManager.GetComponentData<Game.Net.CarLane>(currentLane));

            for (int index = 0; index < navigationLanes.Length; index++)
            {
                CarNavigationLane navigationLane = navigationLanes[index];
                if (navigationLane.m_Lane == Entity.Null)
                {
                    continue;
                }

                if ((navigationLane.m_Flags & Game.Vehicles.CarLaneFlags.ParkingSpace) == 0)
                {
                    sourceLane = navigationLane.m_Lane;
                    sourceIsRoadCarLane = entityManager.HasComponent<Game.Net.EdgeLane>(sourceLane) && entityManager.HasComponent<Game.Net.CarLane>(sourceLane);
                    sourceAllowsSideAccess = sourceIsRoadCarLane && LaneAllowsSideAccess(entityManager.GetComponentData<Game.Net.CarLane>(sourceLane));
                    continue;
                }

                targetLane = navigationLane.m_Lane;
                targetKind = DescribePlannedParkingTarget(entityManager, targetLane);
                return true;
            }

            return false;
        }

        private static bool IsIllegalParkingAccessTransition(EntityManager entityManager, Entity sourceLane, Entity targetLane, out string reason)
        {
            reason = null;

            if (sourceLane == Entity.Null || targetLane == Entity.Null)
            {
                return false;
            }

            if (!entityManager.HasComponent<Game.Net.EdgeLane>(sourceLane) || !entityManager.HasComponent<Game.Net.CarLane>(sourceLane))
            {
                return false;
            }

            Game.Net.CarLane sourceCarLane = entityManager.GetComponentData<Game.Net.CarLane>(sourceLane);
            if (LaneAllowsSideAccess(sourceCarLane))
            {
                return false;
            }

            if (entityManager.HasComponent<Game.Net.ParkingLane>(targetLane))
            {
                reason = "planned parking-access ingress from a lane without side-access permission";
                return true;
            }

            if (entityManager.HasComponent<Game.Net.GarageLane>(targetLane))
            {
                reason = "planned garage-access ingress from a lane without side-access permission";
                return true;
            }

            if (!entityManager.HasComponent<Game.Net.ConnectionLane>(targetLane))
            {
                return false;
            }

            Game.Net.ConnectionLane connectionLane = entityManager.GetComponentData<Game.Net.ConnectionLane>(targetLane);
            if ((connectionLane.m_Flags & Game.Net.ConnectionLaneFlags.Parking) == 0)
            {
                return false;
            }

            reason = "planned parking-connection ingress from a lane without side-access permission";
            return true;
        }

        private static bool LaneAllowsSideAccess(Game.Net.CarLane lane)
        {
            return (lane.m_Flags & (Game.Net.CarLaneFlags.SideConnection | Game.Net.CarLaneFlags.ParkingLeft | Game.Net.CarLaneFlags.ParkingRight)) != 0;
        }

        private static string DescribePlannedParkingTarget(EntityManager entityManager, Entity lane)
        {
            if (lane == Entity.Null)
            {
                return "none";
            }

            if (entityManager.HasComponent<Game.Net.ParkingLane>(lane))
            {
                return "parking-lane";
            }

            if (entityManager.HasComponent<Game.Net.GarageLane>(lane))
            {
                return "garage-lane";
            }

            if (!entityManager.HasComponent<Game.Net.ConnectionLane>(lane))
            {
                return "other";
            }

            Game.Net.ConnectionLane connectionLane = entityManager.GetComponentData<Game.Net.ConnectionLane>(lane);
            bool parking = (connectionLane.m_Flags & Game.Net.ConnectionLaneFlags.Parking) != 0;
            bool road = (connectionLane.m_Flags & Game.Net.ConnectionLaneFlags.Road) != 0;
            if (parking)
            {
                return "parking-connection";
            }

            if (!road)
            {
                return "building-service-access-connection";
            }

            return "road-connection";
        }

        private static bool ShouldTraceValidateParkingDecisions()
        {
            return EnforcementLoggingPolicy.ShouldLogEnforcementEvents() || EnforcementLoggingPolicy.ShouldLogPathfindingPenaltyDiagnostics();
        }

        private static void TraceValidateParkingDecision(Entity vehicle, Entity currentLane, int navigationLaneCount, Entity sourceLane, Entity targetLane, string targetKind, bool sourceIsRoadCarLane, bool sourceAllowsSideAccess, string decision, string reason)
        {
            if (!ShouldTraceValidateParkingDecisions() || s_ValidateParkingTraceLogCount >= ValidateParkingTraceLogLimit)
            {
                return;
            }

            s_ValidateParkingTraceLogCount += 1;
            Mod.log.Info($"ValidateParkingSpace trace: vehicle={vehicle}, currentLane={currentLane}, navCount={navigationLaneCount}, sourceLane={sourceLane}, targetLane={targetLane}, targetKind={targetKind}, sourceIsRoadCarLane={sourceIsRoadCarLane}, sourceAllowsSideAccess={sourceAllowsSideAccess}, decision={decision}, reason={reason}, traceIndex={s_ValidateParkingTraceLogCount}/{ValidateParkingTraceLogLimit}");
        }

        private static void SetRuleFlag(ref RuleFlags rules, RuleFlags flag, bool enabled)
        {
            if (enabled)
            {
                rules |= flag;
            }
            else
            {
                rules &= ~flag;
            }
        }
    }
}
