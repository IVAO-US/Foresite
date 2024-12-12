using CIFPReader;

namespace Foresite.Services;

public class ArcGisService
{
	public FaaAirspaceBoundary[] Boundaries
	{
		get
		{
			if (DateTimeOffset.UtcNow - _cache.Updated > TimeSpan.FromDays(14))
				// Go refresh the boundaries when you get a chance.
				_ = DownloadBoundariesAsync();

			return _cache.Boundaries;
		}
	}

	private readonly HttpClient _http;
	private (FaaAirspaceBoundary[] Boundaries, DateTimeOffset Updated) _cache = ([], DateTimeOffset.MinValue);

	public ArcGisService(HttpClient http)
	{
		_http = http;
		DownloadBoundariesAsync().Wait();
	}

	private async Task DownloadBoundariesAsync()
	{
		try
		{
			var resp = await _http.GetFromJsonAsync<AirspaceBoundaryResponse>(@"https://services6.arcgis.com/ssFJjBXIUyZDrSYZ/arcgis/rest/services/Boundary_Airspace/FeatureServer/0/query?where=COUNTRY%20%3D%20'UNITED%20STATES'%20AND%20TYPE_CODE%20LIKE%20'ARTCC%25'&outFields=IDENT,NAME,TYPE_CODE,CLASS,SECTOR,LEVEL_,UPPER_DESC,UPPER_VAL,UPPER_UOM,UPPER_CODE,LOWER_VAL,LOWER_UOM,LOWER_CODE,ONSHORE,COUNTRY&outSR=4326&f=json");

			if (resp is null) return;

			_cache = ([.. resp.features.AsParallel().AsUnordered().Select(FaaAirspaceBoundary.FromFeature)], DateTimeOffset.UtcNow);
		}
		catch { }
	}


#pragma warning disable IDE1006 // Naming Styles
	private class AirspaceBoundaryResponse
	{
		public string objectIdFieldName { get; set; } = "";
		public Uniqueidfield uniqueIdField { get; set; } = new();
		public string globalIdFieldName { get; set; } = "";
		public GeometryProperties geometryProperties { get; set; } = new();
		public string geometryType { get; set; } = "";
		public SpatialReference spatialReference { get; set; } = new();
		public Field[] fields { get; set; } = [];
		public Feature[] features { get; set; } = [];
	}

	private class Uniqueidfield
	{
		public string name { get; set; } = "";
		public bool isSystemMaintained { get; set; }
	}

	private class GeometryProperties
	{
		public string shapeAreaFieldName { get; set; } = "";
		public string shapeLengthFieldName { get; set; } = "";
		public string units { get; set; } = "";
	}

	private class SpatialReference
	{
		public int wkid { get; set; }
		public int latestWkid { get; set; }
	}

	private class Field
	{
		public string name { get; set; } = "";
		public string type { get; set; } = "";
		public string alias { get; set; } = "";
		public string sqlType { get; set; } = "";
		public int length { get; set; }
		public object domain { get; set; } = new { };
		public object defaultValue { get; set; } = new { };
	}

	internal class Feature
	{
		public Attributes attributes { get; set; } = new();
		public Geometry geometry { get; set; } = new();
	}

	internal class Attributes
	{
		public string IDENT { get; set; } = "";
		public string NAME { get; set; } = "";
		public string TYPE_CODE { get; set; } = "";
		public string CLASS { get; set; } = "";
		public string SECTOR { get; set; } = "";
		public string LEVEL_ { get; set; } = "";
		public string UPPER_DESC { get; set; } = "";
		public int UPPER_VAL { get; set; }
		public string? UPPER_UOM { get; set; } = "";
		public string? UPPER_CODE { get; set; } = "";
		public int LOWER_VAL { get; set; }
		public string? LOWER_UOM { get; set; } = "";
		public string? LOWER_CODE { get; set; } = "";
		public int? ONSHORE { get; set; }
		public string COUNTRY { get; set; } = "";
	}

	internal class Geometry
	{
		public float[][][] rings { get; set; } = [];
	}
#pragma warning restore IDE1006
}

public record FaaAirspaceBoundary(string Identifier, string Name, AltitudeRestriction Altitude, Coordinate[][] Boundaries)
{
	internal static FaaAirspaceBoundary FromFeature(ArcGisService.Feature feature)
	{
		static Altitude ParseAlt(int val, string? uom, string? code)
		{
			if ((uom ?? code) is null)
				return new AltitudeAGL(0, null);

			if (uom != "FT")
				throw new NotImplementedException();

			return code switch {
				"SFC" or "AGL" => new AltitudeAGL(val, null),
				"MSL" => new AltitudeMSL(val),
				_ => throw new NotImplementedException()
			};
		}

		Altitude bottom = ParseAlt(feature.attributes.LOWER_VAL, feature.attributes.LOWER_UOM, feature.attributes.LOWER_CODE);
		Altitude top = ParseAlt(feature.attributes.UPPER_VAL, feature.attributes.UPPER_UOM, feature.attributes.UPPER_CODE);

		if (feature.geometry.rings.SelectMany(r => r).Any(p => p.Length != 2))
			throw new ArgumentException("Invalid coordinate.", nameof(feature));

		return new(
			feature.attributes.IDENT, feature.attributes.NAME,
			new(bottom is AltitudeAGL ba && ba.Feet == 0 ? null : bottom, top is AltitudeAGL ta && ta.Feet == 0 ? null : top),
			[..feature.geometry.rings.Select(r => r.Select(c => 
				// NOT a mistake. They do their coordinates lon, lat like the psychopaths they are.
				new Coordinate((decimal)c[1], (decimal)c[0])
			).ToArray())]
		);
	}
}