using ExtensionsMethods;
using LosSantosRED.lsr.Helper;
using LosSantosRED.lsr.Interface;
using Rage;
using Rage.Native;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace LosSantosRED.lsr.Player.ActiveTasks
{
    public class GangTurfCaptureTask : IPlayerTask
    {
        private ITaskAssignable Player;
        private ITimeReportable Time;
        private IGangs Gangs;
        private IGangTerritories GangTerritories;
        private IZones Zones;
        private PlayerTasks PlayerTasks;
        private IPlacesOfInterest PlacesOfInterest;
        private ISettingsProvideable Settings;
        private IEntityProvideable World;
        private ICrimes Crimes;
        private Gang HiringGang;
        private GangDen HiringGangDen;
        private Gang DefendingGang;
        private Zone TargetZone;
        private PlayerTask CurrentTask;
        private PhoneContact PhoneContact;
        private GangTasks GangTasks;
        private TerritoryCaptureManager CaptureManager;
        private Blip CaptureBlip;

        // Wave state
        private int CurrentWave = 0;
        private int TotalWaves;
        private int WaveBaseEnemyCount;
        private int WaveEnemyIncrement;
        private int KilledMembersAtWaveStart;
        private int WaveKillTarget;
        private bool IsHoldPhase = false;
        private int HoldTimeSeconds;
        private uint HoldStartGameTime;
        private int MoneyToRecieve;

        public GangTurfCaptureTask(ITaskAssignable player, ITimeReportable time, IGangs gangs, PlayerTasks playerTasks,
            IPlacesOfInterest placesOfInterest, ISettingsProvideable settings, IEntityProvideable world, ICrimes crimes,
            PhoneContact phoneContact, GangTasks gangTasks, Gang defendingGang, IGangTerritories gangTerritories,
            IZones zones, Zone targetZone, TerritoryCaptureManager captureManager)
        {
            Player = player;
            Time = time;
            Gangs = gangs;
            PlayerTasks = playerTasks;
            PlacesOfInterest = placesOfInterest;
            Settings = settings;
            World = world;
            Crimes = crimes;
            PhoneContact = phoneContact;
            GangTasks = gangTasks;
            DefendingGang = defendingGang;
            GangTerritories = gangTerritories;
            Zones = zones;
            TargetZone = targetZone;
            CaptureManager = captureManager;
        }

        public void Setup()
        {
        }

        public void Dispose()
        {
            RemoveBlip();
        }

        public void Start(Gang activeGang)
        {
            HiringGang = activeGang;
            if (PlayerTasks.CanStartNewTask(activeGang?.ContactName))
            {
                GetHiringDen();
                if (HiringGangDen == null || TargetZone == null)
                {
                    GangTasks.SendGenericTooSoonMessage(PhoneContact);
                    return;
                }

                TotalWaves = Settings.SettingsManager.GangSettings.TurfCaptureWaveCount;
                WaveBaseEnemyCount = Settings.SettingsManager.GangSettings.TurfCaptureWaveBaseEnemyCount;
                WaveEnemyIncrement = Settings.SettingsManager.GangSettings.TurfCaptureWaveEnemyIncrement;
                HoldTimeSeconds = Settings.SettingsManager.GangSettings.TurfCaptureHoldTimeSeconds;

                GetPayment();
                DeductCaptureCost();
                CaptureManager.SetInProgress(TargetZone.InternalGameName, DefendingGang.ID, HiringGang.ID);
                SendInitialInstructionsMessage();
                AddTask();
                CreateCaptureBlip();

                GameFiber.StartNew(delegate
                {
                    try
                    {
                        Loop();
                        FinishTask();
                    }
                    catch (Exception ex)
                    {
                        EntryPoint.WriteToConsole(ex.Message + " " + ex.StackTrace, 0);
                        EntryPoint.ModController.CrashUnload();
                    }
                }, "TurfCaptureFiber");
            }
        }

        private void Loop()
        {
            // Phase A: Wave-based combat
            CurrentWave = 0;
            while (CurrentWave < TotalWaves)
            {
                CurrentTask = PlayerTasks.GetTask(HiringGang.ContactName);
                if (CurrentTask == null || !CurrentTask.IsActive)
                {
                    OnFailed();
                    return;
                }

                CurrentWave++;
                int enemyCount = WaveBaseEnemyCount + (CurrentWave - 1) * WaveEnemyIncrement;
                GangReputation defenderRep = Player.RelationshipManager.GangRelationships.GetReputation(DefendingGang);
                KilledMembersAtWaveStart = defenderRep != null ? defenderRep.MembersKilled : 0;
                WaveKillTarget = enemyCount;

                SendWaveMessage(CurrentWave, TotalWaves, enemyCount);

                // Wait for player to kill enough members in this wave
                while (true)
                {
                    CurrentTask = PlayerTasks.GetTask(HiringGang.ContactName);
                    if (CurrentTask == null || !CurrentTask.IsActive)
                    {
                        OnFailed();
                        return;
                    }

                    if (!IsPlayerInTargetZone())
                    {
                        SendLeftZoneMessage();
                        OnFailed();
                        return;
                    }

                    int currentKills = (defenderRep != null ? defenderRep.MembersKilled : 0) - KilledMembersAtWaveStart;
                    if (currentKills >= WaveKillTarget)
                    {
                        break;
                    }
                    GameFiber.Sleep(1000);
                }

                if (CurrentWave < TotalWaves)
                {
                    SendWaveClearedMessage(CurrentWave, TotalWaves);
                    GameFiber.Sleep(3000);
                }
            }

            // Phase B: Hold the point
            IsHoldPhase = true;
            HoldStartGameTime = Game.GameTime;
            SendHoldMessage();

            while (true)
            {
                CurrentTask = PlayerTasks.GetTask(HiringGang.ContactName);
                if (CurrentTask == null || !CurrentTask.IsActive)
                {
                    OnFailed();
                    return;
                }

                if (!IsPlayerInTargetZone())
                {
                    SendLeftZoneMessage();
                    OnFailed();
                    return;
                }

                uint elapsed = Game.GameTime - HoldStartGameTime;
                if (elapsed >= (uint)(HoldTimeSeconds * 1000))
                {
                    break;
                }
                GameFiber.Sleep(500);
            }

            // Success
            CurrentTask = PlayerTasks.GetTask(HiringGang.ContactName);
            if (CurrentTask != null && CurrentTask.IsActive)
            {
                int incomePerTick = TerritoryCaptureManager.GetIncomeForEconomy(TargetZone.Economy);
                CaptureManager.CaptureZone(TargetZone.InternalGameName, DefendingGang.ID, HiringGang.ID, incomePerTick);
                CurrentTask.OnReadyForPayment(true, $"Territory ~g~{TargetZone.DisplayName}~s~ captured! Return to the {HiringGang.DenName} for payment.");
            }
        }

        private void FinishTask()
        {
            RemoveBlip();
            if (CurrentTask != null && CurrentTask.IsActive && CurrentTask.IsReadyForPayment)
            {
                SendSuccessMessage();
            }
        }

        private void OnFailed()
        {
            CaptureManager.ClearInProgress(TargetZone.InternalGameName);
            RemoveBlip();
            GangTasks.SendGenericFailMessage(PhoneContact);
        }

        private bool IsPlayerInTargetZone()
        {
            Zone currentZone = Zones.GetZone(Player.Character.Position);
            return currentZone != null && currentZone.InternalGameName.Equals(TargetZone.InternalGameName, StringComparison.OrdinalIgnoreCase);
        }

        private void GetHiringDen()
        {
            HiringGangDen = PlacesOfInterest.GetMainDen(HiringGang.ID, World.IsMPMapLoaded, Player.CurrentLocation);
        }

        private void GetPayment()
        {
            MoneyToRecieve = RandomItems.GetRandomNumberInt(HiringGang.HitPaymentMin, HiringGang.HitPaymentMax).Round(500) * TotalWaves;
            if (MoneyToRecieve <= 0)
            {
                MoneyToRecieve = 2000;
            }
        }

        private void DeductCaptureCost()
        {
            int cost = GetCaptureCost();
            if (cost > 0)
            {
                Player.BankAccounts.GiveMoney(-cost, false);
            }
        }

        private int GetCaptureCost()
        {
            float multiplier = Settings.SettingsManager.GangSettings.TurfCaptureCostMultiplier;
            switch (TargetZone.Economy)
            {
                case eLocationEconomy.Poor: return (int)(5000 * multiplier);
                case eLocationEconomy.Middle: return (int)(10000 * multiplier);
                case eLocationEconomy.Rich: return (int)(20000 * multiplier);
                default: return (int)(10000 * multiplier);
            }
        }

        public static int GetCaptureCostForZone(Zone zone, float multiplier)
        {
            switch (zone.Economy)
            {
                case eLocationEconomy.Poor: return (int)(5000 * multiplier);
                case eLocationEconomy.Middle: return (int)(10000 * multiplier);
                case eLocationEconomy.Rich: return (int)(20000 * multiplier);
                default: return (int)(10000 * multiplier);
            }
        }

        private void AddTask()
        {
            PlayerTasks.AddTask(HiringGang.Contact, MoneyToRecieve, 2000, 0, -500, 7, "Turf Capture");
        }

        private void CreateCaptureBlip()
        {
            try
            {
                if (TargetZone.Boundaries != null && TargetZone.Boundaries.Length > 0)
                {
                    CaptureBlip = new Blip(Player.Character.Position, 200f)
                    {
                        Color = Color.OrangeRed,
                        Alpha = 0.5f
                    };
                    NativeFunction.CallByName<bool>("SET_BLIP_AS_SHORT_RANGE", (uint)CaptureBlip.Handle, false);
                    NativeFunction.CallByName<bool>("BEGIN_TEXT_COMMAND_SET_BLIP_NAME", "STRING");
                    NativeFunction.CallByName<bool>("ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME", $"Turf War: {TargetZone.DisplayName}");
                    NativeFunction.CallByName<bool>("END_TEXT_COMMAND_SET_BLIP_NAME", (uint)CaptureBlip.Handle);
                }
            }
            catch (Exception ex)
            {
                EntryPoint.WriteToConsole($"TurfCapture CreateBlip error: {ex.Message}");
            }
        }

        private void RemoveBlip()
        {
            try
            {
                if (CaptureBlip != null && CaptureBlip.IsValid())
                {
                    CaptureBlip.Delete();
                }
            }
            catch { }
            CaptureBlip = null;
        }

        // Messages
        private void SendInitialInstructionsMessage()
        {
            int cost = GetCaptureCost();
            List<string> Replies = new List<string>() {
                $"Time to claim {DefendingGang.ColorPrefix}{TargetZone.DisplayName}~s~ from those {DefendingGang.ColorPrefix}{DefendingGang.ShortName}~s~ bitches. I've sent some boys your way. Clear out {TotalWaves} waves of their guys and hold the area. Cost you ~r~${cost}~s~ up front, but you'll get ~g~${MoneyToRecieve}~s~ when it's done. Head to {HiringGangDen.FullStreetAddress} after.",
                $"We're taking {DefendingGang.ColorPrefix}{TargetZone.DisplayName}~s~ today. This is costing you ~r~${cost}~s~. Kill {TotalWaves} waves of {DefendingGang.ColorPrefix}{DefendingGang.ShortName}~s~, then hold the block. ~g~${MoneyToRecieve}~s~ waiting for you at the {HiringGang.DenName}.",
                $"Listen up. {DefendingGang.ColorPrefix}{TargetZone.DisplayName}~s~ is ours now. Fight through {TotalWaves} waves and hold it down. ~r~${cost}~s~ to start this operation. ~g~${MoneyToRecieve}~s~ at {HiringGangDen.FullStreetAddress} when you're done.",
            };
            Player.CellPhone.AddPhoneResponse(HiringGang.Contact.Name, HiringGang.Contact.IconName, Replies.PickRandom());
        }

        private void SendWaveMessage(int wave, int total, int enemyCount)
        {
            Game.DisplayHelp($"~r~Wave {wave}/{total}~s~ - Kill ~r~{enemyCount}~s~ {DefendingGang.ShortName} members");
        }

        private void SendWaveClearedMessage(int wave, int total)
        {
            Game.DisplayNotification($"~g~Wave {wave}/{total} cleared!~s~ Get ready for the next wave...");
        }

        private void SendHoldMessage()
        {
            Game.DisplayHelp($"~y~Hold the territory!~s~ Stay in {TargetZone.DisplayName} for {HoldTimeSeconds} seconds.");
        }

        private void SendLeftZoneMessage()
        {
            List<string> Replies = new List<string>() {
                $"You left the zone! The capture is fucked. What the hell?",
                $"We lost the territory because you ran away. Unbelievable.",
                $"You bailed on us mid-fight? That's it, the capture is off.",
            };
            Player.CellPhone.AddScheduledText(PhoneContact, Replies.PickRandom(), 0, false);
        }

        private void SendSuccessMessage()
        {
            List<string> Replies = new List<string>() {
                $"{DefendingGang.ColorPrefix}{TargetZone.DisplayName}~s~ is ours now! Come by the {HiringGang.DenName} on {HiringGangDen.FullStreetAddress} to collect your ~g~${MoneyToRecieve}~s~.",
                $"We did it. {DefendingGang.ColorPrefix}{TargetZone.DisplayName}~s~ belongs to us. ~g~${MoneyToRecieve}~s~ waiting for you at {HiringGangDen.FullStreetAddress}.",
                $"The hood is ours. Get to {HiringGangDen.FullStreetAddress} for your ~g~${MoneyToRecieve}~s~.",
            };
            Player.CellPhone.AddScheduledText(PhoneContact, Replies.PickRandom(), 1, false);
        }
    }
}
