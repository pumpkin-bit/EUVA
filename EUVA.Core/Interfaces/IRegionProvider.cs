// SPDX-License-Identifier: GPL-3.0-or-later


using EUVA.Core.Models;

namespace EUVA.Core.Interfaces;


public interface IRegionProvider
{
   
    IEnumerable<DataRegion> ProvideRegions(BinaryStructure structure, ReadOnlySpan<byte> data);
}
