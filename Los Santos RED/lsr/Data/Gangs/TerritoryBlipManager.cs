using LosSantosRED.lsr.Interface;
using Rage;
using Rage.Native;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

public class TerritoryBlipManager
{
    private IGangTerritories GangTerritories;
    private IGangs Gangs;
    private IZones Zones;
    private Dictionary<string, Blip> TerritoryBlips = new Dictionary<string, Blip>();
    private Dictionary<string, string> LastKnownOwner = new Dictionary<string, string>();

    public TerritoryBlipManager(IGangTerritories gangTerritories, IGangs gangs, IZones zones)
    {
        GangTerritories = gangTerritories;
        Gangs = gangs;
        Zones = zones;
    }

    public void Setup()
    {
        CreateAllBlips();
    }

    public void Dispose()
    {
        RemoveAllBlips();
    }

    public void Update()
    {
        if (GangTerritories?.CaptureManager == null) return;

        foreach (var kvp in new Dictionary<string, Blip>(TerritoryBlips))
        {
            string zoneName = kvp.Key;
            Gang dynamicOwner = GangTerritories.CaptureManager.GetDynamicOwner(zoneName);
            Gang currentGang = dynamicOwner ?? GangTerritories.GetMainGang(zoneName);
            string currentOwnerID = currentGang?.ID ?? "";

            string lastOwner;
            LastKnownOwner.TryGetValue(zoneName, out lastOwner);
            if (lastOwner == null) lastOwner = "";

            if (currentOwnerID != lastOwner)
            {
                LastKnownOwner[zoneName] = currentOwnerID;
                UpdateBlipColor(kvp.Value, currentGang);
            }
        }
    }

    private void CreateAllBlips()
    {
        if (GangTerritories?.AllTerritories == null) return;

        // Group territories by zone (one blip per zone, use main gang color)
        HashSet<string> processedZones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (GangTerritory territory in GangTerritories.AllTerritories)
        {
            if (string.IsNullOrEmpty(territory.ZoneInternalGameName)) continue;
            if (processedZones.Contains(territory.ZoneInternalGameName)) continue;
            processedZones.Add(territory.ZoneInternalGameName);

            Zone zone = Zones.GetZone(territory.ZoneInternalGameName);
            if (zone == null) continue;

            // Determine current owner (dynamic capture overrides static)
            Gang dynamicOwner = GangTerritories.CaptureManager?.GetDynamicOwner(territory.ZoneInternalGameName);
            Gang ownerGang = dynamicOwner ?? GangTerritories.GetMainGang(territory.ZoneInternalGameName);
            if (ownerGang == null) continue;

            Vector3 center;
            float radius;
            ComputeZoneCenterAndRadius(zone, out center, out radius);
            if (radius <= 0f) continue;

            try
            {
                Blip blip = new Blip(center, radius)
                {
                    Color = ownerGang.Color,
                    Alpha = 0.25f
                };
                NativeFunction.CallByName<bool>("SET_BLIP_AS_SHORT_RANGE", (uint)blip.Handle, true);
                NativeFunction.CallByName<bool>("BEGIN_TEXT_COMMAND_SET_BLIP_NAME", "STRING");
                NativeFunction.CallByName<bool>("ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME", ownerGang.ShortName + " - " + zone.DisplayName);
                NativeFunction.CallByName<bool>("END_TEXT_COMMAND_SET_BLIP_NAME", (uint)blip.Handle);

                TerritoryBlips[territory.ZoneInternalGameName] = blip;
                LastKnownOwner[territory.ZoneInternalGameName] = ownerGang.ID;
            }
            catch (Exception ex)
            {
                EntryPoint.WriteToConsole("TerritoryBlipManager CreateBlip error: " + ex.Message, 0);
            }
        }

        EntryPoint.WriteToConsole("TerritoryBlipManager: Created " + TerritoryBlips.Count + " territory blips", 0);
    }

    private void UpdateBlipColor(Blip blip, Gang gang)
    {
        try
        {
            if (blip == null || !blip.IsValid()) return;
            if (gang != null)
            {
                blip.Color = gang.Color;
            }
        }
        catch { }
    }

    private void RemoveAllBlips()
    {
        foreach (var kvp in TerritoryBlips)
        {
            try
            {
                if (kvp.Value != null && kvp.Value.IsValid())
                {
                    kvp.Value.Delete();
                }
            }
            catch { }
        }
        TerritoryBlips.Clear();
        LastKnownOwner.Clear();
    }

    private void ComputeZoneCenterAndRadius(Zone zone, out Vector3 center, out float radius)
    {
        center = Vector3.Zero;
        radius = 0f;

        if (zone.Boundaries == null || zone.Boundaries.Length < 3) return;

        float sumX = 0, sumY = 0;
        for (int i = 0; i < zone.Boundaries.Length; i++)
        {
            sumX += zone.Boundaries[i].X;
            sumY += zone.Boundaries[i].Y;
        }
        float cx = sumX / zone.Boundaries.Length;
        float cy = sumY / zone.Boundaries.Length;

        float maxDist = 0;
        for (int i = 0; i < zone.Boundaries.Length; i++)
        {
            float dx = zone.Boundaries[i].X - cx;
            float dy = zone.Boundaries[i].Y - cy;
            float dist = (float)Math.Sqrt(dx * dx + dy * dy);
            if (dist > maxDist) maxDist = dist;
        }

        float z = 0f;
        NativeFunction.Natives.GET_GROUND_Z_FOR_3D_COORD<bool>(cx, cy, 1000f, out z, false);
        if (z <= 0f) z = 30f;

        center = new Vector3(cx, cy, z);
        radius = maxDist;
    }
}
