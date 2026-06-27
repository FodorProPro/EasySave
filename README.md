# EasySave

EasySave is a small BepInEx 5 quality-of-life mod for **Easy Delivery Co.**
Press **F5** during gameplay to save immediately at the current car position.

The mod updates the game's native respawn checkpoint with the current scene and
car position, adds a small upward offset, and then calls the native save system.
It does **not** read or edit `EasyDeliveryCoSaveData.txt` directly.

Version 1.0.0 also writes `EasySaveState.json` beside the native save. This
mod-owned file stores the exact car transform and a stage-aware snapshot of the
active delivery. The JSON never replaces or edits the native save file.

## Requirements

- Easy Delivery Co.
- BepInEx 5 (Mono), installed manually or through r2modman
- .NET SDK and Mono reference assemblies for building on Linux

EasyDeliveryAPI is not required. The diagnostic `CompleteJob` logging uses the
Harmony library bundled with BepInEx 5.

## Build

From this directory, run:

```sh
dotnet build -c Release
```

The compiled plugin is created at:

```text
bin/Release/net472/EasySave.dll
```

Set the BepInEx and game managed-assembly paths when building if they differ
from your local project defaults:

```sh
dotnet build -c Release \
  -p:BepInExRoot="/path/to/profile/BepInEx" \
  -p:GameManagedRoot="/path/to/EasyDeliveryCo_Data/Managed"
```

## Install

Create an EasySave directory inside your BepInEx plugins directory and copy
the DLL into it:

```sh
mkdir -p "/path/to/BepInEx/plugins/EasySave"
cp "bin/Release/net472/EasySave.dll" "/path/to/BepInEx/plugins/EasySave/"
```

The build itself does not install files automatically.

## Usage

1. Launch Easy Delivery Co. with BepInEx enabled.
2. Load into gameplay and make sure the car is active.
3. Press **F5**.
4. A save notification should appear in the top-left corner.

F5 still performs the native game save first. Delivery checkpoint data is then
written atomically to:

```text
EasyDeliveryCo/EasySaveState.json
```

The BepInEx log records the saved scene index and checkpoint position. Pressing
F5 in a menu without an active car is safe and writes a warning to the log.

The notification uses a lightweight Unity UI overlay mirrored from the Weather
Forecast HUD onto the left side: white pixel text below an icon, without a panel.
It reuses the floppy-disk icon from the game's UI sprite sheet when that texture
is available, with a built-in pixel fallback for other scenes. It does not require
EasyDeliveryAPI and does not intercept player input.

The toast is tied to the active gameplay HUD. Opening pause/settings immediately
hides it, and no toast is created in title, loading, or menu-only screens.

### Stage-aware delivery restore

- `AcceptedGoToPickup` restores the job and resumes the game's own
  `CheckJobProgress` flow. It does not create a payload directly.
- `InTruckOrDelivering` and the in-truck form of `AtDestinationBeforeDelivery`
  rebuild the same vanilla structure: hidden `Placed In Truck`, a live payload
  under `payloadPivot`, destination GPS, and the game's completion wait.
- `PayloadActiveOrInHands` and destination payload-out snapshots are currently
  rejected safely. Restoring less state is preferable to creating an
  unpickable prop.
- The mod never calls `CompleteJob` during restore and never instantiates a raw
  payload prefab itself. Payload objects are created only by game methods.
- Cleanup targets only objects carrying `EasySavePayloadMarker` or the
  `EasySave_RestoredPayload` prefix. Vanilla props are never deleted.
- To disable extended restore for recovery, close the game and rename or remove
  `EasySaveState.json`. The native game save is a separate file and is untouched.

Delivery restore first looks for a compatible runtime job in `jobBoard.jobs` or
`jobBoard.jobsBackup`. If none exists, it reconstructs a controlled job from the
saved DTO without calling the randomizing game constructor. Saved endpoints,
payload prefab, price, mass, distance, bonus distance, duration, start time and
path are validated before use.

The game calculates the final payout from `InterSceneData` delivery metrics, not
directly from `selectedJob.price`. EasySave restores those metrics through the
game's own `SaveResultsData` method and verifies `CalculatePrice` against the job
price. It never edits money, invokes `CompleteJob`, or jumps into a private
coroutine state.

### Configuration

BepInEx creates `com.easydeliveryco.easysave.cfg`. The important defaults are:

```ini
EnableNativeSave = true
EnableCarCheckpoint = true
EnableDeliveryStateCapture = true
EnableDeliveryStateRestore = true
EnablePayloadRestore = true
EnableRouteRestore = true
EnableDiagnosticLogging = false
EnableEconomyDiagnosticLogging = true
RestoreTimeoutSeconds = 10
RestoreRetryIntervalSeconds = 0.25
BackupBeforeWrite = true
BackupBeforeRestore = true
DisableRestoreAfterFailure = true
```

Set `EnableDeliveryStateRestore = false` to retain only the stable native save,
car checkpoint, JSON capture, and toast behavior.

## In-game verification

1. Confirm the BepInEx log contains `EasySave 1.0.0 loaded`.
2. Press F5 during gameplay and confirm the notification and checkpoint log entry.
3. Confirm the native save file updates these values:
   - `deliveryCurrentLastMapBuildIndex`
   - `deliveryCurrentCheckpointPosition_X`
   - `deliveryCurrentCheckpointPosition_Y`
   - `deliveryCurrentCheckpointPosition_Z`
4. Save with no active delivery, reload, and confirm no payload is created.
5. Accept a job without picking up cargo, save/reload, drive to pickup, and confirm the game creates a pickup-able payload normally.
6. Save cargo in the truck, reload, and confirm exactly one active payload appears in `payloadPivot`, GPS targets the destination, and delivery completes once.
7. Confirm the restore log reports `jobSource=LiveJobs`, `LiveJobsBackup`, or `ReconstructedFromSave` and a sane price.
8. Complete the restored delivery and confirm the `CompleteJob prefix` price and actual payout match the saved job rather than the large default payout.
9. Save while cargo is active/in hands and confirm restore aborts without creating a prop payload.
10. Press F5/open pause or menu and confirm that the game does not crash or show the toast over menus.

## Current limitations

Version 1.0.0 uses a fixed F5 hotkey, a `+1.0` native checkpoint Y offset, and a
2.5-second notification duration. Active challenge deliveries and Stage 2 payloads
are not reconstructed because their live runtime references are not yet safe to
recover. The native save remains playable and the failure disables further delivery
restore attempts for the current session by default.
