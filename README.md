## Features

- Allows players to ride RC drones as passengers by standing on them
  - Compatible with drones resized by Drone Scale Manager
- Allows players with permission to deploy seats to RC drones
- Allows players with permission to pilot RC drones while sitting in the seat

## Seating modes

When a player is sitting in a drone seat, they will be in one of three modes:

- **Pilot mode**
  - Controls drone movement
  - Locks player view angles to the drone, like a helicopter pilot seat
  - Does not allow holding items
- **Passenger mode**
  - Allows looking around freely
  - Allows holding items
- **Hybrid mode**
  - Controls drone movement
  - Allows looking around freely
  - Allows holding items

Players with the `ridabledrones.seat.pilot` permission will start in **Pilot** mode by default, and can switch between **Pilot** and **Hybrid** modes using the swap seats key (default: `X`). Players without that permission will be locked to passenger mode.

## Permissions

- `ridabledrones.ridable` -- Drones deployed by players with this permission will allow any player to ride the drone by standing on it.
  - Note: Another player must pilot the drone via a computer station, since this does not allow piloting the drone.
- `ridabledrones.seat.deploy` -- Allows the player to deploy a seat onto a drone using the `droneseat` command.
- `ridabledrones.seat.deploy.free` -- Allows using the `droneseat` command for free (no chair item required).
- `ridabledrones.seat.autodeploy` -- Drones deployed by players with this permission will automatically have a seat, free of charge.
  - Note: Reloading the plugin will automatically add seats to existing drones owned by players with this permission.
  - Not recommended if you want to allow players to deploy other attachments such as auto turrets since they are incompatible.
- `ridabledrones.seat.pilot` -- Allows the player to pilot the drone while sitting in its seat.

## Commands

- `droneseat` -- Deploys a seat onto the drone the player is looking at, consuming a chair item from their inventory unless they have permission for free seats.

## Configuration

Default configuration:

```json
{
  "TipChance": 25
}
```

- `TipChance` (`0` - `100`) -- Chance that a tip message will be shown to a player when they deploy a drone, informing them that they can use the `/droneseat` command. Only applies to players with the `ridabledrones.seat.deploy` permission who do not have the `ridabledrones.seat.autodeploy` permission.

## Localization

```json
{
  "Tip.DeployCommand": "Tip: Look at the drone and run <color=yellow>/droneseat</color> to deploy a seat.",
  "Error.NoPermission": "You don't have permission to do that.",
  "Error.NoDroneFound": "Error: No drone found.",
  "Error.NoChairItem": "Error: You need a chair to do that.",
  "Error.AlreadyHasChair": "Error: That drone already has a seat.",
  "Error.IncompatibleAttachment": "Error: That drone has an incompatible attachment.",
  "Error.DeployFailed": "Error: Failed to deploy seat."
}
```

## Recommended compatible plugins

Drone balance:
- [Drone Settings](https://umod.org/plugins/drone-settings) -- Allows changing speed, toughness and other properties of RC drones.
- [Targetable Drones](https://umod.org/plugins/targetable-drones) -- Allows RC drones to be targeted by Auto Turrets and SAM Sites.
- [Limited Drone Range](https://umod.org/plugins/limited-drone-range) -- Limits how far RC drones can be controlled from computer stations.

Drone fixes and improvements:
- [Better Drone Collision](https://umod.org/plugins/better-drone-collision) -- Overhauls RC drone collision damage so it's more intuitive.
- [Auto Flip Drones](https://umod.org/plugins/auto-flip-drones) -- Auto flips upside-down RC drones when a player takes control.
- [Drone Hover](https://umod.org/plugins/drone-hover) -- Allows RC drones to hover in place while not being controlled.

Drone attachments:
- [Drone Lights](https://umod.org/plugins/drone-lights) -- Adds controllable search lights to RC drones.
- [Drone Turrets](https://umod.org/plugins/drone-turrets) -- Allows players to deploy auto turrets to RC drones.
- [Drone Storage](https://umod.org/plugins/drone-storage) -- Allows players to deploy a small stash to RC drones.
- [Ridable Drones](https://umod.org/plugins/ridable-drones) (This plugin) -- Allows players to ride RC drones by standing on them or mounting a chair.

## Developer Hooks

#### OnDroneParentTriggerCreate

```csharp
bool? OnDroneParentTriggerCreate(Drone drone)
```

- Called when a parent trigger is about to be created on a drone
- Returning `false` will prevent the parent trigger from being created
- Returning `null` will result in the default behavior

#### OnDroneParentTriggerCreated

```csharp
void OnDroneParentTriggerCreated(Drone drone)
```

- Called after a parent trigger has been created on a drone
- No return behavior

#### OnDroneSeatDeploy

```csharp
bool? OnDroneSeatDeploy(Drone drone, BasePlayer optionalDeployer)
```

- Called when a seat is about to be deployed onto a drone
- Returning `false` will prevent the seat from being deployed
- Returning `null` will result in the default behavior

Note: The `BasePlayer` argument will be `null` if the seat is being deployed automatically (not via the `droneseat` command).

#### OnDroneSeatDeployed

```csharp
void OnDroneSeatDeployed(Drone drone, BasePlayer optionalDeployer)
```

- Called after a seat has been deployed onto a drone
- No return behavior

#### OnDroneControlStarted

```csharp
void OnDroneControlStarted(Drone drone, BasePlayer pilot)
```

- Called when a player mounts a drone seat while having the pilot permission
- Also called when a pilot switches seating modes
- No return behavior

#### OnDroneControlEnded

```csharp
void OnDroneControlEnded(Drone drone, BasePlayer pilot)
```

- Called after a pilot has dismounted a drone seat
- Also called when a pilot switches seating modes
- No return behavior
