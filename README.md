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

## FAQ

#### How do I get a drone?

As of this writing, RC drones are a deployable item named `drone`, but they do not appear naturally in any loot table, nor are they craftable. However, since they are simply an item, you can use plugins to add them to loot tables, kits, GUI shops, etc. Admins can also get them with the command `inventory.give drone 1`, or spawn one in directly with `spawn drone.deployed`.

#### How do I remote-control a drone?

If a player has building privilege, they can pull out a hammer and set the ID of the drone. They can then enter that ID at a computer station and select it to start controlling the drone. Controls are `W`/`A`/`S`/`D` to move, `shift` (sprint) to go up, `ctrl` (duck) to go down, and mouse to steer.

Note: If you are unable to steer the drone, that is likely because you have a plugin drawing a UI that is grabbing the mouse cursor. The Movable CCTV was previously guilty of this and was patched in March 2021.

## Recommended compatible plugins

- [Drone Hover](https://umod.org/plugins/drone-hover) -- Allows RC drones to hover in place while not being controlled.
- [Drone Lights](https://umod.org/plugins/drone-lights) -- Adds controllable search lights to RC drones.
- [Drone Storage](https://umod.org/plugins/drone-storage) -- Allows players to deploy a small stash to RC drones.
- [Drone Turrets](https://umod.org/plugins/drone-turrets) -- Allows players to deploy auto turrets to RC drones.
- [Drone Effects](https://umod.org/plugins/drone-effects) -- Adds collision effects and propeller animations to RC drones.
- [Auto Flip Drones](https://umod.org/plugins/auto-flip-drones) -- Auto flips upside-down RC drones when a player takes control.
- [RC Identifier Fix](https://umod.org/plugins/rc-identifier-fix) -- Auto updates RC identifiers saved in computer stations to refer to the correct entity.

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
