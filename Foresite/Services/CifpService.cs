namespace Foresite.Services;

using CIFPReader;

using ShapefileReader;

using System.Collections.Immutable;

public sealed class CifpService
{
	public CIFP Cifp { get; private set; } = CIFP.Load();
	public ImmutableHashSet<ImmutableArray<Coordinate>> Coastlines { get; private set; } = [..
		Reader.ReadShapefile(Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? Environment.CurrentDirectory, "coastline", "coastline_i.shp"))
		.Select(ring => ring.Select(pt => new Coordinate((decimal)pt.y, (decimal)pt.x)).ToImmutableArray())
	];

	public CifpService()
	{
		Task.Run(async () =>
		{
			while (true)
			{
				await Task.Delay(TimeSpan.FromDays(7));

				// Destroy cache.
				if (Directory.Exists("cifp"))
					Directory.Delete("cifp");

				// Download anew!
				Cifp = CIFP.Load();
			}
		});
	}
}

public static class CifpExtensions
{
	public static Aerodrome? Find(this Dictionary<string, Aerodrome> aerodromes, string code)
	{
		code = code.ToUpperInvariant();

		// ICAO code check.
		if (aerodromes.TryGetValue(code, out Aerodrome? icaoAd))
			// User got it right!
			return icaoAd;

		// Check IATA & FAA codes
		if (aerodromes.Values.FirstOrDefault(ad => ad.IATACode == code) is Aerodrome iataAd)
			return iataAd;

		// Did they add an extra K where it doesn't belong?
		if (code.StartsWith('K') && code.Length == 4)
			return aerodromes.Find(code[1..]);
		else
			return null;
	}
}