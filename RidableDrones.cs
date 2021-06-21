using Oxide.Core;
using Oxide.Core.Plugins;
using System;
using UnityEngine;
using VLB;

namespace Oxide.Plugins
{
    [Info("Ridable Drones", "WhiteThunder", "1.0.0")]
    [Description("Allows players to ride RC drones as passengers by standing on them.")]
    internal class RidableDrones : CovalencePlugin
    {
        #region Fields

        [PluginReference]
        Plugin DroneScaleManager, EntityScaleManager;

        private static RidableDrones _pluginInstance;

        private const string PermissionRidable = "ridabledrones.ridable";

        #endregion

        #region Hooks

        private void Init()
        {
            _pluginInstance = this;
            permission.RegisterPermission(PermissionRidable, this);
            Unsubscribe(nameof(OnEntitySpawned));
        }

        private void Unload()
        {
            DronePlatformComponent.DestroyAll();
            _pluginInstance = null;
        }

        private void OnServerInitialized()
        {
            foreach (var entity in BaseNetworkable.serverEntities)
            {
                var drone = entity as Drone;
                if (drone != null)
                    OnEntitySpawned(drone);
            }

            Subscribe(nameof(OnEntitySpawned));
        }

        private void OnEntitySpawned(Drone drone)
        {
            if (!IsDroneEligible(drone))
                return;

            AddOrUpdatePlatform(drone);
        }

        // Must hook before the drone is actually scaled, to move the parent trigger to the root entity.
        // This is done to prevent issues where the player observes the entity resizing while parented to it.
        private void OnDroneScaleBegin(Drone drone, BaseEntity rootEntity, float scale, float previousScale)
        {
            if (previousScale == 1)
            {
                // Drone is being resized from default size.
                var dronePlatform = drone.GetComponent<DronePlatformComponent>();
                if (dronePlatform == null)
                    return;

                // Move parent trigger from drone to root entity.
                UnityEngine.Object.DestroyImmediate(dronePlatform);
                DronePlatformComponent.AddToRootEntity(drone, rootEntity, scale);
                return;
            }

            // Drone is not default size.
            var rootPlatform = rootEntity.GetComponent<DronePlatformComponent>();
            if (rootPlatform == null)
                return;

            if (scale == 1)
            {
                // Drone is being resized to default size.
                // Move parent trigger to drone.
                UnityEngine.Object.DestroyImmediate(rootPlatform);
                drone.Invoke(() => DronePlatformComponent.AddToDrone(drone, scale), 0);
                return;
            }

            // Drone is being resized from a non-default size to a different non-default size.
            rootPlatform.SetScale(scale);
        }

        private bool? OnEntityEnter(TriggerParentEnclosed triggerParent, BasePlayer player)
        {
            var platform = triggerParent.GetComponentInParent<DronePlatformComponent>();
            if (platform == null)
                return null;

            var drone = platform.OwnerDrone;
            if (drone == null)
                return null;

            // Don't allow parenting if the drone is sideways or upside-down.
            // This helps avoid issues where an upside-down drone flips the camera around.
            // Note: This does not solve problems for players already parented.
            if (Vector3.Dot(Vector3.up, drone.transform.up) < 0.8f)
                return false;

            return null;
        }

        #endregion

        #region Helper Methods

        private static bool CreatePlatformWasBlocked(Drone drone)
        {
            object hookResult = Interface.CallHook("OnDroneParentTriggerCreate", drone);
            return hookResult is bool && (bool)hookResult == false;
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

        private static bool TryCreatePlatform(Drone drone)
        {
            if (CreatePlatformWasBlocked(drone))
                return false;

            DronePlatformComponent.AddToDroneOrRootEntity(drone, GetDroneScale(drone));
            Interface.CallHook("OnDroneParentTriggerCreated", drone);

            return true;
        }

        private void AddOrUpdatePlatform(Drone drone)
        {
            if (drone.OwnerID == 0 || !permission.UserHasPermission(drone.OwnerID.ToString(), PermissionRidable))
                return;

            TryCreatePlatform(drone);
        }

        #endregion

        #region Classes

        private class DronePlatformComponent : EntityComponent<BaseEntity>
        {
            public static DronePlatformComponent AddToDrone(Drone drone, float scale) =>
                drone.GetOrAddComponent<DronePlatformComponent>().SetDrone(drone, scale);

            public static DronePlatformComponent AddToRootEntity(Drone drone, BaseEntity rootEntity, float scale) =>
                rootEntity.GetOrAddComponent<DronePlatformComponent>().SetDrone(drone, scale);

            public static DronePlatformComponent AddToDroneOrRootEntity(Drone drone, float scale) =>
                GetDroneOrRootEntity(drone).GetOrAddComponent<DronePlatformComponent>().SetDrone(drone, scale);

            public static void DestroyAll()
            {
                foreach (var entity in BaseNetworkable.serverEntities)
                {
                    var drone = entity as Drone;
                    if (drone == null)
                        continue;

                    var droneOrRootEntity = GetDroneOrRootEntity(drone);

                    var platform = droneOrRootEntity.GetComponent<DronePlatformComponent>();
                    if (platform == null)
                        continue;

                    DestroyImmediate(platform);
                }
            }

            // Scalable vertical offset from the drone where the trigger should be created.
            private static readonly Vector3 LocalPosition = new Vector3(0, 0.05f, 0);

            // Minimum extents, regardless of scale.
            // Note: These are based on default drone size, so they aren't the best for baby drones.
            private static readonly Vector3 MinExtents = new Vector3(0.75f, 1.8f, 0.75f);

            public Drone OwnerDrone { get; private set; }

            private GameObject _child;
            private BoxCollider _collider;

            public DronePlatformComponent SetScale(float scale)
            {
                var childTransform = _child.transform;
                childTransform.localScale = new Vector3(scale, 1, scale);
                childTransform.localPosition = LocalPosition;

                if (baseEntity != OwnerDrone && _pluginInstance.DroneScaleManager != null)
                {
                    // Position the trigger relative to the drone.
                    // This accounts for the root entity being offset from the drone, as well as drone scale.
                    var result = _pluginInstance.DroneScaleManager.Call("API_ParentTransform", OwnerDrone, childTransform);
                    if (!(result is bool) || !(bool)result)
                        _pluginInstance.LogError($"Unable to position parent trigger relative to resized drone {OwnerDrone.net.ID}.");
                }

                // Move the transform upward so it begins at the drone instead of centering on the drone.
                // This is done after positioning relative to the drone since we want to take into account the scaled position.
                childTransform.localPosition += new Vector3(0, MinExtents.y / 2, 0);
                return this;
            }

            private DronePlatformComponent SetDrone(Drone drone, float scale)
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

                _collider = _child.gameObject.AddComponent<BoxCollider>();
                _collider.isTrigger = true;
                _collider.gameObject.layer = (int)Rust.Layer.Trigger;

                var extents = OwnerDrone.bounds.extents;
                _collider.size = new Vector3(
                    Math.Max(extents.x, MinExtents.x / scale),
                    Math.Max(extents.y, MinExtents.y),
                    Math.Max(extents.z, MinExtents.z / scale)
                );

                var triggerParent = _child.AddComponent<TriggerParentEnclosed>();
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
    }
}
