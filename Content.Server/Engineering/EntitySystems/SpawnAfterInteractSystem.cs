using Content.Server.Engineering.Components;
using Content.Server.Stack;
using Content.Shared.Coordinates.Helpers;
using Content.Shared.DoAfter;
using Content.Shared.Interaction;
using Content.Shared.Maps;
using Content.Shared.Stacks;
using Content.Shared.Whitelist;  // Exodus-FoldedPoster
using JetBrains.Annotations;
using Robust.Shared.Map.Components;

namespace Content.Server.Engineering.EntitySystems
{
    [UsedImplicitly]
    public sealed class SpawnAfterInteractSystem : EntitySystem
    {
        [Dependency] private readonly SharedDoAfterSystem _doAfterSystem = default!;
        [Dependency] private readonly StackSystem _stackSystem = default!;
        [Dependency] private readonly EntityWhitelistSystem _whitelistSystem = default!;  // Exodus-FoldedPoster

        public override void Initialize()
        {
            base.Initialize();

            SubscribeLocalEvent<SpawnAfterInteractComponent, AfterInteractEvent>(HandleAfterInteract);
        }

        private async void HandleAfterInteract(EntityUid uid, SpawnAfterInteractComponent component, AfterInteractEvent args)
        {
            // Exodus-FoldedPoster-Start
            if (component.UseWhitelist && !_whitelistSystem.IsValid(component.Whitelist, uid))
                return;
            // Exodus-FoldedPoster-End
            if (!args.CanReach && !component.IgnoreDistance)
                return;
            if (string.IsNullOrEmpty(component.Prototype))
                return;
            if (!TryComp<MapGridComponent>(args.ClickLocation.GetGridUid(EntityManager), out var grid))
                return;
            if (!grid.TryGetTileRef(args.ClickLocation, out var tileRef))
                return;

            bool IsTileClear()
            {
                return tileRef.Tile.IsEmpty == false && !tileRef.IsBlockedTurf(true);
            }

            if (component.NeedClearTile && !IsTileClear())  // Exodus-FoldedPoster
                return;

            if (component.DoAfterTime > 0)
            {
                var doAfterArgs = new DoAfterArgs(EntityManager, args.User, component.DoAfterTime, new AwaitedDoAfterEvent(), null)
                {
                    BreakOnMove = true,
                };
                var result = await _doAfterSystem.WaitDoAfter(doAfterArgs);

                if (result != DoAfterStatus.Finished)
                    return;
            }

            if (component.Deleted || (!IsTileClear() && component.NeedClearTile))  // Exodus-FoldedPoster
                return;

            if (EntityManager.TryGetComponent(uid, out StackComponent? stackComp)
                && component.RemoveOnInteract && !_stackSystem.Use(uid, 1, stackComp))
            {
                return;
            }

            EntityManager.SpawnEntity(component.Prototype, args.ClickLocation.SnapToGrid(grid));

            if (component.RemoveOnInteract && stackComp == null)
                QueueDel(uid); // Exodus-TemporalFix | Idk what's wrong but TryQueueDel right now doesn't working
        }
    }
}
