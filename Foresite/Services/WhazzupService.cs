﻿using CIFPReader;

using System.Collections.Immutable;
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
	WhazzupDigest? _data;

	public WhazzupService(HttpClient http, CifpService cifp)
	{
		_http = http;
		_cifp = cifp;

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

					await Task.Delay(delay);
				}
			}
		});
	}

	async Task<WhazzupDigest?> GetAsync()
	{
		if (await _http.GetFromJsonAsync<WhazzupDigest>(WHAZZUP_URL) is not WhazzupDigest w)
			// Failed. Keep the old data until you get something usable.
			return _data;

		w.clients.pilots = [..
			w.clients.pilots
				.Select(p => {
					p.Category = p.flightPlan?.GetRouteCategory(_cifp.Cifp) ?? WhazzupExtensions.FlightRouteCategory.NonUs;
					return p;
				})
				.Where(p => p.Category is
						WhazzupExtensions.FlightRouteCategory.Departure
						or WhazzupExtensions.FlightRouteCategory.Arrival
						or WhazzupExtensions.FlightRouteCategory.Domestic
				)
		];

		return w;
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

	public enum FlightState
	{
		[JsonStringEnumMemberName("Boarding")] Boarding,
		[JsonStringEnumMemberName("Departing")] Departing,
		[JsonStringEnumMemberName("Initial Climb")] InitialClimb,
		[JsonStringEnumMemberName("En Route")] EnRoute,
		[JsonStringEnumMemberName("Approach")] Approach,
		[JsonStringEnumMemberName("Landed")] Landed,
		[JsonStringEnumMemberName("On Blocks")] OnBlocks
	}

	public static string ToString(this FlightState state) =>
		string.Join(' ', [
			.. Enum.GetName(state)?.Aggregate(ImmutableList<string>.Empty, (s, i) => char.IsUpper(i) ? s.Add(i.ToString()) : s.SetItem(s.Count - 1, s[^1] + i))
			?? ["unknown"]
		]);
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
	public WhazzupExtensions.FlightRouteCategory Category { get; set; } = WhazzupExtensions.FlightRouteCategory.NonUs;
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
