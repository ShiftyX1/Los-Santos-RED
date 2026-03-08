using System;

public class TerritoryCaptureSave
{
    public string ZoneInternalGameName { get; set; } = "";
    public string OriginalGangID { get; set; } = "";
    public string CapturingGangID { get; set; } = "";
    public int IncomePerTick { get; set; } = 100;
    public DateTime CaptureDateTime { get; set; }
    public DateTime NextDefenseCheckTime { get; set; }

    public TerritoryCaptureSave()
    {
    }

    public TerritoryCaptureSave(TerritoryCapture capture)
    {
        ZoneInternalGameName = capture.ZoneInternalGameName;
        OriginalGangID = capture.OriginalGangID;
        CapturingGangID = capture.CapturingGangID;
        IncomePerTick = capture.IncomePerTick;
        CaptureDateTime = capture.CaptureDateTime;
        NextDefenseCheckTime = capture.NextDefenseCheckTime;
    }

    public TerritoryCapture ToCapture()
    {
        return new TerritoryCapture()
        {
            ZoneInternalGameName = ZoneInternalGameName,
            OriginalGangID = OriginalGangID,
            CapturingGangID = CapturingGangID,
            IncomePerTick = IncomePerTick,
            CaptureState = CaptureState.Captured,
            CaptureDateTime = CaptureDateTime,
            NextDefenseCheckTime = NextDefenseCheckTime
        };
    }
}
