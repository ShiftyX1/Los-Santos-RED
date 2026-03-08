using LosSantosRED.lsr.Interface;
using Rage;
using Rage.Native;
using System;
using System.Collections.Generic;
using System.Linq;

public class AITurfWarManager
{
    private IGangTerritories GangTerritories;
    private IGangs Gangs;
    private IZones Zones;
    private ISettingsProvideable Settings;
    private IEntityProvideable World;
    private ITargetable Player;
    private ITimeReportable Time;
    private GangDispatcher GangDispatcher;

    private DateTime NextWarCheckTime;
    private int WarsToday;
    private int LastWarDay;
    private bool IsWarInProgress;

    public AITurfWarManager(IGangTerritories gangTerritories, IGangs gangs, IZones zones, ISettingsProvideable settings, IEntityProvideable world, ITargetable player, ITimeReportable time, GangDispatcher gangDispatcher)
    {
        GangTerritories = gangTerritories;
        Gangs = gangs;
        Zones = zones;
        Settings = settings;
        World = world;
        Player = player;
        Time = time;
        GangDispatcher = gangDispatcher;
        ScheduleNextCheck();
    }

    public void Update()
    {
        if (!Settings.SettingsManager.GangSettings.AllowAITurfWars)
        {
            return;
        }
        if (IsWarInProgress)
        {
            return;
        }
        if (Time.CurrentDay != LastWarDay)
        {
            WarsToday = 0;
            LastWarDay = Time.CurrentDay;
        }
        if (WarsToday >= Settings.SettingsManager.GangSettings.AITurfWarMaxPerDay)
        {
            return;
        }
        if (Time.CurrentDateTime < NextWarCheckTime)
        {
            return;
        }
        float roll = (float)RandomItems.MyRand.NextDouble();
        if (roll < Settings.SettingsManager.GangSettings.AITurfWarChance)
        {
            TryStartWar();
        }
        ScheduleNextCheck();
    }

    private void ScheduleNextCheck()
    {
        int minMinutes = Settings.SettingsManager.GangSettings.AITurfWarCheckIntervalMinutesMin;
        int maxMinutes = Settings.SettingsManager.GangSettings.AITurfWarCheckIntervalMinutesMax;
        if (minMinutes <= 0) minMinutes = 45;
        if (maxMinutes <= minMinutes) maxMinutes = minMinutes + 30;
        int minutes = RandomItems.GetRandomNumberInt(minMinutes, maxMinutes);
        NextWarCheckTime = Time.CurrentDateTime.AddMinutes(minutes);
    }

    private void TryStartWar()
    {
        var candidate = FindWarCandidate();
        if (candidate == null)
        {
            return;
        }
        IsWarInProgress = true;
        WarsToday++;
        string attackerID = candidate.Value.AttackerGangID;
        string defenderID = candidate.Value.DefenderGangID;
        string zoneName = candidate.Value.ZoneInternalGameName;
        GameFiber.StartNew(delegate
        {
            try
            {
                RunWar(attackerID, defenderID, zoneName);
            }
            catch (Exception ex)
            {
                EntryPoint.WriteToConsole($"AI TURF WAR ERROR: {ex.Message}", 0);
            }
            finally
            {
                IsWarInProgress = false;
            }
        }, "AITurfWarFiber");
    }

    private struct WarCandidate
    {
        public string AttackerGangID;
        public string DefenderGangID;
        public string ZoneInternalGameName;
    }

    private WarCandidate? FindWarCandidate()
    {
        List<GangTerritory> allTerritories = GangTerritories.AllTerritories;
        if (allTerritories == null || !allTerritories.Any())
        {
            return null;
        }
        float proximityThreshold = Settings.SettingsManager.GangSettings.AITurfWarProximityThreshold;
        List<WarCandidate> candidates = new List<WarCandidate>();
        foreach (Gang attackerGang in Gangs.AllGangs)
        {
            if (attackerGang.EnemyGangs == null || !attackerGang.EnemyGangs.Any())
            {
                continue;
            }
            List<GangTerritory> attackerZones = allTerritories.Where(x => x.GangID == attackerGang.ID).ToList();
            if (!attackerZones.Any())
            {
                continue;
            }
            foreach (string enemyGangID in attackerGang.EnemyGangs)
            {
                List<GangTerritory> enemyZones = allTerritories.Where(x => x.GangID == enemyGangID && x.Priority == 0).ToList();
                foreach (GangTerritory targetZone in enemyZones)
                {
                    // Skip player-captured zones — those are handled by TerritoryDefenseEvent
                    if (GangTerritories.CaptureManager != null)
                    {
                        Gang dynamicOwner = GangTerritories.CaptureManager.GetDynamicOwner(targetZone.ZoneInternalGameName);
                        if (dynamicOwner != null)
                        {
                            continue;
                        }
                    }
                    // Check proximity between attacker territories and target zone
                    if (IsZoneNearGangTerritory(targetZone.ZoneInternalGameName, attackerZones, proximityThreshold))
                    {
                        candidates.Add(new WarCandidate
                        {
                            AttackerGangID = attackerGang.ID,
                            DefenderGangID = enemyGangID,
                            ZoneInternalGameName = targetZone.ZoneInternalGameName
                        });
                    }
                }
            }
        }
        if (!candidates.Any())
        {
            return null;
        }
        return candidates[RandomItems.MyRand.Next(candidates.Count)];
    }

    private bool IsZoneNearGangTerritory(string targetZoneName, List<GangTerritory> attackerZones, float threshold)
    {
        Zone targetZone = Zones.GetZone(targetZoneName);
        if (targetZone == null || targetZone.Boundaries == null || targetZone.Boundaries.Length < 3)
        {
            return false;
        }
        Vector2 targetCenter = ComputeCentroid(targetZone.Boundaries);
        foreach (GangTerritory attackerTerritory in attackerZones)
        {
            Zone attackerZone = Zones.GetZone(attackerTerritory.ZoneInternalGameName);
            if (attackerZone == null || attackerZone.Boundaries == null || attackerZone.Boundaries.Length < 3)
            {
                continue;
            }
            Vector2 attackerCenter = ComputeCentroid(attackerZone.Boundaries);
            float dx = targetCenter.X - attackerCenter.X;
            float dy = targetCenter.Y - attackerCenter.Y;
            float distance = (float)Math.Sqrt(dx * dx + dy * dy);
            if (distance <= threshold)
            {
                return true;
            }
        }
        return false;
    }

    private Vector2 ComputeCentroid(Vector2[] boundaries)
    {
        float sumX = 0, sumY = 0;
        for (int i = 0; i < boundaries.Length; i++)
        {
            sumX += boundaries[i].X;
            sumY += boundaries[i].Y;
        }
        return new Vector2(sumX / boundaries.Length, sumY / boundaries.Length);
    }

    private void RunWar(string attackerGangID, string defenderGangID, string zoneInternalGameName)
    {
        Gang attackerGang = Gangs.GetGang(attackerGangID);
        Gang defenderGang = Gangs.GetGang(defenderGangID);
        Zone zone = Zones.GetZone(zoneInternalGameName);
        if (attackerGang == null || defenderGang == null || zone == null)
        {
            return;
        }
        float activeDistance = Settings.SettingsManager.GangSettings.AITurfWarPlayerActiveDistance;
        Vector2 zoneCentroid = ComputeCentroid(zone.Boundaries);
        float z = 0f;
        NativeFunction.Natives.GET_GROUND_Z_FOR_3D_COORD<bool>(zoneCentroid.X, zoneCentroid.Y, 1000f, out z, false);
        if (z <= 0f) z = 30f;
        Vector3 zoneCenter3D = new Vector3(zoneCentroid.X, zoneCentroid.Y, z);
        float distanceToPlayer = Player.Character.Exists() ? Player.Character.DistanceTo(zoneCenter3D) : float.MaxValue;
        bool isPlayerNearby = distanceToPlayer <= activeDistance;

        // Notify player about the war
        string attackerColor = attackerGang.ColorPrefix ?? "~r~";
        string defenderColor = defenderGang.ColorPrefix ?? "~b~";
        string notifText = $"{attackerColor}{attackerGang.ShortName}~s~ is attacking {defenderColor}{defenderGang.ShortName}~s~ territory in ~y~{zone.DisplayName}~s~!";
        Game.DisplayNotification("CHAR_BLANK_ENTRY", "CHAR_BLANK_ENTRY", "~o~Gang War", "~w~Territory Conflict", notifText);

        if (isPlayerNearby)
        {
            RunActiveWar(attackerGang, defenderGang, zone, zoneCenter3D);
        }
        else
        {
            RunPassiveWar(attackerGang, defenderGang, zone);
        }
    }

    private void RunActiveWar(Gang attackerGang, Gang defenderGang, Zone zone, Vector3 zoneCenter)
    {
        int durationSeconds = Settings.SettingsManager.GangSettings.AITurfWarActiveDurationSeconds;
        int spawnCount = Settings.SettingsManager.GangSettings.AITurfWarActiveSpawnCount;

        // Spawn attackers via hit squad
        for (int i = 0; i < spawnCount; i++)
        {
            GangDispatcher?.DispatchHitSquad(attackerGang, true);
            GameFiber.Sleep(500);
        }

        // Spawn defenders via hit squad
        for (int i = 0; i < spawnCount; i++)
        {
            GangDispatcher?.DispatchHitSquad(defenderGang, true);
            GameFiber.Sleep(500);
        }

        // Wait for the battle duration
        uint startTime = Game.GameTime;
        uint durationMs = (uint)(durationSeconds * 1000);
        while (Game.GameTime - startTime < durationMs)
        {
            GameFiber.Sleep(2000);
        }

        // Count surviving members near the zone
        int attackerSurvivors = CountNearbyGangMembers(attackerGang, zoneCenter, 200f);
        int defenderSurvivors = CountNearbyGangMembers(defenderGang, zoneCenter, 200f);

        bool attackerWins;
        if (attackerSurvivors > defenderSurvivors)
        {
            attackerWins = true;
        }
        else if (defenderSurvivors > attackerSurvivors)
        {
            attackerWins = false;
        }
        else
        {
            // Tie — defender advantage
            attackerWins = false;
        }

        if (attackerWins)
        {
            OnAttackerWins(attackerGang, defenderGang, zone);
        }
        else
        {
            OnDefenderWins(attackerGang, defenderGang, zone);
        }
    }

    private void RunPassiveWar(Gang attackerGang, Gang defenderGang, Zone zone)
    {
        GameFiber.Sleep(5000);
        float attackerWinChance = Settings.SettingsManager.GangSettings.AITurfWarAttackerWinChance;
        float roll = (float)RandomItems.MyRand.NextDouble();
        if (roll < attackerWinChance)
        {
            OnAttackerWins(attackerGang, defenderGang, zone);
        }
        else
        {
            OnDefenderWins(attackerGang, defenderGang, zone);
        }
    }

    private void OnAttackerWins(Gang attackerGang, Gang defenderGang, Zone zone)
    {
        GangTerritories.TransferZoneOwnership(zone.InternalGameName, attackerGang.ID, defenderGang.ID);
        string attackerColor = attackerGang.ColorPrefix ?? "~r~";
        string defenderColor = defenderGang.ColorPrefix ?? "~b~";
        Game.DisplayNotification("CHAR_BLANK_ENTRY", "CHAR_BLANK_ENTRY", "~o~Territory Captured", "", $"{attackerColor}{attackerGang.ShortName}~s~ captured ~y~{zone.DisplayName}~s~ from {defenderColor}{defenderGang.ShortName}~s~!");

        // Player reputation effects
        UpdatePlayerReputation(attackerGang, defenderGang, true);

        EntryPoint.WriteToConsole($"AI TURF WAR: {attackerGang.ShortName} captured {zone.InternalGameName} from {defenderGang.ShortName}", 0);
    }

    private void OnDefenderWins(Gang attackerGang, Gang defenderGang, Zone zone)
    {
        string attackerColor = attackerGang.ColorPrefix ?? "~r~";
        string defenderColor = defenderGang.ColorPrefix ?? "~b~";
        Game.DisplayNotification("CHAR_BLANK_ENTRY", "CHAR_BLANK_ENTRY", "~o~Territory Defended", "", $"{defenderColor}{defenderGang.ShortName}~s~ defended ~y~{zone.DisplayName}~s~ from {attackerColor}{attackerGang.ShortName}~s~!");

        EntryPoint.WriteToConsole($"AI TURF WAR: {defenderGang.ShortName} defended {zone.InternalGameName} against {attackerGang.ShortName}", 0);
    }

    private void UpdatePlayerReputation(Gang attackerGang, Gang defenderGang, bool attackerWon)
    {
        if (Player.RelationshipManager?.GangRelationships == null)
        {
            return;
        }
        GangRelationships gr = Player.RelationshipManager.GangRelationships;
        Gang playerGang = gr.CurrentGang;
        if (playerGang == null)
        {
            return;
        }
        if (attackerWon)
        {
            if (playerGang.ID == attackerGang.ID)
            {
                gr.ChangeReputation(attackerGang, 100, true);
            }
            else if (playerGang.ID == defenderGang.ID)
            {
                gr.ChangeReputation(defenderGang, -100, true);
            }
        }
    }

    private int CountNearbyGangMembers(Gang gang, Vector3 position, float radius)
    {
        int count = 0;
        if (World?.Pedestrians?.GangMembers == null)
        {
            return 0;
        }
        foreach (GangMember member in World.Pedestrians.GangMembers)
        {
            if (member == null || !member.Pedestrian.Exists() || member.Pedestrian.IsDead)
            {
                continue;
            }
            if (member.Gang?.ID != gang.ID)
            {
                continue;
            }
            if (member.Pedestrian.DistanceTo(position) <= radius)
            {
                count++;
            }
        }
        return count;
    }

    public void Dispose()
    {
        IsWarInProgress = false;
    }
}
