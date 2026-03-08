using ExtensionsMethods;
using LosSantosRED.lsr.Helper;
using LosSantosRED.lsr.Interface;
using Rage;
using System;
using System.Collections.Generic;
using System.Linq;

namespace LosSantosRED.lsr.Player.ActiveTasks
{
    public class TerritoryDefenseEvent
    {
        private ITaskAssignable Player;
        private ITimeReportable Time;
        private IGangs Gangs;
        private IZones Zones;
        private ISettingsProvideable Settings;
        private IEntityProvideable World;
        private TerritoryCaptureManager CaptureManager;
        private bool IsRunning;

        public TerritoryDefenseEvent(ITaskAssignable player, ITimeReportable time, IGangs gangs, IZones zones,
            ISettingsProvideable settings, IEntityProvideable world, TerritoryCaptureManager captureManager)
        {
            Player = player;
            Time = time;
            Gangs = gangs;
            Zones = zones;
            Settings = settings;
            World = world;
            CaptureManager = captureManager;
        }

        public void Update()
        {
            if (IsRunning) return;
            if (!CaptureManager.HasCapturedTerritories) return;
            if (!Settings.SettingsManager.GangSettings.AllowTurfCapture) return;

            TerritoryCapture territoryToDefend = CaptureManager.GetTerritoryReadyForDefense(DateTime.Now);
            if (territoryToDefend == null) return;

            float defenseChance = Settings.SettingsManager.GangSettings.TurfDefenseAttackChance;
            if (RandomItems.RandomPercent((int)(defenseChance * 100)))
            {
                StartDefense(territoryToDefend);
            }
            else
            {
                CaptureManager.ScheduleNextDefense(territoryToDefend.ZoneInternalGameName,
                    Settings.SettingsManager.GangSettings.TurfDefenseCheckIntervalMinutesMin,
                    Settings.SettingsManager.GangSettings.TurfDefenseCheckIntervalMinutesMax);
            }
        }

        private void StartDefense(TerritoryCapture territory)
        {
            IsRunning = true;
            GameFiber.StartNew(delegate
            {
                try
                {
                    RunDefense(territory);
                }
                catch (Exception ex)
                {
                    EntryPoint.WriteToConsole(ex.Message + " " + ex.StackTrace, 0);
                }
                finally
                {
                    IsRunning = false;
                }
            }, "TerritoryDefenseFiber");
        }

        private void RunDefense(TerritoryCapture territory)
        {
            Zone zone = Zones.GetZone(territory.ZoneInternalGameName);
            if (zone == null)
            {
                CaptureManager.ScheduleNextDefense(territory.ZoneInternalGameName, 30, 90);
                return;
            }

            CaptureManager.SetUnderAttack(territory.ZoneInternalGameName);
            SendDefenseNotification(territory, zone);

            // Check if player is in the zone
            Zone playerZone = Zones.GetZone(Player.Character.Position);
            bool playerIsPresent = playerZone != null &&
                playerZone.InternalGameName.Equals(territory.ZoneInternalGameName, StringComparison.OrdinalIgnoreCase);

            if (playerIsPresent)
            {
                // Player is present — wait for combat to resolve
                RunActiveDefense(territory, zone);
            }
            else
            {
                // Player is absent — auto-resolve
                RunAutoDefense(territory, zone);
            }
        }

        private void RunActiveDefense(TerritoryCapture territory, Zone zone)
        {
            // Give player time to fight (60 seconds)
            uint startTime = Game.GameTime;
            uint defenseDuration = 60000;
            GangReputation attackerRep = Player.RelationshipManager.GangRelationships.GetReputation(territory.OriginalGang);
            int killsAtStart = attackerRep != null ? attackerRep.MembersKilled : 0;
            int killsNeeded = 4;

            Game.DisplayHelp($"~r~Your territory in {zone.DisplayName} is under attack!~s~ Kill ~r~{killsNeeded}~s~ attackers to defend it!");

            while (Game.GameTime - startTime < defenseDuration)
            {
                int currentKills = (attackerRep != null ? attackerRep.MembersKilled : 0) - killsAtStart;
                if (currentKills >= killsNeeded)
                {
                    // Defense success
                    OnDefenseSuccess(territory, zone);
                    return;
                }
                GameFiber.Sleep(1000);
            }

            // Time ran out, check kills
            int finalKills = (attackerRep != null ? attackerRep.MembersKilled : 0) - killsAtStart;
            if (finalKills >= killsNeeded)
            {
                OnDefenseSuccess(territory, zone);
            }
            else
            {
                OnDefenseFailed(territory, zone);
            }
        }

        private void RunAutoDefense(TerritoryCapture territory, Zone zone)
        {
            // Auto-resolve with probability
            GameFiber.Sleep(5000); // Brief delay
            float autoWinChance = Settings.SettingsManager.GangSettings.TurfDefenseAutoWinChance;
            if (RandomItems.RandomPercent((int)(autoWinChance * 100)))
            {
                OnDefenseSuccess(territory, zone);
                Game.DisplayNotification($"~g~Territory Defended~s~~n~Your crew held off the attack in ~y~{zone.DisplayName}~s~.");
            }
            else
            {
                OnDefenseFailed(territory, zone);
                Game.DisplayNotification($"~r~Territory Lost~s~~n~The {territory.OriginalGang?.ShortName ?? "enemy"} took back ~y~{zone.DisplayName}~s~.");
            }
        }

        private void OnDefenseSuccess(TerritoryCapture territory, Zone zone)
        {
            CaptureManager.ClearUnderAttack(territory.ZoneInternalGameName);
            CaptureManager.ScheduleNextDefense(territory.ZoneInternalGameName,
                Settings.SettingsManager.GangSettings.TurfDefenseCheckIntervalMinutesMin,
                Settings.SettingsManager.GangSettings.TurfDefenseCheckIntervalMinutesMax);

            Player.RelationshipManager.GangRelationships.ChangeReputation(territory.CapturingGang, 200, true);
            Game.DisplayNotification($"~g~Territory {zone.DisplayName} defended successfully!~s~");
            EntryPoint.WriteToConsole($"TERRITORY DEFENSE: {zone.DisplayName} defended successfully", 0);
        }

        private void OnDefenseFailed(TerritoryCapture territory, Zone zone)
        {
            CaptureManager.LoseZone(territory.ZoneInternalGameName);
            Player.RelationshipManager.GangRelationships.ChangeReputation(territory.CapturingGang, -200, true);
            Game.DisplayNotification($"~r~Territory {zone.DisplayName} lost!~s~ The {territory.OriginalGang?.ShortName ?? "enemy"} took it back.");
            EntryPoint.WriteToConsole($"TERRITORY DEFENSE: {zone.DisplayName} lost", 0);
        }

        private void SendDefenseNotification(TerritoryCapture territory, Zone zone)
        {
            string attackerName = territory.OriginalGang?.ShortName ?? "Unknown";
            Game.DisplayNotification("CHAR_BLANK_ENTRY", "CHAR_BLANK_ENTRY", "Territory Alert",
                $"~r~Under Attack!", $"Your territory in ~y~{zone.DisplayName}~s~ is being attacked by the {attackerName}!");
        }
    }
}
