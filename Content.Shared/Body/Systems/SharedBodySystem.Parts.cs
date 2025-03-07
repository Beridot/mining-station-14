 using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Content.Shared.Body.Components;
using Content.Shared.Body.Events;
using Content.Shared.Body.Organ;
using Content.Shared.Body.Part;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Shared.Movement.Components;
using Content.Shared.Random.Helpers;
using Robust.Shared.Containers;
using Robust.Shared.GameStates;
using Robust.Shared.Map;
using Content.Shared.Humanoid;

namespace Content.Shared.Body.Systems;

public partial class SharedBodySystem
{
    private void InitializeParts()
    {
        SubscribeLocalEvent<BodyPartComponent, ComponentRemove>(OnPartRemoved);
        SubscribeLocalEvent<BodyPartComponent, ComponentGetState>(OnPartGetState);
        SubscribeLocalEvent<BodyPartComponent, ComponentHandleState>(OnPartHandleState);
    }

    private void OnPartGetState(EntityUid uid, BodyPartComponent part, ref ComponentGetState args)
    {
        args.State = new BodyPartComponentState(
            part.Body,
            part.OriginalBody,
            part.ParentSlot,
            part.Children,
            part.Organs,
            part.PartType,
            part.IsVital,
            part.Symmetry,
            part.Attachment,
            part.Container,
            part.Incisable,
            part.Incised,
            part.Opened,
            part.EndoSkeleton,
            part.ExoSkeleton,
            part.EndoOpened,
            part.ExoOpened,
            part.Working
        );
    }

    private void OnPartHandleState(EntityUid uid, BodyPartComponent part, ref ComponentHandleState args)
    {
        if (args.Current is not BodyPartComponentState state)
            return;

        part.Body = state.Body;
        part.OriginalBody = state.OriginalBody;
        part.ParentSlot = state.ParentSlot;
        part.Children = state.Children;
        part.Organs = state.Organs;
        part.PartType = state.PartType;
        part.IsVital = state.IsVital;
        part.Symmetry = state.Symmetry;
        part.Attachment = state.Attachment;
        part.Container = state.Container;
        part.Incisable = state.Incisable;
        part.Incised = state.Incised;
        part.Opened = state.Opened;
        part.EndoSkeleton = state.EndoSkeleton;
        part.ExoSkeleton = state.ExoSkeleton;
        part.EndoOpened = state.EndoOpened;
        part.ExoOpened = state.ExoOpened;
        part.Working = state.Working;
    }

    private void OnPartRemoved(EntityUid uid, BodyPartComponent part, ComponentRemove args)
    {
        if (part.ParentSlot is { } slot)
        {
            slot.Child = null;
            Dirty(slot.Parent);
        }

        foreach (var childSlot in part.Children.Values.ToArray())
        {
            DropPart(childSlot.Child);
        }
    }

    private BodyPartSlot? CreatePartSlot(
        string slotId,
        EntityUid parent,
        BodyPartType partType,
        string species,
        BodyPartComponent? part = null,
        Boolean? wearable = false)
    {
        if (!Resolve(parent, ref part, false))
            return null;

        var slot = new BodyPartSlot(slotId, parent, partType,species,wearable);
        part.Children.Add(slotId, slot);

        return slot;
    }

    public bool TryCreatePartSlot(
        EntityUid? parentId,
        string id,
        [NotNullWhen(true)] out BodyPartSlot? slot,
        BodyPartComponent? parent = null,
        Boolean? wearable = false)
    {
        slot = null;

        if (parentId == null ||
            !Resolve(parentId.Value, ref parent, false))
            return false;

        slot = new BodyPartSlot(id, parentId.Value, null, parent.Species, wearable);
        if (!parent.Children.TryAdd(id, slot))
        {
            slot = null;
            return false;
        }

        return true;
    }

    public bool TryRemovePartSlot(
        EntityUid parentId,
        string id,
        BodyPartComponent parent)
    {
        //do not allow removal of slot if it is occupied
        var slot = parent.Children[id];
        if (slot.Child is not null)
            return false;

        //otherwise, remove from parent part
        parent.Children.Remove(id);

        return true;
    }

    public bool TryCreatePartSlotAndAttach(
        EntityUid? parentId,
        string id,
        EntityUid? childId,
        BodyPartComponent? parent = null,
        BodyPartComponent? child = null)
    {
        return TryCreatePartSlot(parentId, id, out var slot, parent) && AttachPart(childId, slot, child);
    }

    public IEnumerable<(EntityUid Id, BodyPartComponent Component)> GetPartChildren(EntityUid? id, BodyPartComponent? part = null)
    {
        if (id == null || !Resolve(id.Value, ref part, false))
            yield break;

        foreach (var slot in part.Children.Values)
        {
            if (!TryComp(slot.Child, out BodyPartComponent? childPart))
                continue;

            yield return (slot.Child.Value, childPart);

            foreach (var subChild in GetPartChildren(slot.Child, childPart))
            {
                yield return subChild;
            }
        }
    }

    public IEnumerable<(EntityUid Id, OrganComponent Component)> GetPartOrgans(EntityUid? partId, BodyPartComponent? part = null)
    {
        if (partId == null || !Resolve(partId.Value, ref part, false))
            yield break;

        foreach (var slot in part.Organs.Values)
        {
            if (!TryComp(slot.Child, out OrganComponent? organ))
                continue;

            yield return (slot.Child.Value, organ);
        }
    }

    public IEnumerable<BodyPartSlot> GetPartAllSlots(EntityUid? partId, BodyPartComponent? part = null)
    {
        if (partId == null ||
            !Resolve(partId.Value, ref part, false))
            yield break;

        foreach (var slot in part.Children.Values)
        {
            yield return slot;

            if (!TryComp(slot.Child, out BodyComponent? childPart))
                continue;

            foreach (var subChild in GetBodyAllSlots(slot.Child, childPart))
            {
                yield return subChild;
            }
        }
    }

    public bool CanAttachPart([NotNullWhen(true)] EntityUid? partId, BodyPartSlot slot, BodyPartComponent? part = null)
    {
        return partId != null &&
               slot.Child == null &&
               Resolve(partId.Value, ref part, false) &&
               (slot.Type == null || slot.Type == part.PartType) &&
               Containers.TryGetContainer(slot.Parent, BodyContainerId, out var container) &&
               container.CanInsert(partId.Value);
    }

    public virtual bool AttachPart(
        EntityUid? partId,
        BodyPartSlot slot,
        [NotNullWhen(true)] BodyPartComponent? part = null)
    {
        if (partId == null ||
            !Resolve(partId.Value, ref part, false) ||
            !CanAttachPart(partId, slot, part))
            return false;

        DropPart(slot.Child);
        DropPart(partId, part);

        var container = Containers.EnsureContainer<Container>(slot.Parent, BodyContainerId);
        if (!container.Insert(partId.Value))
            return false;

        slot.Child = partId;
        slot.Cauterised = false; //override cauterisation on new part attachment
        part.ParentSlot = slot;
        part.AttachmentIntegrity = part.MaxIntegrity; //always set attachment integrity to max when attached

        if (TryComp(slot.Parent, out BodyPartComponent? parentPart))
        {
            part.Body = parentPart.Body;
            parentPart.Children[slot.Id] = slot;

        }
        else if (TryComp(slot.Parent, out BodyComponent? parentBody))
        {
            part.Body = parentBody.Owner;
        }
        else
        {
            part.Body = null;
        }

        foreach (var childSlot in part.Children.Values)
        {
            if (childSlot.Child is not { } child || !TryComp<BodyPartComponent>(child, out var childPart))
                continue;

            childPart.OriginalBody = childPart.Body;
            childPart.Body = part.Body;
        }

        foreach (var organSlot in part.Organs.Values)
        {
            if (organSlot.Child is not { } child || !TryComp<OrganComponent>(child, out var childOrgan))
                continue;

            childOrgan.Body = part.Body;
        }

        if (TryComp(part.Body, out HumanoidAppearanceComponent? bodyAppearance) &&
                !TryComp(part.Owner, out BodyPartAppearanceComponent? existingAppearance))
        {
            var appearance = AddComp<BodyPartAppearanceComponent>(part.Owner);
            appearance.OriginalBody = part.Body;
            appearance.Color = bodyAppearance.SkinColor;
            UpdateAppearance(part.Owner, appearance);
        }

        Dirty(slot.Parent);
        Dirty(partId.Value);

        //call enable part if it is working (should always be true on init)
        if (part.Working)
            EnablePart(partId.Value, slot, part);

        return true;
    }


    public virtual bool DropPart(EntityUid? partId, [NotNullWhen(true)] BodyPartComponent? part = null)
    {
        if (partId == null ||
            !Resolve(partId.Value, ref part, false) ||
            part.ParentSlot is not { } slot)
            return false;

        var oldBodyNullable = part.Body;

        slot.Child = null;
        part.ParentSlot = null;
        part.OriginalBody = part.Body;
        part.Body = null;
        part.RejectionCounter = 0;

        if (Containers.TryGetContainer(slot.Parent, BodyContainerId, out var container))
            container.Remove(partId.Value);

        if (TryComp(partId, out TransformComponent? transform))
            transform.AttachToGridOrMap();

        part.Owner.RandomOffset(0.25f);

        if (oldBodyNullable is { } oldBody)
        {
            DisablePart(partId.Value, part, oldBody);
            //we do this twice so any effects that require full part removal can take place
            var partRemovedEvent = new BodyPartRemovedEvent(slot.Id, part);
            RaiseLocalEvent(oldBody, ref partRemovedEvent);
        }

        return true;
    }


    //does everything attaching a part does, but without attaching a part (part should be attached already)
    public virtual bool EnablePart(EntityUid partId,
        BodyPartSlot slot,
        BodyPartComponent part) {

        part.Working = true;
        if (slot.Child != part.Owner)
            return false;

        if (part.Body is { } newBody)
        {
            if (part.PartType == BodyPartType.Leg || part.PartType == BodyPartType.Foot)
            {
                UpdateMovementSpeed(newBody);

                if (part.PartType == BodyPartType.Leg && part.Working)
                {
                    //check if leg has a foot slot and foot
                    var hasSlot = false;
                    var slotEmpty = false;
                    foreach (KeyValuePair<string, BodyPartSlot> entry in part.Children)
                    {
                        if (entry.Value.Type == BodyPartType.Foot)
                        {
                            hasSlot = true;
                            if (entry.Value.Child is null || (
                                TryComp<BodyPartComponent>(entry.Value.Child, out var foot) &&
                                !foot.Working
                                ))
                                slotEmpty = true;
                        }
                    }

                    //stand up
                    if ((!hasSlot || !slotEmpty))
                        Standing.Stand(newBody);
                }
                else if (part.PartType == BodyPartType.Foot && part.Working)
                {
                    Standing.Stand(newBody);
                }
            }

            var partAddedEvent = new BodyPartAddedEvent(slot.Id, part);
            RaiseLocalEvent(newBody, ref partAddedEvent);

            RaiseLocalEvent(partId, new PartAddedToBodyEvent(newBody), true);

            // TODO: Body refactor. Somebody is doing it
            //EntitySystem.Get<SharedHumanoidAppearanceSystem>().BodyPartAdded(Owner, argsAdded);

            foreach (var organ in GetPartOrgans(partId, part))
            {
                RaiseLocalEvent(organ.Id, new AddedToBodyEvent(newBody), true);
            }

            Dirty(newBody);
            Dirty(partId);
        }
        return true;
    }

    //does everything dropping a part does, but without dropping a part
    public virtual bool DisablePart(EntityUid partId, BodyPartComponent part,
        EntityUid? oldBody = null,
        BodyPartSlot? slot = null) {

        part.Working = false;

        //if oldbody is set from drop part, great - if not then we should still have it here
        if (oldBody is null)
            oldBody = part.Body;

        if (oldBody is null)
            return false;

        //same with slot
        if (slot is null)
            slot = part.ParentSlot;

        if (slot is null)
            return false;

        var partRemovedEvent = new BodyPartRemovedEvent(slot.Id, part);
        RaiseLocalEvent(oldBody.Value, ref partRemovedEvent);

        RaiseLocalEvent(partId, new PartRemovedFromBodyEvent(oldBody.Value), true);

        if (part.PartType == BodyPartType.Leg || part.PartType == BodyPartType.Foot)
        {
            UpdateMovementSpeed(oldBody.Value);
            if (!GetBodyChildrenOfType(oldBody.Value, BodyPartType.Leg).Any())
                Standing.Down(oldBody.Value);
            else
            {
                var legs = GetBodyChildrenOfType(oldBody.Value, BodyPartType.Leg);
                var emptySlot = 0;
                var footSlot = 0;
                foreach (var leg in legs)
                {
                    foreach (KeyValuePair<string, BodyPartSlot> entry in leg.Component.Children)
                    {
                        if (entry.Value.Type == BodyPartType.Foot)
                        {
                            footSlot++;
                            if (entry.Value.Child is null || (
                            TryComp<BodyPartComponent>(entry.Value.Child, out var foot) &&
                            !foot.Working
                            ))
                                emptySlot++;
                            else if (!leg.Component.Working)
                                emptySlot++;
                        }
                    }
                }
                if (emptySlot == footSlot) //if the legs have a number of footslots and they are all empty, fall over
                    Standing.Down(oldBody.Value);
            }
        }

        foreach (var childSlot in part.Children.Values)
        {
            if (childSlot.Child is not { } child || !TryComp<BodyPartComponent>(child, out var childPart))
                continue;

            childPart.OriginalBody = childPart.Body;
            childPart.Body = part.Body;
        }

        foreach (var organSlot in part.Organs.Values)
        {
            if (organSlot.Child is not { } child || !TryComp<OrganComponent>(child, out var childOrgan))
                continue;

            childOrgan.Body = part.Body;
            RaiseLocalEvent(child, new RemovedFromBodyEvent(oldBody.Value));
        }

        if (part != null && TryComp(oldBody, out HumanoidAppearanceComponent? bodyAppearance) &&
            !TryComp(part.Owner, out BodyPartAppearanceComponent? existingAppearance))
        {
            var appearance = AddComp<BodyPartAppearanceComponent>(part.Owner);
            appearance.OriginalBody = part.OriginalBody;
            appearance.Color = bodyAppearance.SkinColor;
            UpdateAppearance(part.Owner, appearance);
        }
        

        Dirty(slot.Parent);
        Dirty(partId);
        return true;
    }

    public void UpdateMovementSpeed(EntityUid body, BodyComponent? component = null, MovementSpeedModifierComponent? movement = null)
    {
        if (!Resolve(body, ref component, ref movement, false))
            return;

        if (component.RequiredLegs <= 0)
            return;

        if (component.Root?.Child is not { } root)
            return;

        var allSlots = GetAllBodyPartSlots(root).ToHashSet();
        var allLegs = new HashSet<EntityUid>();
        foreach (var slot in allSlots)
        {
            if (slot.Type == BodyPartType.Leg && slot.Child is { } child)
            {
                var emptySlot = false; //the leg should have a foot to count (unless it is some kind of footless leg, anything is possibe)
                if (TryComp<BodyPartComponent>(slot.Child, out var leg))
                {
                    foreach (KeyValuePair<string, BodyPartSlot> entry in leg.Children)
                    {
                        if (entry.Value.Type == BodyPartType.Foot && (entry.Value.Child is null || (
                                TryComp<BodyPartComponent>(entry.Value.Child, out var foot) &&
                                !foot.Working
                                )))
                            emptySlot = true;
                    }
                }
                if (!emptySlot && leg is not null &&leg.Working)
                    allLegs.Add(child);
            }
        }

        var walkSpeed = 0f;
        var sprintSpeed = 0f;
        var acceleration = 0f;
        foreach (var leg in allLegs)
        {
            if (!TryComp<MovementSpeedModifierComponent>(leg, out var legModifier))
                continue;

            walkSpeed += legModifier.BaseWalkSpeed;
            sprintSpeed += legModifier.BaseSprintSpeed;
            acceleration += legModifier.Acceleration;
        }

        walkSpeed /= component.RequiredLegs;
        sprintSpeed /= component.RequiredLegs;
        acceleration /= component.RequiredLegs;
        Movement.ChangeBaseSpeed(body, walkSpeed, sprintSpeed, acceleration, movement);
    }

    public bool DropPartAt(EntityUid? partId, EntityCoordinates dropAt, BodyPartComponent? part = null)
    {
        if (partId == null || !DropPart(partId, part))
            return false;

        if (TryComp(partId.Value, out TransformComponent? transform))
            transform.Coordinates = dropAt;

        return true;
    }

    public bool OrphanPart(EntityUid? partId, BodyPartComponent? part = null)
    {
        if (partId == null || !Resolve(partId.Value, ref part, false))
            return false;

        DropPart(partId, part);

        foreach (var slot in part.Children.Values)
        {
            DropPart(slot.Child);
        }

        return false;
    }

    public bool DeletePart(EntityUid? id, BodyPartComponent? part = null)
    {
        if (id == null || !Resolve(id.Value, ref part, false))
            return false;

        DropPart(id, part);

        if (Deleted(id.Value))
            return false;

        Del(id.Value);
        return true;
    }

    public void SetBodyPartOpen(BodyPartComponent part, bool opened)
    {
            part.Opened = opened;
    }

    public void AttachPartAttachment(EntityUid uid, BodyPartComponent part)
    {
        if (part.Attachment == null)
            part.Attachment = uid;

        //TODO update player sprite if possible
    }

    public void RemovePartAttachment(BodyPartComponent part)
    {
        part.Attachment = null;

        //TODO update player sprite if possible
    }

    public void AttachPartSlotAttachment(EntityUid uid, BodyPartSlot slot)
    {
        if (TryComp<BodyPartComponent>(slot.Parent, out var part))
        {
            if (part.Children[slot.Id].Attachment == null)
                part.Children[slot.Id].Attachment = uid;
        }
    }

    public void RemovePartSlotAttachment(BodyPartSlot slot)
    {
        if (TryComp<BodyPartComponent>(slot.Parent, out var part))
        {
            part.Children[slot.Id].Attachment = null;
        }
    }

    public void SetBodyPartIncised(BodyPartComponent part, bool incised)
    {
        if (part.Incisable)
        {
            //change body part incised to true
            part.Incised = incised;

            //TODO update player sprite if possible
        }
    }

    public void SetBodyPartEndoOpen(BodyPartComponent part, bool opened)
    {
        if (part.EndoSkeleton)
        {
            //change body part incised to true
            part.EndoOpened = opened;

            //TODO update player sprite if possible
        }
    }

    public void SetBodyPartExoOpen(BodyPartComponent part, bool opened)
    {
        if (part.ExoSkeleton)
        {
            //change body part incised to true
            part.ExoOpened = opened;

            //TODO update player sprite if possible
        }
    }

    public void SetCauterisedPartSlot(BodyPartSlot slot, bool cauterised)
    {

        if (TryComp<BodyPartComponent>(slot.Parent, out var part))
            part.Children[slot.Id].Cauterised = cauterised;
    }

    public void IncrementRejectionCounter(BodyPartComponent part, int count = 1)
    {
        part.RejectionCounter += count;
    }

    public IEnumerable<(EntityUid Id, BodyPartComponent Component)> GetBodyChildrenOfType(EntityUid? bodyId, BodyPartType type, BodyComponent? body = null)
    {
        foreach (var part in GetBodyChildren(bodyId, body))
        {
            if (part.Component.PartType == type)
                yield return part;
        }
    }

    public bool BodyHasChildOfType(EntityUid? bodyId, BodyPartType type, BodyComponent? body = null)
    {
        return GetBodyChildrenOfType(bodyId, type, body).Any();
    }

    public bool BodyHasChild(
        EntityUid? parentId,
        EntityUid? childId,
        BodyComponent? parent = null,
        BodyPartComponent? child = null)
    {
        if (parentId == null ||
            !Resolve(parentId.Value, ref parent, false) ||
            childId == null ||
            !Resolve(childId.Value, ref child, false))
            return false;

        return child.ParentSlot?.Child == parentId;
    }

    public IEnumerable<EntityUid> GetBodyPartAdjacentParts(EntityUid partId, BodyPartComponent? part = null)
    {
        if (!Resolve(partId, ref part, false))
            yield break;

        if (part.ParentSlot != null)
            yield return part.ParentSlot.Parent;

        foreach (var slot in part.Children.Values)
        {
            if (slot.Child != null)
                yield return slot.Child.Value;
        }
    }

    public IEnumerable<(EntityUid AdjacentId, T Component)> GetBodyPartAdjacentPartsComponents<T>(
        EntityUid partId,
        BodyPartComponent? part = null)
        where T : Component
    {
        if (!Resolve(partId, ref part, false))
            yield break;

        var query = GetEntityQuery<T>();
        foreach (var adjacentId in GetBodyPartAdjacentParts(partId, part))
        {
            if (query.TryGetComponent(adjacentId, out var component))
                yield return (adjacentId, component);
        }
    }

    public bool TryGetBodyPartAdjacentPartsComponents<T>(
        EntityUid partId,
        [NotNullWhen(true)] out List<(EntityUid AdjacentId, T Component)>? comps,
        BodyPartComponent? part = null)
        where T : Component
    {
        if (!Resolve(partId, ref part, false))
        {
            comps = null;
            return false;
        }

        var query = GetEntityQuery<T>();
        comps = new List<(EntityUid AdjacentId, T Component)>();
        foreach (var adjacentId in GetBodyPartAdjacentParts(partId, part))
        {
            if (query.TryGetComponent(adjacentId, out var component))
                comps.Add((adjacentId, component));
        }

        if (comps.Count != 0)
            return true;

        comps = null;
        return false;
    }

    /// <summary>
    ///     Returns a list of ValueTuples of <see cref="T"/> and OrganComponent on each organ
    ///     in the given part.
    /// </summary>
    /// <param name="uid">The part entity id to check on.</param>
    /// <param name="part">The part to check for organs on.</param>
    /// <typeparam name="T">The component to check for.</typeparam>
    public List<(T Comp, OrganComponent Organ)> GetBodyPartOrganComponents<T>(
        EntityUid uid,
        BodyPartComponent? part = null)
        where T : Component
    {
        if (!Resolve(uid, ref part))
            return new List<(T Comp, OrganComponent Organ)>();

        var query = GetEntityQuery<T>();
        var list = new List<(T Comp, OrganComponent Organ)>();
        foreach (var organ in GetPartOrgans(uid, part))
        {
            if (query.TryGetComponent(organ.Id, out var comp))
                list.Add((comp, organ.Component));
        }

        return list;
    }

    /// <summary>
    ///     Tries to get a list of ValueTuples of <see cref="T"/> and OrganComponent on each organs
    ///     in the given part.
    /// </summary>
    /// <param name="uid">The part entity id to check on.</param>
    /// <param name="comps">The list of components.</param>
    /// <param name="part">The part to check for organs on.</param>
    /// <typeparam name="T">The component to check for.</typeparam>
    /// <returns>Whether any were found.</returns>
    public bool TryGetBodyPartOrganComponents<T>(
        EntityUid uid,
        [NotNullWhen(true)] out List<(T Comp, OrganComponent Organ)>? comps,
        BodyPartComponent? part = null)
        where T : Component
    {
        if (!Resolve(uid, ref part))
        {
            comps = null;
            return false;
        }

        comps = GetBodyPartOrganComponents<T>(uid, part);

        if (comps.Count != 0)
            return true;

        comps = null;
        return false;
    }
}
