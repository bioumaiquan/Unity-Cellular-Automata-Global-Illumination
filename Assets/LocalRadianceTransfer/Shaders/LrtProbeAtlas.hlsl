#ifndef LRT_PROBE_ATLAS_INCLUDED
#define LRT_PROBE_ATLAS_INCLUDED

TEXTURE3D(_LrtFineVolume);
StructuredBuffer<uint4> _LrtProbeLayout;
int _LrtMixedEnabled;
int _LrtFineProbeEdge;
float _LrtProbeTrunkEdge;
float4 _LrtProbeTrunkDimensions;
float4 _LrtCoarseTiles;
float4 _LrtFineTiles;
float4 _LrtCoarseAtlasSize;
float4 _LrtFineAtlasSize;

uint3 LrtAtlasUnflatten(uint index, uint3 size)
{
    uint row = index / size.x;
    return uint3(index % size.x, row % size.y, row / size.y);
}

uint LrtWorldTrunkIndex(uint3 trunk)
{
    uint3 size = uint3(_LrtProbeTrunkDimensions.xyz);
    return trunk.x + size.x * (trunk.y + size.y * trunk.z);
}

uint3 LrtAtlasBrickOrigin(uint slot, uint edge)
{
    uint3 tiles = edge == (uint)_LrtFineProbeEdge ? uint3(_LrtFineTiles.xyz) : uint3(_LrtCoarseTiles.xyz);
    return LrtAtlasUnflatten(slot, tiles) * (edge + 2);
}

#endif
