namespace Foresite.Services;

using CIFPReader;
using ShapefileReader;

using System.Collections.Immutable;

public sealed class CifpService
{
	public CIFP Cifp { get; private set; } = CIFP.Load();
	public ImmutableHashSet<ImmutableArray<Coordinate>> Coastlines { get; private set; } = [..
		Reader.ReadShapefile(@"C:\Users\westo\OneDrive\Flying\IVAO\XA\Web\Foresite\Foresite\coastline\coastline_i.shp")
		.Select(ring => ring.Select(pt => new Coordinate((decimal)pt.y, (decimal)pt.x)).ToImmutableArray())
	];

	public CifpService() { Task.Run(async () => { while (true) { await Task.Delay(TimeSpan.FromDays(7)); Cifp = CIFP.Load(); } }); }
}
