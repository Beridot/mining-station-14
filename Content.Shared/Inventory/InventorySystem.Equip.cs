using System.Diagnostics.CodeAnalysis;
using Content.Shared.ActionBlocker;
using Content.Shared.Clothing.Components;
using Content.Shared.Hands;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Interaction.Events;
using Content.Shared.Inventory.Events;
using Content.Shared.Item;
using Content.Shared.Movement.Systems;
using Content.Shared.Popups;
using Content.Shared.Strip.Components;
using Content.Shared.Body.Components;
using Robust.Shared.Audio;
using Robust.Shared.Containers;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Timing;
using Content.Shared.Body.Part;

namespace Content.Shared.Inventory;

public abstract partial class InventorySystem
{
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly MovementSpeedModifierSystem _movementSpeed = default!;
    [Dependency] private readonly SharedInteractionSystem _interactionSystem = default!;
    [Dependency] private readonly SharedContainerSystem _containerSystem = default!;
    [Dependency] private readonly SharedHandsSystem _handsSystem = default!;
    [Dependency] private readonly IGameTiming _gameTiming = default!;
    [Dependency] private readonly INetManager _netMan = default!;

    private void InitializeEquip()
    {
        //these events ensure that the client also gets its proper events raised when getting its containerstate updated
        SubscribeLocalEvent<InventoryComponent, EntInsertedIntoContainerMessage>(OnEntInserted);
        SubscribeLocalEvent<InventoryComponent, EntRemovedFromContainerMessage>(OnEntRemoved);
        SubscribeLocalEvent<InventoryComponent, BodyPartRemovedEvent>(OnBodyPartRemoved);

        SubscribeAllEvent<UseSlotNetworkMessage>(OnUseSlot);
    }

    protected void QuickEquip(EntityUid uid, ClothingComponent component, UseInHandEvent args)
    {
        if (!TryComp(args.User, out InventoryComponent? inv)
            || !TryComp(args.User, out SharedHandsComponent? hands)
            || !_prototypeManager.TryIndex<InventoryTemplatePrototype>(inv.TemplateId, out var prototype))
            return;

        foreach (var slotDef in prototype.Slots)
        {
            if (!CanEquip(args.User, uid, slotDef.Name, out _, slotDef, inv))
                continue;

            if (TryGetSlotEntity(args.User, slotDef.Name, out var slotEntity, inv))
            {
                // Item in slot has to be quick equipable as well
                if (TryComp(slotEntity, out ClothingComponent? item) && !item.QuickEquip)
                    continue;

                if (!TryUnequip(args.User, slotDef.Name, true, inventory: inv))
                    continue;

                if (!TryEquip(args.User, uid, slotDef.Name, true, inventory: inv))
                    continue;

                _handsSystem.PickupOrDrop(args.User, slotEntity.Value);
            }
            else
            {
                if (!TryEquip(args.User, uid, slotDef.Name, true, inventory: inv))
                    continue;
            }

            args.Handled = true;
            break;
        }
    }

    private void OnEntRemoved(EntityUid uid, InventoryComponent component, EntRemovedFromContainerMessage args)
    {
        if(!TryGetSlot(uid, args.Container.ID, out var slotDef, inventory: component))
            return;

        var unequippedEvent = new DidUnequipEvent(uid, args.Entity, slotDef);
        RaiseLocalEvent(uid, unequippedEvent, true);

        var gotUnequippedEvent = new GotUnequippedEvent(uid, args.Entity, slotDef);
        RaiseLocalEvent(args.Entity, gotUnequippedEvent, true);
    }

    private void OnEntInserted(EntityUid uid, InventoryComponent component, EntInsertedIntoContainerMessage args)
    {
        if(!TryGetSlot(uid, args.Container.ID, out var slotDef, inventory: component))
           return;

        var equippedEvent = new DidEquipEvent(uid, args.Entity, slotDef);
        RaiseLocalEvent(uid, equippedEvent, true);

        var gotEquippedEvent = new GotEquippedEvent(uid, args.Entity, slotDef);
        RaiseLocalEvent(args.Entity, gotEquippedEvent, true);
    }

    private void OnBodyPartRemoved(EntityUid uid, InventoryComponent component, ref BodyPartRemovedEvent args)
    {
        var bodyPartTypes = GetBodyPartTypes(uid);

        if (!TryComp(uid, out InventoryComponent? inv)
            || !_prototypeManager.TryIndex<InventoryTemplatePrototype>(inv.TemplateId, out var prototype))
            return;

        foreach (var slotDef in prototype.Slots)
        {

            if(slotDef.BodyPart != null && !bodyPartTypes.Contains(slotDef.BodyPart))
            {
                TryUnequip(uid, slotDef.Name, true, true, false);
            }
            
        }

    }

    /// <summary>
    ///     Will attempt to equip or unequip an item to/from the clicked slot. If the user clicked on an occupied slot
    ///     with some entity, will instead attempt to interact with this entity.
    /// </summary>
    private void OnUseSlot(UseSlotNetworkMessage ev, EntitySessionEventArgs eventArgs)
    {
        if (eventArgs.SenderSession.AttachedEntity is not { Valid: true } actor)
            return;

        if (!TryComp(actor, out InventoryComponent? inventory) || !TryComp<SharedHandsComponent>(actor, out var hands))
            return;

        var held = hands.ActiveHandEntity;
        TryGetSlotEntity(actor, ev.Slot, out var itemUid, inventory);

        // attempt to perform some interaction
        if (held != null && itemUid != null)
        {
            _interactionSystem.InteractUsing(actor, held.Value, itemUid.Value,
                Transform(itemUid.Value).Coordinates);
            return;
        }

        // unequip the item.
        if (itemUid != null)
        {
            if (!TryUnequip(actor, ev.Slot, out var item, predicted: true, inventory: inventory))
                return;

            _handsSystem.PickupOrDrop(actor, item.Value);
            return;
        }

        // finally, just try to equip the held item.
        if (held == null)
            return;

        // before we drop the item, check that it can be equipped in the first place.
        if (!CanEquip(actor, held.Value, ev.Slot, out var reason))
        {
            if (_gameTiming.IsFirstTimePredicted)
                _popup.PopupCursor(Loc.GetString(reason));
            return;
        }

        if (!_handsSystem.CanDropHeld(actor, hands.ActiveHand!, checkActionBlocker: false))
            return;

        var gotUnequipped = new GotUnequippedHandEvent(actor, held.Value, hands.ActiveHand!);
        var didUnequip = new DidUnequipHandEvent(actor, held.Value, hands.ActiveHand!);
        RaiseLocalEvent(held.Value, gotUnequipped, false);
        RaiseLocalEvent(actor, didUnequip, true);
        RaiseLocalEvent(held.Value, new HandDeselectedEvent(actor), false);

        TryEquip(actor, actor, held.Value, ev.Slot, predicted: true, inventory: inventory, force: true);
    }

    public bool TryEquip(EntityUid uid, EntityUid itemUid, string slot, bool silent = false, bool force = false, bool predicted = false,
        InventoryComponent? inventory = null, ClothingComponent? clothing = null) =>
        TryEquip(uid, uid, itemUid, slot, silent, force, predicted, inventory, clothing);

    public bool TryEquip(EntityUid actor, EntityUid target, EntityUid itemUid, string slot, bool silent = false, bool force = false, bool predicted = false,
        InventoryComponent? inventory = null, ClothingComponent? clothing = null)
    {
        if (!Resolve(target, ref inventory, false))
        {
            if(!silent && _gameTiming.IsFirstTimePredicted)
                _popup.PopupCursor(Loc.GetString("inventory-component-can-equip-cannot"));
            return false;
        }

        // Not required to have, since pockets can take any item.
        // CanEquip will still check, so we don't have to worry about it.
        Resolve(itemUid, ref clothing, false);

        if (!TryGetSlotContainer(target, slot, out var slotContainer, out var slotDefinition, inventory))
        {
            if(!silent && _gameTiming.IsFirstTimePredicted)
                _popup.PopupCursor(Loc.GetString("inventory-component-can-equip-cannot"));
            return false;
        }

        if (!force && !CanEquip(actor, target, itemUid, slot, out var reason, slotDefinition, inventory, clothing))
        {
            if(!silent && _gameTiming.IsFirstTimePredicted)
                _popup.PopupCursor(Loc.GetString(reason));
            return false;
        }

        if (!slotContainer.Insert(itemUid))
        {
            if(!silent && _gameTiming.IsFirstTimePredicted)
                _popup.PopupCursor(Loc.GetString("inventory-component-can-unequip-cannot"));
            return false;
        }

        if(!silent && clothing != null && clothing.EquipSound != null && _gameTiming.IsFirstTimePredicted)
        {
            Filter filter;

            if (_netMan.IsClient)
                filter = Filter.Local();
            else
            {
                filter = Filter.Pvs(target);

                // don't play double audio for predicted interactions
                if (predicted)
                    filter.RemoveWhereAttachedEntity(entity => entity == actor);
            }

            SoundSystem.Play(clothing.EquipSound.GetSound(), filter, target, clothing.EquipSound.Params.WithVolume(-2f));
        }

        inventory.Dirty();

        _movementSpeed.RefreshMovementSpeedModifiers(target);

        return true;
    }

    public bool CanAccess(EntityUid actor, EntityUid target, EntityUid itemUid)
    {
        // if the item is something like a hardsuit helmet, it may be contained within the hardsuit.
        // in that case, we check accesibility for the owner-entity instead.
        if (TryComp(itemUid, out AttachedClothingComponent? attachedComp))
            itemUid = attachedComp.AttachedUid;

        // Can the actor reach the target?
        if (actor != target && !(_interactionSystem.InRangeUnobstructed(actor, target) && _containerSystem.IsInSameOrParentContainer(actor, target)))
            return false;

        // Can the actor reach the item?
        if (_interactionSystem.InRangeUnobstructed(actor, itemUid) && _containerSystem.IsInSameOrParentContainer(actor, itemUid))
            return true;

        // Is the item in an open storage UI, i.e., is the user quick-equipping from an open backpack?
        if (_interactionSystem.CanAccessViaStorage(actor, itemUid))
            return true;

        // Is the actor currently stripping the target? Here we could check if the actor has the stripping UI open, but
        // that requires server/client specific code.
        // Uhhh TODO, fix this. This doesn't even fucking check if the target item is IN the targets inventory.
        return actor != target &&
            HasComp<SharedStrippableComponent>(target) &&
            HasComp<SharedStrippingComponent>(actor) &&
            HasComp<SharedHandsComponent>(actor);
    }

    private List<BodyPartSlot> GetBodyPartSlots(EntityUid? bodyPart)
    {
        List<BodyPartSlot> bodyPartSlots = new List<BodyPartSlot>();

        if (TryComp<BodyPartComponent>(bodyPart, out var bodyPartComp))
        {
            if (bodyPartComp.Children != null)
            {
                foreach (KeyValuePair<string, BodyPartSlot> partSlot in bodyPartComp.Children)
                {
                    bodyPartSlots.Add(partSlot.Value);
                }
            }
        }

        return bodyPartSlots;
    }

    private List<string> GetBodyPartTypes(EntityUid bodyOwner)
    {
        EntityUid? rootPart;

        //using body uid, get root part slot's child (usually the torso)
        if (TryComp<BodyComponent>(bodyOwner, out var body))
        {
            if (body.Root == null)
                return new List<string>();

            rootPart = body.Root.Child;

            if (rootPart == null)
                return new List<string>();

        }
        else if (TryComp<BodyPartComponent>(bodyOwner, out var bodyPart))
        {
            rootPart = bodyOwner;
        }
        else
            return new List<string>();

        //proceed to get all part slots
        var bodyPartList = GetBodyPartSlots(rootPart);
        List<BodyPartSlot> additionalPartList = new List<BodyPartSlot>();
        //then check all parts from that
        for (var i = 0; i < bodyPartList.Count; i++)
        {
            void RecursiveGetSlots(BodyPartSlot bodyPartSlot)
            {
                if (bodyPartSlot.Child == null)
                    return;

                var subPartList = GetBodyPartSlots(bodyPartSlot.Child);
                for (var j = 0; j < subPartList.Count; j++)
                {
                    RecursiveGetSlots(subPartList[j]);
                }
                additionalPartList.AddRange(subPartList);
            }
            RecursiveGetSlots(bodyPartList[i]);
        }
        bodyPartList.AddRange(additionalPartList);

        if (TryComp<BodyPartComponent>(rootPart, out var bodyPartComp) && bodyPartComp.ParentSlot != null)
        {
            bodyPartList.Add(bodyPartComp.ParentSlot);
        }
        else if (bodyPartComp != null && rootPart != null)
        {
            var tempSelfSlot = new BodyPartSlot("self", rootPart.Value, bodyPartComp.PartType, bodyPartComp.Species);
            tempSelfSlot.Child = rootPart;
            tempSelfSlot.IsRoot = true;
            bodyPartList.Add(tempSelfSlot);
        }

        List<string> bodyPartTypeList = new List<string>();
        foreach (var partSlot in bodyPartList)
        {
            if (partSlot.Child != null && partSlot.Type != null)
            {
                var bodyPartType = Enum.GetName(partSlot.Type.GetType(), partSlot.Type);
                if (bodyPartType != null)
                    bodyPartTypeList.Add(bodyPartType);
            }
        }

        return bodyPartTypeList;
    }

    public bool CanEquip(EntityUid uid, EntityUid itemUid, string slot, [NotNullWhen(false)] out string? reason,
        SlotDefinition? slotDefinition = null, InventoryComponent? inventory = null,
        ClothingComponent? clothing = null, ItemComponent? item = null) =>
        CanEquip(uid, uid, itemUid, slot, out reason, slotDefinition, inventory, clothing, item);

    public bool CanEquip(EntityUid actor, EntityUid target, EntityUid itemUid, string slot, [NotNullWhen(false)] out string? reason, SlotDefinition? slotDefinition = null,
        InventoryComponent? inventory = null, ClothingComponent? clothing = null, ItemComponent? item = null)
    {
        reason = "inventory-component-can-equip-cannot";
        if (!Resolve(target, ref inventory, false))
            return false;

        Resolve(itemUid, ref clothing, ref item, false);

        if (slotDefinition == null && !TryGetSlot(target, slot, out slotDefinition, inventory: inventory))
            return false;

        if (slotDefinition.DependsOn != null && !TryGetSlotEntity(target, slotDefinition.DependsOn, out _, inventory))
            return false;

        var bodyPartTypes = GetBodyPartTypes(target);

        if (slotDefinition.BodyPart != null && !bodyPartTypes.Contains(slotDefinition.BodyPart))
            return false;

        var fittingInPocket = slotDefinition.SlotFlags.HasFlag(SlotFlags.POCKET) && item is { Size: <= (int) ReferenceSizes.Pocket };
        if (clothing == null && !fittingInPocket
            || clothing != null && !clothing.Slots.HasFlag(slotDefinition.SlotFlags) && !fittingInPocket)
        {
            reason = "inventory-component-can-equip-does-not-fit";
            return false;
        }

        if (!CanAccess(actor, target, itemUid))
        {
            reason = "interaction-system-user-interaction-cannot-reach";
            return false;
        }

        if (slotDefinition.Whitelist != null && !slotDefinition.Whitelist.IsValid(itemUid))
        {
            reason = "inventory-component-can-equip-does-not-fit";
            return false;
        }

        if (slotDefinition.Blacklist != null && slotDefinition.Blacklist.IsValid(itemUid))
        {
            reason = "inventory-component-can-equip-does-not-fit";
            return false;
        }

        var attemptEvent = new IsEquippingAttemptEvent(actor, target, itemUid, slotDefinition);
        RaiseLocalEvent(target, attemptEvent, true);
        if (attemptEvent.Cancelled)
        {
            reason = attemptEvent.Reason ?? reason;
            return false;
        }

        if (actor != target)
        {
            //reuse the event. this is gucci, right?
            attemptEvent.Reason = null;
            RaiseLocalEvent(actor, attemptEvent, true);
            if (attemptEvent.Cancelled)
            {
                reason = attemptEvent.Reason ?? reason;
                return false;
            }
        }

        var itemAttemptEvent = new BeingEquippedAttemptEvent(actor, target, itemUid, slotDefinition);
        RaiseLocalEvent(itemUid, itemAttemptEvent, true);
        if (itemAttemptEvent.Cancelled)
        {
            reason = itemAttemptEvent.Reason ?? reason;
            return false;
        }

        return true;
    }

    public bool TryUnequip(EntityUid uid, string slot, bool silent = false, bool force = false, bool predicted = false,
        InventoryComponent? inventory = null, ClothingComponent? clothing = null) => TryUnequip(uid, uid, slot, silent, force, predicted, inventory, clothing);

    public bool TryUnequip(EntityUid actor, EntityUid target, string slot, bool silent = false,
        bool force = false, bool predicted = false, InventoryComponent? inventory = null, ClothingComponent? clothing = null) =>
        TryUnequip(actor, target, slot, out _, silent, force, predicted, inventory, clothing);

    public bool TryUnequip(EntityUid uid, string slot, [NotNullWhen(true)] out EntityUid? removedItem, bool silent = false, bool force = false, bool predicted = false,
        InventoryComponent? inventory = null, ClothingComponent? clothing = null) => TryUnequip(uid, uid, slot, out removedItem, silent, force, predicted, inventory, clothing);

    public bool TryUnequip(EntityUid actor, EntityUid target, string slot, [NotNullWhen(true)] out EntityUid? removedItem, bool silent = false,
        bool force = false, bool predicted = false, InventoryComponent? inventory = null, ClothingComponent? clothing = null)
    {
        removedItem = null;
        if (!Resolve(target, ref inventory, false))
        {
            if(!silent && _gameTiming.IsFirstTimePredicted)
                _popup.PopupCursor(Loc.GetString("inventory-component-can-unequip-cannot"));
            return false;
        }

        if (!TryGetSlotContainer(target, slot, out var slotContainer, out var slotDefinition, inventory))
        {
            if(!silent && _gameTiming.IsFirstTimePredicted)
                _popup.PopupCursor(Loc.GetString("inventory-component-can-unequip-cannot"));
            return false;
        }

        removedItem = slotContainer.ContainedEntity;

        if (!removedItem.HasValue) return false;

        if (!force && !CanUnequip(actor, target, slot, out var reason, slotContainer, slotDefinition, inventory))
        {
            if(!silent && _gameTiming.IsFirstTimePredicted)
                _popup.PopupCursor(Loc.GetString(reason));
            return false;
        }

        //we need to do this to make sure we are 100% removing this entity, since we are now dropping dependant slots
        if (!force && !slotContainer.CanRemove(removedItem.Value))
            return false;

        foreach (var slotDef in GetSlots(target, inventory))
        {
            if (slotDef != slotDefinition && slotDef.DependsOn == slotDefinition.Name)
            {
                //this recursive call might be risky
                TryUnequip(actor, target, slotDef.Name, true, true, predicted, inventory);
            }
        }

        if (force)
        {
            slotContainer.ForceRemove(removedItem.Value);
        }
        else
        {
            if (!slotContainer.Remove(removedItem.Value))
            {
                //should never happen bc of the canremove lets just keep in just in case
                return false;
            }
        }

        Transform(removedItem.Value).Coordinates = Transform(target).Coordinates;

        if (!silent && Resolve(removedItem.Value, ref clothing, false) && clothing.UnequipSound != null && _gameTiming.IsFirstTimePredicted)
        {
            Filter filter;

            if (_netMan.IsClient)
                filter = Filter.Local();
            else
            {
                filter = Filter.Pvs(target);

                // don't play double audio for predicted interactions
                if (predicted)
                    filter.RemoveWhereAttachedEntity(entity => entity == actor);
            }

            SoundSystem.Play(clothing.UnequipSound.GetSound(), filter, target, clothing.UnequipSound.Params.WithVolume(-2f));
        }

        inventory.Dirty();

        _movementSpeed.RefreshMovementSpeedModifiers(target);

        return true;
    }

    public bool CanUnequip(EntityUid uid, string slot, [NotNullWhen(false)] out string? reason,
        ContainerSlot? containerSlot = null, SlotDefinition? slotDefinition = null,
        InventoryComponent? inventory = null) =>
        CanUnequip(uid, uid, slot, out reason, containerSlot, slotDefinition, inventory);

    public bool CanUnequip(EntityUid actor, EntityUid target, string slot, [NotNullWhen(false)] out string? reason, ContainerSlot? containerSlot = null, SlotDefinition? slotDefinition = null, InventoryComponent? inventory = null)
    {
        reason = "inventory-component-can-unequip-cannot";
        if (!Resolve(target, ref inventory, false))
            return false;

        if ((containerSlot == null || slotDefinition == null) && !TryGetSlotContainer(target, slot, out containerSlot, out slotDefinition, inventory))
            return false;

        if (containerSlot.ContainedEntity == null)
            return false;

        if (!containerSlot.ContainedEntity.HasValue || !containerSlot.CanRemove(containerSlot.ContainedEntity.Value))
            return false;

        var itemUid = containerSlot.ContainedEntity.Value;

        // make sure the user can actually reach the target
        if (!CanAccess(actor, target, itemUid))
        {
            reason = "interaction-system-user-interaction-cannot-reach";
            return false;
        }

        var attemptEvent = new IsUnequippingAttemptEvent(actor, target, itemUid, slotDefinition);
        RaiseLocalEvent(target, attemptEvent, true);
        if (attemptEvent.Cancelled)
        {
            reason = attemptEvent.Reason ?? reason;
            return false;
        }

        if (actor != target)
        {
            //reuse the event. this is gucci, right?
            attemptEvent.Reason = null;
            RaiseLocalEvent(actor, attemptEvent, true);
            if (attemptEvent.Cancelled)
            {
                reason = attemptEvent.Reason ?? reason;
                return false;
            }
        }

        var itemAttemptEvent = new BeingUnequippedAttemptEvent(actor, target, itemUid, slotDefinition);
        RaiseLocalEvent(itemUid, itemAttemptEvent, true);
        if (itemAttemptEvent.Cancelled)
        {
            reason = attemptEvent.Reason ?? reason;
            return false;
        }

        return true;
    }

    public bool TryGetSlotEntity(EntityUid uid, string slot, [NotNullWhen(true)] out EntityUid? entityUid, InventoryComponent? inventoryComponent = null, ContainerManagerComponent? containerManagerComponent = null)
    {
        entityUid = null;
        if (!Resolve(uid, ref inventoryComponent, ref containerManagerComponent, false)
            || !TryGetSlotContainer(uid, slot, out var container, out _, inventoryComponent, containerManagerComponent))
            return false;

        entityUid = container.ContainedEntity;
        return entityUid != null;
    }
}
