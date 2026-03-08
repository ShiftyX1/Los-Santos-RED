using LosSantosRED.lsr.Interface;
using Rage;
using System;
using System.Collections.Generic;
using System.Linq;

public class TerritoryCaptureManager
{
    private IGangs GangProvider;
    private ISettingsProvideable Settings;
    private List<TerritoryCapture> CapturedTerritories = new List<TerritoryCapture>();
    private DateTime LastIncomeCollectionTime;

    public TerritoryCaptureManager(IGangs gangProvider, ISettingsProvideable settings)
    {
        GangProvider = gangProvider;
        Settings = settings;
        LastIncomeCollectionTime = DateTime.Now;
    }

    public List<TerritoryCapture> AllCapturedTerritories => CapturedTerritories.Where(x => x.CaptureState == CaptureState.Captured || x.CaptureState == CaptureState.UnderAttack).ToList();
    public int CapturedCount => AllCapturedTerritories.Count;
    public bool HasCapturedTerritories => CapturedTerritories.Any(x => x.CaptureState == CaptureState.Captured || x.CaptureState == CaptureState.UnderAttack);
    public bool HasActiveCaptureInProgress => CapturedTerritories.Any(x => x.CaptureState == CaptureState.InProgress);

    public void Setup()
    {
        foreach (TerritoryCapture tc in CapturedTerritories)
        {
            Gang capturingGang = GangProvider.GetGang(tc.CapturingGangID);
            Gang originalGang = GangProvider.GetGang(tc.OriginalGangID);
            tc.Setup(capturingGang, originalGang);
        }
    }

    public Gang GetDynamicOwner(string zoneInternalGameName)
    {
        TerritoryCapture capture = CapturedTerritories.FirstOrDefault(x =>
            x.ZoneInternalGameName.Equals(zoneInternalGameName, StringComparison.OrdinalIgnoreCase) &&
            (x.CaptureState == CaptureState.Captured || x.CaptureState == CaptureState.UnderAttack));
        return capture?.CapturingGang;
    }

    public bool IsCapturedBy(string zoneInternalGameName, string gangID)
    {
        return CapturedTerritories.Any(x =>
            x.ZoneInternalGameName.Equals(zoneInternalGameName, StringComparison.OrdinalIgnoreCase) &&
            x.CapturingGangID == gangID &&
            (x.CaptureState == CaptureState.Captured || x.CaptureState == CaptureState.UnderAttack));
    }

    public bool IsCapturableBy(string zoneInternalGameName, string attackingGangID)
    {
        if (!Settings.SettingsManager.GangSettings.AllowTurfCapture) return false;
        if (CapturedTerritories.Count(x => x.CapturingGangID == attackingGangID && x.CaptureState == CaptureState.Captured) >= Settings.SettingsManager.GangSettings.MaxCapturedTerritories) return false;
        if (HasActiveCaptureInProgress) return false;
        if (IsCapturedBy(zoneInternalGameName, attackingGangID)) return false;
        return true;
    }

    public void CaptureZone(string zoneInternalGameName, string originalGangID, string capturingGangID, int incomePerTick)
    {
        CapturedTerritories.RemoveAll(x => x.ZoneInternalGameName.Equals(zoneInternalGameName, StringComparison.OrdinalIgnoreCase));
        TerritoryCapture newCapture = new TerritoryCapture(zoneInternalGameName, originalGangID, capturingGangID, incomePerTick);
        Gang capturingGang = GangProvider.GetGang(capturingGangID);
        Gang originalGang = GangProvider.GetGang(originalGangID);
        newCapture.Setup(capturingGang, originalGang);
        CapturedTerritories.Add(newCapture);
        EntryPoint.WriteToConsole($"TERRITORY CAPTURE: {zoneInternalGameName} captured by {capturingGangID} from {originalGangID}", 0);
    }

    public void LoseZone(string zoneInternalGameName)
    {
        CapturedTerritories.RemoveAll(x => x.ZoneInternalGameName.Equals(zoneInternalGameName, StringComparison.OrdinalIgnoreCase));
        EntryPoint.WriteToConsole($"TERRITORY CAPTURE: {zoneInternalGameName} lost", 0);
    }

    public void SetInProgress(string zoneInternalGameName, string originalGangID, string capturingGangID)
    {
        CapturedTerritories.RemoveAll(x => x.ZoneInternalGameName.Equals(zoneInternalGameName, StringComparison.OrdinalIgnoreCase));
        TerritoryCapture inProgress = new TerritoryCapture()
        {
            ZoneInternalGameName = zoneInternalGameName,
            OriginalGangID = originalGangID,
            CapturingGangID = capturingGangID,
            CaptureState = CaptureState.InProgress
        };
        Gang capturingGang = GangProvider.GetGang(capturingGangID);
        Gang originalGang = GangProvider.GetGang(originalGangID);
        inProgress.Setup(capturingGang, originalGang);
        CapturedTerritories.Add(inProgress);
    }

    public void ClearInProgress(string zoneInternalGameName)
    {
        CapturedTerritories.RemoveAll(x =>
            x.ZoneInternalGameName.Equals(zoneInternalGameName, StringComparison.OrdinalIgnoreCase) &&
            x.CaptureState == CaptureState.InProgress);
    }

    public void SetUnderAttack(string zoneInternalGameName)
    {
        TerritoryCapture capture = CapturedTerritories.FirstOrDefault(x =>
            x.ZoneInternalGameName.Equals(zoneInternalGameName, StringComparison.OrdinalIgnoreCase) &&
            x.CaptureState == CaptureState.Captured);
        if (capture != null)
        {
            capture.CaptureState = CaptureState.UnderAttack;
        }
    }

    public void ClearUnderAttack(string zoneInternalGameName)
    {
        TerritoryCapture capture = CapturedTerritories.FirstOrDefault(x =>
            x.ZoneInternalGameName.Equals(zoneInternalGameName, StringComparison.OrdinalIgnoreCase) &&
            x.CaptureState == CaptureState.UnderAttack);
        if (capture != null)
        {
            capture.CaptureState = CaptureState.Captured;
        }
    }

    public List<TerritoryCapture> GetCapturedTerritories(string gangID)
    {
        return CapturedTerritories.Where(x => x.CapturingGangID == gangID &&
            (x.CaptureState == CaptureState.Captured || x.CaptureState == CaptureState.UnderAttack)).ToList();
    }

    public TerritoryCapture GetTerritoryReadyForDefense(DateTime currentTime)
    {
        return CapturedTerritories.FirstOrDefault(x =>
            x.CaptureState == CaptureState.Captured &&
            currentTime >= x.NextDefenseCheckTime);
    }

    public void ScheduleNextDefense(string zoneInternalGameName, int minMinutes, int maxMinutes)
    {
        TerritoryCapture capture = CapturedTerritories.FirstOrDefault(x =>
            x.ZoneInternalGameName.Equals(zoneInternalGameName, StringComparison.OrdinalIgnoreCase));
        if (capture != null)
        {
            int minutes = RandomItems.GetRandomNumberInt(minMinutes, maxMinutes);
            capture.NextDefenseCheckTime = DateTime.Now.AddMinutes(minutes);
        }
    }

    public int CollectIncome()
    {
        int totalIncome = 0;
        foreach (TerritoryCapture tc in AllCapturedTerritories)
        {
            totalIncome += tc.IncomePerTick;
        }
        LastIncomeCollectionTime = DateTime.Now;
        return totalIncome;
    }

    // Save/Load
    public List<TerritoryCaptureSave> GetSaveData()
    {
        List<TerritoryCaptureSave> saves = new List<TerritoryCaptureSave>();
        foreach (TerritoryCapture tc in AllCapturedTerritories)
        {
            saves.Add(new TerritoryCaptureSave(tc));
        }
        return saves;
    }

    public void LoadSaveData(List<TerritoryCaptureSave> saves)
    {
        CapturedTerritories.Clear();
        if (saves == null) return;
        foreach (TerritoryCaptureSave save in saves)
        {
            TerritoryCapture tc = save.ToCapture();
            Gang capturingGang = GangProvider.GetGang(tc.CapturingGangID);
            Gang originalGang = GangProvider.GetGang(tc.OriginalGangID);
            if (capturingGang != null)
            {
                tc.Setup(capturingGang, originalGang);
                CapturedTerritories.Add(tc);
            }
        }
        EntryPoint.WriteToConsole($"TERRITORY CAPTURE: Loaded {CapturedTerritories.Count} captured territories", 0);
    }

    public void Dispose()
    {
        CapturedTerritories.Clear();
    }

    public static int GetIncomeForEconomy(eLocationEconomy economy)
    {
        switch (economy)
        {
            case eLocationEconomy.Poor: return 50;
            case eLocationEconomy.Middle: return 150;
            case eLocationEconomy.Rich: return 300;
            default: return 100;
        }
    }
}
