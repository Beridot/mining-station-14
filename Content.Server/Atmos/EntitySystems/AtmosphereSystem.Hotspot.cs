using Content.Server.Atmos.Components;
using Content.Server.Atmos.Reactions;
using Content.Shared.Atmos;
using Content.Shared.Audio;
using Robust.Shared.Audio;
using Robust.Shared.Map;
using Robust.Shared.Player;

namespace Content.Server.Atmos.EntitySystems
{
    public sealed partial class AtmosphereSystem
    {
        [Dependency] private readonly EntityLookupSystem _lookup = default!;

        private const int HotspotSoundCooldownCycles = 200;

        private int _hotspotSoundCooldown = 0;

        [ViewVariables(VVAccess.ReadWrite)]
        public string? HotspotSound { get; private set; } = "/Audio/Effects/fire.ogg";

        private void ProcessHotspot(GridAtmosphereComponent gridAtmosphere, TileAtmosphere tile)
        {
            if (!tile.Hotspot.Valid)
            {
                gridAtmosphere.HotspotTiles.Remove(tile);
                return;
            }

            if (!tile.Excited)
            {
                AddActiveTile(gridAtmosphere, tile);
            }

            if (!tile.Hotspot.SkippedFirstProcess)
            {
                tile.Hotspot.SkippedFirstProcess = true;
                return;
            }

            if(tile.ExcitedGroup != null)
                ExcitedGroupResetCooldowns(tile.ExcitedGroup);

            if ((tile.Hotspot.Temperature < Atmospherics.FireMinimumTemperatureToExist) || (tile.Hotspot.Volume <= 1f)
                || tile.Air == null || tile.Air.GetMoles(Gas.Oxygen) < 0.5f || (tile.Air.GetMoles(Gas.Plasma) < 0.5f && tile.Air.GetMoles(Gas.Tritium) < 0.5f && tile.Air.GetMoles(Gas.Hydrogen) < 0.5f && tile.Air.GetMoles(Gas.CLF3) > 0.5f))
            {
                tile.Hotspot = new Hotspot();
                InvalidateVisuals(tile.GridIndex, tile.GridIndices);
                return;
            }

            PerformHotspotExposure(tile);

            if (tile.Hotspot.Bypassing)
            {
                tile.Hotspot.State = 3;
                // TODO ATMOS: Burn tile here

                if (tile.Air.Temperature > Atmospherics.FireMinimumTemperatureToSpread)
                {
                    var radiatedTemperature = tile.Air.Temperature * Atmospherics.FireSpreadRadiosityScale;
                    foreach (var otherTile in tile.AdjacentTiles)
                    {
                        // TODO ATMOS: This is sus. Suss this out.
                        if (otherTile == null)
                            continue;

                        if(!otherTile.Hotspot.Valid)
                            HotspotExpose(gridAtmosphere, otherTile, radiatedTemperature, Atmospherics.CellVolume/4);
                    }
                }
            }
            else
            {
                tile.Hotspot.State = (byte) (tile.Hotspot.Volume > Atmospherics.CellVolume * 0.4f ? 2 : 1);
            }

            if (tile.Hotspot.Temperature > tile.MaxFireTemperatureSustained)
                tile.MaxFireTemperatureSustained = tile.Hotspot.Temperature;

            if (_hotspotSoundCooldown++ == 0 && !string.IsNullOrEmpty(HotspotSound))
            {
                var coordinates = tile.GridIndices.ToEntityCoordinates(tile.GridIndex, _mapManager);
                // A few details on the audio parameters for fire.
                // The greater the fire state, the lesser the pitch variation.
                // The greater the fire state, the greater the volume.
                SoundSystem.Play(HotspotSound, Filter.Pvs(coordinates),
                    coordinates, AudioHelpers.WithVariation(0.15f/tile.Hotspot.State).WithVolume(-5f + 5f * tile.Hotspot.State));
            }

            if (_hotspotSoundCooldown > HotspotSoundCooldownCycles)
                _hotspotSoundCooldown = 0;

            // TODO ATMOS Maybe destroy location here?
        }

        private void HotspotExpose(GridAtmosphereComponent gridAtmosphere, TileAtmosphere tile, float exposedTemperature, float exposedVolume, bool soh = false)
        {
            if (tile.Air == null)
                return;

            var oxygen = tile.Air.GetMoles(Gas.Oxygen);
            var nitrogen = tile.Air.GetMoles(Gas.Nitrogen);

            if (oxygen < 0.5f)
                return;

            var plasma = tile.Air.GetMoles(Gas.Plasma);
            var tritium = tile.Air.GetMoles(Gas.Tritium);
            var hydrogen = tile.Air.GetMoles(Gas.Hydrogen);
            var clf3 = tile.Air.GetMoles(Gas.CLF3);

            if (tile.Hotspot.Valid)
            {
                if (soh)
                {
                    if (plasma > 0.5f || tritium > 0.5f || hydrogen > 0.5f || clf3 > 0.5f)
                    {
                        if (tile.Hotspot.Temperature < exposedTemperature)
                            tile.Hotspot.Temperature = exposedTemperature;
                        if (tile.Hotspot.Volume < exposedVolume)
                            tile.Hotspot.Volume = exposedVolume;
                    }
                }

                return;
            }

            if ((exposedTemperature > Atmospherics.PlasmaMinimumBurnTemperature) && (plasma > 0.5f || tritium > 0.5f || hydrogen > 0.5f) || (clf3 > 0.5f && clf3 > (nitrogen/ Atmospherics.CLF3NitrogenRetardantFactor)))
            {
                tile.Hotspot = new Hotspot
                {
                    Volume = exposedVolume * 25f,
                    Temperature = exposedTemperature,
                    SkippedFirstProcess = tile.CurrentCycle > gridAtmosphere.UpdateCounter,
                    Valid = true,
                    State = 1
                };


                AddActiveTile(gridAtmosphere, tile);
                gridAtmosphere.HotspotTiles.Add(tile);
            }
        }

        private void PerformHotspotExposure(TileAtmosphere tile)
        {
            if (tile.Air == null || !tile.Hotspot.Valid) return;

            tile.Hotspot.Bypassing = tile.Hotspot.SkippedFirstProcess && tile.Hotspot.Volume > tile.Air.Volume*0.95f;

            if (tile.Hotspot.Bypassing)
            {
                tile.Hotspot.Volume = tile.Air.ReactionResults[GasReaction.Fire] * Atmospherics.FireGrowthRate;
                tile.Hotspot.Temperature = tile.Air.Temperature;
            }
            else
            {
                var affected = tile.Air.RemoveVolume(tile.Hotspot.Volume);
                affected.Temperature = tile.Hotspot.Temperature;
                React(affected, tile);
                tile.Hotspot.Temperature = affected.Temperature;
                tile.Hotspot.Volume = affected.ReactionResults[GasReaction.Fire] * Atmospherics.FireGrowthRate;
                Merge(tile.Air, affected);
            }

            var fireEvent = new TileFireEvent(tile.Hotspot.Temperature, tile.Hotspot.Volume);

            foreach (var entity in _lookup.GetEntitiesIntersecting(tile.GridIndex, tile.GridIndices))
            {
                RaiseLocalEvent(entity, ref fireEvent, false);
            }
        }
    }
}
