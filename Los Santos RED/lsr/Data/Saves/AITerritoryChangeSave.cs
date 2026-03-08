using System;

public class AITerritoryChangeSave
{
    public string ZoneInternalGameName { get; set; } = "";
    public string NewGangID { get; set; } = "";
    public string OriginalGangID { get; set; } = "";

    public AITerritoryChangeSave()
    {
    }

    public AITerritoryChangeSave(string zoneInternalGameName, string newGangID, string originalGangID)
    {
        ZoneInternalGameName = zoneInternalGameName;
        NewGangID = newGangID;
        OriginalGangID = originalGangID;
    }
}
