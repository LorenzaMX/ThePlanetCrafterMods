﻿using BepInEx;
using HarmonyLib;
using SpaceCraft;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using FeatMultiplayer.MessageTypes;

namespace FeatMultiplayer
{
    public partial class Plugin : BaseUnityPlugin
    {
        /// <summary>
        /// Marker component to avoid launching the same rocket multiple times.
        /// </summary>
        class RocketAlreadyLaunched : MonoBehaviour
        {

        }


        /// <summary>
        /// The vanilla game calls ActionSendInSpace::OnAction when the player presses
        /// the button. It locates the rocket's world object, then locates the game object
        /// which links to that world object, attaches a MachineRocket component, ignites
        /// it, shakes the camera, triggers the appropriate meteor event, then
        /// accounts the rocket.
        /// 
        /// On the client, we send a launch request to via the rocket's world object id.
        /// 
        /// On the host, we need to confirm the launch request and notify the client
        /// to play out the launch itself
        /// </summary>
        /// <param name="__instance">The instance to call any associated ActionnableInteractive components</param>
        /// <param name="___locationGameObject">The object that holds the location info on the platform's rocket spawn position</param>
        /// <param name="___hudHandler">To notify the player there is nothing to launch.</param>
        /// <returns>False in multiplayer, true in singleplayer</returns>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ActionSendInSpace), nameof(ActionSendInSpace.OnAction))]
        static bool ActionSendInSpace_OnAction(
            ActionSendInSpace __instance,
            GameObject ___locationGameObject, 
            BaseHudHandler ___hudHandler)
        {
            if (updateMode == MultiplayerMode.CoopClient)
            {
                if (___locationGameObject == null)
                {
                    Destroy(__instance);
                }
                if (TryFindRocket(___locationGameObject.transform.position, 1, out var rocketWo, out _))
                {
                    LogInfo("ActionSendInSpace_OnAction: Request Launch " + DebugWorldObject(rocketWo));
                    SendHost(new MessageLaunch() { rocketId = rocketWo.GetId() }, true);
                }
                else
                {
                    ___hudHandler.DisplayCursorText("UI_nothing_to_launch_in_space", 2f, "");
                }
                return false;
            }
            else
            if (updateMode == MultiplayerMode.CoopHost)
            {
                if (___locationGameObject == null)
                {
                    Destroy(__instance);
                }
                if (TryFindRocket(___locationGameObject.transform.position, 1, out var rocketWo, out var rocketGo))
                {
                    if (rocketGo.GetComponent<RocketAlreadyLaunched>() == null)
                    {
                        rocketGo.AddComponent<RocketAlreadyLaunched>();

                        LogInfo("LaunchRocket: Launch " + DebugWorldObject(rocketWo));
                        SendAllClients(new MessageLaunch() { rocketId = rocketWo.GetId() }, true);

                        var machineRocket = rocketGo.GetComponent<MachineRocket>();
                        if (machineRocket == null)
                        {
                            machineRocket = rocketGo.AddComponent<MachineRocket>();
                        }
                        machineRocket.Ignite();

                        if (__instance.GetComponent<ActionnableInteractive>() != null)
                        {
                            __instance.GetComponent<ActionnableInteractive>().OnActionInteractive();
                        }

                        Managers.GetManager<MeteoHandler>().SendSomethingInSpace(rocketWo.GetGroup());
                        GetPlayerMainController().GetPlayerCameraShake().SetShaking(true, 0.06f, 0.0035f);
                        HandleRocketMultiplier(rocketWo);

                        machineRocket.StartCoroutine(RocketLaunchTracker(rocketWo, machineRocket));
                    }
                }
                else
                {
                    LogWarning("LaunchRocket: Can't find any rocket around " + ___locationGameObject.transform.position);
                    ___hudHandler.DisplayCursorText("UI_nothing_to_launch_in_space", 2f, "");
                }
                return false;
            }
            return true;
        }

        static bool TryFindRocket(Vector3 around, float radius, out WorldObject rocketWo, out GameObject rocketGo)
        {
            foreach (WorldObject wo in WorldObjectsHandler.GetAllWorldObjects())
            {
                if (Vector3.Distance(wo.GetPosition(), around) < radius && wo.GetGroup().GetId().StartsWith("Rocket"))
                {
                    if (TryGetGameObject(wo, out rocketGo) && rocketGo != null && rocketGo.activeSelf) {
                        rocketWo = wo;
                        return true;
                    }
                }
            }
            rocketWo = null;
            rocketGo = null;
            return false;
        }

        /// <summary>
        /// After launching a rocket, hide the WorldObject after this amount of time and
        /// notify the client.
        /// </summary>
        static float hideRocketDelay = 38f;
        static float updateWorldObjectPositionDelay = 0.05f;

        static IEnumerator RocketLaunchTracker(WorldObject rocketWo, MachineRocket rocket)
        {
            float until = Time.time + hideRocketDelay;
            for (; ; )
            {
                if (until > Time.time)
                {
                    yield return new WaitForSeconds(updateWorldObjectPositionDelay);
                    rocketWo.SetPositionAndRotation(rocket.transform.position, rocket.transform.rotation);
                    SendWorldObjectToClients(rocketWo, false);
                }
                else
                {
                    break;
                }
            }
            LogInfo("RocketLaunchTracker:   Orbit reached: " + DebugWorldObject(rocketWo));
            rocketWo.ResetPositionAndRotation();
            SendWorldObjectToClients(rocketWo, false);
        }

        /// <summary>
        /// Caches the hidden rocket inventories.
        /// </summary>
        static Dictionary<string, Inventory> hiddenRocketInventories = new();

        /// <summary>
        /// We replicate ActionSendIntoSpace::HandleRocketMultiplier as we might not have
        /// an ActionSendIntoSpace instance to call it on.
        /// 
        /// The vanilla version finds or creates a hidden rocket container and adds the rocket
        /// to its inventory. Later, since this hidden rocket container is constructible,
        /// the WorldUnit::SetIncreaseAndDecreaseForWorldObjects will find it
        /// and get the total multiplier based on how many rockets are registered inside.
        /// </summary>
        /// <param name="rocketWo">The rocket used to find out what world unit it affects</param>
        static void HandleRocketMultiplier(WorldObject rocketWo)
        {
            foreach (WorldUnit worldUnit in Managers.GetManager<WorldUnitsHandler>().GetAllWorldUnits())
            {
                DataConfig.WorldUnitType unitType = worldUnit.GetUnitType();
                if (((GroupItem)rocketWo.GetGroup()).GetGroupUnitMultiplier(unitType) != 0f)
                {
                    if (GameConfig.spaceGlobalMultipliersGroupIds.TryGetValue(unitType, out var hiddenGroupId))
                    {

                        if (!hiddenRocketInventories.TryGetValue(hiddenGroupId, out var hiddenInv))
                        {
                            WorldObject hiddenRocketContainer = null;

                            foreach (var wo in WorldObjectsHandler.GetConstructedWorldObjects())
                            {
                                if (wo.GetGroup().GetId() == hiddenGroupId)
                                {
                                    hiddenRocketContainer = wo;
                                    break;
                                }
                            }

                            if (hiddenRocketContainer == null)
                            {
                                LogInfo("HandleRocketMultiplier: Creating shadow rocket container for " + hiddenGroupId);
                                hiddenRocketContainer = WorldObjectsHandler.CreateNewWorldObject(GroupsHandler.GetGroupViaId(hiddenGroupId), 0);
                                hiddenRocketContainer.SetPositionAndRotation(GameConfig.spaceLocation, Quaternion.identity);
                                WorldObjectsHandler.InstantiateWorldObject(hiddenRocketContainer, false);
                            }
                            hiddenInv = InventoriesHandler.GetInventoryById(hiddenRocketContainer.GetLinkedInventoryId());
                            hiddenRocketInventories[hiddenGroupId] = hiddenInv;
                        }

                        // Do not add duplicates
                        bool found = false;
                        foreach (WorldObject rocketAlreadyThere in hiddenInv.GetInsideWorldObjects())
                        {
                            if (rocketAlreadyThere.GetId() == rocketWo.GetId())
                            {
                                found = true;
                                break;
                            }
                        }
                        if (!found && !hiddenInv.AddItem(rocketWo))
                        {
                            // automatically resize to accomodate
                            hiddenInv.SetSize(hiddenInv.GetSize() * 11 / 10 + 1);
                            hiddenInv.AddItem(rocketWo);
                        }

                        Managers.GetManager<WorldUnitsHandler>().GetUnit(unitType).ForceResetValues();
                    }
                }
            }
        }

        /// <summary>
        /// After the iginite sequence, we tell the system to ignore collisions between
        /// the rocket and the player, so they can't knock them off course.
        /// We don't want to sync their phyiscs
        /// </summary>
        /// <param name="___rigidbody">The body of the rocket.</param>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(MachineRocket), nameof(MachineRocket.Ignite))]
        static void MachineRocket_Ignite(Rigidbody ___rigidbody)
        {
            Physics.IgnoreCollision(___rigidbody.GetComponentInChildren<Collider>(), GetPlayerMainController().GetComponentInChildren<Collider>());
        }

        static float rocketShakeDistance = 100f;

        /// <summary>
        /// Set of rocket ids that are in flight and updates to its
        /// position should be ignored.
        /// </summary>
        static readonly HashSet<int> rocketsInFlight = new();

        static void ReceiveMessageLaunch(MessageLaunch ml)
        {
            if (worldObjectById.TryGetValue(ml.rocketId, out var rocketWo))
            {
                if (TryGetGameObject(rocketWo, out var rocketGo) && rocketGo != null && rocketGo.activeSelf)
                {
                    if (rocketGo.GetComponent<RocketAlreadyLaunched>() == null)
                    {
                        rocketGo.AddComponent<RocketAlreadyLaunched>();
                        var machineRocket = rocketGo.GetComponent<MachineRocket>();
                        if (machineRocket == null)
                        {
                            machineRocket = rocketGo.AddComponent<MachineRocket>();
                        }
                        if (updateMode == MultiplayerMode.CoopHost)
                        {
                            SendAllClients(new MessageLaunch() { rocketId = rocketWo.GetId() }, true);
                        }
                        machineRocket.Ignite();
                        PlayerMainController pm = GetPlayerMainController();
                        if (Vector3.Distance(pm.transform.position, rocketWo.GetPosition()) < rocketShakeDistance)
                        {
                            pm.GetPlayerCameraShake().SetShaking(true, 0.06f, 0.0035f);
                        }
                        LogInfo("ReceiveMessageLaunch: Launch " + DebugWorldObject(rocketWo));
                        if (updateMode == MultiplayerMode.CoopHost)
                        {
                            Managers.GetManager<MeteoHandler>().SendSomethingInSpace(rocketWo.GetGroup());

                            HandleRocketMultiplier(rocketWo);

                            machineRocket.StartCoroutine(RocketLaunchTracker(rocketWo, machineRocket));
                        }
                        else
                        {
                            rocketsInFlight.Add(rocketWo.GetId());
                        }

                    }
                }
                else
                {
                    LogWarning("ReceiveMessageLaunch: No rocket GameObject = " + DebugWorldObject(rocketWo));
                }
            }
            else
            {
                LogWarning("ReceiveMessageLaunch: Unknown rocketId " + ml.rocketId);
            }
        }

        static void LaunchStuckRockets()
        {
            LogInfo("Trying to launch stuck rockets: " + worldObjectById.Count);
            foreach (WorldObject wo in worldObjectById.Values)
            {
                string gid = wo.GetGroup().GetId();
                if (gid.StartsWith("Rocket") && gid != "RocketReactor")
                {
                    if (wo.GetIsPlaced())
                    {
                        LogInfo("LaunchStuckRockets: " + DebugWorldObject(wo));
                        var launchMessage = new MessageLaunch()
                        {
                            rocketId = wo.GetId()
                        };
                        ReceiveMessageLaunch(launchMessage);
                    }
                    else
                    if (updateMode == MultiplayerMode.CoopHost)
                    {
                        // Make sure the hidden rocket container has the rocket
                        HandleRocketMultiplier(wo);
                    }
                }
            }
        }
    }
}
