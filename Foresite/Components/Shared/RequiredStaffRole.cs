using Foresite.Services;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Web;

namespace Foresite.Components.Shared;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
public class RequiredStaffRoleAttribute(params StaffRole[] permittedRoles) : AuthorizeAttribute, IAsyncAuthorizationFilter
{
	public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
	{
		// Make sure they're signed in.
		if (context.HttpContext.User.Identity is not IIdentity ident || !ident.IsAuthenticated)
		{
			context.Result = new RedirectToRouteResult($"/auth/login?returnUrl={HttpUtility.UrlEncode(context.HttpContext.Request.Path)}");
			return;
		}

		var user = ident.ToUser(await context.HttpContext.GetTokenAsync());

		// We have no secondments. If they're not US div or not staff, then they're not US staff.
		if (user.Division != "US" || user.StaffPositions.Length == 0)
		{
			context.Result = new ForbidResult();
			return;
		}

		// If no permitted roles specified, assume they're just checking the user is staff at all.
		if (permittedRoles.Length == 0)
			return;

		// Check each permitted role to make sure the staff member has at least one of them.
		if (!permittedRoles.Where(_staffPositionChecks.ContainsKey).Any(r => user.StaffPositions.Any(p => _staffPositionChecks[r].IsMatch(p))))
		{
			context.Result = new ForbidResult();
			return;
		}	
	}

#pragma warning disable SYSLIB1045 // Convert to 'GeneratedRegexAttribute'.
	private readonly Dictionary<StaffRole, Regex> _staffPositionChecks = new() {
		{ StaffRole.DivHq, new(@"US-A?DIR") },
		{ StaffRole.Membership, new(@"US-MA?C") },
		{ StaffRole.AtcOps, new(@"US-AOA?C") },
		{ StaffRole.ArtccStaff, new(@"(KZ..|PHZH|PAZA|PGZU)-A?CH(A\d)?") },
		{ StaffRole.Training, new(@"US-T(A?C|A\d|0\d)") }
	};
#pragma warning restore
}

public enum StaffRole
{
	DivHq,
	Membership,
	AtcOps,
	ArtccStaff,
	Training
}