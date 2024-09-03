using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using VLB;

namespace Oxide.Plugins
{
    [Info("Ridable Drones", "WhiteThunder", "2.0.3")]
    [Description("Allows players to deploy signs and chairs onto RC drones to allow riding them.")]
    internal class RidableDrones : CovalencePlugin
    {
        #region Fields

        [PluginReference]
        private readonly Plugin DroneSettings;

        private Configuration _config;

        private const string PermissionSignDeploy = "ridabledrones.sign.deploy";
        private const string PermissionSignDeployFree = "ridabledrones.sign.deploy.free";

        private const string PermissionChairDeploy = "ridabledrones.chair.deploy";
        private const string PermissionChairDeployFree = "ridabledrones.chair.deploy.free";
        private const string PermissionChairAutoDeploy = "ridabledrones.chair.autodeploy";
        private const string PermissionChairPilot = "ridabledrones.chair.pilot";

        private const string SmallWoodenSignPrefab = "assets/prefabs/deployable/signs/sign.small.wood.prefab";
        private const string DeploySignEffectPrefab = "assets/prefabs/deployable/signs/effects/wood-sign-deploy.prefab";

        private const string PilotChairPrefab = "assets/prefabs/vehicle/seats/miniheliseat.prefab";
        private const string PassengerChairPrefab = "assets/prefabs/deployable/chair/chair.deployed.prefab";
        private const string VisibleChairPrefab = "assets/prefabs/vehicle/seats/passengerchair.prefab";
        private const string ChairDeployEffectPrefab = "assets/prefabs/deployable/chair/effects/chair-deploy.prefab";

        private const int SignItemId = -1138208076;
        private const int ChairItemId = 1534542921;

        private static SlotConfig ChairSlots = new(BaseEntity.Slot.UpperModifier);
        private static SlotConfig SignSlots = new(BaseEntity.Slot.MiddleModifier, BaseEntity.Slot.UpperModifier);

        private readonly object True = true;
        private readonly object False = false;

        private static readonly Vector3 SignLocalPosition = new(0, 0.114f, 0.265f);
        private static readonly Vector3 SignLocalRotationAngles = new(270, 0, 0);
        private static readonly Vector3 PassengerChairLocalPosition = new(0, 0.081f, 0);
        private static readonly Vector3 PilotChairLocalPosition = new(-0.006f, 0.027f, 0.526f);

        private readonly Dictionary<string, object> _signRemoveInfo = new();
        private readonly Dictionary<string, object> _refundInfo = new()
        {
            ["sign.wooden.small"] = new Dictionary<string, object>
            {
                ["Amount"] = 1,
            },
        };

        private readonly Dictionary<string, object> _chairRemoveInfo = new();
        private readonly Dictionary<string, object> _chairRefundInfo = new()
        {
            ["chair"] = new Dictionary<string, object>
            {
                ["Amount"] = 1,
            },
        };

        private readonly ObservableHashSet<BaseEntity> _chairDrones = new();
        private readonly ObservableHashSet<BaseEntity> _mountedChairDrones = new();
        private readonly ObservableHashSet<BaseEntity> _signDrones = new();

        private readonly GatedHookCollection[] _hookCollections;

        public RidableDrones()
        {
            var anySignDrones = new ObservableGate(_chairDrones, () => _chairDrones.Count > 0);
            var anyChairDrones = new ObservableGate(_chairDrones, () => _chairDrones.Count > 0);
            var anyChairDronesMounted = new ObservableGate(_mountedChairDrones, () => _mountedChairDrones.Count > 0);

            _hookCollections = new[]
            {
                new GatedHookCollection(
                    this,
                    anyChairDrones,
                    nameof(OnEntityMounted),
                    nameof(OnEntityDismounted),
                    nameof(OnPlayerDismountFailed)
                ),
                new GatedHookCollection(
                    this,
                    anyChairDronesMounted,
                    nameof(OnServerCommand)
                ),
                new GatedHookCollection(
                    this,
                    new MultiObservableGate(anySignDrones, anyChairDrones),
                    nameof(OnEntityTakeDamage),
                    nameof(CanPickupEntity),
                    nameof(OnRemovableEntityInfo),
                    nameof(canRemove)
                ),
            };
        }

        #endregion

        #region Hooks

        private void Init()
        {
            permission.RegisterPermission(PermissionSignDeploy, this);
            permission.RegisterPermission(PermissionSignDeployFree, this);

            permission.RegisterPermission(PermissionChairDeploy, this);
            permission.RegisterPermission(PermissionChairDeployFree, this);
            permission.RegisterPermission(PermissionChairAutoDeploy, this);
            permission.RegisterPermission(PermissionChairPilot, this);

            Unsubscribe(nameof(OnEntitySpawned));

            foreach (var hookCollection in _hookCollections)
            {
                hookCollection.Unsubscribe();
            }
        }

        private void OnServerInitialized()
        {
            foreach (var entity in BaseNetworkable.serverEntities)
            {
                var drone = entity as Drone;
                if (drone == null || !IsDroneEligible(drone))
                    continue;

                MaybeRefreshDroneSign(drone);
                MaybeAddOrRefreshChairs(drone);
            }

            foreach (var player in BasePlayer.activePlayerList)
            {
                var drone = GetMountedDrone(player, out var currentChair);
                if (drone == null)
                    continue;

                if (!TryGetChairs(drone, out var pilotChair, out _))
                    continue;

                if (!permission.UserHasPermission(player.UserIDString, PermissionChairPilot))
                    continue;

                var isPilotChair = currentChair == pilotChair;
                DroneController.Mount(this, player, drone, isPilotChair);
            }

            Subscribe(nameof(OnEntitySpawned));
        }

        private void Unload()
        {
            foreach (var entity in BaseNetworkable.serverEntities)
            {
                var drone = entity as Drone;
                if (drone == null)
                    continue;

                var sign = GetDroneSign(drone);
                if (sign != null)
                {
                    SignComponent.RemoveFromSign(sign);
                }

                if (TryGetPassengerChair(drone, out var passengerChair))
                {
                    ChairComponent.RemoveFromChair(passengerChair);
                }
            }

            foreach (var player in BasePlayer.activePlayerList)
            {
                DroneController.RemoveFromPlayer(player);
            }
        }

        private void OnEntitySpawned(Drone drone)
        {
            if (!IsDroneEligible(drone))
                return;

            var drone2 = drone;

            // Delay to give other plugins a moment to cache the drone id so they can block this.
            NextTick(() =>
            {
                if (drone2 == null || drone2.IsDestroyed)
                    return;

                MaybeAutoDeployChair(drone2);
            });
        }

        private void OnEntityBuilt(Planner planner, GameObject go)
        {
            if (planner == null || go == null)
                return;

            var drone = go.ToBaseEntity() as Drone;
            if (drone == null)
                return;

            var player = planner.GetOwnerPlayer();
            if (player == null)
                return;

            var drone2 = drone;

            NextTick(() =>
            {
                // Delay this check to allow time for other plugins to deploy an entity to this slot.
                if (drone2 == null || player == null)
                    return;

                if (SignSlots.IsCompatibleWithHost(drone2)
                    && permission.UserHasPermission(player.UserIDString, PermissionSignDeploy)
                    && UnityEngine.Random.Range(0, 100) < _config.SignTipChance)
                {
                    ChatMessage(player, Lang.TipDeploySignCommand);
                }

                if (ChairSlots.IsCompatibleWithHost(drone2)
                    && permission.UserHasPermission(player.UserIDString, PermissionChairDeploy)
                    && !permission.UserHasPermission(player.UserIDString, PermissionChairAutoDeploy)
                    && UnityEngine.Random.Range(0, 100) < _config.ChairTipChance)
                {
                    ChatMessage(player, Lang.TipDeployChairCommand);
                }
            });
        }

        private object OnEntityTakeDamage(BaseChair mountable, HitInfo info)
        {
            if (mountable.PrefabName != PassengerChairPrefab)
                return null;

            var drone = GetParentDrone(mountable);
            if (drone == null)
                return null;

            drone.Hurt(info);
            HitNotify(drone, info);

            return True;
        }

        private object OnEntityTakeDamage(Signage sign, HitInfo info)
        {
            var drone = GetParentDrone(sign);
            if (drone == null)
                return null;

            drone.Hurt(info);
            HitNotify(drone, info);

            return True;
        }

        // Allow swapping between between the seating modes
        private void OnServerCommand(ConsoleSystem.Arg arg)
        {
            if (arg.Connection == null || arg.cmd.FullName != "vehicle.swapseats")
                return;

            var player = arg.Player();
            if (player == null)
                return;

            var drone = GetMountedDrone(player, out var currentChair);
            if (drone == null)
                return;

            // Only players with the pilot permission may switch chairs.
            if (!permission.UserHasPermission(player.UserIDString, PermissionChairPilot))
                return;

            if (!TryGetChairs(drone, out var pilotChair, out var passengerChair))
                return;

            var desiredChair = currentChair == passengerChair
                ? pilotChair
                : passengerChair;

            SwitchToChair(player, currentChair, desiredChair);
        }

        private void OnEntityMounted(BaseMountable currentChair, BasePlayer player)
        {
            var drone = GetParentDrone(currentChair);
            if (drone == null)
                return;

            if (!TryGetChairs(drone, out var pilotChair, out var passengerChair))
                return;

            // The rest of the logic is only for pilots.
            if (!permission.UserHasPermission(player.UserIDString, PermissionChairPilot))
                return;

            var isPilotChair = currentChair == pilotChair;
            if (isPilotChair)
            {
                // Since the passenger chair is the mount ingress, prevent it from being mounted while the pilot chair is mounted.
                passengerChair.SetFlag(BaseEntity.Flags.Busy, true);
            }
            else if (!DroneController.Exists(player))
            {
                // The player is mounting the drone fresh (not switching chairs), so automatically switch to the pilot chair.
                SwitchToChair(player, currentChair, pilotChair);
                return;
            }

            DroneController.Mount(this, player, drone, isPilotChair);
        }

        private void OnEntityDismounted(BaseMountable previousChair, BasePlayer player)
        {
            var drone = GetParentDrone(previousChair);
            if (drone == null)
                return;

            if (!TryGetChairs(drone, out var pilotChair, out var passengerChair))
                return;

            if (previousChair == pilotChair)
            {
                // Since the passenger chair is the mount ingress, re-enable it when the pilot chair is dismounted.
                passengerChair.SetFlag(BaseEntity.Flags.Busy, false);
            }

            DroneController.Dismount(this, player, drone);
        }

        private void OnPlayerDismountFailed(BasePlayer player, BaseMountable mountable)
        {
            var drone = GetMountedDrone(player);
            if (drone == null)
                return;

            var droneTransform = drone.transform;
            droneTransform.rotation = Quaternion.Euler(0, droneTransform.rotation.eulerAngles.y, 0);
        }

        private object CanPickupEntity(BasePlayer player, Drone drone)
        {
            if (CanPickupInternal(drone, out var errorLangKey))
                return null;

            ChatMessage(player, errorLangKey);
            return False;
        }

        private object CanPickupEntity(BasePlayer player, Signage sign)
        {
            if (CanPickupInternal(sign, out var errorLangKey))
                return null;

            ChatMessage(player, errorLangKey);
            return False;
        }

        private object CanPickupEntity(BasePlayer player, BaseChair chair)
        {
            if (CanPickupInternal(chair, out var errorLangKey))
                return null;

            ChatMessage(player, errorLangKey);
            return False;
        }

        // This hook is exposed by plugin: Remover Tool (RemoverTool).
        private Dictionary<string, object> OnRemovableEntityInfo(Signage sign, BasePlayer player)
        {
            if (!IsDroneSign(sign))
                return null;

            _signRemoveInfo["DisplayName"] = GetMessage(player.UserIDString, Lang.InfoSignName);

            if (sign.pickup.enabled)
            {
                _signRemoveInfo["Refund"] = _refundInfo;
            }
            else
            {
                _signRemoveInfo.Remove("Refund");
            }

            return _signRemoveInfo;
        }

        // This hook is exposed by plugin: Remover Tool (RemoverTool).
        private Dictionary<string, object> OnRemovableEntityInfo(BaseChair chair, BasePlayer player)
        {
            if (!IsDroneChair(chair))
                return null;

            _chairRemoveInfo["DisplayName"] = GetMessage(player.UserIDString, Lang.InfoChairName);

            if (chair.pickup.enabled)
            {
                _chairRemoveInfo["Refund"] = _chairRefundInfo;
            }
            else
            {
                _chairRemoveInfo.Remove("Refund");
            }

            return _chairRemoveInfo;
        }

        // This hook is exposed by plugin: Remover Tool (RemoverTool).
        private string canRemove(BasePlayer player, Drone drone)
        {
            if (CanPickupInternal(drone, out var errorLangKey))
                return null;

            return GetMessage(player.UserIDString, errorLangKey);
        }

        // This hook is exposed by plugin: Drone Settings (DroneSettings).
        private string OnDroneTypeDetermine(Drone drone, List<string> droneTypeList)
        {
            var droneType = DetermineDroneType(drone);
            if (droneType == null)
                return null;

            if (droneTypeList == null)
                return droneType;

            droneTypeList.Add(droneType);
            return null;
        }

        #endregion

        #region Exposed Hooks

        private static class ExposedHooks
        {
            public static object OnDroneSignDeploy(Drone drone, BasePlayer deployer)
            {
                return Interface.CallHook("OnDroneSignDeploy", drone, deployer);
            }

            public static void OnDroneSignDeployed(Drone drone, BasePlayer deployer)
            {
                Interface.CallHook("OnDroneSignDeployed", drone, deployer);
            }

            public static object OnDroneChairDeploy(Drone drone, BasePlayer deployer)
            {
                return Interface.CallHook("OnDroneChairDeploy", drone, deployer);
            }

            public static void OnDroneChairDeployed(Drone drone, BasePlayer deployer)
            {
                Interface.CallHook("OnDroneChairDeployed", drone, deployer);
            }

            public static void OnDroneControlStarted(Drone drone, BasePlayer player)
            {
                Interface.CallHook("OnDroneControlStarted", drone, player);
            }

            public static void OnDroneControlEnded(Drone drone, BasePlayer player)
            {
                Interface.CallHook("OnDroneControlEnded", drone, player);
            }
        }

        #endregion

        #region Commands

        [Command("dronesign")]
        private void DroneSignCommand(IPlayer player)
        {
            if (!VerifyPlayer(player, out var basePlayer)
                || !VerifyPermission(player, PermissionSignDeploy)
                || !VerifyDroneFound(player, out var drone))
                return;

            if (GetDroneSign(drone) != null)
            {
                ReplyToPlayer(player, Lang.ErrorAlreadyHasSign);
                return;
            }

            if (!VerifyDroneHasSlotVacant(player, drone, SignSlots))
                return;

            var isFree = player.HasPermission(PermissionSignDeployFree);
            if (!isFree && basePlayer.inventory.FindItemByItemID(SignItemId) == null)
            {
                ReplyToPlayer(player, Lang.ErrorNoSignItem);
                return;
            }

            if (TryDeploySign(drone, basePlayer, allowRefund: !isFree) == null)
            {
                ReplyToPlayer(player, Lang.ErrorDeploySignFailed);
            }
            else if (!isFree)
            {
                basePlayer.inventory.Take(null, SignItemId, 1);
                basePlayer.Command("note.inv", SignItemId, -1);
            }
        }

        [Command("dronechair", "droneseat")]
        private void DroneChairCommand(IPlayer player)
        {
            if (!VerifyPlayer(player, out var basePlayer)
                || !VerifyPermission(player, PermissionChairDeploy)
                || !VerifyDroneFound(player, out var drone))
                return;

            if (HasChair(drone))
            {
                ReplyToPlayer(player, Lang.ErrorAlreadyHasChair);
                return;
            }

            if (!VerifyDroneHasSlotVacant(player, drone, ChairSlots))
                return;

            var isFree = player.HasPermission(PermissionChairDeployFree);
            if (!isFree && basePlayer.inventory.FindItemByItemID(ChairItemId) == null)
            {
                ReplyToPlayer(player, Lang.ErrorNoChairItem);
                return;
            }

            if (TryDeployChairs(drone, basePlayer, allowRefund: !isFree) == null)
            {
                ReplyToPlayer(player, Lang.ErrorDeployChairFailed);
            }
            else if (!isFree)
            {
                basePlayer.inventory.Take(null, ChairItemId, 1);
                basePlayer.Command("note.inv", ChairItemId, -1);
            }
        }

        #endregion

        #region Helper Methods - Command Checks

        private bool VerifyPlayer(IPlayer player, out BasePlayer basePlayer)
        {
            basePlayer = player.Object as BasePlayer;
            return !player.IsServer && basePlayer != null;
        }

        private bool VerifyPermission(IPlayer player, string perm)
        {
            if (player.HasPermission(perm))
                return true;

            ReplyToPlayer(player, Lang.ErrorNoPermission);
            return false;
        }

        private bool VerifyDroneFound(IPlayer player, out Drone drone)
        {
            var basePlayer = player.Object as BasePlayer;
            drone = GetLookEntity(basePlayer, 3) as Drone;
            if (drone != null && IsDroneEligible(drone))
                return true;

            ReplyToPlayer(player, Lang.ErrorNoDroneFound);
            return false;
        }

        private bool VerifyDroneHasSlotVacant(IPlayer player, Drone drone, SlotConfig slotConfig)
        {
            if (slotConfig.IsCompatibleWithHost(drone))
                return true;

            ReplyToPlayer(player, Lang.ErrorIncompatibleAttachment);
            return false;
        }

        #endregion

        #region Helpers

        private static class RCUtils
        {
            public static bool IsRCDrone(Drone drone)
            {
                return drone is not DeliveryDrone;
            }
        }

        private static bool IsDroneEligible(Drone drone)
        {
            return drone.skinID == 0 && RCUtils.IsRCDrone(drone);
        }

        private static Drone GetParentDrone(BaseEntity entity)
        {
            return entity.GetParentEntity() as Drone;
        }

        private static Drone GetMountedDrone(BasePlayer player, out BaseMountable currentChair)
        {
            currentChair = player.GetMounted();
            if (currentChair == null)
                return null;

            return currentChair.PrefabName == PilotChairPrefab || currentChair.PrefabName == PassengerChairPrefab
                ? GetParentDrone(currentChair)
                : null;
        }

        private static Drone GetMountedDrone(BasePlayer player)
        {
            return GetMountedDrone(player, out _);
        }

        private static Signage GetDroneSign(Drone drone)
        {
            return SignSlots.GetOccupant(drone) as Signage;
        }

        private static bool IsDroneSign(Signage sign)
        {
            return GetParentDrone(sign) != null;
        }

        private static bool TryGetChairs(Drone drone, out BaseMountable pilotChair, out BaseMountable passengerChair, out BaseMountable visibleChair)
        {
            pilotChair = null;
            passengerChair = null;
            visibleChair = null;

            foreach (var child in drone.children)
            {
                var mountable = child as BaseMountable;
                if (mountable == null)
                    continue;

                if (mountable.PrefabName == PilotChairPrefab)
                {
                    pilotChair = mountable;
                }

                if (mountable.PrefabName == PassengerChairPrefab)
                {
                    passengerChair = mountable;
                }

                if (mountable.PrefabName == VisibleChairPrefab)
                {
                    visibleChair = mountable;
                }
            }

            return pilotChair != null && passengerChair != null && visibleChair != null;
        }

        private static bool TryGetChairs(Drone drone, out BaseMountable pilotChair, out BaseMountable passengerChair)
        {
            return TryGetChairs(drone, out pilotChair, out passengerChair, out _);
        }

        private static bool TryGetPassengerChair(Drone drone, out BaseMountable passengerChair)
        {
            return TryGetChairs(drone, out _, out passengerChair, out _);
        }

        private static bool HasChair(Drone drone)
        {
            return TryGetPassengerChair(drone, out _);
        }

        private static bool IsDroneChair(BaseChair chair)
        {
            return GetParentDrone(chair) != null;
        }

        private static void HitNotify(BaseEntity entity, HitInfo info)
        {
            var player = info.Initiator as BasePlayer;
            if (player == null)
                return;

            entity.ClientRPCPlayer(null, player, "HitNotify");
        }

        private static void RemoveProblemComponents(BaseEntity entity)
        {
            foreach (var collider in entity.GetComponentsInChildren<MeshCollider>())
            {
                UnityEngine.Object.DestroyImmediate(collider);
            }
        }

        private static void SetupChair(BaseMountable mountable)
        {
            if (!BaseMountable.AllMountables.Contains(mountable))
            {
                BaseMountable.AllMountables.Add(mountable);
            }

            mountable.isMobile = true;
            mountable.EnableSaving(true);
            RemoveProblemComponents(mountable);
        }

        private static BaseEntity GetLookEntity(BasePlayer basePlayer, float maxDistance = 3)
        {
            return Physics.Raycast(basePlayer.eyes.HeadRay(), out var hit, maxDistance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)
                ? hit.GetEntity()
                : null;
        }

        private static void SwitchToChair(BasePlayer player, BaseMountable currentChair, BaseMountable desiredChair)
        {
            currentChair.DismountPlayer(player, lite: true);
            desiredChair.MountPlayer(player);
        }

        private static string DetermineDroneType(Drone drone)
        {
            if (GetDroneSign(drone) != null)
                return "DroneSign";

            if (HasChair(drone))
                return "DroneChair";

            return null;
        }

        private static bool CanPickupInternal(Drone drone, out string errorLangKey)
        {
            errorLangKey = null;

            if (!RCUtils.IsRCDrone(drone))
                return true;

            // Prevent drone pickup while there is a sign attached that can be picked up.
            var sign = GetDroneSign(drone);
            if (sign != null)
            {
                errorLangKey = Lang.ErrorCannotPickupWithSign;
                return !sign.pickup.enabled;
            }

            // Prevent drone pickup while there is a chair attached that can be picked up.
            if (TryGetPassengerChair(drone, out var passengerChair))
            {
                errorLangKey = Lang.ErrorCannotPickupWithChair;
                return !passengerChair.pickup.enabled;
            }

            return true;
        }

        private static bool CanPickupInternal(Signage sign, out string errorLangKey)
        {
            errorLangKey = null;

            if (!IsDroneSign(sign))
                return true;

            errorLangKey = Lang.ErrorCannotPickupAttachment;
            return sign.pickup.enabled;
        }

        private static bool CanPickupInternal(BaseChair chair, out string errorLangKey)
        {
            errorLangKey = null;

            if (!IsDroneChair(chair))
                return true;

            errorLangKey = Lang.ErrorCannotPickupAttachment;
            return chair.pickup.enabled;
        }

        private void RefreshDroneSettingsProfile(Drone drone)
        {
            DroneSettings?.Call("API_RefreshDroneProfile", drone);
        }

        private void SetupSign(Drone drone, Signage sign)
        {
            drone.playerCheckRadius = 0;

            // Damage will be processed by the drone.
            sign.baseProtection = null;

            UnityEngine.Object.DestroyImmediate(sign.GetComponent<GroundWatch>());
            UnityEngine.Object.DestroyImmediate(sign.GetComponent<DestroyOnGroundMissing>());

            SignComponent.AddToDrone(this, drone, sign);
        }

        private Signage TryDeploySign(Drone drone, BasePlayer deployer = null, bool allowRefund = false)
        {
            if (ExposedHooks.OnDroneSignDeploy(drone, deployer) is false)
                return null;

            var sign = GameManager.server.CreateEntity(SmallWoodenSignPrefab, SignLocalPosition, Quaternion.Euler(SignLocalRotationAngles)) as Signage;
            if (sign == null)
                return null;

            SetupSign(drone, sign);

            if (deployer != null)
            {
                sign.OwnerID = deployer.userID;
            }

            sign.SetParent(drone);
            sign.Spawn();

            // Claim slots to prevent deploying incompatible attachments.
            SignSlots.OccupyHost(drone, sign);

            // This flag is used to remember whether the sign should be refundable.
            // This information is lost on restart but that's a minor concern.
            sign.pickup.enabled = allowRefund;

            Effect.server.Run(DeploySignEffectPrefab, sign.transform.position);
            ExposedHooks.OnDroneSignDeployed(drone, deployer);
            RefreshDroneSettingsProfile(drone);

            return sign;
        }

        private void SetupAllChairs(Drone drone, BaseMountable pilotChair, BaseMountable passengerChair, BaseMountable visibleChair)
        {
            SetupChair(pilotChair);
            SetupChair(passengerChair);
            SetupChair(visibleChair);

            pilotChair.dismountPositions = passengerChair.dismountPositions;

            // Damage will be processed by the drone.
            passengerChair.baseProtection = null;

            UnityEngine.Object.DestroyImmediate(passengerChair.GetComponent<GroundWatch>());
            UnityEngine.Object.DestroyImmediate(passengerChair.GetComponent<DestroyOnGroundMissing>());

            // Box colliders on the deployable chair block dismount from the pilot chair.
            foreach (var collider in passengerChair.GetComponentsInChildren<BoxCollider>())
            {
                UnityEngine.Object.DestroyImmediate(collider);
            }

            ChairComponent.AddToDrone(this, drone, pilotChair, passengerChair, visibleChair);
        }

        private BaseMountable TryDeployChairs(Drone drone, BasePlayer deployer = null, bool allowRefund = false)
        {
            var hookResult = ExposedHooks.OnDroneChairDeploy(drone, deployer);
            if (hookResult is false)
                return null;

            // The driver chair is ideal for mouse movement since it locks the player view angles.
            var pilotChair = GameManager.server.CreateEntity(PilotChairPrefab, PilotChairLocalPosition) as BaseMountable;
            if (pilotChair == null)
                return null;

            pilotChair.SetParent(drone);
            pilotChair.Spawn();

            // The passenger chair shows the "mount" prompt and allows for unlocking view angles.
            var passengerChair = GameManager.server.CreateEntity(PassengerChairPrefab, PassengerChairLocalPosition) as BaseMountable;
            if (passengerChair == null)
            {
                pilotChair.Kill();
                return null;
            }

            passengerChair.pickup.enabled = allowRefund;

            if (deployer != null)
            {
                passengerChair.OwnerID = deployer.userID;
            }

            passengerChair.SetParent(drone);
            passengerChair.Spawn();

            // This chair is visible, even as the drone moves, but doesn't show a mount prompt.
            var visibleChair = GameManager.server.CreateEntity(VisibleChairPrefab, PassengerChairLocalPosition) as BaseMountable;
            if (visibleChair == null)
            {
                pilotChair.Kill();
                passengerChair.Kill();
                return null;
            }

            visibleChair.SetParent(drone);
            visibleChair.Spawn();

            SetupAllChairs(drone, pilotChair, passengerChair, visibleChair);

            // Claim slots to prevent deploying incompatible attachments.
            ChairSlots.OccupyHost(drone, passengerChair);

            Effect.server.Run(ChairDeployEffectPrefab, passengerChair.transform.position);
            ExposedHooks.OnDroneChairDeployed(drone, deployer);
            RefreshDroneSettingsProfile(drone);
            _chairDrones.Add(drone);

            return passengerChair;
        }

        private void MaybeRefreshDroneSign(Drone drone)
        {
            var sign = GetDroneSign(drone);
            if (sign == null)
                return;

            SetupSign(drone, sign);
        }

        private void MaybeAutoDeployChair(Drone drone)
        {
            if (drone.OwnerID == 0
                || !ChairSlots.IsCompatibleWithHost(drone)
                || !permission.UserHasPermission(drone.OwnerID.ToString(), PermissionChairAutoDeploy))
                return;

            TryDeployChairs(drone);
        }

        private void MaybeAddOrRefreshChairs(Drone drone)
        {
            if (!TryGetChairs(drone, out var pilotChair, out var passengerChair, out var visibleChair))
            {
                MaybeAutoDeployChair(drone);
                return;
            }

            SetupAllChairs(drone, pilotChair, passengerChair, visibleChair);
            RefreshDroneSettingsProfile(drone);
            _chairDrones.Add(drone);
        }

        private class ChairComponent : FacepunchBehaviour
        {
            public static void AddToDrone(RidableDrones plugin, Drone drone, BaseMountable pilotChair, BaseMountable passengerChair, BaseMountable visibleChair)
            {
                var component = passengerChair.gameObject.AddComponent<ChairComponent>();
                component._plugin = plugin;
                component._drone = drone;
                component._chairs = new[] { pilotChair, passengerChair, visibleChair };
                component.CreateCollider(drone, passengerChair);
            }

            public static void RemoveFromChair(BaseMountable chair)
            {
                var component = chair.gameObject.GetComponent<ChairComponent>();
                if (component == null)
                    return;

                component._isUnloading = true;
                DestroyImmediate(component);
            }

            private const float ColliderHeight = 3;

            private RidableDrones _plugin;
            private BaseMountable[] _chairs;
            private BaseEntity _drone;
            private GameObject _child;
            private bool _isUnloading;

            private void CreateCollider(Drone drone, BaseMountable passengerChair)
            {
                var centerOfMass = drone.body.centerOfMass;

                _child = passengerChair.gameObject.CreateChild();
                // Layers that seem to work as desired (no player collision): 9, 12, 15, 20, 22, 26.
                _child.gameObject.layer = (int)Rust.Layer.Vehicle_World;
                _child.transform.localPosition = new Vector3(0, ColliderHeight / 4, 0);

                var collider = _child.AddComponent<BoxCollider>();
                var droneExtents = drone.bounds.extents;
                collider.size = droneExtents.WithY(ColliderHeight / 2);

                drone.body.centerOfMass = centerOfMass;
            }

            private void OnDestroy()
            {
                if (_child != null)
                {
                    Destroy(_child);
                }

                if (!_isUnloading)
                {
                    foreach (var chair in _chairs)
                    {
                        if (chair == null || chair.IsDestroyed)
                            continue;

                        chair.Kill();
                    }
                }

                _plugin._chairDrones.Remove(_drone);
                _plugin._mountedChairDrones.Remove(_drone);
            }
        }

        #endregion

        #region Dynamic Hooks

        private interface IGate
        {
            bool Enabled { get; }
        }

        private interface IObservable
        {
            event Action OnChange;
        }

        private interface IObservableGate : IGate, IObservable {}

        private class ObservableHashSet<T> : HashSet<T>, IObservable
        {
            public event Action OnChange;

            public new bool Add(T item)
            {
                var added = base.Add(item);

                if (added)
                {
                    OnChange?.Invoke();
                }

                return added;
            }

            public new bool Remove(T item)
            {
                var removed = base.Remove(item);

                if (removed)
                {
                    OnChange?.Invoke();
                }

                return removed;
            }

            public new void Clear()
            {
                if (Count > 0)
                    return;

                base.Clear();
                OnChange?.Invoke();
            }
        }

        private class ObservableGate : IObservableGate
        {
            public event Action OnChange;
            private readonly Func<bool> _enableWhen;

            public bool Enabled => _enableWhen();

            public ObservableGate(IObservable observable, Func<bool> enableWhen)
            {
                _enableWhen = enableWhen;

                observable.OnChange += HandleChange;
            }

            private void HandleChange()
            {
                OnChange?.Invoke();
            }
        }

        private class MultiObservableGate : IObservableGate
        {
            public event Action OnChange;

            private readonly IObservableGate[] _gates;

            public bool Enabled
            {
                get
                {
                    foreach (var gate in _gates)
                    {
                        if (gate.Enabled)
                            return true;
                    }

                    return false;
                }
            }

            public MultiObservableGate(params IObservableGate[] gates)
            {
                _gates = gates;

                var handleChange = new Action(HandleChange);

                foreach (var gate in gates)
                {
                    gate.OnChange += handleChange;
                }
            }

            private void HandleChange()
            {
                OnChange?.Invoke();
            }
        }

        private class GatedHookCollection
        {
            public bool IsSubscribed { get; private set; } = true;
            private readonly RidableDrones _plugin;
            private readonly IObservableGate _gate;
            private readonly string[] _hookNames;

            public GatedHookCollection(RidableDrones plugin, IObservableGate gate, params string[] hookNames)
            {
                _plugin = plugin;
                _gate = gate;
                _hookNames = hookNames;

                gate.OnChange += Refresh;
            }

            public void Subscribe()
            {
                foreach (var hookName in _hookNames)
                {
                    _plugin.Subscribe(hookName);
                }

                IsSubscribed = true;
            }

            public void Unsubscribe()
            {
                foreach (var hookName in _hookNames)
                {
                    _plugin.Unsubscribe(hookName);
                }

                IsSubscribed = false;
            }

            public void Refresh(bool shouldSubscribe)
            {
                if (shouldSubscribe)
                {
                    if (!IsSubscribed)
                    {
                        Subscribe();
                    }
                }
                else if (IsSubscribed)
                {
                    Unsubscribe();
                }
            }

            public void Refresh()
            {
                Refresh(_gate.Enabled);
            }
        }

        #endregion

        #region Slot Config

        private class SlotConfig
        {
            public readonly BaseEntity.Slot[] Slots;

            public SlotConfig(params BaseEntity.Slot[] slots)
            {
                if (slots.Length == 0)
                    throw new ArgumentOutOfRangeException(nameof(slots), "Must not be empty");

                Slots = slots;
            }

            public BaseEntity GetOccupant(BaseEntity host)
            {
                return host.GetSlot(Slots[0]);
            }

            public bool IsCompatibleWithHost(BaseEntity host)
            {
                foreach (var slot in Slots)
                {
                    if (host.GetSlot(slot) != null)
                        return false;
                }

                return true;
            }

            public void OccupyHost(BaseEntity host, BaseEntity occupant)
            {
                foreach (var slot in Slots)
                {
                    host.SetSlot(slot, occupant);
                }
            }
        }

        #endregion

        #region Parent Trigger

        private class SignTriggerParentEnclosed : TriggerParentEnclosed
        {
            public static SignTriggerParentEnclosed AddToDrone(Drone drone, GameObject host)
            {
                var component = host.gameObject.AddComponent<SignTriggerParentEnclosed>();
                component._drone = drone;
                return component;
            }

            private Drone _drone;

            public override bool ShouldParent(BaseEntity entity, bool bypassOtherTriggerCheck = false)
            {
                // This avoids the drone trying to parent itself when using the Targetable Drones plugin.
                // Targetable Drones uses a child object with the player layer, which the parent trigger is interested in.
                // This also avoids other drones being parented which can create some problems such as recursive parenting.
                if (entity is Drone)
                    return false;

                var player = entity as BasePlayer;
                if ((object)player != null && Vector3.Dot(Vector3.up, _drone.transform.up) < 0.8f)
                    return false;

                return base.ShouldParent(entity, bypassOtherTriggerCheck);
            }
        }

        private class SignComponent : EntityComponent<BaseEntity>
        {
            public static void AddToDrone(RidableDrones plugin, Drone drone, Signage sign)
            {
                var component = sign.gameObject.AddComponent<SignComponent>();
                component._plugin = plugin;
                component._drone = drone;
                component.CreateParentTrigger(drone, sign);
                plugin._signDrones.Add(drone);
            }

            public static void RemoveFromSign(Signage sign)
            {
                DestroyImmediate(sign.gameObject.GetComponent<SignComponent>());
            }

            private const float ColliderHeight = 1.8f;

            private RidableDrones _plugin;
            private Drone _drone;
            private GameObject _triggerHost;

            private void CreateParentTrigger(Drone drone, Signage sign)
            {
                var signExtents = sign.bounds.extents;
                var colliderExtents = new Vector3(signExtents.x, ColliderHeight / 2f, signExtents.y);

                _triggerHost = drone.gameObject.CreateChild();
                _triggerHost.transform.localPosition += new Vector3(0, colliderExtents.y, 0);

                // Without this hack, the drone's sweep test can collide with other entities using the
                // parent trigger collider, causing the drone to occasionally reduce altitude.
                _triggerHost.GetOrAddComponent<Rigidbody>().isKinematic = true;

                var triggerCollider = _triggerHost.gameObject.AddComponent<BoxCollider>();
                triggerCollider.isTrigger = true;
                triggerCollider.gameObject.layer = (int)Rust.Layer.Trigger;
                triggerCollider.size = 2 * colliderExtents;

                var triggerParent = SignTriggerParentEnclosed.AddToDrone(drone, _triggerHost);
                triggerParent.intersectionMode = TriggerParentEnclosed.TriggerMode.PivotPoint;
                triggerParent.interestLayers = Rust.Layers.Mask.Player_Server;
            }

            private void OnDestroy()
            {
                if (_triggerHost != null)
                {
                    Destroy(_triggerHost);
                }

                _plugin._signDrones.Remove(_drone);
            }
        }

        #endregion

        #region DroneController

        private class DroneController : FacepunchBehaviour
        {
            public static bool Exists(BasePlayer player)
            {
                return player.GetComponent<DroneController>() != null;
            }

            public static void Mount(RidableDrones plugin, BasePlayer player, Drone drone, bool isPilotChair)
            {
                var component = player.GetComponent<DroneController>();
                var alreadyExists = component != null;

                if (!alreadyExists)
                {
                    component = player.gameObject.AddComponent<DroneController>();
                }

                component.OnMount(player, drone, isPilotChair);

                if (!alreadyExists)
                {
                    ExposedHooks.OnDroneControlStarted(drone, player);
                }

                plugin._mountedChairDrones.Add(drone);
            }

            public static void Dismount(RidableDrones plugin, BasePlayer player, Drone drone)
            {
                player.GetComponent<DroneController>()?.OnDismount();
                plugin._mountedChairDrones.Remove(drone);
            }

            public static void RemoveFromPlayer(BasePlayer player)
            {
                DestroyImmediate(player.GetComponent<DroneController>());
            }

            private Drone _drone;
            private BasePlayer _controller;
            private CameraViewerId _viewerId;
            private bool _isPilotChair;

            private void DelayedDestroy() => DestroyImmediate(this);

            private void OnMount(BasePlayer controller, Drone drone, bool isPilotChair)
            {
                // If they were swapping chairs, cancel destroying this component.
                CancelInvoke(DelayedDestroy);

                _drone = drone;
                _controller = controller;
                _viewerId = new CameraViewerId(controller.userID, 0);
                _isPilotChair = isPilotChair;

                if (isPilotChair && drone.ControllingViewerId.HasValue)
                {
                    drone.StopControl(drone.ControllingViewerId.Value);
                }

                drone.InitializeControl(_viewerId);

                drone.playerCheckRadius = 0;
            }

            // Don't destroy the component immediately, in case the player is swapping chairs.
            private void OnDismount() => Invoke(DelayedDestroy, 0);

            private void Update()
            {
                if (_drone == null || _drone.IsDestroyed)
                {
                    DestroyImmediate(this);
                    return;
                }

                // Optimization: Skip if there was no user input this frame.
                if (_controller.lastTickTime < Time.time)
                    return;

                _drone.UserInput(_controller.serverInput, _viewerId);

                if (!_isPilotChair)
                {
                    // In hybrid mode, move relative to the direction the player is facing, instead of relative to the direction the drone is facing.
                    var worldDirection = _drone.transform.InverseTransformVector(_drone.currentInput.movement);
                    var playerRotation = Quaternion.Euler(0, _controller.viewAngles.y, 0);

                    _drone.currentInput.movement = playerRotation * worldDirection;
                }
            }

            private void OnDestroy()
            {
                if (_drone != null && !_drone.IsDestroyed)
                {
                    if (_drone.ControllingViewerId.HasValue)
                    {
                        _drone.StopControl(_viewerId);
                    }

                    ExposedHooks.OnDroneControlEnded(_drone, _controller);
                }
            }
        }

        #endregion

        #region Configuration

        private class Configuration : BaseConfiguration
        {
            [JsonProperty("Chair tip chance")]
            public int ChairTipChance = 25;

            [JsonProperty("Sign tip chance")]
            public int SignTipChance = 25;

            [JsonProperty("TipChance")]
            private int DeprecatedTipChance { set { ChairTipChance = SignTipChance = value; } }
        }

        private Configuration GetDefaultConfig() => new();

        #endregion

        #region Configuration Helpers

        private class BaseConfiguration
        {
            public string ToJson() => JsonConvert.SerializeObject(this);

            public Dictionary<string, object> ToDictionary() => JsonHelper.Deserialize(ToJson()) as Dictionary<string, object>;
        }

        private static class JsonHelper
        {
            public static object Deserialize(string json) => ToObject(JToken.Parse(json));

            private static object ToObject(JToken token)
            {
                switch (token.Type)
                {
                    case JTokenType.Object:
                        return token.Children<JProperty>()
                                    .ToDictionary(prop => prop.Name,
                                                  prop => ToObject(prop.Value));

                    case JTokenType.Array:
                        return token.Select(ToObject).ToList();

                    default:
                        return ((JValue)token).Value;
                }
            }
        }

        private bool MaybeUpdateConfig(BaseConfiguration config)
        {
            var currentWithDefaults = config.ToDictionary();
            var currentRaw = Config.ToDictionary(x => x.Key, x => x.Value);
            return MaybeUpdateConfigDict(currentWithDefaults, currentRaw);
        }

        private bool MaybeUpdateConfigDict(Dictionary<string, object> currentWithDefaults, Dictionary<string, object> currentRaw)
        {
            var changed = false;

            foreach (var key in currentWithDefaults.Keys)
            {
                if (currentRaw.TryGetValue(key, out var currentRawValue))
                {
                    var defaultDictValue = currentWithDefaults[key] as Dictionary<string, object>;
                    var currentDictValue = currentRawValue as Dictionary<string, object>;

                    if (defaultDictValue != null)
                    {
                        if (currentDictValue == null)
                        {
                            currentRaw[key] = currentWithDefaults[key];
                            changed = true;
                        }
                        else if (MaybeUpdateConfigDict(defaultDictValue, currentDictValue))
                            changed = true;
                    }
                }
                else
                {
                    currentRaw[key] = currentWithDefaults[key];
                    changed = true;
                }
            }

            return changed;
        }

        protected override void LoadDefaultConfig() => _config = GetDefaultConfig();

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<Configuration>();
                if (_config == null)
                {
                    throw new JsonException();
                }

                if (MaybeUpdateConfig(_config))
                {
                    LogWarning("Configuration appears to be outdated; updating and saving");
                    SaveConfig();
                }
            }
            catch (Exception e)
            {
                LogError(e.Message);
                LogWarning($"Configuration file {Name}.json is invalid; using defaults");
                LoadDefaultConfig();
            }
        }

        protected override void SaveConfig()
        {
            Log($"Configuration changes saved to {Name}.json");
            Config.WriteObject(_config, true);
        }

        #endregion

        #region Localization

        private void ReplyToPlayer(IPlayer player, string messageName, params object[] args)
        {
            player.Reply(string.Format(GetMessage(player.Id, messageName), args));
        }

        private void ChatMessage(BasePlayer player, string messageName, params object[] args)
        {
            player.ChatMessage(string.Format(GetMessage(player.UserIDString, messageName), args));
        }

        private string GetMessage(string playerId, string messageName, params object[] args)
        {
            var message = lang.GetMessage(messageName, this, playerId);
            return args.Length > 0 ? string.Format(message, args) : message;
        }

        private static class Lang
        {
            public const string TipDeployChairCommand = "Tip.DeployChairCommand";
            public const string TipDeploySignCommand = "Tip.DeploySignCommand";
            public const string InfoSignName = "Info.SignName";
            public const string InfoChairName = "Info.ChairName";
            public const string ErrorNoPermission = "Error.NoPermission";
            public const string ErrorNoDroneFound = "Error.NoDroneFound";
            public const string ErrorNoSignItem = "Error.NoSignItem";
            public const string ErrorNoChairItem = "Error.NoChairItem";
            public const string ErrorAlreadyHasChair = "Error.AlreadyHasChair";
            public const string ErrorAlreadyHasSign = "Error.AlreadyHasSign";
            public const string ErrorIncompatibleAttachment = "Error.IncompatibleAttachment";
            public const string ErrorDeploySignFailed = "Error.DeploySignFailed";
            public const string ErrorDeployChairFailed = "Error.DeployChairFailed";
            public const string ErrorCannotPickupWithSign = "Error.CannotPickupWithSign";
            public const string ErrorCannotPickupWithChair = "Error.CannotPickupWithChair";
            public const string ErrorCannotPickupAttachment = "Error.CannotPickupAttachment";
        }

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                [Lang.TipDeployChairCommand] = "Tip: Look at the drone and run <color=yellow>/dronechair</color> to deploy a chair.",
                [Lang.TipDeploySignCommand] = "Tip: Look at the drone and run <color=yellow>/dronesign</color> to deploy a sign.",
                [Lang.InfoSignName] = "Drone Sign",
                [Lang.InfoChairName] = "Drone Chair",
                [Lang.ErrorNoPermission] = "You don't have permission to do that.",
                [Lang.ErrorNoDroneFound] = "Error: No drone found.",
                [Lang.ErrorNoSignItem] = "Error: You need a small wooden sign to do that.",
                [Lang.ErrorNoChairItem] = "Error: You need a chair to do that.",
                [Lang.ErrorAlreadyHasChair] = "Error: That drone already has a chair.",
                [Lang.ErrorAlreadyHasSign] = "Error: That drone already has a sign.",
                [Lang.ErrorIncompatibleAttachment] = "Error: That drone has an incompatible attachment.",
                [Lang.ErrorDeploySignFailed] = "Error: Failed to deploy sign.",
                [Lang.ErrorDeployChairFailed] = "Error: Failed to deploy chair.",
                [Lang.ErrorCannotPickupWithSign] = "Error: Cannot pick up that drone while it has a sign.",
                [Lang.ErrorCannotPickupWithChair] = "Error: Cannot pick up that drone while it has a chair.",
                [Lang.ErrorCannotPickupAttachment] = "Error: Cannot pick up that attachment. Pick up the drone instead.",
            }, this, "en");
        }

        #endregion
    }
}
