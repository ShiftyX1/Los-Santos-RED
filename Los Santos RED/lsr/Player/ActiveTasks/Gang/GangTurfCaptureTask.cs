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
        private int WaveKillTarget;
        private bool IsHoldPhase = false;
        private int HoldTimeSeconds;
        private uint HoldStartGameTime;
        private int MoneyToRecieve;

        // Active tracking
        private int WaveKillsCurrent;
        private bool IsActive;
        private float ZoneCenterX;
        private float ZoneCenterY;
        private float ZoneRadius;

        // HUD state
        private string HudStatusText = "";
        private float HudProgress;
        private Color HudProgressColor = Color.Green;

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
            IsActive = false;
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

                ComputeZoneCenter();
                GetPayment();
                DeductCaptureCost();
                CaptureManager.SetInProgress(TargetZone.InternalGameName, DefendingGang.ID, HiringGang.ID);
                SendInitialInstructionsMessage();
                AddTask();
                CreateCaptureBlip();
                IsActive = true;

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

                GameFiber.StartNew(delegate
                {
                    try
                    {
                        DrawLoopFiber();
                    }
                    catch (Exception ex)
                    {
                        EntryPoint.WriteToConsole("TurfCapture DrawLoop error: " + ex.Message, 0);
                    }
                }, "TurfCaptureDrawFiber");
            }
        }

        private void Loop()
        {
            GameFiber.Sleep(2000);

            // Phase A: Wave-based combat
            CurrentWave = 0;
            while (CurrentWave < TotalWaves)
            {
                if (!CheckTaskAlive()) { OnFailed(); return; }

                CurrentWave++;
                int enemyCount = WaveBaseEnemyCount + (CurrentWave - 1) * WaveEnemyIncrement;
                WaveKillTarget = enemyCount;
                WaveKillsCurrent = 0;
                IsHoldPhase = false;

                HudStatusText = "WAVE " + CurrentWave + "/" + TotalWaves;
                HudProgress = 0f;
                HudProgressColor = Color.OrangeRed;

                Game.DisplayHelp("~r~Wave " + CurrentWave + "/" + TotalWaves + "~s~ - Kill ~r~" + enemyCount + "~s~ " + DefendingGang.ShortName + " members");

                // Spawn enemies for this wave via hit squads
                SpawnWaveEnemies(enemyCount);
                GameFiber.Sleep(1000);

                // Track kills via MembersKilled delta
                GangReputation defenderRep = Player.RelationshipManager.GangRelationships.GetReputation(DefendingGang);
                int killsAtWaveStart = defenderRep != null ? defenderRep.MembersKilled : 0;

                uint waveTimeout = (uint)(90000 + enemyCount * 15000);
                uint waveStartTime = Game.GameTime;

                while (true)
                {
                    if (!CheckTaskAlive()) { OnFailed(); return; }

                    // Zone boundary check with grace period
                    if (!IsPlayerInTargetZone())
                    {
                        Game.DisplayHelp("~r~You are leaving the capture zone!~s~ Return to ~y~" + TargetZone.DisplayName + "~s~ immediately!");
                        uint graceStart = Game.GameTime;
                        bool returned = false;
                        while (Game.GameTime - graceStart < 10000)
                        {
                            if (IsPlayerInTargetZone()) { returned = true; break; }
                            HudStatusText = "RETURN TO ZONE!";
                            HudProgressColor = Color.Red;
                            GameFiber.Sleep(500);
                        }
                        if (!returned)
                        {
                            SendLeftZoneMessage();
                            OnFailed();
                            return;
                        }
                        HudStatusText = "WAVE " + CurrentWave + "/" + TotalWaves;
                        HudProgressColor = Color.OrangeRed;
                    }

                    int currentKills = (defenderRep != null ? defenderRep.MembersKilled : 0) - killsAtWaveStart;
                    WaveKillsCurrent = Math.Min(currentKills, WaveKillTarget);
                    HudProgress = (float)WaveKillsCurrent / WaveKillTarget;

                    if (currentKills >= WaveKillTarget)
                    {
                        break;
                    }

                    // Spawn reinforcements if wave is taking too long
                    if (Game.GameTime - waveStartTime > waveTimeout)
                    {
                        SpawnWaveEnemies(Math.Max(2, enemyCount / 2));
                        waveStartTime = Game.GameTime;
                    }

                    GameFiber.Sleep(500);
                }

                // Wave cleared
                if (CurrentWave < TotalWaves)
                {
                    HudStatusText = "WAVE " + CurrentWave + " CLEARED!";
                    HudProgress = 1f;
                    HudProgressColor = Color.LimeGreen;
                    NativeHelper.PlaySuccessSound();
                    Game.DisplayNotification("~g~Wave " + CurrentWave + "/" + TotalWaves + " cleared!~s~ Get ready for the next wave...");
                    GameFiber.Sleep(5000);
                }
            }

            // All waves cleared
            HudStatusText = "ALL WAVES CLEARED!";
            HudProgress = 1f;
            HudProgressColor = Color.LimeGreen;
            NativeHelper.PlaySuccessSound();
            Game.DisplayNotification("~g~All " + TotalWaves + " waves cleared!~s~ Now ~y~hold the territory~s~ for " + HoldTimeSeconds + " seconds!");
            GameFiber.Sleep(3000);

            // Phase B: Hold the point
            IsHoldPhase = true;
            HoldStartGameTime = Game.GameTime;
            HudProgressColor = Color.Gold;

            while (true)
            {
                if (!CheckTaskAlive()) { OnFailed(); return; }

                if (!IsPlayerInTargetZone())
                {
                    Game.DisplayHelp("~r~You are leaving the capture zone!~s~ Return to ~y~" + TargetZone.DisplayName + "~s~ immediately!");
                    uint graceStart = Game.GameTime;
                    bool returned = false;
                    while (Game.GameTime - graceStart < 10000)
                    {
                        if (IsPlayerInTargetZone()) { returned = true; break; }
                        HudStatusText = "RETURN TO ZONE!";
                        HudProgressColor = Color.Red;
                        GameFiber.Sleep(500);
                    }
                    if (!returned)
                    {
                        SendLeftZoneMessage();
                        OnFailed();
                        return;
                    }
                    HudProgressColor = Color.Gold;
                }

                uint elapsed = Game.GameTime - HoldStartGameTime;
                uint totalMs = (uint)(HoldTimeSeconds * 1000);
                float holdProgress = Math.Min((float)elapsed / totalMs, 1f);
                int remainingSeconds = Math.Max(0, (int)((totalMs - elapsed) / 1000));
                HudStatusText = "HOLD  " + remainingSeconds + "s";
                HudProgress = holdProgress;

                if (elapsed >= totalMs)
                {
                    break;
                }
                GameFiber.Sleep(500);
            }

            // Success
            IsHoldPhase = false;
            HudStatusText = "TERRITORY CAPTURED!";
            HudProgress = 1f;
            HudProgressColor = Color.LimeGreen;
            NativeHelper.PlaySuccessSound();

            if (CheckTaskAlive())
            {
                int incomePerTick = TerritoryCaptureManager.GetIncomeForEconomy(TargetZone.Economy);
                CaptureManager.CaptureZone(TargetZone.InternalGameName, DefendingGang.ID, HiringGang.ID, incomePerTick);
                CurrentTask.OnReadyForPayment(true, "Territory ~g~" + TargetZone.DisplayName + "~s~ captured! Return to the " + HiringGang.DenName + " for payment.");
            }

            GameFiber.Sleep(5000);
            IsActive = false;
        }

        private void SpawnWaveEnemies(int enemyCount)
        {
            try
            {
                if (Player.Dispatcher == null || Player.Dispatcher.GangDispatcher == null) return;

                int spawned = 0;
                int attempts = 0;
                while (spawned < enemyCount && attempts < 6)
                {
                    Player.Dispatcher.GangDispatcher.DispatchHitSquad(DefendingGang, true);
                    spawned += 3;
                    attempts++;
                    GameFiber.Sleep(1500);
                }

                EntryPoint.WriteToConsole("TURF CAPTURE: Spawned wave enemies for " + DefendingGang.ShortName + ", attempts=" + attempts, 0);
            }
            catch (Exception ex)
            {
                EntryPoint.WriteToConsole("TURF CAPTURE: SpawnWaveEnemies error: " + ex.Message, 0);
            }
        }

        private void DrawLoopFiber()
        {
            while (IsActive)
            {
                DrawZoneBoundary();
                DrawCaptureHUD();
                GameFiber.Yield();
            }
        }

        private void DrawZoneBoundary()
        {
            if (TargetZone?.Boundaries == null || TargetZone.Boundaries.Length < 3) return;

            Vector2[] bounds = TargetZone.Boundaries;
            bool inZone = IsPlayerInTargetZone();
            int r = inZone ? 0 : 255;
            int g = inZone ? 200 : 0;
            int b = 0;
            int a = 200;

            for (int i = 0; i < bounds.Length; i++)
            {
                int next = (i + 1) % bounds.Length;
                Vector3 start = new Vector3(bounds[i].X, bounds[i].Y, Player.Character.Position.Z);
                Vector3 end = new Vector3(bounds[next].X, bounds[next].Y, Player.Character.Position.Z);

                float distStart = Vector3.Distance2D(Player.Character.Position, start);
                float distEnd = Vector3.Distance2D(Player.Character.Position, end);
                if (distStart < 300f || distEnd < 300f)
                {
                    float z1 = GetGroundZ(start);
                    float z2 = GetGroundZ(end);
                    if (z1 > 0f) start = new Vector3(start.X, start.Y, z1 + 0.3f);
                    if (z2 > 0f) end = new Vector3(end.X, end.Y, z2 + 0.3f);

                    NativeFunction.Natives.DRAW_LINE(start.X, start.Y, start.Z, end.X, end.Y, end.Z, r, g, b, a);

                    // Draw vertical markers at each vertex for better visibility
                    if (distStart < 200f)
                    {
                        NativeFunction.Natives.DRAW_MARKER(1, start.X, start.Y, start.Z - 0.3f,
                            0f, 0f, 0f, 0f, 0f, 0f, 1.0f, 1.0f, 15.0f,
                            r, g, b, 80, false, false, 2, false, 0, 0, false);
                    }
                }
            }
        }

        private float GetGroundZ(Vector3 pos)
        {
            float z = 0f;
            NativeFunction.Natives.GET_GROUND_Z_FOR_3D_COORD<bool>(pos.X, pos.Y, pos.Z + 100f, out z, false);
            return z;
        }

        private void DrawCaptureHUD()
        {
            float bgX = 0.5f;
            float bgY = 0.88f;
            float bgW = 0.22f;
            float bgH = 0.08f;
            NativeFunction.Natives.DRAW_RECT(bgX, bgY, bgW, bgH, 0, 0, 0, 160, false);

            // Title
            NativeHelper.DisplayTextOnScreen("TURF WAR: " + TargetZone.DisplayName, bgX, bgY - 0.032f, 0.35f, Color.White, GTAFont.FontChaletLondon, GTATextJustification.Center, true);

            // Status
            NativeHelper.DisplayTextOnScreen(HudStatusText, bgX, bgY - 0.012f, 0.30f, HudProgressColor, GTAFont.FontChaletLondon, GTATextJustification.Center, false);

            // Progress bar background
            float barX = bgX - 0.09f;
            float barY = bgY + 0.02f;
            float barW = 0.18f;
            float barH = 0.012f;
            NativeFunction.Natives.DRAW_RECT(barX + barW / 2f, barY, barW, barH, 40, 40, 40, 200, false);

            // Progress bar fill
            float clampedProgress = Math.Max(0f, Math.Min(1f, HudProgress));
            float fillW = barW * clampedProgress;
            if (fillW > 0.001f)
            {
                NativeFunction.Natives.DRAW_RECT(barX + fillW / 2f, barY, fillW, barH,
                    HudProgressColor.R, HudProgressColor.G, HudProgressColor.B, 220, false);
            }

            // Kill counter (only during wave phase)
            if (!IsHoldPhase)
            {
                NativeHelper.DisplayTextOnScreen("Kills: " + WaveKillsCurrent + "/" + WaveKillTarget, bgX, bgY + 0.028f, 0.25f, Color.LightGray, GTAFont.FontChaletLondon, GTATextJustification.Center, false);
            }
        }

        private void FinishTask()
        {
            IsActive = false;
            RemoveBlip();
            if (CurrentTask != null && CurrentTask.IsActive && CurrentTask.IsReadyForPayment)
            {
                SendSuccessMessage();
            }
        }

        private void OnFailed()
        {
            IsActive = false;
            CaptureManager.ClearInProgress(TargetZone.InternalGameName);
            RemoveBlip();
            GangTasks.SendGenericFailMessage(PhoneContact);
        }

        private bool CheckTaskAlive()
        {
            CurrentTask = PlayerTasks.GetTask(HiringGang.ContactName);
            return CurrentTask != null && CurrentTask.IsActive;
        }

        private bool IsPlayerInTargetZone()
        {
            if (TargetZone?.Boundaries != null && TargetZone.Boundaries.Length >= 3)
            {
                Vector2 playerPos2D = new Vector2(Player.Character.Position.X, Player.Character.Position.Y);
                return NativeHelper.IsPointInPolygon(playerPos2D, TargetZone.Boundaries);
            }
            Zone currentZone = Zones.GetZone(Player.Character.Position);
            return currentZone != null && currentZone.InternalGameName.Equals(TargetZone.InternalGameName, StringComparison.OrdinalIgnoreCase);
        }

        private void ComputeZoneCenter()
        {
            if (TargetZone?.Boundaries != null && TargetZone.Boundaries.Length >= 3)
            {
                float sumX = 0, sumY = 0;
                for (int i = 0; i < TargetZone.Boundaries.Length; i++)
                {
                    sumX += TargetZone.Boundaries[i].X;
                    sumY += TargetZone.Boundaries[i].Y;
                }
                ZoneCenterX = sumX / TargetZone.Boundaries.Length;
                ZoneCenterY = sumY / TargetZone.Boundaries.Length;

                float maxDist = 0;
                for (int i = 0; i < TargetZone.Boundaries.Length; i++)
                {
                    float dx = TargetZone.Boundaries[i].X - ZoneCenterX;
                    float dy = TargetZone.Boundaries[i].Y - ZoneCenterY;
                    float dist = (float)Math.Sqrt(dx * dx + dy * dy);
                    if (dist > maxDist) maxDist = dist;
                }
                ZoneRadius = maxDist;
            }
            else
            {
                ZoneCenterX = Player.Character.Position.X;
                ZoneCenterY = Player.Character.Position.Y;
                ZoneRadius = 200f;
            }
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
                float z = GetGroundZ(new Vector3(ZoneCenterX, ZoneCenterY, 50f));
                if (z <= 0f) z = 30f;
                CaptureBlip = new Blip(new Vector3(ZoneCenterX, ZoneCenterY, z), ZoneRadius)
                {
                    Color = Color.OrangeRed,
                    Alpha = 0.3f
                };
                NativeFunction.CallByName<bool>("SET_BLIP_AS_SHORT_RANGE", (uint)CaptureBlip.Handle, false);
                NativeFunction.CallByName<bool>("BEGIN_TEXT_COMMAND_SET_BLIP_NAME", "STRING");
                NativeFunction.CallByName<bool>("ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME", "Turf War: " + TargetZone.DisplayName);
                NativeFunction.CallByName<bool>("END_TEXT_COMMAND_SET_BLIP_NAME", (uint)CaptureBlip.Handle);
            }
            catch (Exception ex)
            {
                EntryPoint.WriteToConsole("TurfCapture CreateBlip error: " + ex.Message);
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
                "Time to claim " + DefendingGang.ColorPrefix + TargetZone.DisplayName + "~s~ from those " + DefendingGang.ColorPrefix + DefendingGang.ShortName + "~s~ bitches. Clear out " + TotalWaves + " waves of their guys and hold the area. Cost you ~r~$" + cost + "~s~ up front, but you'll get ~g~$" + MoneyToRecieve + "~s~ when it's done. Head to " + HiringGangDen.FullStreetAddress + " after.",
                "We're taking " + DefendingGang.ColorPrefix + TargetZone.DisplayName + "~s~ today. This is costing you ~r~$" + cost + "~s~. Kill " + TotalWaves + " waves of " + DefendingGang.ColorPrefix + DefendingGang.ShortName + "~s~, then hold the block. ~g~$" + MoneyToRecieve + "~s~ waiting for you at the " + HiringGang.DenName + ".",
                "Listen up. " + DefendingGang.ColorPrefix + TargetZone.DisplayName + "~s~ is ours now. Fight through " + TotalWaves + " waves and hold it down. ~r~$" + cost + "~s~ to start this operation. ~g~$" + MoneyToRecieve + "~s~ at " + HiringGangDen.FullStreetAddress + " when you're done.",
            };
            Player.CellPhone.AddPhoneResponse(HiringGang.Contact.Name, HiringGang.Contact.IconName, Replies.PickRandom());
        }

        private void SendLeftZoneMessage()
        {
            List<string> Replies = new List<string>() {
                "You left the zone! The capture is fucked. What the hell?",
                "We lost the territory because you ran away. Unbelievable.",
                "You bailed on us mid-fight? That's it, the capture is off.",
            };
            Player.CellPhone.AddScheduledText(PhoneContact, Replies.PickRandom(), 0, false);
        }

        private void SendSuccessMessage()
        {
            List<string> Replies = new List<string>() {
                DefendingGang.ColorPrefix + TargetZone.DisplayName + "~s~ is ours now! Come by the " + HiringGang.DenName + " on " + HiringGangDen.FullStreetAddress + " to collect your ~g~$" + MoneyToRecieve + "~s~.",
                "We did it. " + DefendingGang.ColorPrefix + TargetZone.DisplayName + "~s~ belongs to us. ~g~$" + MoneyToRecieve + "~s~ waiting for you at " + HiringGangDen.FullStreetAddress + ".",
                "The hood is ours. Get to " + HiringGangDen.FullStreetAddress + " for your ~g~$" + MoneyToRecieve + "~s~.",
            };
            Player.CellPhone.AddScheduledText(PhoneContact, Replies.PickRandom(), 1, false);
        }
    }
}
