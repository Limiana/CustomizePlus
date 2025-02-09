﻿using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using OtterGui.Log;
using OtterGui.Classes;
using Penumbra.GameData.Actors;
using System.Numerics;
using CustomizePlus.Core.Data;
using CustomizePlus.Armatures.Events;
using CustomizePlus.Armatures.Data;
using CustomizePlus.Profiles;
using CustomizePlus.Profiles.Data;
using CustomizePlus.Game.Services;
using CustomizePlus.Templates.Events;
using CustomizePlus.Profiles.Events;
using CustomizePlus.Core.Extensions;
using CustomizePlus.GameData.Data;
using CustomizePlus.GameData.Services;
using CustomizePlus.GameData.Extensions;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using System.Drawing;
using Penumbra.GameData.Enums;

namespace CustomizePlus.Armatures.Services;

public unsafe sealed class ArmatureManager : IDisposable
{
    private readonly ProfileManager _profileManager;
    private readonly IObjectTable _objectTable;
    private readonly GameObjectService _gameObjectService;
    private readonly TemplateChanged _templateChangedEvent;
    private readonly ProfileChanged _profileChangedEvent;
    private readonly Logger _logger;
    private readonly FrameworkManager _framework;
    private readonly ObjectManager _objectManager;
    private readonly ActorManager _actorManager;
    private readonly ArmatureChanged _event;

    public Dictionary<ActorIdentifier, Armature> Armatures { get; private set; } = new();

    public ArmatureManager(
        ProfileManager profileManager,
        IObjectTable objectTable,
        GameObjectService gameObjectService,
        TemplateChanged templateChangedEvent,
        ProfileChanged profileChangedEvent,
        Logger logger,
        FrameworkManager framework,
        ObjectManager objectManager,
        ActorManager actorManager,
        ArmatureChanged @event)
    {
        _profileManager = profileManager;
        _objectTable = objectTable;
        _gameObjectService = gameObjectService;
        _templateChangedEvent = templateChangedEvent;
        _profileChangedEvent = profileChangedEvent;
        _logger = logger;
        _framework = framework;
        _objectManager = objectManager;
        _actorManager = actorManager;
        _event = @event;

        _templateChangedEvent.Subscribe(OnTemplateChange, TemplateChanged.Priority.ArmatureManager);
        _profileChangedEvent.Subscribe(OnProfileChange, ProfileChanged.Priority.ArmatureManager);
    }

    public void Dispose()
    {
        _templateChangedEvent.Unsubscribe(OnTemplateChange);
        _profileChangedEvent.Unsubscribe(OnProfileChange);
    }

    /// <summary>
    /// Main rendering function, called from rendering hook
    /// </summary>
    public void OnRender()
    {
        try
        {
            RefreshArmatures();
            ApplyArmatureTransforms();
        }
        catch (Exception ex)
        {
            _logger.Error($"Exception while rendering armatures:\n\t{ex}");
        }
    }

    /// <summary>
    /// Function called when game object movement is detected
    /// </summary>
    public void OnGameObjectMove(Actor actor)
    {
        if (!actor.Identifier(_actorManager, out var identifier))
            return;

        if (Armatures.TryGetValue(identifier, out var armature) && armature.IsBuilt && armature.IsVisible)
            ApplyRootTranslation(armature, actor);
    }

    /// <summary>
    /// Deletes armatures which no longer have actor associated with them and creates armatures for new actors
    /// </summary>
    private void RefreshArmatures()
    {
        _objectManager.Update();

        var currentTime = DateTime.UtcNow;
        var armatureExpirationDateTime = currentTime.AddSeconds(-30);
        foreach (var kvPair in Armatures.ToList())
        {
            var armature = kvPair.Value;
            if (!_objectManager.ContainsKey(kvPair.Value.ActorIdentifier) &&
                armature.LastSeen <= armatureExpirationDateTime) //Only remove armatures which haven't been seen for a while
            {
                _logger.Debug($"Removing armature {armature} because {kvPair.Key.IncognitoDebug()} is gone");
                RemoveArmature(armature, ArmatureChanged.DeletionReason.Gone);

                continue;
            }

            //armature is considered visible if 1 or less seconds passed since last time we've seen the actor
            armature.IsVisible = armature.LastSeen.AddSeconds(1) >= currentTime;
        }

        Profile? GetProfileForActor(ActorIdentifier identifier)
        {
            foreach (var profile in _profileManager.GetEnabledProfilesByActor(identifier))
            {
                if (profile.LimitLookupToOwnedObjects &&
                    identifier.Type == IdentifierType.Owned &&
                    identifier.PlayerName != _objectManager.PlayerData.Identifier.PlayerName)
                    continue;

                return profile;
            }

            return null;
        }

        foreach (var obj in _objectManager)
        {
            var actorIdentifier = obj.Key.CreatePermanent();
            if (!Armatures.ContainsKey(actorIdentifier))
            {
                var activeProfile = GetProfileForActor(actorIdentifier);
                if (activeProfile == null)
                    continue;

                var newArm = new Armature(actorIdentifier, activeProfile);
                TryLinkSkeleton(newArm);
                Armatures.Add(actorIdentifier, newArm);
                _logger.Debug($"Added '{newArm}' for {actorIdentifier.IncognitoDebug()} to cache");
                _event.Invoke(ArmatureChanged.Type.Created, newArm, activeProfile);

                continue;
            }

            var armature = Armatures[actorIdentifier];

            armature.UpdateLastSeen(currentTime);

            if (armature.IsPendingProfileRebind)
            {
                _logger.Debug($"Armature {armature} is pending profile rebind, rebinding...");
                armature.IsPendingProfileRebind = false;

                var activeProfile = GetProfileForActor(actorIdentifier);
                if (activeProfile == armature.Profile)
                    continue;

                if (activeProfile == null)
                {
                    _logger.Debug($"Removing armature {armature} because it doesn't have any active profiles");
                    RemoveArmature(armature, ArmatureChanged.DeletionReason.NoActiveProfiles);
                    continue;
                }

                Profile oldProfile = armature.Profile;

                armature.Profile.Armatures.Remove(armature);
                armature.Profile = activeProfile;
                activeProfile.Armatures.Add(armature);
                armature.RebuildBoneTemplateBinding();

                _event.Invoke(ArmatureChanged.Type.Rebound, armature, activeProfile);
            }

            //Needed because skeleton sometimes appears to be not ready when armature is created
            //and also because we want to augment armature with new bones if they are available
            TryLinkSkeleton(armature);
        }
    }

    private unsafe void ApplyArmatureTransforms()
    {
        foreach (var kvPair in Armatures)
        {
            var armature = kvPair.Value;
            if (armature.IsBuilt && armature.IsVisible && _objectManager.ContainsKey(armature.ActorIdentifier))
            {
                foreach (var actor in _objectManager[armature.ActorIdentifier].Objects)
                    ApplyPiecewiseTransformation(armature, actor, armature.ActorIdentifier);
            }
        }
    }

    /// <summary>
    /// Returns whether or not a link can be established between the armature and an in-game object.
    /// If unbuilt, the armature will be rebuilded.
    /// </summary>
    private bool TryLinkSkeleton(Armature armature, bool forceRebuild = false)
    {
        _objectManager.Update();

        try
        {
            if (!_objectManager.ContainsKey(armature.ActorIdentifier))
                return false;

            var actor = _objectManager[armature.ActorIdentifier].Objects[0];

            if (!armature.IsBuilt || forceRebuild)
            {
                armature.RebuildSkeleton(actor.Model.AsCharacterBase);
            }
            else if (armature.NewBonesAvailable(actor.Model.AsCharacterBase))
            {
                armature.AugmentSkeleton(actor.Model.AsCharacterBase);
            }

            return true;
        }
        catch (Exception ex)
        {
            // This is on wait until isse #191 on Github responds. Keeping it in code, delete it if I forget and this is longer then a month ago.

            // Disabling this if its any Default Profile due to Log spam. A bit crazy but hey, if its for me id Remove Default profiles all together so this is as much as ill do for now! :)
            //if(!(Profile.CharacterName.Equals(Constants.DefaultProfileCharacterName) || Profile.CharacterName.Equals("DefaultCutscene"))) {
            _logger.Error($"Error occured while attempting to link skeleton: {armature}");
            throw;
            //}
        }
    }

    /// <summary>
    /// Iterate through the skeleton of the given character base, and apply any transformations
    /// for which this armature contains corresponding model bones. This method of application
    /// is safer but more computationally costly
    /// </summary>
    private void ApplyPiecewiseTransformation(Armature armature, Actor actor, ActorIdentifier actorIdentifier)
    {
        var cBase = actor.Model.AsCharacterBase;

        var isMount = actorIdentifier.Type == IdentifierType.Owned &&
            actorIdentifier.Kind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.MountType;

        Actor? mountOwner = null;
        Armature? mountOwnerArmature = null;
        if (isMount)
        {
            (var ident, mountOwner) = _gameObjectService.FindActorsByName(actorIdentifier.PlayerName.ToString()).FirstOrDefault();
            Armatures.TryGetValue(ident, out mountOwnerArmature);
        }

        if (cBase != null)
        {
            for (var pSkeleIndex = 0; pSkeleIndex < cBase->Skeleton->PartialSkeletonCount; ++pSkeleIndex)
            {
                var currentPose = cBase->Skeleton->PartialSkeletons[pSkeleIndex].GetHavokPose(Constants.TruePoseIndex);

                if (currentPose != null)
                {
                    for (var boneIndex = 0; boneIndex < currentPose->Skeleton->Bones.Length; ++boneIndex)
                    {
                        if (armature.GetBoneAt(pSkeleIndex, boneIndex) is ModelBone mb
                            && mb != null
                            && mb.BoneName == currentPose->Skeleton->Bones[boneIndex].Name.String)
                        {
                            if (mb == armature.MainRootBone)
                            {
                                if (_gameObjectService.IsActorHasScalableRoot(actor) && mb.IsModifiedScale())
                                {
                                    cBase->DrawObject.Object.Scale = mb.CustomizedTransform!.Scaling;

                                    //Fix mount owner's scale if needed
                                    //todo: always keep owner's scale proper instead of scaling with mount if no armature found
                                    if (isMount && mountOwner != null && mountOwnerArmature != null)
                                    {
                                        var ownerDrawObject = cBase->DrawObject.Object.ChildObject;

                                        //limit to only modified scales because that is just easier to handle
                                        //because we don't need to hook into dismount code to reset character scale
                                        //todo: hook into dismount
                                        //https://github.com/Cytraen/SeatedSidekickSpectator/blob/main/SetModeHook.cs?
                                        if (cBase->DrawObject.Object.ChildObject == mountOwner.Value.Model &&
                                            mountOwnerArmature.MainRootBone.IsModifiedScale())
                                        {
                                            var baseScale = mountOwnerArmature.MainRootBone.CustomizedTransform!.Scaling;

                                            ownerDrawObject->Scale = new Vector3(Math.Abs(baseScale.X / cBase->DrawObject.Object.Scale.X),
                                                    Math.Abs(baseScale.Y / cBase->DrawObject.Object.Scale.Y),
                                                    Math.Abs(baseScale.Z / cBase->DrawObject.Object.Scale.Z));
                                        }
                                    }
                                }
                            }
                            else
                            {
                                mb.ApplyModelTransform(cBase);
                            }
                        }
                    }
                }
            }
        }
    }

    private void ApplyRootTranslation(Armature arm, Actor actor)
    {
        //I'm honestly not sure if we should or even can check if cBase->DrawObject or cBase->DrawObject.Object is a valid object
        //So for now let's assume we don't need to check for that

        var cBase = actor.Model.AsCharacterBase;
        if (cBase != null)
        {
            var rootBoneTransform = arm.GetAppliedBoneTransform("n_root");
            if (rootBoneTransform == null)
                return;

            if (rootBoneTransform.Translation.X == 0 &&
                rootBoneTransform.Translation.Y == 0 &&
                rootBoneTransform.Translation.Z == 0)
                return;

            if (!cBase->DrawObject.IsVisible)
                return;

            var newPosition = new FFXIVClientStructs.FFXIV.Common.Math.Vector3
            {
                X = cBase->DrawObject.Object.Position.X + MathF.Max(rootBoneTransform.Translation.X, 0.01f),
                Y = cBase->DrawObject.Object.Position.Y + MathF.Max(rootBoneTransform.Translation.Y, 0.01f),
                Z = cBase->DrawObject.Object.Position.Z + MathF.Max(rootBoneTransform.Translation.Z, 0.01f)
            };

            cBase->DrawObject.Object.Position = newPosition;
        }
    }

    private void RemoveArmature(Armature armature, ArmatureChanged.DeletionReason reason)
    {
        armature.Profile.Armatures.Remove(armature);
        Armatures.Remove(armature.ActorIdentifier);
        _logger.Debug($"Armature {armature} removed from cache");

        _event.Invoke(ArmatureChanged.Type.Deleted, armature, reason);
    }

    private void OnTemplateChange(TemplateChanged.Type type, Templates.Data.Template? template, object? arg3)
    {
        if (type is not TemplateChanged.Type.NewBone &&
            type is not TemplateChanged.Type.DeletedBone &&
            type is not TemplateChanged.Type.EditorCharacterChanged &&
            type is not TemplateChanged.Type.EditorLimitLookupToOwnedChanged &&
            type is not TemplateChanged.Type.EditorEnabled &&
            type is not TemplateChanged.Type.EditorDisabled)
            return;

        if (type == TemplateChanged.Type.NewBone ||
            type == TemplateChanged.Type.DeletedBone) //type == TemplateChanged.Type.EditorCharacterChanged?
        {
            //In case a lot of events are triggered at the same time for the same template this should limit the amount of times bindings are unneccessary rebuilt
            _framework.RegisterImportant($"TemplateRebuild @ {template.UniqueId}", () =>
            {
                foreach (var profile in _profileManager.GetProfilesUsingTemplate(template))
                {
                    _logger.Debug($"ArmatureManager.OnTemplateChange New/Deleted bone or character changed: {type}, template: {template.Name.Text.Incognify()}, profile: {profile.Name.Text.Incognify()}->{profile.Enabled}->{profile.Armatures.Count} armatures");
                    if (!profile.Enabled || profile.Armatures.Count == 0)
                        continue;

                    profile.Armatures.ForEach(x => x.RebuildBoneTemplateBinding());
                }
            });

            return;
        }

        if (type == TemplateChanged.Type.EditorCharacterChanged)
        {
            (var characterName, var profile) = ((string, Profile))arg3;

            foreach (var armature in GetArmaturesForCharacterName(characterName))
            {
                armature.IsPendingProfileRebind = true;
                _logger.Debug($"ArmatureManager.OnTemplateChange Editor profile character name changed, armature rebind scheduled: {type}, {armature}");
            }

            if (profile.Armatures.Count == 0)
                return;

            //Rebuild armatures for previous character
            foreach (var armature in profile.Armatures)
                armature.IsPendingProfileRebind = true;

            _logger.Debug($"ArmatureManager.OnTemplateChange Editor profile character name changed, armature rebind scheduled: {type}, profile: {profile.Name.Text.Incognify()}->{profile.Enabled}, new name: {characterName.Incognify()}");

            return;
        }

        if(type == TemplateChanged.Type.EditorLimitLookupToOwnedChanged)
        {
            var profile = (Profile)arg3!;

            if (profile.Armatures.Count == 0)
                return;

            foreach (var armature in profile.Armatures)
                armature.IsPendingProfileRebind = true;

            _logger.Debug($"ArmatureManager.OnTemplateChange Editor profile limit lookup setting changed, armature rebind scheduled: {type}, profile: {profile.Name.Text.Incognify()}->{profile.Enabled}");

            return;
        }

        if (type == TemplateChanged.Type.EditorEnabled ||
            type == TemplateChanged.Type.EditorDisabled)
        {
            foreach (var armature in GetArmaturesForCharacterName((string)arg3!))
            {
                armature.IsPendingProfileRebind = true;
                _logger.Debug($"ArmatureManager.OnTemplateChange template editor enabled/disabled: {type}, pending profile set for {armature}");
            }

            return;
        }
    }

    private void OnProfileChange(ProfileChanged.Type type, Profile? profile, object? arg3)
    {
        if (type is not ProfileChanged.Type.AddedTemplate &&
            type is not ProfileChanged.Type.RemovedTemplate &&
            type is not ProfileChanged.Type.MovedTemplate &&
            type is not ProfileChanged.Type.ChangedTemplate &&
            type is not ProfileChanged.Type.Toggled &&
            type is not ProfileChanged.Type.Deleted &&
            type is not ProfileChanged.Type.TemporaryProfileAdded &&
            type is not ProfileChanged.Type.TemporaryProfileDeleted &&
            type is not ProfileChanged.Type.ChangedCharacterName &&
            type is not ProfileChanged.Type.ChangedDefaultProfile &&
            type is not ProfileChanged.Type.LimitLookupToOwnedChanged)
            return;

        if (type == ProfileChanged.Type.ChangedDefaultProfile)
        {
            var oldProfile = (Profile?)arg3;

            if (oldProfile == null || oldProfile.Armatures.Count == 0)
                return;

            foreach (var armature in oldProfile.Armatures)
                armature.IsPendingProfileRebind = true;

            _logger.Debug($"ArmatureManager.OnProfileChange Profile no longer default, armatures rebind scheduled: {type}, old profile: {oldProfile.Name.Text.Incognify()}->{oldProfile.Enabled}");

            return;
        }

        if (profile == null)
        {
            _logger.Error($"ArmatureManager.OnProfileChange Invalid input for event: {type}, profile is null.");
            return;
        }

        if (type == ProfileChanged.Type.Toggled)
        {
            if (!profile.Enabled && profile.Armatures.Count == 0)
                return;

            if (profile == _profileManager.DefaultProfile)
            {
                foreach (var kvPair in Armatures)
                {
                    var armature = kvPair.Value;
                    if (armature.Profile == profile)
                        armature.IsPendingProfileRebind = true;

                    _logger.Debug($"ArmatureManager.OnProfileChange default profile toggled, planning rebind for armature {armature}");
                }

                return;
            }

            if (string.IsNullOrWhiteSpace(profile.CharacterName))
                return;

            foreach (var armature in GetArmaturesForCharacterName(profile.CharacterName))
            {
                armature.IsPendingProfileRebind = true;
                _logger.Debug($"ArmatureManager.OnProfileChange profile {profile} toggled, planning rebind for armature {armature}");
            }

            return;
        }

        if (type == ProfileChanged.Type.TemporaryProfileAdded)
        {
            if (!profile.TemporaryActor.IsValid || !Armatures.ContainsKey(profile.TemporaryActor))
                return;

            var armature = Armatures[profile.TemporaryActor];
            if (armature.Profile == profile)
                return;

            armature.UpdateLastSeen();

            armature.IsPendingProfileRebind = true;

            _logger.Debug($"ArmatureManager.OnProfileChange TemporaryProfileAdded, calling rebind for existing armature: {type}, data payload: {arg3?.ToString()}, profile: {profile.Name.Text.Incognify()}->{profile.Enabled}");

            return;
        }

        if (type == ProfileChanged.Type.ChangedCharacterName ||
            type == ProfileChanged.Type.Deleted ||
            type == ProfileChanged.Type.TemporaryProfileDeleted ||
            type == ProfileChanged.Type.LimitLookupToOwnedChanged)
        {
            if (profile.Armatures.Count == 0)
                return;

            foreach (var armature in profile.Armatures)
            {
                if (type == ProfileChanged.Type.TemporaryProfileDeleted)
                    armature.UpdateLastSeen(); //just to be safe

                armature.IsPendingProfileRebind = true;
            }

            _logger.Debug($"ArmatureManager.OnProfileChange CCN/DEL/TPD/LLTOC, armature rebind scheduled: {type}, data payload: {arg3?.ToString()?.Incognify()}, profile: {profile.Name.Text.Incognify()}->{profile.Enabled}");

            return;
        }

        //todo: shouldn't happen, but happens sometimes? I think?
        if (profile.Armatures.Count == 0)
            return;

        _logger.Debug($"ArmatureManager.OnProfileChange Added/Deleted/Moved/Changed template: {type}, data payload: {arg3?.ToString()}, profile: {profile.Name}->{profile.Enabled}->{profile.Armatures.Count} armatures");

        profile!.Armatures.ForEach(x => x.RebuildBoneTemplateBinding());
    }

    private IEnumerable<Armature> GetArmaturesForCharacterName(string characterName)
    {
        var actors = _gameObjectService.FindActorsByName(characterName).ToList();
        if (actors.Count == 0)
            yield break;

        foreach (var actorData in actors)
        {
            if (!Armatures.TryGetValue(actorData.Item1, out var armature))
                continue;

            yield return armature;
        }
    }
}