// SPDX-License-Identifier: GPL-3.0-or-later


using EUVA.Core.Models;

namespace EUVA.Core.Interfaces;


public interface IBinaryMapper
{
    
    BinaryStructure Parse(ReadOnlySpan<byte> data);

  
    IReadOnlyList<DataRegion> GetRegions();

   
    DataRegion? FindRegionAt(long offset);

    
    void RegisterRegionProvider(IRegionProvider provider);

    
    BinaryStructure? RootStructure { get; }
}
