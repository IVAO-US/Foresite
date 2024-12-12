using Foresite.Components.Shared;

using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Foresite.Services;

public class IvaoApiService(HttpClient _http)
{
	public event Action? OnCacheUpdated;

	public bool Registered => _user is not null;

	private ForesiteAuthorizeView.ForesiteUser? _user = null;
	public void RegisterUser(ForesiteAuthorizeView.ForesiteUser user)
	{
		_user = user;
		_http.BaseAddress = new("https://api.ivao.aero/v2/");
		_http.DefaultRequestHeaders.Authorization = AuthenticationHeaderValue.Parse($"bearer {_user.BearerToken}");
	}

	private static (DateTimeOffset Updated, Fra[] Permissions) _fraCache = (DateTimeOffset.MinValue, []);

	public Fra[] GetFras()
	{
		VerifyState();

		if (DateTimeOffset.UtcNow - _fraCache.Updated > TimeSpan.FromMinutes(5))
			Task.Run(async () =>
			{
				// Ah well. Not cached. Fix the cache for the next time it's called.
				_fraCache = (DateTimeOffset.UtcNow, _fraCache.Permissions);

				HashSet<FraGrant> grants = [];
				FraApiResponse? resp;
				string url = "fras?perPage=100&divisionId=US&expand=true&page=";
				JsonSerializerOptions opts = new(JsonSerializerDefaults.Web);

				// Go get all the pages and combine them.
				try
				{
					for (int page = 1; (resp = await _http.GetFromJsonAsync<FraApiResponse>(url + page, opts, CancellationToken.None))?.Page <= resp?.Pages; ++page)
						grants.UnionWith(resp.Items);


					_fraCache = (DateTimeOffset.UtcNow, [.. grants.AsParallel().AsUnordered().Select(g => new Fra(g))]);
					OnCacheUpdated?.Invoke();
				}
				catch { }
			}).Wait();

		return _fraCache.Permissions;
	}

	[MemberNotNull(nameof(_user))]
	private void VerifyState() { if (_user is null) throw new Exception("You must authenticate before calling API methods!"); }


	public class FraApiResponse
	{
		public int TotalItems { get; set; }
		public int PerPage { get; set; }
		public int Page { get; set; }
		public int Pages { get; set; }
		public FraGrant[] Items { get; set; } = [];
	}

	public class FraGrant
	{
		public int Id { get; set; }
		public int? UserId { get; set; }
		public int? AtcPositionId { get; set; }
		public int? SubcenterId { get; set; }
		public string StartTime { get; set; } = "";
		public string EndTime { get; set; } = "";
		public bool DaySun { get; set; }
		public bool DayMon { get; set; }
		public bool DayTue { get; set; }
		public bool DayWed { get; set; }
		public bool DayThu { get; set; }
		public bool DayFri { get; set; }
		public bool DaySat { get; set; }
		public DateTimeOffset? Date { get; set; }
		public ControllerRating? MinAtc { get; set; }
		public bool Active { get; set; }
		public AtcPosition? AtcPosition { get; set; }
		public Subcenter? Subcenter { get; set; }
	}

	public class AtcPosition
	{
		public int Id { get; set; }
		public string AirportId { get; set; } = "";
		public string AtcCallsign { get; set; } = "";
		public string ComposePosition { get; set; } = "";
		public string MiddleIdentifier { get; set; } = "";
		public string Position { get; set; } = "";
	}

	public class Subcenter
	{
		public int Id { get; set; }
		public string CenterId { get; set; } = "";
		public string AtcCallsign { get; set; } = "";
		public string ComposePosition { get; set; } = "";
		public string MiddleIdentifier { get; set; } = "";
		public string Position { get; set; } = "";
	}

}

public sealed record Fra(bool Enabled, string Position, PositionType Level, Fra.FraPrincipal Principal, Fra.FraTiming Timing)
{
	public Fra(IvaoApiService.FraGrant g) : this(false, "", 0, new UserPrincipal(""), new WeekdayTiming([]))
	{
		Enabled = g.Active;
		Position = g.AtcPosition?.ComposePosition ?? g.Subcenter!.ComposePosition;
		Level = Enum.Parse<PositionType>(g.AtcPosition?.Position ?? g.Subcenter!.Position);
		Principal =
			g.MinAtc is null
			? new UserPrincipal(g.UserId!.Value.ToString("000000"))
			: new RatingPrincipal(g.MinAtc.Value);
		Timing =
			g.Date is null
			? new WeekdayTiming([
				..(g.DaySun ? (string[])["Sunday"] : []),
				..(g.DayMon ? (string[])["Monday"] : []),
				..(g.DayTue ? (string[])["Tuesday"] : []),
				..(g.DayWed ? (string[])["Wednesday"] : []),
				..(g.DayThu ? (string[])["Thursday"] : []),
				..(g.DayFri ? (string[])["Friday"] : []),
				..(g.DaySat ? (string[])["Saturday"] : []),
			])
			: new DateTiming(g.Date.Value);
	}

	public abstract record FraPrincipal;
	public sealed record UserPrincipal(string Vid) : FraPrincipal;
	public sealed record RatingPrincipal(ControllerRating MinimumRating) : FraPrincipal;

	public abstract record FraTiming;
	public sealed record WeekdayTiming(string[] PermittedDays) : FraTiming;
	public sealed record DateTiming(DateTimeOffset PermittedDate) : FraTiming;
}

public enum PositionType
{
	DEL = 2,
	GND,
	TWR,
	APP,
	DEP,
	CTR,
	FSS
}

public enum ControllerRating
{
	AS1 = 2,
	AS2,
	AS3,
	ADC,
	APC,
	ACC,
	SEC,
	SAI,
	CAI
}