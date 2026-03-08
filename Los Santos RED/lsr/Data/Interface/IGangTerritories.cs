using LosSantosRED.lsr.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LosSantosRED.lsr.Interface
{
    public interface IGangTerritories
    {
        TerritoryCaptureManager CaptureManager { get; set; }
        List<GangTerritory> AllTerritories { get; }
        Gang GetRandomGang(string internalGameName, int wantedLevel);
        List<Gang> GetGangs(string internalGameName, int wantedLevel);
        Gang GetMainGang(string internalGameName);
        Gang GetNthGang(string internalGameName, int v);
        List<GangTerritory> GetGangTerritory(string iD);
        bool TransferZoneOwnership(string zone, string newGangID, string originalGangID);
        List<AITerritoryChangeSave> GetAITerritoryChangeSaveData();
        void LoadAITerritoryChanges(List<AITerritoryChangeSave> saves);
    }
}
