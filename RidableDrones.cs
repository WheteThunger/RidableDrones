using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
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
    [Info("Ridable Drones", "WhiteThunder", "1.1.0")]
    [Description("Allows players to ride RC drones by standing on them or mounting a chair.")]
    internal class RidableDrones : CovalencePlugin
    {
        #region Fields

        [PluginReference]
        Plugin DroneScaleManager, DroneSettings, EntityScaleManager;

        private static RidableDrones _pluginInstance;
        private Configuration _pluginConfig;

        private const string PermissionRidable = "ridabledrones.ridable";
        private const string PermissionSeatDeploy = "ridabledrones.seat.deploy";
        private const string PermissionSeatDeployFree = "ridabledrones.seat.deploy.free";
        private const string PermissionSeatAutoDeploy = "ridabledrones.seat.autodeploy";
        private const string PermissionSeatPilot = "ridabledrones.seat.pilot";

        private const string PilotSeatPrefab = "assets/prefabs/vehicle/seats/miniheliseat.prefab";
        private const string PassengerSeatPrefab = "assets/bundled/prefabs/static/chair.invisible.static.prefab";
        private const string VisibleSeatPrefab = "assets/prefabs/vehicle/seats/passengerchair.prefab";
        private const string ChairDeployEffectPrefab = "assets/prefabs/deployable/chair/effects/chair-deploy.prefab";

        private const int ChairItemId = 1534542921;

        private const BaseEntity.Slot SeatSlot = BaseEntity.Slot.UpperModifier;

        private static readonly Vector3 PassenterSeatLocalPosition = new Vector3(0, 0.081f, 0);
        private static readonly Vector3 PilotSeatLocalPosition = new Vector3(-0.006f, 0.027f, 0.526f);

        private RidableDroneTracker _ridableDronesTracker = new RidableDroneTracker();
        private MountableDroneTracker _mountableDronesTracker = new MountableDroneTracker();
        private MountedDroneTracker _mountedDroneTracker = new MountedDroneTracker();

        #endregion

        #region Hooks

        private void Init()
        {
            _pluginInstance = this;

            permission.RegisterPermission(PermissionRidable, this);
            permission.RegisterPermission(PermissionSeatDeploy, this);
            permission.RegisterPermission(PermissionSeatDeployFree, this);
            permission.RegisterPermission(PermissionSeatAutoDeploy, this);
            permission.RegisterPermission(PermissionSeatPilot, this);

            Unsubscribe(nameof(OnEntitySpawned));

            // These will be dynamically hooked to improve performance.
            Unsubscribe(nameof(OnEntityTakeDamage));
            Unsubscribe(nameof(OnServerCommand));
            Unsubscribe(nameof(OnEntityMounted));
            Unsubscribe(nameof(OnEntityDismounted));
            Unsubscribe(nameof(OnEntityEnter));
        }

        private void Unload()
        {
            DroneParentTriggerComponent.DestroyAll();

            foreach (var player in BasePlayer.activePlayerList)
            {
                DroneController.RemoveFromPlayer(player);
                DismountHelper.RemoveFromPlayer(player);
            }

            _pluginInstance = null;
        }

        private void OnServerInitialized()
        {
            foreach (var entity in BaseNetworkable.serverEntities)
            {
                var drone = entity as Drone;
                if (drone == null || !IsDroneEligible(drone))
                    continue;

                MaybeCreateParentTrigger(drone);
                MaybeAddOrRefreshSeats(drone);
            }

            foreach (var player in BasePlayer.activePlayerList)
            {
                BaseMountable currentSeat;
                var drone = GetMountedDrone(player, out currentSeat);
                if (drone == null)
                    continue;

                BaseMountable pilotSeat, passengerSeat;
                if (!TryGetSeats(drone, out pilotSeat, out passengerSeat))
                    continue;

                DismountHelper.Mount(player, drone);

                if (!permission.UserHasPermission(player.UserIDString, PermissionSeatPilot))
                    continue;

                var isPilotSeat = currentSeat == pilotSeat;
                DroneController.Mount(player, drone, isPilotSeat);
            }

            Subscribe(nameof(OnEntitySpawned));
        }

        private void OnEntitySpawned(Drone drone)
        {
            if (!IsDroneEligible(drone))
                return;

            // Delay to give other plugins a moment to cache the drone id so they can block this.
            NextTick(() =>
            {
                if (drone == null)
                    return;

                MaybeCreateParentTrigger(drone);
                MaybeAutoDeploySeat(drone);
            });
        }

        private void OnEntityKill(Drone drone)
        {
            _ridableDronesTracker.RemoveDrone(drone);
            _mountableDronesTracker.RemoveDrone(drone);
            _mountedDroneTracker.RemoveDrone(drone);
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

            NextTick(() =>
            {
                // Delay this check to allow time for other plugins to deploy an entity to this slot.
                if (drone == null || player == null || HasIncompabitleAttachment(drone))
                    return;

                if (permission.UserHasPermission(player.UserIDString, PermissionSeatDeploy)
                    && !permission.UserHasPermission(player.UserIDString, PermissionSeatAutoDeploy)
                    && UnityEngine.Random.Range(0, 100) < _pluginConfig.TipChance)
                {
                    ChatMessage(player, Lang.TipDeployCommand);
                }
            });
        }

        private bool? OnEntityTakeDamage(BaseChair mountable, HitInfo info)
        {
            if (mountable.PrefabName != PassengerSeatPrefab)
                return null;

            var drone = GetParentDrone(mountable);
            if (drone == null)
                return null;

            drone.Hurt(info);
            HitNotify(drone, info);

            return true;
        }

        // Allow swapping between between the seating modes
        private void OnServerCommand(ConsoleSystem.Arg arg)
        {
            if (arg.Connection == null || arg.cmd.FullName != "vehicle.swapseats")
                return;

            var player = arg.Player();
            if (player == null)
                return;

            BaseMountable currentSeat;
            var drone = GetMountedDrone(player, out currentSeat);
            if (drone == null)
                return;

            // Only players with the pilot permission may switch seats.
            if (!permission.UserHasPermission(player.UserIDString, PermissionSeatPilot))
                return;

            BaseMountable pilotSeat, passengerSeat;
            if (!TryGetSeats(drone, out pilotSeat, out passengerSeat))
                return;

            var desiredSeat = currentSeat == passengerSeat
                ? pilotSeat
                : passengerSeat;

            SwitchToSeat(player, currentSeat, desiredSeat);
        }

        private void OnEntityMounted(BaseMountable currentSeat, BasePlayer player)
        {
            var drone = GetParentDrone(currentSeat);
            if (drone == null)
                return;

            BaseMountable pilotSeat, passengerSeat;
            if (!TryGetSeats(drone, out pilotSeat, out passengerSeat))
                return;

            DismountHelper.Mount(player, drone);

            // The rest of the logic is only for pilots.
            if (!permission.UserHasPermission(player.UserIDString, PermissionSeatPilot))
                return;

            var isPilotSeat = currentSeat == pilotSeat;
            if (isPilotSeat)
            {
                // Since the passenger seat is the mount ingress, prevent it from being mounted while the pilot seat is mounted.
                passengerSeat.SetFlag(BaseEntity.Flags.Busy, true);
            }
            else if (!DroneController.Exists(player))
            {
                // The player is mounting the drone fresh (not switching seats), so automatically switch to the pilot seat.
                SwitchToSeat(player, currentSeat, pilotSeat);
                return;
            }

            DroneController.Mount(player, drone, isPilotSeat);
        }

        private void OnEntityDismounted(BaseMountable previousSeat, BasePlayer player)
        {
            var drone = GetParentDrone(previousSeat);
            if (drone == null)
                return;

            BaseMountable pilotSeat, passengerSeat;
            if (!TryGetSeats(drone, out pilotSeat, out passengerSeat))
                return;

            if (previousSeat == pilotSeat)
            {
                // Since the passenger seat is the mount ingress, re-enable it when the pilot seat is dismounted.
                passengerSeat.SetFlag(BaseEntity.Flags.Busy, false);
            }

            DismountHelper.Dismount(player, drone);
            DroneController.Dismount(player, drone);
        }

        private bool? OnEntityEnter(TriggerParentEnclosed triggerParent, BasePlayer player)
        {
            var parentTriggerComponent = triggerParent.GetComponentInParent<DroneParentTriggerComponent>();
            if (parentTriggerComponent == null)
                return null;

            var drone = parentTriggerComponent.OwnerDrone;
            if (drone == null)
                return null;

            // Don't allow parenting if the drone is sideways or upside-down.
            // This helps avoid issues where an upside-down drone flips the camera around.
            // Note: This does not solve problems for players already parented.
            if (Vector3.Dot(Vector3.up, drone.transform.up) < 0.8f)
                return false;

            return null;
        }

        private void OnPlayerDismountFailed(BasePlayer player, BaseMountable mountable)
        {
            var drone = GetMountedDrone(player);
            if (drone == null)
                return;

            drone.transform.rotation = Quaternion.identity;
        }

        // Must hook before the drone is actually scaled, to move the parent trigger to the root entity.
        // This is done to prevent issues where the player observes the entity resizing while parented to it.
        private void OnDroneScaleBegin(Drone drone, BaseEntity rootEntity, float scale, float previousScale)
        {
            if (previousScale == 1)
            {
                // Drone is being resized from default size.
                var droneComponent = drone.GetComponent<DroneParentTriggerComponent>();
                if (droneComponent == null)
                    return;

                // Move parent trigger from drone to root entity.
                UnityEngine.Object.DestroyImmediate(droneComponent);
                DroneParentTriggerComponent.AddToRootEntity(drone, rootEntity, scale);
                return;
            }

            // Drone is not default size.
            var rootComponent = rootEntity.GetComponent<DroneParentTriggerComponent>();
            if (rootComponent == null)
                return;

            if (scale == 1)
            {
                // Drone is being resized to default size.
                // Move parent trigger to drone.
                UnityEngine.Object.DestroyImmediate(rootComponent);
                drone.Invoke(() => DroneParentTriggerComponent.AddToDrone(drone, scale), 0);
                return;
            }

            // Drone is being resized from a non-default size to a different non-default size.
            rootComponent.SetScale(scale);
        }

        // This hook is exposed by plugin: Drone Settings (DroneSettings).
        private string OnDroneTypeDetermine(Drone drone)
        {
            return HasSeat(drone) ? Name : null;
        }

        #endregion

        #region Commands

        [Command("droneseat")]
        private void DroneSeatCommand(IPlayer player)
        {
            if (player.IsServer)
                return;

            if (!player.HasPermission(PermissionSeatDeploy))
            {
                ReplyToPlayer(player, Lang.ErrorNoPermission);
                return;
            }

            var basePlayer = player.Object as BasePlayer;
            var drone = GetLookEntity(basePlayer, 3) as Drone;
            if (drone == null || !IsDroneEligible(drone))
            {
                ReplyToPlayer(player, Lang.ErrorNoDroneFound);
                return;
            }

            if (HasSeat(drone))
            {
                ReplyToPlayer(player, Lang.ErrorAlreadyHasSeat);
                return;
            }

            if (HasIncompabitleAttachment(drone))
            {
                ReplyToPlayer(player, Lang.ErrorIncompatibleAttachment);
                return;
            }

            var isFree = player.HasPermission(PermissionSeatDeployFree);
            if (!isFree && basePlayer.inventory.FindItemID(ChairItemId) == null)
            {
                ReplyToPlayer(player, Lang.ErrorNoChairItem);
                return;
            }

            if (TryDeploySeats(drone, basePlayer) == null)
            {
                ReplyToPlayer(player, Lang.ErrorDeployFailed);
            }
            else if (!isFree)
            {
                basePlayer.inventory.Take(null, ChairItemId, 1);
                basePlayer.Command("note.inv", ChairItemId, -1);
            }
        }

        #endregion

        #region Helper Methods

        private static bool CreateParentTriggerWasBlocked(Drone drone)
        {
            object hookResult = Interface.CallHook("OnDroneParentTriggerCreate", drone);
            return hookResult is bool && (bool)hookResult == false;
        }

        private static bool DeploySeatWasBlocked(Drone drone, BasePlayer deployer)
        {
            object hookResult = Interface.CallHook("OnDroneSeatDeploy", drone, deployer);
            return hookResult is bool && (bool)hookResult == false;
        }

        private void RefreshDronSettingsProfile(Drone drone)
        {
            DroneSettings?.Call("API_RefreshDroneProfile", drone);
        }

        private static float GetDroneScale(Drone drone)
        {
            if (_pluginInstance.EntityScaleManager == null)
                return 1;

            return Convert.ToSingle(_pluginInstance.EntityScaleManager.Call("API_GetScale", drone));
        }

        private static BaseEntity GetRootEntity(Drone drone)
        {
            return _pluginInstance.DroneScaleManager?.Call("API_GetRootEntity", drone) as BaseEntity;
        }

        private static BaseEntity GetDroneOrRootEntity(Drone drone)
        {
            var rootEntity = GetRootEntity(drone);
            return rootEntity != null ? rootEntity : drone;
        }

        private static bool IsDroneEligible(Drone drone) =>
            !(drone is DeliveryDrone);

        private static Drone GetParentDrone(BaseEntity entity) =>
            entity.GetParentEntity() as Drone;

        private static Drone GetMountedDrone(BasePlayer player, out BaseMountable currentSeat)
        {
            currentSeat = player.GetMounted();
            if (currentSeat == null)
                return null;

            return currentSeat.PrefabName == PilotSeatPrefab || currentSeat.PrefabName == PassengerSeatPrefab
                ? GetParentDrone(currentSeat)
                : null;
        }

        private static Drone GetMountedDrone(BasePlayer player)
        {
            BaseMountable currentSeat;
            return GetMountedDrone(player, out currentSeat);
        }

        private static bool HasIncompabitleAttachment(Drone drone) =>
            drone.GetSlot(SeatSlot) != null;

        private static bool TryGetSeats(Drone drone, out BaseMountable pilotSeat, out BaseMountable passengerSeat, out BaseMountable visibleSeat)
        {
            pilotSeat = null;
            passengerSeat = null;
            visibleSeat = null;

            foreach (var child in drone.children)
            {
                var mountable = child as BaseMountable;
                if (mountable == null)
                    continue;

                if (mountable.PrefabName == PilotSeatPrefab)
                    pilotSeat = mountable;

                if (mountable.PrefabName == PassengerSeatPrefab)
                    passengerSeat = mountable;

                if (mountable.PrefabName == VisibleSeatPrefab)
                    visibleSeat = mountable;
            }

            return pilotSeat != null && passengerSeat != null && visibleSeat != null;
        }

        private static bool TryGetSeats(Drone drone, out BaseMountable pilotSeat, out BaseMountable passengerSeat)
        {
            BaseMountable visibleSeat;
            return TryGetSeats(drone, out pilotSeat, out passengerSeat, out visibleSeat);
        }

        private static bool HasSeat(Drone drone)
        {
            BaseMountable pilotSeat, passengerSeat;
            return TryGetSeats(drone, out pilotSeat, out passengerSeat);
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
                UnityEngine.Object.DestroyImmediate(collider);
        }

        private static void SetupSeat(BaseMountable mountable)
        {
            mountable.isMobile = true;
            mountable.EnableSaving(true);
            RemoveProblemComponents(mountable);
        }

        private static void SetupAllSeats(BaseMountable pilotSeat, BaseMountable passengerSeat, BaseMountable visibleSeat)
        {
            SetupSeat(pilotSeat);
            SetupSeat(passengerSeat);
            SetupSeat(visibleSeat);

            pilotSeat.dismountPositions = passengerSeat.dismountPositions;

            // Damage will be processed by the drone.
            passengerSeat.baseProtection = null;
        }

        private static BaseEntity GetLookEntity(BasePlayer basePlayer, float maxDistance = 3)
        {
            RaycastHit hit;
            return Physics.Raycast(basePlayer.eyes.HeadRay(), out hit, maxDistance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)
                ? hit.GetEntity()
                : null;
        }

        private static void SwitchToSeat(BasePlayer player, BaseMountable currentSeat, BaseMountable desiredSeat)
        {
            currentSeat.DismountPlayer(player, lite: true);
            desiredSeat.MountPlayer(player);
        }

        private static BaseMountable TryDeploySeats(Drone drone, BasePlayer deployer = null)
        {
            if (DeploySeatWasBlocked(drone, deployer))
                return null;

            // The driver seat is ideal for mouse movement since it locks the player view angles.
            var pilotSeat = GameManager.server.CreateEntity(PilotSeatPrefab, PilotSeatLocalPosition) as BaseMountable;
            if (pilotSeat == null)
                return null;

            pilotSeat.SetParent(drone);
            pilotSeat.Spawn();

            // The passenger seat shows the "mount" prompt and allows for unlocking view angles.
            var passengerSeat = GameManager.server.CreateEntity(PassengerSeatPrefab, PassenterSeatLocalPosition) as BaseMountable;
            if (passengerSeat == null)
            {
                pilotSeat.Kill();
                return null;
            }

            passengerSeat.SetParent(drone);
            passengerSeat.Spawn();

            // This chair is visibile, even as the drone moves, but doesn't show a mount prompt.
            var visibleSeat = GameManager.server.CreateEntity(VisibleSeatPrefab, PassenterSeatLocalPosition) as BaseMountable;
            if (visibleSeat == null)
            {
                pilotSeat.Kill();
                passengerSeat.Kill();
                return null;
            }

            visibleSeat.SetParent(drone);
            visibleSeat.Spawn();

            SetupAllSeats(pilotSeat, passengerSeat, visibleSeat);

            // This signals to other plugins not to deploy entities here.
            drone.SetSlot(SeatSlot, passengerSeat);

            Effect.server.Run(ChairDeployEffectPrefab, passengerSeat.transform.position);
            Interface.CallHook("OnDroneSeatDeployed", drone, deployer);
            _pluginInstance.RefreshDronSettingsProfile(drone);
            _pluginInstance._mountableDronesTracker.AddDrone(drone);

            return passengerSeat;
        }

        private void MaybeCreateParentTrigger(Drone drone)
        {
            if (drone.OwnerID == 0 || !permission.UserHasPermission(drone.OwnerID.ToString(), PermissionRidable))
                return;

            if (CreateParentTriggerWasBlocked(drone))
                return;

            DroneParentTriggerComponent.AddToDroneOrRootEntity(drone, GetDroneScale(drone));
            Interface.CallHook("OnDroneParentTriggerCreated", drone);
        }

        private void MaybeAutoDeploySeat(Drone drone)
        {
            if (drone.OwnerID == 0
                || HasIncompabitleAttachment(drone)
                || !permission.UserHasPermission(drone.OwnerID.ToString(), PermissionSeatAutoDeploy))
                return;

            TryDeploySeats(drone);
        }

        private void MaybeAddOrRefreshSeats(Drone drone)
        {
            BaseMountable pilotSeat, passengerSeat, visibleSeat;
            if (!TryGetSeats(drone, out pilotSeat, out passengerSeat, out visibleSeat))
            {
                MaybeAutoDeploySeat(drone);
                return;
            }

            SetupAllSeats(pilotSeat, passengerSeat, visibleSeat);
            RefreshDronSettingsProfile(drone);
            _mountableDronesTracker.AddDrone(drone);
        }

        #endregion

        #region Drone Trackers

        // Tracks drones that have parent triggers.
        private class RidableDroneTracker
        {
            private HashSet<Drone> _ridableDrones = new HashSet<Drone>();

            public void AddDrone(Drone drone)
            {
                if (_ridableDrones.Add(drone) && _ridableDrones.Count == 1)
                {
                    _pluginInstance.Subscribe(nameof(OnEntityEnter));
                }
            }

            public void RemoveDrone(Drone drone)
            {
                if (_ridableDrones.Remove(drone) && _ridableDrones.Count == 0)
                {
                    _pluginInstance.Unsubscribe(nameof(OnEntityEnter));
                }
            }
        }

        // Tracks drones that have seats.
        private class MountableDroneTracker
        {
            private HashSet<Drone> _mountableDrones = new HashSet<Drone>();

            public void AddDrone(Drone drone)
            {
                if (_mountableDrones.Add(drone) && _mountableDrones.Count == 1)
                {
                    _pluginInstance.Subscribe(nameof(OnEntityTakeDamage));
                    _pluginInstance.Subscribe(nameof(OnEntityMounted));
                    _pluginInstance.Subscribe(nameof(OnEntityDismounted));
                }
            }

            public void RemoveDrone(Drone drone)
            {
                if (_mountableDrones.Remove(drone) && _mountableDrones.Count == 0)
                {
                    _pluginInstance.Unsubscribe(nameof(OnEntityTakeDamage));
                    _pluginInstance.Unsubscribe(nameof(OnEntityMounted));
                    _pluginInstance.Unsubscribe(nameof(OnEntityDismounted));
                }
            }
        }

        // Tracks drones that have a pilot-eligible player in them.
        private class MountedDroneTracker
        {
            private HashSet<Drone> _mountedDrones = new HashSet<Drone>();

            public void AddDrone(Drone drone)
            {
                if (_mountedDrones.Add(drone) && _mountedDrones.Count == 1)
                {
                    _pluginInstance.Subscribe(nameof(OnServerCommand));
                }
            }

            public void RemoveDrone(Drone drone)
            {
                if (_mountedDrones.Remove(drone) && _mountedDrones.Count == 0)
                {
                    _pluginInstance.Unsubscribe(nameof(OnServerCommand));
                }
            }
        }

        #endregion

        #region Parent Trigger

        private class TriggerParentEnclosedIgnoreSelf : TriggerParentEnclosed
        {
            public BaseEntity _thisEntity;

            private void Awake()
            {
                _thisEntity = gameObject.ToBaseEntity();
            }

            protected override bool ShouldParent(BaseEntity entity)
            {
                // This avoids the drone trying to parent itself when using the Targetable Drones plugin.
                // Targetable Drones uses a child object with the player layer, which the parent trigger is interested in.
                if (entity == _thisEntity)
                    return false;

                return base.ShouldParent(entity);
            }
        }

        private class DroneParentTriggerComponent : EntityComponent<BaseEntity>
        {
            public static void AddToDrone(Drone drone, float scale) =>
                drone.GetOrAddComponent<DroneParentTriggerComponent>().InitForDrone(drone, scale);

            public static void AddToRootEntity(Drone drone, BaseEntity rootEntity, float scale) =>
                rootEntity.GetOrAddComponent<DroneParentTriggerComponent>().InitForDrone(drone, scale);

            public static void AddToDroneOrRootEntity(Drone drone, float scale)
            {
                GetDroneOrRootEntity(drone).GetOrAddComponent<DroneParentTriggerComponent>().InitForDrone(drone, scale);
                _pluginInstance._ridableDronesTracker.AddDrone(drone);
            }

            public static void DestroyAll()
            {
                foreach (var entity in BaseNetworkable.serverEntities)
                {
                    var drone = entity as Drone;
                    if (drone == null)
                        continue;

                    var component = GetDroneOrRootEntity(drone).GetComponent<DroneParentTriggerComponent>();
                    if (component == null)
                        continue;

                    DestroyImmediate(component);
                }
            }

            // Scalable vertical offset from the drone where the trigger should be created.
            private static readonly Vector3 LocalPosition = new Vector3(0, 0.05f, 0);

            // Minimum extents, regardless of scale.
            // Note: These are based on default drone size, so they aren't the best for baby drones.
            private static readonly Vector3 MinExtents = new Vector3(0.75f, 1.8f, 0.75f);

            public Drone OwnerDrone { get; private set; }

            private GameObject _child;
            private BoxCollider _triggerCollider;

            public void SetScale(float scale)
            {
                var childTransform = _child.transform;
                childTransform.localScale = new Vector3(scale, 1, scale);
                childTransform.localPosition = LocalPosition;

                if (baseEntity != OwnerDrone && _pluginInstance.DroneScaleManager != null)
                {
                    // Position the trigger relative to the drone.
                    // This accounts for the root entity being offset from the drone, as well as drone scale.
                    var result = _pluginInstance.DroneScaleManager.Call("API_ParentTransform", OwnerDrone, childTransform);
                    var success = result is bool && (bool)result;
                    if (!success)
                        _pluginInstance.LogError($"Unable to position parent trigger relative to resized drone {OwnerDrone.net.ID}.");
                }

                // Move the transform upward so it begins at the drone instead of centering on the drone.
                // This is done after positioning relative to the drone since we want to take into account the scaled position.
                childTransform.localPosition += new Vector3(0, MinExtents.y / 2, 0);
            }

            private DroneParentTriggerComponent InitForDrone(Drone drone, float scale)
            {
                OwnerDrone = drone;
                EnsureParentTrigger(scale);
                return this;
            }

            private void EnsureParentTrigger(float scale = 1)
            {
                if (_child != null)
                    return;

                _child = gameObject.CreateChild();
                SetScale(scale);

                // Without this hack, the drone's sweep test can collide with other entities using the
                // parent trigger collider, causing the drone to ocassionally reduce altitude.
                _child.GetOrAddComponent<Rigidbody>().isKinematic = true;

                _triggerCollider = _child.gameObject.AddComponent<BoxCollider>();
                _triggerCollider.isTrigger = true;
                _triggerCollider.gameObject.layer = (int)Rust.Layer.Trigger;

                var extents = OwnerDrone.bounds.extents;
                _triggerCollider.size = new Vector3(
                    Math.Max(extents.x, MinExtents.x / scale),
                    Math.Max(extents.y, MinExtents.y),
                    Math.Max(extents.z, MinExtents.z / scale)
                );

                var triggerParent = _child.AddComponent<TriggerParentEnclosedIgnoreSelf>();
                triggerParent.intersectionMode = TriggerParentEnclosed.TriggerMode.PivotPoint;
                triggerParent.interestLayers = Rust.Layers.Mask.Player_Server;
            }

            private void OnDestroy()
            {
                if (_child != null)
                    Destroy(_child);
            }
        }

        #endregion

        #region DroneController

        private class DismountHelper : EntityComponent<BasePlayer>
        {
            public static void Mount(BasePlayer player, Drone drone) =>
                player.GetOrAddComponent<DismountHelper>().OnMount(drone);

            public static void Dismount(BasePlayer player, Drone drone) =>
                player.GetComponent<DismountHelper>()?.OnDismount();

            public static void RemoveFromPlayer(BasePlayer player) =>
                DestroyImmediate(player.GetComponent<DismountHelper>());

            private Drone _drone;

            private void DelayedDestroy() => DestroyImmediate(this);

            private void OnMount(Drone drone)
            {
                // If they were swapping seats, cancel destroying this component.
                CancelInvoke(DelayedDestroy);

                _drone = drone;
            }

            // Don't destroy the component immediately, in case the player is swapping seats.
            private void OnDismount() => Invoke(DelayedDestroy, 0);

            private void Update()
            {
                var mountable = baseEntity.GetMounted();
                if (mountable == null)
                {
                    OnDismount();
                    return;
                }

                var input = baseEntity.serverInput;
                if (!input.WasJustPressed(BUTTON.JUMP))
                    return;

                if (mountable.HasValidDismountPosition(baseEntity))
                    return;

                var droneTransform = _drone.transform;
                if (Vector3.Dot(Vector3.up, droneTransform.up) > 0.1f)
                    return;

                // Player failed to dismount, and drone is at a bad angle, flip it upright.
                droneTransform.rotation = Quaternion.identity;
            }
        }

        private class DroneController : EntityComponent<BasePlayer>
        {
            public static bool Exists(BasePlayer player) =>
                player.GetComponent<DroneController>() != null;

            public static void Mount(BasePlayer player, Drone drone, bool isPilotSeat)
            {
                player.GetOrAddComponent<DroneController>().OnMount(drone, isPilotSeat);
                _pluginInstance._mountedDroneTracker.AddDrone(drone);
            }

            public static void Dismount(BasePlayer player, Drone drone)
            {
                player.GetComponent<DroneController>()?.OnDismount();
                _pluginInstance._mountedDroneTracker.RemoveDrone(drone);
            }

            public static void RemoveFromPlayer(BasePlayer player) =>
                DestroyImmediate(player.GetComponent<DroneController>());

            private Drone _drone;
            private bool _isPilotSeat;

            private void DelayedDestroy() => DestroyImmediate(this);

            private void OnMount(Drone drone, bool isPilotSeat)
            {
                // If they were swapping seats, cancel destroying this component.
                CancelInvoke(DelayedDestroy);

                _drone = drone;
                _isPilotSeat = isPilotSeat;
                drone.InitializeControl(baseEntity);
                Interface.CallHook("OnDroneControlStarted", drone, baseEntity);
            }

            // Don't destroy the component immediately, in case the player is swapping seats.
            private void OnDismount() => Invoke(DelayedDestroy, 0);

            private void Update()
            {
                if (_drone == null || _drone.IsDestroyed)
                {
                    DestroyImmediate(this);
                    return;
                }

                _drone.UserInput(baseEntity.serverInput, baseEntity);
            }

            private void OnDestroy()
            {
                if (_drone != null && !_drone.IsDestroyed)
                {
                    _drone.StopControl();
                    Interface.CallHook("OnDroneControlEnded", _drone, baseEntity);
                }
            }
        }

        #endregion

        #region Configuration

        private class Configuration : SerializableConfiguration
        {
            [JsonProperty("TipChance")]
            public int TipChance = 25;
        }

        private Configuration GetDefaultConfig() => new Configuration();

        #endregion

        #region Configuration Boilerplate

        private class SerializableConfiguration
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

        private bool MaybeUpdateConfig(SerializableConfiguration config)
        {
            var currentWithDefaults = config.ToDictionary();
            var currentRaw = Config.ToDictionary(x => x.Key, x => x.Value);
            return MaybeUpdateConfigDict(currentWithDefaults, currentRaw);
        }

        private bool MaybeUpdateConfigDict(Dictionary<string, object> currentWithDefaults, Dictionary<string, object> currentRaw)
        {
            bool changed = false;

            foreach (var key in currentWithDefaults.Keys)
            {
                object currentRawValue;
                if (currentRaw.TryGetValue(key, out currentRawValue))
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

        protected override void LoadDefaultConfig() => _pluginConfig = GetDefaultConfig();

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _pluginConfig = Config.ReadObject<Configuration>();
                if (_pluginConfig == null)
                {
                    throw new JsonException();
                }

                if (MaybeUpdateConfig(_pluginConfig))
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
            Config.WriteObject(_pluginConfig, true);
        }

        #endregion

        #region Localization

        private void ReplyToPlayer(IPlayer player, string messageName, params object[] args) =>
            player.Reply(string.Format(GetMessage(player.Id, messageName), args));

        private void ChatMessage(BasePlayer player, string messageName, params object[] args) =>
            player.ChatMessage(string.Format(GetMessage(player.UserIDString, messageName), args));

        private string GetMessage(string playerId, string messageName, params object[] args)
        {
            var message = lang.GetMessage(messageName, this, playerId);
            return args.Length > 0 ? string.Format(message, args) : message;
        }

        private class Lang
        {
            public const string TipDeployCommand = "Tip.DeployCommand";
            public const string ErrorNoPermission = "Error.NoPermission";
            public const string ErrorNoDroneFound = "Error.NoDroneFound";
            public const string ErrorNoChairItem = "Error.NoChairItem";
            public const string ErrorAlreadyHasSeat = "Error.AlreadyHasChair";
            public const string ErrorIncompatibleAttachment = "Error.IncompatibleAttachment";
            public const string ErrorDeployFailed = "Error.DeployFailed";
        }

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                [Lang.TipDeployCommand] = "Tip: Look at the drone and run <color=yellow>/droneseat</color> to deploy a seat.",
                [Lang.ErrorNoPermission] = "You don't have permission to do that.",
                [Lang.ErrorNoDroneFound] = "Error: No drone found.",
                [Lang.ErrorNoChairItem] = "Error: You need a chair to do that.",
                [Lang.ErrorAlreadyHasSeat] = "Error: That drone already has a seat.",
                [Lang.ErrorIncompatibleAttachment] = "Error: That drone has an incompatible attachment.",
                [Lang.ErrorDeployFailed] = "Error: Failed to deploy seat.",
            }, this, "en");
        }

        #endregion
    }
}
