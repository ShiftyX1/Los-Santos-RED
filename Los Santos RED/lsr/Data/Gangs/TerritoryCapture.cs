using System;
using System.Xml.Serialization;

[Serializable()]
public class TerritoryCapture
{
    public string ZoneInternalGameName { get; set; } = "";
    public string OriginalGangID { get; set; } = "";
    public string CapturingGangID { get; set; } = "";
    public CaptureState CaptureState { get; set; } = CaptureState.None;
    public DateTime CaptureDateTime { get; set; }
    public DateTime NextDefenseCheckTime { get; set; }
    public int IncomePerTick { get; set; } = 100;

    [XmlIgnore]
    public Gang CapturingGang { get; set; }
    [XmlIgnore]
    public Gang OriginalGang { get; set; }

    public TerritoryCapture()
    {
    }

    public TerritoryCapture(string zoneInternalGameName, string originalGangID, string capturingGangID, int incomePerTick)
    {
        ZoneInternalGameName = zoneInternalGameName;
        OriginalGangID = originalGangID;
        CapturingGangID = capturingGangID;
        IncomePerTick = incomePerTick;
        CaptureState = CaptureState.Captured;
        CaptureDateTime = DateTime.Now;
        NextDefenseCheckTime = DateTime.Now.AddMinutes(60);
    }

    public void Setup(Gang capturingGang, Gang originalGang)
    {
        CapturingGang = capturingGang;
        OriginalGang = originalGang;
    }
}

public enum CaptureState
{
    None,
    InProgress,
    Captured,
    UnderAttack
}
