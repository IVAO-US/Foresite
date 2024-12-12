using CIFPReader;

using System.Text.Json;
using System.Text.Json.Serialization;

using static Foresite.Services.WhazzupExtensions;

namespace Foresite.Services;

public class WhazzupService
{
	const string WHAZZUP_URL = "https://api.ivao.aero/v2/tracker/whazzup";

	public WhazzupDigest? Data => _data;

	public event Action<WhazzupDigest?>? DataUpdated;

	readonly HttpClient _http;
	readonly CifpService _cifp;
	readonly ArcGisService _gis;
	WhazzupDigest? _data;

	public WhazzupService(HttpClient http, CifpService cifp, ArcGisService gis)
	{
		_http = http;
		_cifp = cifp;
		_gis = gis;

		Task.Run(async () =>
		{
			while (true)
			{
				_data = await GetAsync();
				DataUpdated?.Invoke(_data);

				if (_data is WhazzupDigest d)
				{
					TimeSpan delay = d.updatedAt.AddSeconds(15.5) - DateTime.UtcNow;

					if (delay.TotalSeconds > 15.5)
						delay = TimeSpan.FromSeconds(15.5);

					if (delay.TotalSeconds > 0)
						await Task.Delay(delay);
				}
			}
		});
	}

	async Task<WhazzupDigest?> GetAsync()
	{
		try
		{
			if (await _http.GetFromJsonAsync<WhazzupDigest>(WHAZZUP_URL, new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter<FlightRouteCategory>() } }) is not WhazzupDigest w)
				// Failed. Keep the old data until you get something usable.
				return _data;

			CIFP cifp = _cifp.Cifp;

			w.clients.pilots = [..
				w.clients.pilots
					.Select(p => {
						p.Category = p.flightPlan?.GetRouteCategory(cifp) ?? FlightRouteCategory.NonUs;

						if (p.flightPlan?.route?.Split() is string[] routeElems)
						{
							List<ICoordinate?> points = [];

							NamedCoordinate last = new("", new(0, 0));
							Airway? pendingAirway = null;

							if (p.flightPlan!.departureId is string d && (cifp.Aerodromes.Find(d) is Aerodrome depAd))
							{
								last = depAd.Location is NamedCoordinate nc ? nc : new(depAd.Name, depAd.Location.GetCoordinate());
								points.Add(last);
							}

							string[] wps = [..routeElems.Select(e => e.ToUpperInvariant().Split('/')[0].Trim())];
							for (int idx = 0; idx < wps.Length; ++idx)
							{
								string wp = wps[idx];

								if (wp == "DCT") continue;

								void GenerateAirway(string endpointName)
								{
									if (pendingAirway is not Airway aw) return;

									int startIdx = pendingAirway.TakeWhile(awf => awf.Name != last.Name).Count();
									int endIdx = pendingAirway.TakeWhile(awf => awf.Name != endpointName).Count();

									for (int awfIdx = startIdx; awfIdx != endIdx; awfIdx += startIdx < endIdx ? 1 : -1)
										points.Add(aw.Skip(awfIdx).First().Point);

									pendingAirway = null;
								}

								// Check if aerodrome ID.
								if (cifp.Aerodromes.TryGetValue(wp, out Aerodrome? wpAd))
								{
									GenerateAirway(wpAd.Name);
									points.Add(wpAd.Location is NamedCoordinate nc ? nc : new NamedCoordinate(wpAd.Name, wpAd.Location.GetCoordinate()));
								}

								// Check if SID/STAR/IAP.
								else if (cifp.Procedures.TryGetValue(wp, out var procSet)
								 && (procSet.FirstOrDefault(pr => pr.Airport == p.flightPlan.departureId || pr.Airport == p.flightPlan.arrivalId) ?? procSet.First()) is Procedure proc)
								{
									IEnumerable<Procedure.Instruction?> instructions = proc switch {
										SID sid when sid.HasRoute(null, wps[idx + 1]) => sid.SelectRoute(null, wps[idx + 1]),
										STAR star when star.HasRoute(last.Name, null) => star.SelectRoute(last.Name, null),
										_ => proc.SelectAllRoutes(cifp.Fixes)
									};

									foreach (Procedure.Instruction? i in instructions)
									{
										if (i is null)
										{
											points.Add(null);
											continue;
										}

										if (i.Endpoint is ICoordinate c)
											points.Add(c);
									}
								}

								// Check if fix.
								else if (cifp.Fixes.TryGetValue(wp, out var fixes))
								{
									ICoordinate fix = fixes.MinBy(f => last.GetCoordinate().DistanceTo(f.GetCoordinate())) ?? fixes.First();

									if (fix is NamedCoordinate nc)
										last = nc;
									else
										last = new(wp, fix.GetCoordinate());

									GenerateAirway(last.Name);
									points.Add(fix);
								}

								// Check if airway.
								else if (cifp.Airways.TryGetValue(wp, out var airways))
								{
									if (airways.FirstOrDefault(aw => aw.Any(awf => awf.Name == last.Name)) is Airway naw)
										pendingAirway = naw;
								}

								// <shrug>.
								else
								{
									pendingAirway = null;
									continue;
								}
							}

							if (p.flightPlan!.arrivalId is string a && (cifp.Aerodromes.Find(a) is Aerodrome arrAd))
								points.Add(arrAd.Location is NamedCoordinate nc ? nc : new NamedCoordinate(arrAd.Name, arrAd.Location.GetCoordinate()));

							p.Route = [..points.Where(p => p is null || !(p is NamedCoordinate nc && string.IsNullOrEmpty(nc.Name)))];
						}

						return p;
					})
					.Where(p => p.Category is
							FlightRouteCategory.Departure
							or FlightRouteCategory.Arrival
							or FlightRouteCategory.Domestic
							|| IsOverflight(p)
					)
			];

			return w;
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine(ex.ToString(), "ERROR");

			return _data;
		}
	}

	private bool IsOverflight(Pilot p)
	{
		if (p.lastTrack is not Lasttrack ltr) return false;

		// Careful! This is Euclidean!
		bool vectorsIntersect(Coordinate vector1From, Coordinate vector1To, (decimal lat, decimal lon) vector2From, (decimal lat, decimal lon) vector2To)
		{
			decimal vec1xCoef = vector1To.Latitude - vector1From.Latitude;
			decimal vec1yCoef = vector1From.Longitude - vector1To.Longitude;
			decimal vec1Const = (vector1To.Longitude * vector1From.Latitude) - (vector1From.Longitude * vector1To.Latitude);

			decimal checkPoint1 = (vec1xCoef * vector2From.lon) + (vec1yCoef * vector2From.lat) + vec1Const;
			decimal checkPoint2 = (vec1xCoef * vector2To.lon) + (vec1yCoef * vector2To.lat) + vec1Const;

			// If both check-points are the same side of the line, they don't intersect.
			if (checkPoint1 > 0 && checkPoint2 > 0) return false;
			if (checkPoint1 < 0 && checkPoint2 < 0) return false;

			// Now check the other line.
			decimal vec2xCoef = vector2To.lat - vector2From.lat;
			decimal vec2yCoef = vector2From.lon - vector2To.lon;
			decimal vec2Const = (vector2To.lon * vector2From.lat) - (vector2From.lon * vector2To.lat);

			checkPoint1 = (vec2xCoef * vector1From.Longitude) + (vec2yCoef * vector1From.Latitude) + vec2Const;
			checkPoint2 = (vec2xCoef * vector1To.Longitude) + (vec2yCoef * vector1To.Latitude) + vec2Const;

			// If both check-points are the same side of the line, they don't intersect.
			if (checkPoint1 > 0 && checkPoint2 > 0) return false;
			if (checkPoint1 < 0 && checkPoint2 < 0) return false;

			// We'll treat collinear as inside because we don't care.
			return true;
		}

		bool inPolygon(Coordinate[] poly)
		{
			// Step 1: Check the bounding box. Quick, cheap, and will already filter out most misses.
			decimal minLat = poly.Min(c => c.Latitude), maxLat = poly.Max(c => c.Latitude);
			decimal minLon = poly.Min(c => c.Longitude), maxLon = poly.Max(c => c.Longitude);

			if (ltr.latitude < minLat || ltr.latitude > maxLat ||
				ltr.longitude < minLon || ltr.longitude > maxLon)
				return false;

			(Coordinate Fst, Coordinate Snd)[] sides = [..poly.Zip(poly[1..].Append(poly[0]))];

			// Count how many lines the aircraft would need to cross to get west of the polygon.
			return sides.Count(s => vectorsIntersect(s.Fst, s.Snd, (ltr.latitude, ltr.longitude), (ltr.latitude, minLon - 1))) % 2 != 0;
		}

		// If they're in any of our boundaries, they're fair game!
		return _gis.Boundaries.Any(b => b.Boundaries.Any(inPolygon));
	}
}

public static class WhazzupExtensions
{
	public enum FlightRouteCategory
	{
		/// <summary>The flight does not depart from or arrive into the US.</summary>
		NonUs = 0b00,
		/// <summary>The flight departs from a US airport.</summary>
		Departure = 0b01,
		/// <summary>The flight arrives into a US airport.</summary>
		Arrival = 0b10,
		/// <summary>The flight departs from and arrives into US airports.</summary>
		Domestic = 0b11
	}

	public static FlightRouteCategory GetRouteCategory(this Flightplan fpl, CIFP cifp)
	{
		bool departingUs = fpl.departureId is string d && (cifp.Aerodromes.Find(d) is not null);
		bool arrivingUs = fpl.arrivalId is string a && (cifp.Aerodromes.Find(a) is not null);

		return
			(departingUs ? FlightRouteCategory.Departure : FlightRouteCategory.NonUs)
			| (arrivingUs ? FlightRouteCategory.Arrival : FlightRouteCategory.NonUs);
	}

	public static string FormatAltitude(this Lasttrack ltr) => ltr.altitude switch {
		_ when ltr.onGround => "GND",
		>= 18000 => $"FL{ltr.altitude / 100:000}",
		_ => $"{ltr.altitude} ft"
	};


	public static string FormatSpeed(this Lasttrack ltr)
	{
		if (ltr.altitude >= 24000)
		{
			double machSpeed = ltr.groundSpeed / (20.1 * (Math.Sqrt(288.15 - (ltr.altitude / 1000.0 * 2)) * (3.6 / 1.852)));
			return "M" + machSpeed.ToString("F2");
		}
		else if (ltr.groundSpeed == 0 || ltr.onGround)
		{
			return "--";
		}
		else
		{
			return ltr.groundSpeed.ToString() + " kts";
		}
	}

	public static string CalculateRemainingTime(this Lasttrack ltr)
	{
		if (ltr.onGround)
		{
			return "00:00";
		}
		double remainingHours = (Convert.ToDouble(ltr.arrivalDistance) / Convert.ToDouble(ltr.groundSpeed) * 3600);

		if (remainingHours < TimeSpan.MaxValue.TotalSeconds && remainingHours > TimeSpan.MinValue.TotalSeconds)
		{
			TimeSpan timeSpan = TimeSpan.FromSeconds(remainingHours);
			return timeSpan.ToString(@"hh\:mm");
		}
		else
		{
			return "00:00";
		}
	}

	[JsonConverter(typeof(FlightStateJsonConverter))]
	public enum FlightState
	{
		Boarding,
		Departing,
		InitialClimb,
		EnRoute,
		Approach,
		Landed,
		OnBlocks
	}

	public class FlightStateJsonConverter : JsonConverter<FlightState>
	{
		public override FlightState Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
		{
			if (reader.TokenType != JsonTokenType.String || reader.GetString() is not string str)
				throw new JsonException();

			return str switch {
				"Boarding" => FlightState.Boarding,
				"Departing" => FlightState.Departing,
				"Initial Climb" => FlightState.InitialClimb,
				"En Route" => FlightState.EnRoute,
				"Approach" => FlightState.Approach,
				"Landed" => FlightState.Landed,
				"On Blocks" => FlightState.OnBlocks,
				_ => throw new JsonException(),
			};
		}

		public override void Write(Utf8JsonWriter writer, FlightState value, JsonSerializerOptions options)
		{
			writer.WriteStringValue(value switch {
				FlightState.Boarding => "Boarding",
				FlightState.Departing => "Departing",
				FlightState.InitialClimb => "Initial Climb",
				FlightState.EnRoute => "En Route",
				FlightState.Approach => "Approach",
				FlightState.Landed => "Landed",
				FlightState.OnBlocks => "On Blocks",
				_ => throw new JsonException(),
			});
		}
	}

	public static string ToString(this FlightState state) => System.Text.Json.JsonSerializer.Serialize(state);
}

#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.
#pragma warning disable IDE1006 // Upper case not permitted due to JSON deserialization requirements.
public class WhazzupDigest
{
	public DateTime updatedAt { get; set; }

	public Server[] servers { get; set; }
	public Voiceserver[] voiceServers { get; set; }
	public Connections connections { get; set; }
	public Clients clients { get; set; }
}
public class Connections
{
	public int total { get; set; }
	public int supervisor { get; set; }
	public int atc { get; set; }
	public int observer { get; set; }
	public int pilot { get; set; }
	public int worldTour { get; set; }
	public int followMe { get; set; }
	public int uniqueUsers24h { get; set; }
}

public class Clients
{
	public Pilot[] pilots { get; set; }
	public Atc[] atcs { get; set; }
	public object[] followMe { get; set; }
	public Observer[] observers { get; set; }
}

public class Pilot
{
	public int id { get; set; }
	public int userId { get; set; }
	public string callsign { get; set; }
	public string serverId { get; set; }
	public string softwareTypeId { get; set; }
	public string softwareVersion { get; set; }
	public int rating { get; set; }
	public DateTime createdAt { get; set; }
	public int time { get; set; }
	public Pilotsession pilotSession { get; set; }
	public Lasttrack? lastTrack { get; set; }
	public Flightplan? flightPlan { get; set; }
	[JsonIgnore] public FlightRouteCategory Category { get; set; } = FlightRouteCategory.NonUs;
	[JsonIgnore] public ICoordinate?[] Route { get; set; } = [];
}

public class Pilotsession
{
	public string simulatorId { get; set; }
	public int? textureId { get; set; }
}

public class Lasttrack
{
	public int altitude { get; set; }
	public int altitudeDifference { get; set; }
	public float? arrivalDistance { get; set; }
	public float? departureDistance { get; set; }
	public int groundSpeed { get; set; }
	public int heading { get; set; }
	public decimal latitude { get; set; }
	public decimal longitude { get; set; }
	public bool onGround { get; set; }
	public FlightState state { get; set; }
	public DateTime timestamp { get; set; }
	public int transponder { get; set; }
	public string transponderMode { get; set; }
	public int time { get; set; }
}

public class Flightplan
{
	public int id { get; set; }
	public int revision { get; set; }
	public string aircraftId { get; set; }
	public int aircraftNumber { get; set; }
	public string? departureId { get; set; }
	public string? arrivalId { get; set; }
	public string alternativeId { get; set; }
	public string alternative2Id { get; set; }
	public string route { get; set; }
	public string remarks { get; set; }
	public string speed { get; set; }
	public string level { get; set; }
	public string flightRules { get; set; }
	public string flightType { get; set; }
	public int eet { get; set; }
	public int endurance { get; set; }
	public int departureTime { get; set; }
	public object actualDepartureTime { get; set; }
	public int peopleOnBoard { get; set; }
	public DateTime createdAt { get; set; }
	public Aircraft aircraft { get; set; }
	public string aircraftEquipments { get; set; }
	public string aircraftTransponderTypes { get; set; }
}

public class Aircraft
{
	public string icaoCode { get; set; }
	public string model { get; set; }
	public string wakeTurbulence { get; set; }
	public bool? isMilitary { get; set; }
	public string description { get; set; }
}

public class Atc
{
	public int id { get; set; }
	public int userId { get; set; }
	public string callsign { get; set; }
	public string serverId { get; set; }
	public string softwareTypeId { get; set; }
	public string softwareVersion { get; set; }
	public int rating { get; set; }
	public DateTime createdAt { get; set; }
	public int time { get; set; }
	public Atcsession atcSession { get; set; }
	public Lasttrack1 lastTrack { get; set; }
	public Atis atis { get; set; }
}

public class Atcsession
{
	public float frequency { get; set; }
	public string position { get; set; }
}

public class Lasttrack1
{
	public int altitude { get; set; }
	public int altitudeDifference { get; set; }
	public object arrivalDistance { get; set; }
	public object departureDistance { get; set; }
	public int groundSpeed { get; set; }
	public int heading { get; set; }
	public float latitude { get; set; }
	public float longitude { get; set; }
	public bool onGround { get; set; }
	public string state { get; set; }
	public DateTime timestamp { get; set; }
	public int transponder { get; set; }
	public string transponderMode { get; set; }
	public int time { get; set; }
}

public class Atis
{
	public string[] lines { get; set; }
	public string revision { get; set; }
	public DateTime timestamp { get; set; }
}

public class Observer
{
	public int id { get; set; }
	public int userId { get; set; }
	public string callsign { get; set; }
	public string serverId { get; set; }
	public string softwareTypeId { get; set; }
	public string softwareVersion { get; set; }
	public int rating { get; set; }
	public DateTime createdAt { get; set; }
	public int time { get; set; }
	public Atcsession1 atcSession { get; set; }
	public Lasttrack2 lastTrack { get; set; }
}

public class Atcsession1
{
	public float frequency { get; set; }
	public object position { get; set; }
}

public class Lasttrack2
{
	public int altitude { get; set; }
	public int altitudeDifference { get; set; }
	public object arrivalDistance { get; set; }
	public object departureDistance { get; set; }
	public int groundSpeed { get; set; }
	public int heading { get; set; }
	public float latitude { get; set; }
	public float longitude { get; set; }
	public bool onGround { get; set; }
	public string state { get; set; }
	public DateTime timestamp { get; set; }
	public int transponder { get; set; }
	public string transponderMode { get; set; }
	public int time { get; set; }
}

public class Server
{
	public string id { get; set; }
	public string hostname { get; set; }
	public string ip { get; set; }
	public string description { get; set; }
	public string countryId { get; set; }
	public int currentConnections { get; set; }
	public int maximumConnections { get; set; }
}

public class Voiceserver
{
	public string id { get; set; }
	public string hostname { get; set; }
	public string ip { get; set; }
	public string description { get; set; }
	public string countryId { get; set; }
	public int currentConnections { get; set; }
	public int maximumConnections { get; set; }
}
#pragma warning restore
