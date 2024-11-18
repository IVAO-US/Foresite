using System;
using System.Text.Json;
using System.Text.Json.Serialization;

using static CIFPReader.ProcedureLine;

namespace CIFPReader;

[JsonConverter(typeof(ProcedureJsonConverter))]
public class Procedure
{
	public string Name { get; init; }
	public string? Airport { get; init; }

	protected readonly List<Instruction> instructions = [];

	protected Procedure(string name) => Name = name;

	public Procedure(string name, IEnumerable<Instruction> instructions) : this(name) =>
		this.instructions = instructions.ToList();

	public virtual bool HasRoute(string? inboundTransition, string? outboundTransition) => false;

	public virtual IEnumerable<Instruction> SelectRoute(string? inboundTransition, string? outboundTransition) =>
		instructions.AsEnumerable().Select(i => i);

	public virtual IEnumerable<Instruction?> SelectAllRoutes(Dictionary<string, HashSet<ICoordinate>> fixes) =>
		instructions.AsEnumerable().Select(i => i);

	public record Instruction(PathTermination Termination, IProcedureEndpoint? Endpoint, IProcedureVia? Via, ICoordinate? ReferencePoint, SpeedRestriction Speed, AltitudeRestriction Altitude, [property:JsonIgnore] bool OnGround = false)
	{
		public bool IsComplete(Coordinate position, Altitude altitude, decimal tolerance)
		{
			if (Termination.HasFlag(PathTermination.UntilTerminated))
				return false;

			return Endpoint?.IsConditionReached(Termination, (position, altitude, null), tolerance) ?? false;
		}
	}

	public class ProcedureJsonConverter : JsonConverter<Procedure>
	{
		public override Procedure? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
		{
			if (reader.TokenType != JsonTokenType.StartObject)
				throw new JsonException();

			reader.Read();
			reader.Read();
			string proctype = reader.GetString() ?? throw new JsonException();
			reader.Read();
			reader.Read();

			Procedure? retval = proctype switch {
				"SID" => JsonSerializer.Deserialize<SID>(ref reader, options),
				"STAR" => JsonSerializer.Deserialize<STAR>(ref reader, options),
				"IAP" => JsonSerializer.Deserialize<Approach>(ref reader, options),
				_ => throw new JsonException()
			};

			reader.Read();

			if (reader.TokenType != JsonTokenType.EndObject)
				throw new JsonException();

			return retval;
		}

		public override void Write(Utf8JsonWriter writer, Procedure value, JsonSerializerOptions options)
		{
			writer.WriteStartObject();

			switch (value)
			{
				case SID s:
					writer.WriteString("type", "SID");
					writer.WritePropertyName("$");
					JsonSerializer.Serialize(writer, s, options);
					break;

				case STAR s:
					writer.WriteString("type", "STAR");
					writer.WritePropertyName("$");
					JsonSerializer.Serialize(writer, s, options);
					break;

				case Approach a:
					writer.WriteString("type", "IAP");
					writer.WritePropertyName("$");
					JsonSerializer.Serialize(writer, a, options);
					break;

				default:
					throw new NotSupportedException();
			}

			writer.WriteEndObject();
		}
	}

	protected readonly char[] _runwayLetters = ['L', 'C', 'R'];
}

[JsonConverter(typeof(SIDJsonConverter))]
public class SID : Procedure
{
	private readonly Dictionary<string, Instruction[]> runwayTransitions = [];
	private readonly Instruction[] commonRoute = [];
	private readonly Dictionary<string, Instruction[]> enrouteTransitions = [];

	private SID(string name, string airport, Dictionary<string, Instruction[]> runwayTransitions, Instruction[] commonRoute, Dictionary<string, Instruction[]> enrouteTransitions) : base(name) =>
		(Airport, this.runwayTransitions, this.commonRoute, this.enrouteTransitions) = (airport, runwayTransitions, commonRoute, enrouteTransitions);

	public SID(SIDLine[] lines, Dictionary<string, HashSet<ICoordinate>> fixes, Dictionary<string, HashSet<Navaid>> navaids, Dictionary<string, Aerodrome> aerodromes) : base("<EMPTY PROCEDURE>")
	{
		if (lines.Length == 0)
			return;

		Name = lines.First().Name;
		Airport = lines.First().Airport;
		if (lines.Any(l => l.Name != Name))
			throw new ArgumentException("The provided lines represent multiple SIDs", nameof(lines));

		ICoordinate? referencePoint = null;
		if (aerodromes.TryGetValue(Airport, out Aerodrome? value))
			referencePoint = value.Location;
		else if (lines.Any(l => l.Endpoint is Coordinate))
			referencePoint = lines.First(l => l.Endpoint is Coordinate).Endpoint as Coordinate?;
		else if (lines.Count(l => l.Endpoint is UnresolvedWaypoint) >= 2)
		{
			UnresolvedWaypoint[] uwps =
				lines
				.Where(l => l.Endpoint is not null && l.Endpoint is UnresolvedWaypoint)
				.Select(l => (UnresolvedWaypoint)l.Endpoint!)
				.Take(2).ToArray();

			referencePoint = uwps[0].Resolve(fixes, uwps[1]);
		}

		SIDLine fix(SIDLine line)
		{
			MagneticCourse fixMagnetic(MagneticCourse mc) =>
				mc with {
					Variation = aerodromes.GetLocalMagneticVariation(
							(line.Endpoint, referencePoint) switch {
								(ICoordinate c, _) => c.GetCoordinate(),
								(_, null) => throw new Exception("Unable to pin magvar for SID."),
								(_, ICoordinate rp) => rp.GetCoordinate()
							}).Variation
				};

			if (line.Endpoint is UnresolvedWaypoint uwep)
				line = line with { Endpoint = uwep.Resolve(fixes, referencePoint?.GetCoordinate()) };
			else if (line.Endpoint is UnresolvedDistance urd)
				line = line with { Endpoint = urd.Resolve(fixes, referencePoint?.GetCoordinate()) };
			else if (line.Endpoint is UnresolvedRadial urr)
				line = line with { Endpoint = urr.Resolve(navaids, referencePoint?.GetCoordinate()) };

			if (line.Via is Arc a)
			{
				if (a.Centerwaypoint is UnresolvedWaypoint uwap)
					a = a with { Centerpoint = uwap.Resolve(fixes, referencePoint?.GetCoordinate()) };
				if (a.ArcTo.Variation is null)
					a = a with { ArcTo = fixMagnetic(a.ArcTo) };

				line = line with { Via = a };
			}
			else if (line.Via is Racetrack r && r.Waypoint is UnresolvedWaypoint uwrp)
				line = line with { Via = r with { Point = uwrp.Resolve(fixes, referencePoint?.GetCoordinate()) } };
			else if (line.Via is MagneticCourse mc && mc.Variation is null)
			{
				line = line with {
					Via = fixMagnetic(mc)
				};
			}

			return line;
		}

		for (int linectr = 0; linectr < lines.Length;)
		{
			SIDLine lineHead = lines[linectr];

			switch (lineHead.RouteType)
			{
				case SIDLine.SIDRouteType.RunwayTransition:
				case SIDLine.SIDRouteType.RunwayTransition_RNAV:
				case SIDLine.SIDRouteType.RunwayTransition_Vector:
					List<Instruction> rt = [];
					for (; linectr < lines.Length && (lines[linectr].RouteType, lines[linectr].Transition) == (lineHead.RouteType, lineHead.Transition); ++linectr)
					{
						var line = fix(lines[linectr]);

						rt.Add(new(line.FixInstruction, line.Endpoint, line.Via, line.ReferenceFix?.Resolve(fixes, new UnresolvedWaypoint(line.Airport)), line.SpeedRestriction, line.AltitudeRestriction));
					}
					runwayTransitions.Add(lineHead.Transition, [.. rt]);
					break;

				case SIDLine.SIDRouteType.CommonRoute:
				case SIDLine.SIDRouteType.CommonRoute_RNAV:
					List<Instruction> cr = [];
					for (; linectr < lines.Length && "25".Contains((char)lines[linectr].RouteType); ++linectr)
					{
						var line = fix(lines[linectr]);

						cr.Add(new(line.FixInstruction, line.Endpoint, line.Via, line.ReferenceFix?.Resolve(fixes, new UnresolvedWaypoint(line.Airport)), line.SpeedRestriction, line.AltitudeRestriction));
					}
					commonRoute = [.. cr];
					break;

				case SIDLine.SIDRouteType.EnrouteTransition:
				case SIDLine.SIDRouteType.EnrouteTransition_RNAV:
				case SIDLine.SIDRouteType.EnrouteTransition_Vector:
					List<Instruction> et = [];
					for (; linectr < lines.Length && (lines[linectr].RouteType, lines[linectr].Transition) == (lineHead.RouteType, lineHead.Transition); ++linectr)
					{
						var line = fix(lines[linectr]);

						et.Add(new(line.FixInstruction, line.Endpoint, line.Via, line.ReferenceFix?.Resolve(fixes), line.SpeedRestriction, line.AltitudeRestriction));
					}
					enrouteTransitions.Add(lineHead.Transition, [.. et]);
					break;
			}
		}
	}

	public override IEnumerable<Instruction?> SelectAllRoutes(Dictionary<string, HashSet<ICoordinate>> fixes)
	{
		Instruction? lastReturned = null;

		foreach (var inboundTransition in runwayTransitions.SelectMany(rt =>
			(rt.Key.StartsWith("RW") && rt.Key.EndsWith('B') ? (string[])[rt.Key[..^1] + "L", rt.Key[..^1] + "R"] : [rt.Key])
				.Select(k => new KeyValuePair<string, Instruction[]>(k, rt.Value))
		))
		{
			string refFix = (inboundTransition.Key.StartsWith("RW") && inboundTransition.Key.Length >= 4 && inboundTransition.Key[2..4].All(char.IsDigit)) ? (Airport + "/" + inboundTransition.Key[2..]) : (Airport + "/" + inboundTransition.Key);

			try
			{
				lastReturned = new(PathTermination.UntilCrossing | PathTermination.Direct, new UnresolvedWaypoint(refFix).Resolve(fixes, Airport is null ? null : new UnresolvedWaypoint(Airport)), null, null, SpeedRestriction.Unrestricted, AltitudeRestriction.Unrestricted);
			}
			catch (Exception) { }

			if (lastReturned is not null)
				yield return lastReturned;

			if (!(char.IsDigit(refFix[^1]) || "LCR".Contains(refFix[^1])))
				System.Diagnostics.Debugger.Break();

			foreach (var instr in inboundTransition.Value)
			{
				if (instr.Endpoint is ICoordinate)
					lastReturned = instr;

				yield return instr;
			}

			yield return null;
		}

		foreach (Instruction i in commonRoute)
		{
			if (i.Endpoint is ICoordinate)
				lastReturned = i;

			yield return i;
		}

		foreach (var outboundTransition in enrouteTransitions.Values)
		{
			if (lastReturned is not null)
				yield return new(PathTermination.UntilCrossing | PathTermination.Direct, lastReturned.Endpoint, null, null, SpeedRestriction.Unrestricted, AltitudeRestriction.Unrestricted);

			foreach (var instr in outboundTransition)
				yield return instr;

			yield return null;
		}
	}

	public override bool HasRoute(string? inboundTransition, string? outboundTransition) =>
		(outboundTransition is null || enrouteTransitions.ContainsKey(outboundTransition)) && (inboundTransition is null || runwayTransitions.ContainsKey(inboundTransition));

	public override IEnumerable<Instruction> SelectRoute(string? inboundTransition, string? outboundTransition)
	{
		string lastName = "";

		if (outboundTransition is not null && !enrouteTransitions.ContainsKey(outboundTransition))
			throw new ArgumentException($"Enroute transition {outboundTransition} was not found.", nameof(outboundTransition));

		if (inboundTransition is null && runwayTransitions.TryGetValue("ALL", out Instruction[]? value))
			foreach (Instruction i in value)
			{
				if (i.Endpoint is NamedCoordinate nc)
					if (nc.Name == lastName)
						continue;
					else
						lastName = nc.Name;

				yield return i;
			}

		else if (inboundTransition is not null)
		{
			if (!runwayTransitions.ContainsKey(inboundTransition))
			{
				if (_runwayLetters.Contains(inboundTransition.Last()) && runwayTransitions.ContainsKey(inboundTransition[..^1] + "B"))
					inboundTransition = inboundTransition[..^1] + "B";
				else
					throw new ArgumentException($"Runway transition {inboundTransition} was not found.", nameof(inboundTransition));
			}

			foreach (Instruction i in runwayTransitions[inboundTransition])
			{
				if (i.Endpoint is NamedCoordinate nc)
					if (nc.Name == lastName)
						continue;
					else
						lastName = nc.Name;

				yield return i;
			}
		}

		foreach (Instruction i in commonRoute)
		{
			if (i.Endpoint is NamedCoordinate nc)
				if (nc.Name == lastName)
					continue;
				else
					lastName = nc.Name;

			yield return i;
		}

		if (outboundTransition is null && enrouteTransitions.TryGetValue("ALL", out Instruction[]? instrs))
			foreach (Instruction i in instrs)
			{
				if (i.Endpoint is NamedCoordinate nc)
					if (nc.Name == lastName)
						continue;
					else
						lastName = nc.Name;

				yield return i;
			}
		else if (outboundTransition is not null)
			foreach (Instruction i in enrouteTransitions[outboundTransition])
			{
				if (i.Endpoint is NamedCoordinate nc)
					if (nc.Name == lastName)
						continue;
					else
						lastName = nc.Name;

				yield return i;
			}

		yield break;
	}

	public IEnumerable<(string? Inbound, string? Outbound)> EnumerateTransitions()
	{
		HashSet<string?> inbounds = runwayTransitions.Keys.Select(k => k == "ALL" ? null : k).ToHashSet();
		HashSet<string?> outbounds = enrouteTransitions.Keys.Select(k => k == "ALL" ? null : k).ToHashSet();

		if (inbounds.Count == 0)
			inbounds = new(new string?[] { null });
		if (outbounds.Count == 0)
			outbounds = new(new string?[] { null });

		return inbounds.SelectMany(i => outbounds.Select(o => (i, o)));
	}

	public override string ToString() => $"{Name} (SID - {Airport})";

	public class SIDJsonConverter : JsonConverter<SID>
	{
		public override SID? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
		{
			if (reader.TokenType != JsonTokenType.StartObject)
				throw new JsonException();

			string name = string.Empty, airport = string.Empty;
			Dictionary<string, Instruction[]> runwayTransitions = [];
			Instruction[] commonRoute = [];
			Dictionary<string, Instruction[]> enrouteTransitions = [];

			while (reader.Read())
			{
				if (reader.TokenType == JsonTokenType.EndObject)
					break;

				switch (reader.GetString())
				{
					case "Name":
						reader.Read();
						name = reader.GetString() ?? throw new JsonException();
						break;

					case "Ap":
						reader.Read();
						airport = reader.GetString() ?? throw new JsonException();
						break;

					case "RwTrans":
						reader.Read();
						runwayTransitions = JsonSerializer.Deserialize<Dictionary<string, Instruction[]>>(ref reader, options) ?? throw new JsonException();
						break;

					case "Common":
						reader.Read();
						commonRoute = JsonSerializer.Deserialize<Instruction[]>(ref reader, options) ?? throw new JsonException();
						break;

					case "EnrTrans":
						reader.Read();
						enrouteTransitions = JsonSerializer.Deserialize<Dictionary<string, Instruction[]>>(ref reader, options) ?? throw new JsonException();
						break;

					default:
						throw new JsonException();
				}
			}

			if (reader.TokenType != JsonTokenType.EndObject)
				throw new JsonException();

			return new SID(name, airport, runwayTransitions, commonRoute, enrouteTransitions);
		}

		public override void Write(Utf8JsonWriter writer, SID value, JsonSerializerOptions options)
		{
			writer.WriteStartObject();

			writer.WriteString("Name", value.Name);
			writer.WriteString("Ap", value.Airport);

			writer.WritePropertyName("RwTrans");
			JsonSerializer.Serialize(writer, value.runwayTransitions, options);

			writer.WritePropertyName("Common");
			JsonSerializer.Serialize(writer, value.commonRoute, options);

			writer.WritePropertyName("EnrTrans");
			JsonSerializer.Serialize(writer, value.enrouteTransitions, options);

			writer.WriteEndObject();
		}
	}
}

[JsonConverter(typeof(STARJsonConverter))]
public class STAR : Procedure
{
	private readonly Dictionary<string, Instruction[]> enrouteTransitions = [];
	private readonly Instruction[] commonRoute = [];
	private readonly Dictionary<string, Instruction[]> runwayTransitions = [];

	private STAR(string name, string airport, Dictionary<string, Instruction[]> runwayTransitions, Instruction[] commonRoute, Dictionary<string, Instruction[]> enrouteTransitions) : base(name) =>
		(Airport, this.runwayTransitions, this.commonRoute, this.enrouteTransitions) = (airport, runwayTransitions, commonRoute, enrouteTransitions);

	public STAR(STARLine[] lines, Dictionary<string, HashSet<ICoordinate>> fixes, Dictionary<string, HashSet<Navaid>> navaids, Dictionary<string, Aerodrome> aerodromes) : base("<EMPTY PROCEDURE>")
	{
		if (lines.Length == 0)
			return;

		Name = lines.First().Name;
		Airport = lines.First().Airport;
		if (lines.Any(l => l.Name != Name))
			throw new ArgumentException("The provided lines represent multiple STARs", nameof(lines));

		ICoordinate? referencePoint = null;
		if (aerodromes.TryGetValue(Airport, out Aerodrome? value))
			referencePoint = value.Location;
		else if (lines.Any(l => l.Endpoint is Coordinate))
			referencePoint = lines.First(l => l.Endpoint is Coordinate).Endpoint as Coordinate?;
		else if (lines.Count(l => l.Endpoint is UnresolvedWaypoint) >= 2)
		{
			UnresolvedWaypoint[] uwps =
				lines
				.Where(l => l.Endpoint is not null && l.Endpoint is UnresolvedWaypoint)
				.Select(l => (UnresolvedWaypoint)l.Endpoint!)
				.Take(2).ToArray();

			referencePoint = uwps[0].Resolve(fixes, uwps[1]);
		}

		STARLine fix(STARLine line)
		{
			MagneticCourse fixMagnetic(MagneticCourse mc) =>
				mc with {
					Variation = aerodromes.GetLocalMagneticVariation(
							(line.Endpoint, referencePoint) switch {
								(ICoordinate c, _) => c.GetCoordinate(),
								(_, null) => throw new Exception("Unable to pin magvar for SID."),
								(_, ICoordinate rp) => rp.GetCoordinate()
							}).Variation
				};

			if (line.Endpoint is UnresolvedWaypoint uwep)
				line = line with { Endpoint = uwep.Resolve(fixes, referencePoint?.GetCoordinate()) };
			else if (line.Endpoint is UnresolvedDistance urd)
				line = line with { Endpoint = urd.Resolve(fixes, referencePoint?.GetCoordinate()) };
			else if (line.Endpoint is UnresolvedRadial urr)
				line = line with { Endpoint = urr.Resolve(navaids, referencePoint?.GetCoordinate()) };

			if (line.Via is Arc a)
			{
				if (a.Centerwaypoint is UnresolvedWaypoint uwap)
					a = a with { Centerpoint = uwap.Resolve(fixes, referencePoint?.GetCoordinate()) };
				if (a.ArcTo.Variation is null)
					a = a with { ArcTo = fixMagnetic(a.ArcTo) };

				line = line with { Via = a };
			}
			else if (line.Via is Racetrack r && r.Waypoint is UnresolvedWaypoint uwrp)
				line = line with { Via = r with { Point = uwrp.Resolve(fixes, referencePoint?.GetCoordinate()) } };
			else if (line.Via is MagneticCourse mc && mc.Variation is null)
			{
				line = line with {
					Via = fixMagnetic(mc)
				};
			}

			return line;
		}

		for (int linectr = 0; linectr < lines.Length;)
		{
			STARLine lineHead = lines[linectr];

			switch (lineHead.RouteType)
			{
				case STARLine.STARRouteType.RunwayTransition:
				case STARLine.STARRouteType.RunwayTransition_RNAV:
					List<Instruction> rt = [];
					for (; linectr < lines.Length && (lines[linectr].RouteType, lines[linectr].Transition) == (lineHead.RouteType, lineHead.Transition); ++linectr)
					{
						var line = fix(lines[linectr]);

						rt.Add(new(line.FixInstruction, line.Endpoint, line.Via, line.ReferenceFix?.Resolve(fixes, new UnresolvedWaypoint(line.Airport)), line.SpeedRestriction, line.AltitudeRestriction));
					}
					runwayTransitions.Add(lineHead.Transition, [.. rt]);
					break;

				case STARLine.STARRouteType.CommonRoute:
				case STARLine.STARRouteType.CommonRoute_RNAV:
					List<Instruction> cr = [];
					for (; linectr < lines.Length && "25".Contains((char)lines[linectr].RouteType); ++linectr)
					{
						var line = fix(lines[linectr]);

						cr.Add(new(line.FixInstruction, line.Endpoint, line.Via, line.ReferenceFix?.Resolve(fixes, new UnresolvedWaypoint(line.Airport)), line.SpeedRestriction, line.AltitudeRestriction));
					}
					commonRoute = [.. cr];
					break;

				case STARLine.STARRouteType.EnrouteTransition:
				case STARLine.STARRouteType.EnrouteTransition_RNAV:
					List<Instruction> et = [];
					for (; linectr < lines.Length && (lines[linectr].RouteType, lines[linectr].Transition) == (lineHead.RouteType, lineHead.Transition); ++linectr)
					{
						var line = fix(lines[linectr]);

						et.Add(new(line.FixInstruction, line.Endpoint, line.Via, line.ReferenceFix?.Resolve(fixes, new UnresolvedWaypoint(line.Airport)), line.SpeedRestriction, line.AltitudeRestriction));
					}
					enrouteTransitions.Add(lineHead.Transition, [.. et]);
					break;
			}
		}
	}

	public override IEnumerable<Instruction?> SelectAllRoutes(Dictionary<string, HashSet<ICoordinate>> fixes)
	{
		Instruction? lastReturned = null;
		foreach (var inboundTransition in enrouteTransitions.Values)
		{
			foreach (var instr in inboundTransition)
			{
				if (instr.Endpoint is ICoordinate)
					lastReturned = instr;

				yield return instr;
			}

			yield return null;
		}

		foreach (Instruction i in commonRoute)
		{
			if (i.Endpoint is ICoordinate)
				lastReturned = i;
			yield return i;
		}

		foreach (var outboundTransition in runwayTransitions.Values)
		{
			if (lastReturned is not null)
			yield return new(PathTermination.UntilCrossing | PathTermination.Direct, lastReturned!.Endpoint, null, null, SpeedRestriction.Unrestricted, AltitudeRestriction.Unrestricted);

			foreach (var instr in outboundTransition)
				yield return instr;

			yield return null;
		}
	}

	public override bool HasRoute(string? inboundTransition, string? outboundTransition) =>
		(outboundTransition is null || runwayTransitions.ContainsKey(outboundTransition)) && (inboundTransition is null || enrouteTransitions.ContainsKey(inboundTransition));

	public override IEnumerable<Instruction> SelectRoute(string? inboundTransition, string? outboundTransition)
	{
		string lastName = "";

		if (inboundTransition is not null && !enrouteTransitions.ContainsKey(inboundTransition))
			throw new ArgumentException($"Enroute transition {inboundTransition} was not found.", nameof(inboundTransition));

		if (outboundTransition is not null && !runwayTransitions.ContainsKey(outboundTransition))
		{
			if (_runwayLetters.Contains(outboundTransition.Last()) && runwayTransitions.ContainsKey(outboundTransition[..^1] + "B"))
				outboundTransition = outboundTransition[..^1] + "B";
			else
				throw new ArgumentException($"Runway transition {outboundTransition} was not found.", nameof(outboundTransition));
		}

		if (inboundTransition is null && enrouteTransitions.TryGetValue("ALL", out Instruction[]? value))
			foreach (Instruction i in value)
			{
				if (i.Endpoint is NamedCoordinate nc)
					if (nc.Name == lastName)
						continue;
					else
						lastName = nc.Name;

				yield return i;
			}
		else if (inboundTransition is not null)
			foreach (Instruction i in enrouteTransitions[inboundTransition])
			{
				if (i.Endpoint is NamedCoordinate nc)
					if (nc.Name == lastName)
						continue;
					else
						lastName = nc.Name;

				yield return i;
			}

		foreach (Instruction i in commonRoute)
		{
			if (i.Endpoint is NamedCoordinate nc)
				if (nc.Name == lastName)
					continue;
				else
					lastName = nc.Name;

			yield return i;
		}

		if (outboundTransition is null && runwayTransitions.TryGetValue("ALL", out Instruction[]? instrs))
			foreach (Instruction i in instrs)
			{
				if (i.Endpoint is NamedCoordinate nc)
					if (nc.Name == lastName)
						continue;
					else
						lastName = nc.Name;

				yield return i;
			}
		else if (outboundTransition is not null)
			foreach (Instruction i in runwayTransitions[outboundTransition])
			{
				if (i.Endpoint is NamedCoordinate nc)
					if (nc.Name == lastName)
						continue;
					else
						lastName = nc.Name;

				yield return i;
			}

		yield break;
	}

	public IEnumerable<(string? Inbound, string? Outbound)> EnumerateTransitions()
	{
		HashSet<string?> inbounds = enrouteTransitions.Keys.Select(k => k == "ALL" ? null : k).ToHashSet();
		HashSet<string?> outbounds = runwayTransitions.Keys.Select(k => k == "ALL" ? null : k).ToHashSet();

		if (inbounds.Count == 0)
			inbounds = new(new string?[] { null });
		if (outbounds.Count == 0)
			outbounds = new(new string?[] { null });

		return inbounds.SelectMany(i => outbounds.Select(o => (i, o)));
	}

	public override string ToString() => $"{Name} (STAR - {Airport})";

	public class STARJsonConverter : JsonConverter<STAR>
	{
		public override STAR? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
		{
			if (reader.TokenType != JsonTokenType.StartObject)
				throw new JsonException();

			string name = string.Empty, airport = string.Empty;
			Dictionary<string, Instruction[]> runwayTransitions = [];
			Instruction[] commonRoute = [];
			Dictionary<string, Instruction[]> enrouteTransitions = [];

			while (reader.Read())
			{
				if (reader.TokenType == JsonTokenType.EndObject)
					break;

				switch (reader.GetString())
				{
					case "Name":
						reader.Read();
						name = reader.GetString() ?? throw new JsonException();
						break;

					case "Ap":
						reader.Read();
						airport = reader.GetString() ?? throw new JsonException();
						break;

					case "EnrTrans":
						reader.Read();
						enrouteTransitions = JsonSerializer.Deserialize<Dictionary<string, Instruction[]>>(ref reader, options) ?? throw new JsonException();
						break;

					case "Common":
						reader.Read();
						commonRoute = JsonSerializer.Deserialize<Instruction[]>(ref reader, options) ?? throw new JsonException();
						break;

					case "RwTrans":
						reader.Read();
						runwayTransitions = JsonSerializer.Deserialize<Dictionary<string, Instruction[]>>(ref reader, options) ?? throw new JsonException();
						break;

					default:
						throw new JsonException();
				}
			}

			if (reader.TokenType != JsonTokenType.EndObject)
				throw new JsonException();

			return new(name, airport, runwayTransitions, commonRoute, enrouteTransitions);
		}

		public override void Write(Utf8JsonWriter writer, STAR value, JsonSerializerOptions options)
		{
			writer.WriteStartObject();

			writer.WriteString("Name", value.Name);
			writer.WriteString("Ap", value.Airport);

			writer.WritePropertyName("EnrTrans");
			JsonSerializer.Serialize(writer, value.enrouteTransitions, options);

			writer.WritePropertyName("Common");
			JsonSerializer.Serialize(writer, value.commonRoute, options);

			writer.WritePropertyName("RwTrans");
			JsonSerializer.Serialize(writer, value.runwayTransitions, options);

			writer.WriteEndObject();
		}
	}
}

[JsonConverter(typeof(ApproachJsonConverter))]
public class Approach : Procedure
{
	private readonly Dictionary<string, Instruction[]> transitions = [];
	private readonly Instruction[] commonRoute = [];

	private Approach(string name, string airport, Dictionary<string, Instruction[]> transitions, Instruction[] commonRoute) : base(name) =>
		(Airport, this.transitions, this.commonRoute) = (airport, transitions, commonRoute);

	public Approach(ApproachLine[] lines, Dictionary<string, HashSet<ICoordinate>> fixes, Dictionary<string, HashSet<Navaid>> navaids, Dictionary<string, Aerodrome> aerodromes) : base("<EMPTY PROCEDURE>")
	{
		if (lines.Length == 0)
			return;

		Name = lines.First().Name;
		Airport = lines.First().Airport;
		if (lines.Any(l => l.Name != Name))
			throw new ArgumentException("The provided lines represent multiple IAPs", nameof(lines));

		ICoordinate? referencePoint = null;
		if (aerodromes.TryGetValue(Airport, out Aerodrome? value))
			referencePoint = value.Location;
		else if (lines.Any(l => l.Endpoint is Coordinate))
			referencePoint = lines.First(l => l.Endpoint is Coordinate).Endpoint as Coordinate?;
		else if (lines.Count(l => l.Endpoint is UnresolvedWaypoint) >= 2)
		{
			UnresolvedWaypoint[] uwps =
				lines
				.Where(l => l.Endpoint is not null && l.Endpoint is UnresolvedWaypoint)
				.Select(l => (UnresolvedWaypoint)l.Endpoint!)
				.Take(2).ToArray();

			referencePoint = uwps[0].Resolve(fixes, uwps[1]);
		}

		/// <exception cref="ArgumentException">Could not find a waypoint.</exception>
		ApproachLine fix(ApproachLine line)
		{
			MagneticCourse fixMagnetic(MagneticCourse mc)
			{
				decimal? var = null;
				if (line.ReferencedNavaid is not null && navaids.TryGetValue(line.ReferencedNavaid, out HashSet<Navaid>? value))
					var =
value.OrderBy(na => na.Position.DistanceTo((referencePoint ?? (line.Endpoint is ICoordinate c ? c : throw new Exception("Unable to pin magvar for IAP."))).GetCoordinate()))
					   .Select(na =>
						   na switch
						   {
							   VOR v => v.MagneticVariation,
							   NavaidILS ni => ni.MagneticVariation,
							   ILS i => i.LocalizerCourse.Variation,
							   NDB n => n.MagneticVariation,
							   _ => null
						   }
					   )
					   .FirstOrDefault(d => d is not null);

				return mc with {
					Variation = var ??
						aerodromes.GetLocalMagneticVariation(
							(line.Endpoint, referencePoint) switch {
								(ICoordinate c, _) => c.GetCoordinate(),
								(_, null) => throw new Exception("Unable to pin magvar for IAP."),
								(_, ICoordinate rp) => rp.GetCoordinate()
							}).Variation
				};
			}

			if (line.Endpoint is UnresolvedWaypoint uwep)
				line = line with { Endpoint = uwep.Resolve(fixes, referencePoint?.GetCoordinate()) };
			else if (line.Endpoint is UnresolvedDistance urd)
				line = line with { Endpoint = urd.Resolve(fixes, referencePoint?.GetCoordinate()) };
			else if (line.Endpoint is UnresolvedRadial urr)
				line = line with { Endpoint = urr.Resolve(navaids, referencePoint?.GetCoordinate()) };

			if (line.Via is Arc a)
			{
				if (a.Centerwaypoint is UnresolvedWaypoint uwap && a.Centerpoint is null)
					a = a with { Centerpoint = uwap.Resolve(fixes, referencePoint?.GetCoordinate()) };
				if (a.ArcTo.Variation is null)
					a = a with { ArcTo = fixMagnetic(a.ArcTo) };

				line = line with { Via = a };
			}
			else if (line.Via is Racetrack r && r.Waypoint is UnresolvedWaypoint uwrp)
				line = line with { Via = r with { Point = uwrp.Resolve(fixes, referencePoint?.GetCoordinate()) } };
			else if (line.Via is MagneticCourse mc && mc.Variation is null)
			{
				line = line with {
					Via = fixMagnetic(mc)
				};
			}

			if (line.Via is Racetrack rt && rt.Point is null)
				throw new Exception();

			return line;
		}

		for (int linectr = 0; linectr < lines.Length;)
		{
			ApproachLine lineHead = lines[linectr];

			switch (lineHead.RouteType)
			{
				case ApproachLine.ApproachRouteType.Transition:
					List<Instruction> rt = [];
					for (; linectr < lines.Length && (lines[linectr].RouteType, lines[linectr].Transition) == (ApproachLine.ApproachRouteType.Transition, lineHead.Transition); ++linectr)
					{
						try
						{
							var line = fix(lines[linectr]);

							rt.Add(new(line.FixInstruction, line.Endpoint, line.Via, line.ReferenceFix?.Resolve(fixes, new UnresolvedWaypoint(line.Airport)), line.SpeedRestriction, line.AltitudeRestriction));
						}
						catch (Exception aex)
						{
							// Fix not in DB. FAA screwed up.
							Console.Error.WriteLine($"{lines[linectr].Name} @ {lines[linectr].Airport}: {aex.Message} Please call the FAA at +1 (800) 638-8972 to report the discrepancy.");
						}
					}
					transitions.Add(lineHead.Transition, [.. rt]);
					break;

				default:
					List<Instruction> cr = [];
					for (; linectr < lines.Length && lines[linectr].RouteType != ApproachLine.ApproachRouteType.Transition; ++linectr)
					{
						try
						{
							var line = fix(lines[linectr]);

							cr.Add(new(line.FixInstruction, line.Endpoint, line.Via, line.ReferenceFix?.Resolve(fixes, new UnresolvedWaypoint(line.Airport)), line.SpeedRestriction, line.AltitudeRestriction));
						}
						catch (Exception aex)
						{
							// Fix not in DB. FAA screwed up.
							Console.Error.WriteLine($"{lines[linectr].Name} @ {lines[linectr].Airport}: {aex.Message} Please call the FAA at +1 (800) 638-8972 to report the discrepancy.");
						}
					}
					commonRoute = [.. cr];
					break;
			}
		}
	}

	public override IEnumerable<Instruction?> SelectAllRoutes(Dictionary<string, HashSet<ICoordinate>> fixes)
	{
		foreach (var inboundTransition in transitions.Values)
		{
			foreach (var instr in inboundTransition)
				yield return instr;

			yield return null;
		}

		foreach (Instruction i in commonRoute)
			yield return i;
	}

	public override bool HasRoute(string? inboundTransition, string? outboundTransition) =>
		outboundTransition is null && (inboundTransition is null || transitions.ContainsKey(inboundTransition));

	public override IEnumerable<Instruction> SelectRoute(string? inboundTransition, string? outboundTransition)
	{
		string lastName = "";

		if (outboundTransition is not null)
			throw new ArgumentException($"Outbound transitions don't make sense for an IAP.", nameof(outboundTransition));

		else if (inboundTransition is not null)
		{
			if (!transitions.TryGetValue(inboundTransition, out Instruction[]? value))
				throw new ArgumentException($"Approach transition {inboundTransition} was not found.", nameof(inboundTransition));

			foreach (Instruction i in value)
			{
				if (i.Endpoint is NamedCoordinate nc)
					if (nc.Name == lastName)
						continue;
					else
						lastName = nc.Name;

				yield return i;
			}
		}

		foreach (Instruction i in commonRoute)
		{
			if (i.Endpoint is NamedCoordinate nc)
				if (nc.Name == lastName)
					continue;
				else
					lastName = nc.Name;

			yield return i;
		}

		yield break;
	}

	public IEnumerable<(string? Inbound, string? Outbound)> EnumerateTransitions()
	{
		HashSet<string?> inbounds = transitions.Keys.Select(k => k == "ALL" ? null : k).ToHashSet();

		if (inbounds.Count == 0)
			inbounds = new(new string?[] { null });

		return inbounds.Select(i => (i, (string?)null));
	}

	public override string ToString() => $"{Name} (IAP - {Airport})";

	public class ApproachJsonConverter : JsonConverter<Approach>
	{
		public override Approach? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
		{
			if (reader.TokenType != JsonTokenType.StartObject)
				throw new JsonException();

			string name = string.Empty, airport = string.Empty;
			Dictionary<string, Instruction[]> transitions = [];
			Instruction[] commonRoute = [];

			while (reader.Read())
			{
				if (reader.TokenType == JsonTokenType.EndObject)
					break;

				switch (reader.GetString())
				{
					case "Name":
						reader.Read();
						name = reader.GetString() ?? throw new JsonException();
						break;

					case "Ap":
						reader.Read();
						airport = reader.GetString() ?? throw new JsonException();
						break;

					case "Trans":
						reader.Read();
						transitions = JsonSerializer.Deserialize<Dictionary<string, Instruction[]>>(ref reader, options) ?? throw new JsonException();
						break;

					case "Common":
						reader.Read();
						commonRoute = JsonSerializer.Deserialize<Instruction[]>(ref reader, options) ?? throw new JsonException();
						break;

					default:
						throw new JsonException();
				}
			}

			if (reader.TokenType != JsonTokenType.EndObject)
				throw new JsonException();

			return new(name, airport, transitions, commonRoute);
		}

		public override void Write(Utf8JsonWriter writer, Approach value, JsonSerializerOptions options)
		{
			writer.WriteStartObject();

			writer.WriteString("Name", value.Name);
			writer.WriteString("Ap", value.Airport);

			writer.WritePropertyName("Trans");
			JsonSerializer.Serialize(writer, value.transitions, options);

			writer.WritePropertyName("Common");
			JsonSerializer.Serialize(writer, value.commonRoute, options);

			writer.WriteEndObject();
		}
	}
}