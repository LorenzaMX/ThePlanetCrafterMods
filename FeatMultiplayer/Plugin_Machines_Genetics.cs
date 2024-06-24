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
        /// The vanilla game uses UiWindowGenetics::SetDisplayDependingOnGrowth
        /// to show the status of the machine based on the associated WorldObject's
        /// growth amount.
        /// 
        /// On the host, we have to redo it to intercept the creation of the final
        /// product.
        /// 
        /// On the client, we have to redo it to avoid it to create the item locally
        /// and wait for the host to send it down.
        /// </summary>
        /// <returns></returns>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(UiWindowGenetics), "SetDisplayDependingOnGrowth")]
        static bool UiWindowGenetics_SetDisplayDependingOnGrowth(
            UiWindowGenetics __instance,
            WorldObject ___worldObject, 
            Inventory ___inventoryRight,
            ref Group ___matchingGroup,
            DataConfig.CraftableIn ___craftableIdentifier,
            GroupData ___groupLarvae
        )
        {
            if (updateMode != MultiplayerMode.SinglePlayer)
            {
                CleanStateDisplay(__instance);
                if (___worldObject.GetGrowth() >= 100f)
                {
                    __instance.buttonAnalyze.SetActive(value: true);
                }
                else if (___worldObject.GetGrowth() > 0f)
                {
                    __instance.buttonCancelCraft.SetActive(true);
                    LockInventory(__instance, ___inventoryRight);
                    __instance.sequencingAnimationContainer.SetActive(true);
                    ___matchingGroup = ___worldObject.GetLinkedGroups()[0];

                    if (___craftableIdentifier == DataConfig.CraftableIn.CraftGeneticT1)
                    {
                        __instance.groupLoadingDisplayer.SetDisplayLoadingGroup(___matchingGroup, true);
                    }
                    else if (___craftableIdentifier == DataConfig.CraftableIn.CraftIncubatorT1)
                    {
                        __instance.groupLoadingDisplayer.SetDisplayLoadingGroup(GroupsHandler.GetGroupViaId(___groupLarvae.id), true, _showInterogationPoint: true);
                    }
                }
                else
                {
                    __instance.buttonAnalyze.SetActive(true);
                    __instance.StopCoroutine(GetUpdateUiCoroutine(__instance));
                }
                __instance.textGrowth.text = ((___worldObject.GetGrowth() == 0f) ? "" : (Mathf.Round(___worldObject.GetGrowth()).ToString() + "%"));
                if (__instance.groupLoadingDisplayer.GetGroupDisplayer() != null)
                {
                    __instance.groupLoadingDisplayer.GetGroupDisplayer().SetFillLevel(___worldObject.GetGrowth() / 100f);
                }
                return false;
            }
            return true;
        }

        static IEnumerator GetUpdateUiCoroutine(UiWindowGenetics __instance)
        {
            return (IEnumerator)AccessTools.Field(typeof(UiWindowGenetics), "updateUiCoroutine").GetValue(__instance);
        }

        /// <summary>
        /// Recreation of UiWindowGenetics::CleanStateDisplay as it is private
        /// yet the field it manipulates are public
        /// </summary>
        /// <param name="instance">The ui instance</param>
        static void CleanStateDisplay(UiWindowGenetics instance)
        {
            instance.buttonAnalyze.SetActive(false);
            instance.buttonCraft.SetActive(false);
            instance.buttonCancelCraft.SetActive(false);
            instance.dnaLoading.SetActive(false);
            instance.resultNoMatch.SetActive(false);
            instance.groupLoadingDisplayer.gameObject.SetActive(false);
            instance.sequencingAnimationContainer.SetActive(false);
        }

        static void LockInventory(MonoBehaviour parent, Inventory inventoryRight)
        {
            foreach (WorldObject worldObject in inventoryRight.GetInsideWorldObjects())
            {
                worldObject.SetLockInInventoryTime(Time.time + 0.01f);
            }
            parent.StartCoroutine(RefreshInventoryAfter(0.1f, inventoryRight));
        }

        static IEnumerator RefreshInventoryAfter(float time, Inventory inventoryRight)
        {
            yield return new WaitForSeconds(time);
            inventoryRight.RefreshDisplayerContent();
        }

        /// <summary>
        /// The vanilla game calls UiWindowGenetics::StartCraftProcess
        /// when the user clicks the start button. It takes the UiWindowGenetics.matchingGroup
        /// and sets it as the linked group object on the parent machine WorldObject.
        /// 
        /// On the host, we simply send an update to the client.
        /// 
        /// On the client, we send the host this matchingGroup::GetId so it can
        /// set it on the WorldObject and activate the sequencing process.
        /// </summary>
        /// <returns></returns>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(UiWindowGenetics), nameof(UiWindowGenetics.StartCraftProcess))]
        static bool UiWindowGenetics_StartCraftProcess(
            UiWindowGenetics __instance,
            WorldObject ___worldObject,
            Group ___matchingGroup,
            Inventory ___inventoryRight
            )
        {
            if (updateMode != MultiplayerMode.SinglePlayer)
            {
                ___worldObject.SetGrowth(1f);
                ___worldObject.SetLinkedGroups(new List<Group> { ___matchingGroup });
                LockInventory(__instance, ___inventoryRight);
                __instance.StartCoroutine(GetUpdateUiCoroutine(__instance));

                var msg = new MessageGeneticsAction()
                {
                    machineId = ___worldObject.GetId(),
                    groupId = ___matchingGroup.GetId()
                };

                if (updateMode == MultiplayerMode.CoopHost)
                {
                    SendAllClients(msg, true);
                    SendWorldObjectToClients(___worldObject, false);
                }
                else
                {
                    SendHost(msg, true);
                }
                return false;
            }
            return true;
        }

        /// <summary>
        /// The vanilla game calls UiWindowGenetics::CancelCraftProcess
        /// when the sequencing process is cancelled. The inventory is unlocked
        /// and the progress is cleared.
        /// 
        /// On the host, we let it happen, then send an update with the now cleared
        /// machine WorldObject by the original method.
        /// 
        /// On the client, we send a request with empty groupId so the host can clear
        /// the machine WorldObject and send the updated state back.
        /// </summary>
        /// <param name="___isFinishingSequencing">If true, the code won't execute as the sequencing has just finished</param>
        /// <param name="___worldObject">The machine's WorldObject to clear</param>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(UiWindowGenetics), nameof(UiWindowGenetics.CancelCraftProcess))]
        static void UiWindowGenetics_CancelCraftProcess(
            WorldObject ___worldObject)
        {

            if (updateMode == MultiplayerMode.CoopHost)
            {
                SendAllClients(new MessageGeneticsAction()
                {
                    machineId = ___worldObject.GetId(),
                    groupId = ""
                }, true);
                SendWorldObjectToClients(___worldObject, false);
            }
            else
            if (updateMode == MultiplayerMode.CoopClient)
            {
                SendHost(new MessageGeneticsAction()
                {
                    machineId = ___worldObject.GetId(),
                    groupId = ""
                }, true);
            }
        }

        /// <summary>
        /// The vanilla game calls MachineGrowerIfLinkedGroup::UpdateGrowth
        /// to return a coroutine which checks if there is something to
        /// grow and make progress of it.
        /// 
        /// On the host, we have to rewrite this so the growth update is sent out
        /// to the client.
        /// 
        /// On the client, we use an empty coroutine as it won't grow anything but
        /// rely on the host signals.
        /// </summary>
        /// <param name="__instance">The grower component to find the actual grower machine.</param>
        /// <param name="timeRepeat">How often grow the thing</param>
        /// <param name="__result">To replace the original coroutine with ours</param>
        /// <returns>false in multiplayer, true in singleplayer</returns>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(MachineGrowerIfLinkedGroup), "UpdateGrowth")]
        static bool MachineGrowerIfLinkedGroup_UpdateGrowth(
            MachineGrowerIfLinkedGroup __instance,
            float timeRepeat, 
            float ___increaseRate,
            ref IEnumerator __result)
        {
            if (updateMode == MultiplayerMode.CoopClient)
            {
                __result = new List<int>().GetEnumerator();
                return false;
            }
            else
            if (updateMode == MultiplayerMode.CoopHost)
            {
                __result = MachineGrowerIfLinkedGroup_UpdateGrowth_Override(timeRepeat, __instance, ___increaseRate);
                return false;
            }
            return true;
        }

        /// <summary>
        /// Coroutine to update the growth on the host.
        /// </summary>
        /// <param name="timeRepeat"></param>
        /// <param name="instance"></param>
        /// <param name="increaseRate"></param>
        /// <returns>The coroutine as IEnumerator</returns>
        static IEnumerator MachineGrowerIfLinkedGroup_UpdateGrowth_Override(float timeRepeat, MachineGrowerIfLinkedGroup instance, float increaseRate)
        {
            for (; ; )
            {
                var machineWo = (WorldObject)machineGrowerIfLinkedGroupWorldObject.GetValue(instance);
                var hasEnergy = (bool)machineGrowerIfLinkedGroupHasEnergy.GetValue(instance);
                if (machineWo != null && hasEnergy)
                {
                    if (machineWo.GetGrowth() < 100f)
                    {
                        var linkedGroups = machineWo.GetLinkedGroups();
                        if (linkedGroups != null && linkedGroups.Count != 0)
                        {
                            machineWo.SetGrowth(machineWo.GetGrowth() + increaseRate);
                            if (machineWo.GetGrowth() >= 100f)
                            {
                                machineWo.SetGrowth(100f);
                                machineGrowerIfLinkedGroupSetInteractiveStatus.Invoke(instance, new object[] { false, false });
                            } 
                            else
                            {
                                machineGrowerIfLinkedGroupSetInteractiveStatus.Invoke(instance, new object[] { true, false });
                            }
                            SendWorldObjectToClients(machineWo, false);
                        }
                        else
                        {
                            machineGrowerIfLinkedGroupSetInteractiveStatus.Invoke(instance, new object[] { false, false });
                        }
                    }
                    else
                    {
                        machineGrowerIfLinkedGroupSetInteractiveStatus.Invoke(instance, new object[] { false, false });
                    }
                }
                yield return new WaitForSeconds(timeRepeat);
            }
        }

        /// <summary>
        /// The vanilla game uses MachineConvertRecipe::CheckIfFullyGrown to periodically
        /// check if an associated WorldObject is fully grown (WorldObject.GetGrowth() &gt;= 100).
        /// If so, removes everything from the inventory and adds a produce back into it, resetting
        /// the growth of the associated WorldObject.
        /// 
        /// On the host, we have to intercept the creation of the replacement object and send it to
        /// the client, along with the info about the growth getting a reset.
        /// 
        /// On the client, we don't do anything and let the world object and inventory update messages
        /// take care of things.
        /// </summary>
        /// <param name="___worldObject">The associated grower machine's WorldObject.</param>
        /// <param name="___inventory">The associated grower machine's inventory.</param>
        /// <returns>true in singleplayer, false otherwise</returns>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(MachineConvertRecipe), nameof(MachineConvertRecipe.CheckIfFullyGrown))]
        static bool MachineConvertRecipe_CheckIfFullyGrown(
            WorldObject ___worldObject,
            Inventory ___inventory
        )
        {
            if (updateMode == MultiplayerMode.CoopHost)
            {
                if (___worldObject != null && ___worldObject.GetGrowth() >= 100f)
                {
                    var linkedGroups = ___worldObject.GetLinkedGroups();
                    if (linkedGroups != null && linkedGroups.Count != 0)
                    {
                        Group group = linkedGroups[0];
                        GroupItem groupItem = (GroupItem)group;
                        if (group != null)
                        {
                            List<WorldObject> insideWorldObjects = ___inventory.GetInsideWorldObjects();
                            for (int i = insideWorldObjects.Count - 1; i > -1; i--)
                            {
                                WorldObject innerWo = insideWorldObjects[i];
                                ___inventory.RemoveItem(innerWo, true);
                            }
                            WorldObject product = WorldObjectsHandler.CreateNewWorldObject(groupItem, 0);
                            SendWorldObjectToClients(product, false);
                            ___inventory.AddItem(product);
                            ___worldObject.SetGrowth(0f);
                            ___worldObject.SetLinkedGroups(null);
                            SendWorldObjectToClients(___worldObject, false);
                        }
                    }
                }
                return false;
            }
            else
            if (updateMode == MultiplayerMode.CoopClient)
            {
                return false;
            }
            return true;
        }

        static void ReceiveMessageGeneticsAction(MessageGeneticsAction mga)
        {
            if (worldObjectById.TryGetValue(mga.machineId, out var machineWo))
            {
                if (TryGetGameObject(machineWo, out var machineGo))
                {
                    var invAssoc = machineGo.GetComponentInParent<InventoryAssociated>();
                    if (invAssoc != null)
                    {
                        Inventory inv = invAssoc.GetInventory();
                        if (string.IsNullOrEmpty(mga.groupId))
                        {
                            machineWo.SetGrowth(0f);
                            machineWo.SetLinkedGroups(null);
                            foreach (WorldObject worldObject in inv.GetInsideWorldObjects())
                            {
                                worldObject.SetLockInInventoryTime(0f);
                            }
                            inv.RefreshDisplayerContent();
                            if (updateMode == MultiplayerMode.CoopHost)
                            {
                                SendWorldObjectToClients(machineWo, false);
                            }
                        }
                        else
                        {
                            Group gr = GroupsHandler.GetGroupViaId(mga.groupId);
                            if (gr != null)
                            {
                                if (machineWo.GetGrowth() == 0f)
                                {
                                    machineWo.SetGrowth(1f);
                                    machineWo.SetLinkedGroups(new List<Group> { gr });
                                    if (updateMode == MultiplayerMode.CoopHost)
                                    {
                                        SendWorldObjectToClients(machineWo, false);
                                    }

                                    LockInventory(invAssoc, inv);

                                    var ui = Managers.GetManager<WindowsHandler>().GetWindowViaUiId(DataConfig.UiType.Genetics);
                                    if (ui.gameObject.activeSelf)
                                    {
                                        ui.StartCoroutine(GetUpdateUiCoroutine((UiWindowGenetics)ui));
                                    }
                                }
                            }
                            else
                            {
                                LogWarning("ReceiveMessageGeneticsAction: Unknown groupId " + mga.groupId + " for " + DebugWorldObject(machineWo));
                            }
                        }
                    } 
                    else
                    {
                        LogWarning("ReceiveMessageGeneticsAction: Inventory not found " + DebugWorldObject(machineWo));
                    }
                }
                else
                {
                    LogWarning("ReceiveMessageGeneticsAction: GameObject not found " + DebugWorldObject(machineWo));
                }
            }
            else
            {
                LogWarning("ReceiveMessageGeneticsAction: Unknown machine " + mga.machineId);
            }
        }
    }
}
